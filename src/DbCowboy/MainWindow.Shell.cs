using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DbCowboy;

/// <summary>
/// The window shell: the sidebar connection block, the footer commands, the page surface and the status strip.
/// </summary>
public sealed partial class MainWindow
{
    const string CommandsTag = "@commands";

    readonly StackPanel connectionPills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly TextBlock productionWarning = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Ellipse trayDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// The sidebar connection block: which saved connection is selected, whether it is live,
    /// and the one action that makes sense right now.
    /// </summary>
    FrameworkElement BuildConnectionBlock()
    {
        // The picker shows the name with its endpoint underneath, so two similar names stay distinguishable.
        connections.DisplayMemberPath = "";
        connections.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='1'>" +
            "<TextBlock Text='{Binding Name}' FontSize='13' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>" +
            "<TextBlock Text='{Binding Endpoint}' FontSize='11' Opacity='0.75' TextTrimming='CharacterEllipsis'/>" +
            "</StackPanel></DataTemplate>");
        connections.SelectionChanged += (_, _) => RememberSelectedConnection();
        AutomationProperties.SetName(connections, "Choose saved Postgres connection");
        BrandControl(connections);

        connect.HorizontalAlignment = HorizontalAlignment.Stretch;
        var add = IconButton(Symbol.Add, "New connection", NewConnectionClick);
        disconnectSession.Content = new Viewbox { Width = 16, Height = 16, Child = new FontIcon { Glyph = "" } };
        disconnectSession.Padding = new Thickness(9, 8, 9, 8);
        disconnectSession.Click += DisconnectClick;
        ToolTipService.SetToolTip(disconnectSession, "Disconnect active session");
        AutomationProperties.SetName(disconnectSession, "Disconnect active session");

        // Identity art sits beside the picker when the active profile has one. It is a cut-out: no badge behind it.
        connectionIdentity.Width = connectionIdentity.Height = 32;
        connectionIdentity.VerticalAlignment = VerticalAlignment.Center;
        var picker = new Grid { ColumnSpacing = 6 };
        picker.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        picker.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        picker.Children.Add(connectionIdentity);
        Grid.SetColumn(connections, 1); picker.Children.Add(connections);

        var actions = new Grid { ColumnSpacing = 6 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.Children.Add(connect);
        Grid.SetColumn(add, 1); actions.Children.Add(add);
        Grid.SetColumn(disconnectSession, 2); actions.Children.Add(disconnectSession);

        Themed(productionWarning, (label, dark) =>
        {
            if (HighContrast) label.ClearValue(TextBlock.ForegroundProperty);
            else label.Foreground = new SolidColorBrush(ToneColor(Tone.Warn, dark));
        });

        var block = new StackPanel { Spacing = 8 };
        block.Children.Add(Eyebrow("Connection"));
        block.Children.Add(picker);
        block.Children.Add(connectionPills);
        block.Children.Add(productionWarning);
        block.Children.Add(actions);
        var card = Surface(block, 12, 12);
        // The menu button lives in the title bar now, so the card sits at the pane's own inset.
        card.Margin = new Thickness(8, 4, 8, 10);

        // Read-only is part of the connection's state, so the pills follow the checkbox.
        readOnly.Checked += (_, _) => RenderConnectionState();
        readOnly.Unchecked += (_, _) => RenderConnectionState();
        // A collapsed pane has no room for the block.
        nav.PaneClosing += (_, _) => card.Visibility = Visibility.Collapsed;
        nav.PaneOpening += (_, _) => card.Visibility = Visibility.Visible;
        return card;
    }

    /// <summary>Redraws the state pills and the production warning in the sidebar block.</summary>
    void RenderConnectionState()
    {
        connectionPills.Children.Clear();
        if (connectingProfile != null) connectionPills.Children.Add(Pill("Connecting…", Tone.Accent));
        else if (db != null) connectionPills.Children.Add(Pill("Connected", Tone.Good));
        else connectionPills.Children.Add(Pill("Not connected"));
        if (db != null)
        {
            connectionPills.Children.Add(Pill(readOnly.IsChecked == true ? "Read-only" : "Writes on", readOnly.IsChecked == true ? Tone.Neutral : Tone.Accent));
            ToolTipService.SetToolTip(connectionPills, $"{db.Profile.Name}\n{db.Profile.Username}@{db.Profile.Host}:{db.Profile.Port}/{db.Profile.Database}");
        }
        else ToolTipService.SetToolTip(connectionPills, "No live database session");
        bool production = db?.Profile.Production == true;
        productionWarning.Text = production ? "Production database. Check every write before you run it." : "";
        productionWarning.Visibility = production ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Commands lives in the navigation footer as an action, never a selected page; the theme switch sits beside it.</summary>
    void AddShellFooterCommands()
    {
        NavigationViewItem Item(string tag, string label, IconElement icon, string tip)
        {
            var item = new NavigationViewItem { Content = label, Tag = tag, Icon = icon, Height = 36, MinHeight = 36, FontSize = 15, SelectsOnInvoked = false };
            AutomationProperties.SetName(item, label);
            ToolTipService.SetToolTip(item, tip);
            nav.FooterMenuItems.Add(item);
            return item;
        }
        Item(CommandsTag, "Commands", Fluent.Icon(Fluent.Motif.Find), "Search commands, tables and saved queries (Ctrl+K)");

        // Theme is a visible three-way choice under the footer items, not a menu to open.
        var theme = new ThemeSwitch(root.RequestedTheme, segment => BrandControl(segment)) { Margin = new Thickness(12, 4, 12, 10) };
        theme.Changed += chosen =>
        {
            var previous = (root.RequestedTheme, workspace.Preferences);
            root.RequestedTheme = chosen;
            workspace.Preferences = workspace.Preferences with { Theme = chosen.ToString() };
            if (Store.IsUnreadable(WorkspaceState.File)) { status.Text = "Theme applied for this session · workspace.json could not be read at startup, so it is not saved"; return; }
            // Saved at once so a failed write can be undone here instead of surfacing later in autosave.
            if (SaveWorkspace()) return;
            (root.RequestedTheme, workspace.Preferences) = previous;
            theme.Display(root.RequestedTheme);
            status.Text += " · theme not changed";
        };
        nav.PaneFooter = theme;
    }

    /// <summary>Runs a footer command. Returns false when the tag is an ordinary page route.</summary>
    bool ShellCommand(string tag)
    {
        if (tag == CommandsTag) FocusTitleSearch();
        else return false;
        return true;
    }

    /// <summary>Paints the page area with the brand surface: paper in light, night in dark.</summary>
    void ApplyShellSurface()
    {
        // BrandControl(nav) created these dictionaries; NavigationView reads its content background from them.
        var surfaces = new[] { ("Light", Brand.Paper), ("Default", Color.FromArgb(255, 19, 31, 38)) };
        foreach (var (theme, color) in surfaces)
            if (nav.Resources.ThemeDictionaries[theme] is ResourceDictionary dictionary)
            {
                dictionary["NavigationViewContentBackground"] = new SolidColorBrush(color);
                // The Windows 11 content edge: a hairline and a rounded top-left corner where the page meets the Mica sidebar.
                dictionary["NavigationViewContentGridBorderBrush"] = BrandUi.LineBrush(theme == "Default");
            }
        nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(8, 0, 0, 0);
        nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(1, 1, 0, 0);
    }

    /// <summary>The status strip's Activity toggle: a clock, the word, and a chevron for the drawer state.</summary>
    static void SetTrayToggle(Button toggle, bool open)
    {
        if (toggle.Tag is bool shown && shown == open) return;   // called every second; only rebuild on a change
        toggle.Tag = open;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new Viewbox { Width = 14, Height = 14, Child = new SymbolIcon(Symbol.Clock) });
        content.Children.Add(new TextBlock { Text = "Activity", FontSize = 13 });
        content.Children.Add(new Viewbox { Width = 10, Height = 10, Child = new FontIcon { Glyph = open ? "" : "" } });
        toggle.Content = content;
        toggle.Padding = new Thickness(10, 5, 10, 5);
        AutomationProperties.SetName(toggle, open ? "Hide activity log" : "Show activity log");
        ToolTipService.SetToolTip(toggle, open ? "Hide this session's activity log" : "Show this session's activity log");
    }

    /// <summary>Puts the live-state dot in front of the status line.</summary>
    FrameworkElement TrayStatus(TextBlock line)
    {
        var row = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(trayDot);
        Grid.SetColumn(line, 1); row.Children.Add(line);
        AutomationProperties.SetAccessibilityView(trayDot, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return row;
    }

    /// <summary>Amber while something runs, teal while connected and idle, grey when there is no session.</summary>
    void UpdateTrayDot(bool active)
    {
        bool dark = root.ActualTheme == ElementTheme.Dark;
        if (HighContrast) { trayDot.Fill = SystemAccent(); return; }
        trayDot.Fill = new SolidColorBrush(ToneColor(active ? Tone.Accent : db != null ? Tone.Good : Tone.Neutral, dark));
        ToolTipService.SetToolTip(trayDot, active ? "An operation is running" : db != null ? "Connected and idle" : "Not connected");
    }

    /// <summary>The strip sits on the page surface with a hairline above it.</summary>
    static Border TrayChrome(FrameworkElement content) =>
        Themed(new Border { Child = content, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 2, 0, 2) },
            (strip, dark) => strip.BorderBrush = HighContrast ? SystemAccent() : BrandUi.LineBrush(dark));
}
