using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>
/// The navigation list, the status strip under every page, and the session log the strip opens.
/// The log is kept in memory for this run of the app only.
/// </summary>
public sealed partial class MainWindow
{
    sealed record SessionEvent(DateTimeOffset At, string Message);

    string currentRoute = "Home";
    readonly List<SessionEvent> sessionEvents = [];
    readonly ListView sessionEventList = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly TextBlock stripStatus = Text("Ready · no active operation", 12);
    readonly DispatcherTimer stripClock = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTimeOffset? operationStarted;
    Action? refreshSessionEvents;

    /// <summary>The system highlight colour in high contrast, the brand copper otherwise.</summary>
    static SolidColorBrush SystemAccent() => new(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
        ? new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight)
        : Windows.UI.Color.FromArgb(255, 181, 93, 50));

    /// <summary>Adds a line to the session log and shows it in the strip. Repeats of the latest line are skipped.</summary>
    void LogEvent(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || sessionEvents.LastOrDefault()?.Message == message) return;
        sessionEvents.Add(new(DateTimeOffset.Now, message.Length > 1200 ? message[..1200] : message));
        if (sessionEvents.Count > 200) sessionEvents.RemoveAt(0);
        refreshSessionEvents?.Invoke();
        stripStatus.Text = message.Split('\n')[0];
    }

    FrameworkElement BuildStatusStrip()
    {
        // ---- The drawer: this session's log, newest first ----
        var search = BrandControl(new TextBox { PlaceholderText = "Search this session's log…", MinWidth = 240 });
        AutomationProperties.SetName(search, "Search the session log");
        BrandControl(sessionEventList);
        AutomationProperties.SetName(sessionEventList, "Session log");
        // Rows are built in code: the time, then the message on up to two lines.
        sessionEventList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid/></DataTemplate>");
        sessionEventList.ContainerContentChanging += (_, e) =>
        {
            if (e.ItemContainer.ContentTemplateRoot is not Grid cell || e.Item is not SessionEvent entry) return;
            cell.Children.Clear(); cell.ColumnDefinitions.Clear();
            if (e.InRecycleQueue) return;
            cell.ColumnSpacing = 16; cell.Margin = new Thickness(0, 6, 0, 6);
            cell.ColumnDefinitions.Add(new() { Width = new GridLength(96) });
            cell.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var time = Muted(entry.At.LocalDateTime.ToString("T"), 12);
            var message = Text(entry.Message, 13);
            message.FontSize = 13; message.MaxLines = 2; message.TextTrimming = TextTrimming.CharacterEllipsis;
            cell.Children.Add(time);
            Grid.SetColumn(message, 1); cell.Children.Add(message);
            ToolTipService.SetToolTip(cell, entry.Message);
            AutomationProperties.SetName(e.ItemContainer, entry.At.LocalDateTime.ToString("T") + " " + entry.Message);
        };
        var copy = ToolButton(Symbol.Copy, "Copy selected", (_, _) =>
        {
            if (sessionEventList.SelectedItem is not SessionEvent entry) return;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText($"{entry.At.LocalDateTime:G}  {entry.Message}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }, "Copy the selected log line");
        var drawerEmpty = Muted("Nothing has happened in this session yet.", 13);
        drawerEmpty.HorizontalAlignment = HorizontalAlignment.Center; drawerEmpty.VerticalAlignment = VerticalAlignment.Center;
        refreshSessionEvents = () =>
        {
            var shown = sessionEvents.Where(e => string.IsNullOrWhiteSpace(search.Text) || e.Message.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray();
            sessionEventList.ItemsSource = shown;
            drawerEmpty.Text = sessionEvents.Count == 0 ? "Nothing has happened in this session yet." : "No log line matches this search.";
            drawerEmpty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            copy.IsEnabled = sessionEventList.SelectedItem is SessionEvent;
        };
        search.TextChanged += (_, _) => refreshSessionEvents();
        sessionEventList.SelectionChanged += (_, _) => copy.IsEnabled = sessionEventList.SelectedItem is SessionEvent;

        var drawerTitle = new StackPanel { Spacing = 1 };
        drawerTitle.Children.Add(Display("This session", 16));
        drawerTitle.Children.Add(Muted("What the app has done since it started. Kept in memory only.", 12));
        var drawerBar = CommandRow([drawerTitle], [search, copy]);
        var drawerLayout = new Grid { RowSpacing = 8 };
        drawerLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        drawerLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        drawerLayout.Children.Add(drawerBar);
        Grid.SetRow(sessionEventList, 1); drawerLayout.Children.Add(sessionEventList);
        Grid.SetRow(drawerEmpty, 1); drawerLayout.Children.Add(drawerEmpty);
        var drawer = Surface(drawerLayout, 14);
        drawer.Visibility = Visibility.Collapsed; drawer.Margin = new Thickness(20, 0, 20, 10); drawer.Height = 250;
        root.SizeChanged += (_, _) => drawer.Height = Math.Clamp(root.ActualHeight * .27, 170, 290);

        // ---- The strip: log toggle, live-state dot and status line, elapsed time, cancel ----
        var toggle = Button("Activity", (_, _) => drawer.Visibility = drawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        SetTrayToggle(toggle, false);
        var cancelActive = Button("Cancel active", (_, _) => operation?.Cancel());
        cancelActive.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(cancelActive, "Cancel the running operation (Esc)");
        var elapsed = Muted("", 12);
        elapsed.VerticalAlignment = VerticalAlignment.Center;
        stripStatus.MaxLines = 1; stripStatus.TextTrimming = TextTrimming.CharacterEllipsis; stripStatus.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(stripStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var statusLine = TrayStatus(stripStatus);
        var controls = Row(elapsed, cancelActive);
        var strip = new Grid { ColumnSpacing = 12, Padding = new Thickness(20, 4, 20, 4), MinHeight = 40 };
        strip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        strip.Children.Add(toggle);
        Grid.SetColumn(statusLine, 1); strip.Children.Add(statusLine);
        Grid.SetColumn(controls, 2); strip.Children.Add(controls);

        stripClock.Tick += (_, _) =>
        {
            bool active = operation != null;
            cancelActive.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            UpdateTrayDot(active);
            if (active)
            {
                operationStarted ??= DateTimeOffset.Now;
                elapsed.Text = "Running · " + (DateTimeOffset.Now - operationStarted.Value).ToString(@"mm\:ss");
            }
            else
            {
                if (operationStarted != null) elapsed.Text = "Last operation · " + (DateTimeOffset.Now - operationStarted.Value).ToString(@"mm\:ss");
                operationStarted = null;
            }
            SetTrayToggle(toggle, drawer.Visibility == Visibility.Visible);
        };
        root.Loaded += (_, _) => stripClock.Start();
        Closed += (_, _) => stripClock.Stop();
        // Every status message the pages write becomes a log line.
        status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => LogEvent(status.Text));
        refreshSessionEvents();
        return Stack(TrayChrome(strip), drawer);
    }

    /// <summary>The pages every database has: Home and Connections, SQL, Tables and Extensions, then Activity and the two shell commands in the footer.</summary>
    void BuildNavigation()
    {
        void Add(string key, string label, Symbol icon, bool footer = false)
        {
            var item = new NavigationViewItem { Content = label, Tag = key, Icon = new SymbolIcon(icon), Height = 36, MinHeight = 36, FontSize = 15 };
            if (footer) nav.FooterMenuItems.Add(item); else nav.MenuItems.Add(item);
        }
        // Route keys are internal and stable ("Query" and "Table" predate the labels shown here).
        Add("Home", "Home", Symbol.Home); Add("Connections", "Connections", Symbol.Contact);
        nav.MenuItems.Add(new NavigationViewItemHeader { Content = "DATABASE" });
        Add("Query", "SQL", Symbol.Edit); Add("Table", "Tables", Symbol.List); Add("Extensions", "Extensions", Symbol.AllApps);
        // Pages that depend on an installed extension are added by RebuildDatabaseNavigation.
        Add("Activity", "Activity", Symbol.Clock, true);
        AddShellFooterCommands();
    }

    /// <summary>Keeps the window-level accent and text brushes on the brand colours; high contrast clears them.</summary>
    void ApplyAccentTheme()
    {
        string[] keys = ["AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush", "NavigationViewSelectionIndicatorForeground", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed", "TextFillColorPrimaryBrush"];
        if (HighContrast) { foreach (var key in keys) root.Resources.Remove(key); return; }
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var accent = Windows.UI.Color.FromArgb(255, dark ? (byte)241 : (byte)181, dark ? (byte)188 : (byte)93, dark ? (byte)119 : (byte)50);
        var ink = dark ? Windows.UI.Color.FromArgb(255, 240, 239, 235) : Windows.UI.Color.FromArgb(255, 24, 34, 54);
        foreach (var key in keys)
        {
            var color = key == "TextFillColorPrimaryBrush" ? ink : accent;
            // Mounted controls hold the brush, so its colour is changed in place.
            if (root.Resources.TryGetValue(key, out var prior) && prior is SolidColorBrush brush) brush.Color = color;
            else root.Resources[key] = new SolidColorBrush(color);
        }
    }
}
