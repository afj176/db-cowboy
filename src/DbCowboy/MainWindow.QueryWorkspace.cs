using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

/// <summary>The SQL page: its header, the command bar, and the tabbed editor with framed results.</summary>
public sealed partial class MainWindow
{
    Action? refreshSqlChrome;

    /// <summary>
    /// The editor view: one command bar over the query tabs. Running is the primary action; everything that is
    /// not part of the write-run-read loop sits in the More menu.
    /// </summary>
    FrameworkElement BuildSqlEditorView()
    {
        // run and cancel are created in the constructor because Work() toggles them; they get their look here.
        var runContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        runContent.Children.Add(new Viewbox { Width = 14, Height = 14, Child = new SymbolIcon(Symbol.Play) });
        runContent.Children.Add(new TextBlock { Text = "Run statement" });
        run.Content = runContent;
        AutomationProperties.SetName(run, "Run statement");
        ToolTipService.SetToolTip(run, "Run the statement at the caret, or the selection (Ctrl+Enter)");

        var runAll = ToolButton(Symbol.Forward, "Run all", RunAllClick, "Run every statement in this tab (Ctrl+Shift+Enter)");
        var explain = ToolButton(Symbol.View, "Explain plan", ExplainClick, "Show the planner's estimate for the current statement. The query is not executed.");
        var format = ToolButton(Symbol.AlignLeft, "Format", (_, _) => editor.Format(), "Format the selection, or the whole tab (Ctrl+Shift+F)");
        var chart = ToolButton(Symbol.ThreeBars, "Chart", (_, _) => ActiveQuery.ShowChart(), "Chart the visible rows of the current result");

        // Cancel only exists while something is running.
        ToolTipService.SetToolTip(cancel, "Cancel the running statement (Esc)");
        void ShowCancel() => cancel.Visibility = cancel.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        cancel.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => ShowCancel());
        ShowCancel();

        ToolTipService.SetToolTip(readOnly, "On: statements run in a read-only transaction. Off: writes are allowed and each run asks first.");

        var more = new DropDownButton { Content = "More", Padding = new Thickness(12, 7, 10, 7), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(more, "More SQL commands");
        var menu = Fluent.Acrylic(new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight });
        more.Flyout = menu;
        void Item(string label, Symbol icon, RoutedEventHandler action, string shortcut = "")
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(icon), KeyboardAcceleratorTextOverride = shortcut };
            item.Click += action;
            menu.Items.Add(item);
        }
        Item("Complete SQL", Symbol.Find, (_, _) => editor.Complete(), "Ctrl+Space");
        Item("New query tab", Symbol.Add, (_, _) => AddQuery(), "Ctrl+T");
        Item("Rename or favorite this tab…", Symbol.Rename, RenameTabClick);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Open SQL file…", Symbol.OpenFile, OpenSqlClick);
        Item("Save SQL file…", Symbol.Save, SaveSqlClick);
        Item("Save as snippet…", Symbol.Favorite, SaveSnippetClick);
        Item("Open query library…", Symbol.Library, SnippetsClick);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Export visible results…", Symbol.Download, (_, _) => Export(results.VisibleTable));
        Item("Copy selected row as JSON", Symbol.Copy, (_, _) => CopyRow(results.Selected));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Browse tables", Symbol.List, (_, _) => Navigate("Table"));

        var bar = CommandRow([run, runAll, explain, cancel, ToolSeparator(), format, chart], [readOnly, more]);
        var page = new Grid { RowSpacing = 4 };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(bar);
        // TabView aligns to the top by default; the editor and results should use the whole page.
        queryTabs.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(queryTabs, 1); page.Children.Add(queryTabs);
        return page;
    }

    /// <summary>Wraps the editor view with the page header.</summary>
    FrameworkElement BuildQueryWorkspace(FrameworkElement editorView)
    {
        var target = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var header = PageHeader(Display("SQL", 30), Muted("Write queries, read plans and chart results. Tabs are saved per connection.", 14), target);

        // Says where Run will send the statement, so a production target is never a surprise.
        refreshSqlChrome = () =>
        {
            if (db == null) { target.Content = Row(Pill("Not connected"), Button("Connections", ConnectionsClick)); return; }
            var pills = Row(Pill("Runs on " + db.Profile.Name, Tone.Good));
            if (db.Profile.Production) pills.Children.Add(Pill("Production", Tone.Warn));
            target.Content = pills;
        };
        refreshSqlChrome();

        var page = new Grid { RowSpacing = 8 };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(header);
        Grid.SetRow(editorView, 1); page.Children.Add(editorView);
        return page;
    }
}
