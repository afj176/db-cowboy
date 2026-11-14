using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DbCowboy;

/// <summary>Native RichEdit SQL surface with lexical coloring, completion and caret diagnostics.</summary>
public sealed class SqlEditor : Grid
{
    readonly RichEditBox box = new() { AcceptsReturn = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, Padding = new Thickness(16, 6, 16, 6) };
    readonly TextBlock caret = new() { FontSize = 11, Opacity = .65, Margin = new Thickness(12, 4, 12, 4) };
    readonly DispatcherTimer highlight = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly ListView suggestions = new() { MaxHeight = 260, MinWidth = 320, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    readonly Flyout completion = new();
    bool coloring; string lastText = "";
    public event Action? Changed;
    public List<Completion> Catalog { get; set; } = [];
    public RichEditBox Input => box;
    public int Caret => box.Document.Selection.StartPosition;
    public string Text
    {
        get { box.Document.GetText(TextGetOptions.None, out string value); return value.EndsWith('\r') ? value[..^1] : value; }
        set { box.Document.SetText(TextSetOptions.None, value.Replace("\r\n", "\r").Replace('\n', '\r')); lastText = ""; Schedule(); }
    }
    public string SelectedText { get { box.Document.Selection.GetText(TextGetOptions.None, out string text); return text; } }
    public SqlEditor()
    {
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(box); SetRow(caret, 1); Children.Add(caret);
        AutomationProperties.SetName(box, "SQL editor");
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.TextChanged += (_, _) => { if (!coloring) { Schedule(); Changed?.Invoke(); } };
        box.SelectionChanged += (_, _) => UpdateCaret();
        ActualThemeChanged += (_, _) => { lastText = ""; Schedule(); };
        highlight.Tick += (_, _) => { highlight.Stop(); Colorize(); UpdateCaret(); };
        completion.Content = suggestions;
        suggestions.ItemClick += (_, e) => Apply((Completion)e.ClickedItem);
        var completeKey = new KeyboardAccelerator { Key = VirtualKey.Space, Modifiers = VirtualKeyModifiers.Control };
        completeKey.Invoked += (_, e) => { e.Handled = true; Complete(); }; box.KeyboardAccelerators.Add(completeKey);
        var formatKey = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        formatKey.Invoked += (_, e) => { e.Handled = true; Format(); }; box.KeyboardAccelerators.Add(formatKey);
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Tab && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { box.Document.Selection.SetText(TextSetOptions.None, "    "); e.Handled = true; } };
        suggestions.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Tab && suggestions.SelectedItem is Completion item) { Apply(item); e.Handled = true; } };
        ToolTipService.SetToolTip(box, "Ctrl+Enter runs the current statement or selection · Ctrl+Space completes · Ctrl+Shift+F formats");
    }
    /// <summary>
    /// Drops the text box border and fill so a surrounding card can frame the editor.
    /// High contrast keeps the system chrome.
    /// </summary>
    public void Frameless()
    {
        if (BrandUi.HighContrast) return;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = clear;
        box.BorderThickness = new Thickness(0);
        box.Padding = new Thickness(16, 12, 16, 8);
        caret.Margin = new Thickness(16, 4, 16, 8);
    }
    void Schedule() { highlight.Stop(); highlight.Start(); }
    void UpdateCaret()
    {
        var text = Text; int at = Math.Clamp(Caret, 0, text.Length); var before = text[..at]; int line = 1 + before.Count(c => c == '\r'); int column = at - before.LastIndexOf('\r');
        caret.Text = $"Ln {line}, Col {column}     PostgreSQL     UTF-8     Ctrl+Space to complete";
    }
    void Colorize()
    {
        string sql = Text; if (sql == lastText || sql.Length > 250_000) return; lastText = sql;
        coloring = true;
        try
        {
            var baseColor = ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 35, 49, 68) : Windows.UI.Color.FromArgb(255, 221, 229, 241);
            box.Document.GetRange(0, sql.Length).CharacterFormat.ForegroundColor = baseColor;
            foreach (var token in SqlLanguage.Tokens(sql))
            {
                var rgb = token.Kind switch { SqlTokenKind.Keyword => ActualTheme == ElementTheme.Light ? (106, 60, 159) : (201, 163, 255), SqlTokenKind.String => ActualTheme == ElementTheme.Light ? (36, 111, 69) : (148, 209, 166), SqlTokenKind.Comment => ActualTheme == ElementTheme.Light ? (103, 116, 132) : (118, 142, 166), SqlTokenKind.Number => ActualTheme == ElementTheme.Light ? (153, 88, 27) : (237, 188, 121), SqlTokenKind.Identifier => ActualTheme == ElementTheme.Light ? (30, 108, 153) : (124, 200, 231), _ => (-1, -1, -1) };
                if (rgb.Item1 >= 0) box.Document.GetRange(token.Start, token.Start + token.Length).CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, (byte)rgb.Item1, (byte)rgb.Item2, (byte)rgb.Item3);
            }
        }
        finally { coloring = false; }
    }
    public void Format() { string selected = SelectedText; if (selected.Length > 0) box.Document.Selection.SetText(TextSetOptions.None, SqlLanguage.Format(selected)); else Text = SqlLanguage.Format(Text); }
    public void Complete()
    {
        string sql = Text; int end = Math.Clamp(Caret, 0, sql.Length), start = end;
        while (start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] is '_' or '.')) start--;
        suggestions.ItemsSource = SqlLanguage.Complete(sql[start..end], Catalog); suggestions.SelectedIndex = 0;
        if (suggestions.Items.Count > 0) completion.ShowAt(box, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }
    void Apply(Completion item)
    {
        string sql = Text; int end = Math.Clamp(Caret, 0, sql.Length), start = end;
        while (start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] is '_' or '.')) start--;
        var range = box.Document.GetRange(start, end); range.SetText(TextSetOptions.None, item.Insert); box.Document.Selection.SetRange(start + item.Insert.Length, start + item.Insert.Length); completion.Hide(); box.Focus(FocusState.Programmatic);
    }
    public void MarkError(int oneBasedPosition)
    {
        int position = Math.Clamp(oneBasedPosition - 1, 0, Text.Length); box.Document.Selection.SetRange(position, Math.Min(position + 1, Text.Length)); box.Focus(FocusState.Programmatic);
    }
}
