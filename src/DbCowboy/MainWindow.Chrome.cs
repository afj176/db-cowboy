using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>
/// Window chrome: content drawn into the title bar (WinUI TitleBar with the brand mark, the live connection and a
/// command search), Mica showing through the sidebar and caption area, page transitions and window placement.
/// </summary>
public sealed partial class MainWindow
{
    TitleBar? titleBar;
    readonly AutoSuggestBox titleSearch = new() { PlaceholderText = "Search commands, tables and queries", QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 260, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    Dictionary<string, Action> titleCommands = [];
    string? previousRoute;

    /// <summary>
    /// Extends the window into the caption area and puts a TitleBar there: pane toggle, brand mark, the connected
    /// database as subtitle, and the command search in the middle. The caption buttons stay the system's own.
    /// </summary>
    FrameworkElement BuildTitleBar(FrameworkElement brand)
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // The brand lockup is the title; the live connection sits on the right, by the caption buttons.
        titleConnection.VerticalAlignment = VerticalAlignment.Center;
        titleConnection.Margin = new Thickness(8, 0, 12, 0);
        titleBar = new TitleBar
        {
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = true,
            LeftHeader = brand,
            Content = titleSearch,
            RightHeader = titleConnection,
            Height = 48
        };
        // The pane toggle moves up into the title bar, as in Windows Settings; the NavigationView's own one goes.
        nav.IsPaneToggleButtonVisible = false;
        titleBar.PaneToggleRequested += (_, _) => nav.IsPaneOpen = !nav.IsPaneOpen;
        SetTitleBar(titleBar);

        AutomationProperties.SetName(titleSearch, "Search commands, tables and queries");
        ToolTipService.SetToolTip(titleSearch, "Search commands, tables and favorite queries (Ctrl+K)");
        // Open the full list when the text field itself takes focus; focus moving onto a suggestion must not reopen it.
        titleSearch.GotFocus += (_, e) => { if (e.OriginalSource is not TextBox || titleSearchClosing) return; titleCommands = PaletteCommands(); FilterTitleSearch(); DispatcherQueue.TryEnqueue(() => titleSearch.IsSuggestionListOpen = true); };
        titleSearch.TextChanged += (_, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) FilterTitleSearch(); };

        titleSearch.QuerySubmitted += (_, e) =>
        {
            string? key = e.ChosenSuggestion as string ?? titleCommands.Keys.FirstOrDefault(k => k.Contains(titleSearch.Text, StringComparison.OrdinalIgnoreCase));
            if (key == null || !titleCommands.TryGetValue(key, out var run)) return;
            CloseTitleSearch();
            run();
        };
        titleSearch.SuggestionChosen += (_, e) => { /* keep the typed text; QuerySubmitted runs the choice */ };
        return titleBar;
    }

    bool titleSearchClosing;

    /// <summary>
    /// Clears and closes the title bar search and hands focus back to the current page's navigation item, so the
    /// list cannot linger and a command that opens a dialog does not fight the popup.
    /// </summary>
    void CloseTitleSearch()
    {
        titleSearchClosing = true;
        titleSearch.Text = ""; titleSearch.ItemsSource = null; titleSearch.IsSuggestionListOpen = false;
        (nav.SelectedItem as Control)?.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => { titleSearch.IsSuggestionListOpen = false; titleSearchClosing = false; });
    }

    void FilterTitleSearch()
    {
        var text = titleSearch.Text.Trim();
        titleSearch.ItemsSource = titleCommands.Keys.Where(k => text.Length == 0 || k.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
    }

    /// <summary>Ctrl+K and the Commands item both land in the title bar search.</summary>
    void FocusTitleSearch()
    {
        titleSearch.Focus(FocusState.Keyboard);
    }

    readonly ContentControl titleConnection = new() { IsTabStop = false };

    /// <summary>The connected database rides in the title bar, so it stays in view on every page.</summary>
    void UpdateTitleConnection()
    {
        if (db == null) { titleConnection.Content = connectingProfile != null ? Pill("Connecting…", Tone.Accent) : null; return; }
        var pills = Row(Pill(db.Profile.Name, Tone.Good));
        if (db.Profile.Production) pills.Children.Add(Pill("Production", Tone.Warn));
        pills.Spacing = 6;
        ToolTipService.SetToolTip(pills, $"{db.Profile.Username}@{db.Profile.Host}:{db.Profile.Port}/{db.Profile.Database}");
        AutomationProperties.SetName(pills, "Connected to " + db.Profile.Name);
        titleConnection.Content = pills;
        Fluent.Appear(pills, 4);
    }

    /// <summary>
    /// Lets Mica show through the sidebar and title bar while the page itself keeps the brand paper and night
    /// surfaces, with the rounded top-left corner of a Windows 11 content area. High contrast keeps solid system colours.
    /// </summary>
    void ApplyWindowMaterial(bool dark)
    {
        var chrome = AppWindow.TitleBar;
        if (HighContrast)
        {
            root.Background = new SolidColorBrush(brandSystemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
            chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = null;
            chrome.ButtonForegroundColor = chrome.ButtonInactiveForegroundColor = chrome.ButtonHoverBackgroundColor = chrome.ButtonHoverForegroundColor = null;
            return;
        }
        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var ink = dark ? Windows.UI.Color.FromArgb(255, 255, 240, 210) : Windows.UI.Color.FromArgb(255, 25, 39, 46);
        chrome.ButtonBackgroundColor = chrome.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        chrome.ButtonForegroundColor = ink;
        chrome.ButtonInactiveForegroundColor = Brand.Fade(ink, 140);
        chrome.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(28, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        chrome.ButtonHoverForegroundColor = ink;
        chrome.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(18, 255, 255, 255) : Windows.UI.Color.FromArgb(12, 0, 0, 0);
    }

    /// <summary>
    /// Which way a page enters: from a Home card it drills in; moving down the navigation it rises from below, moving
    /// up it settles from above, like the stacked pages of Windows Settings.
    /// </summary>
    Fluent.PageMotion MotionFor(string from, string to)
    {
        if (from == "Home") return Fluent.PageMotion.DrillIn;
        var order = nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>().Select(i => i.Tag as string).ToList();
        int a = order.IndexOf(from), b = order.IndexOf(to == ExtensionViewPage && currentExtensionKey != null ? ExtensionRoute(currentExtensionKey) : to);
        return a >= 0 && b >= 0 && b < a ? Fluent.PageMotion.EntranceFromAbove : Fluent.PageMotion.Entrance;
    }

    // ── Window placement ────────────────────────────────────────────────────────────────────────────

    /// <summary>Reopens the window where it was, at its size, maximized if it was; centred on first run.</summary>
    void RestorePlacement()
    {
        var saved = workspace.Window;
        try
        {
            if (saved is { Width: > 400, Height: > 300 })
            {
                var rect = new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
                // Only restore onto a display that still exists; otherwise fall back to the primary one.
                var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
                if (area != null) { AppWindow.MoveAndResize(rect); if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize(); return; }
            }
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min(1640, work.Width - 80), height = Math.Min(1060, work.Height - 60);
            AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        }
        catch { /* placement is a convenience */ }
    }

    void TrackPlacement()
    {
        AppWindow.Changed += (sender, args) =>
        {
            if (!(args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)) return;
            bool maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            bool minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized) return;
            // While maximized keep the restored bounds, so un-maximizing next time lands in the old place.
            var previous = workspace.Window;
            workspace.Window = maximized && previous != null
                ? previous with { Maximized = true }
                : new WindowPlacement(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height, maximized);
        };
    }
}
