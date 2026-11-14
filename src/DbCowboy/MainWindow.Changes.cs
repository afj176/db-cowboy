using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DbCowboy;

public sealed partial class MainWindow
{
    async Task<bool> CanStage(bool requireKey)
    {
        if (operation != null || current == null) return false;
        if (current.Kind is "v" or "m") { await Notice("View is read-only", "Browse views here; use an explicit SQL statement if the database supports writing to this view."); return false; }
        if (readOnly.IsChecked == true) { await Notice("Read-only workspace", "Turn off Read-only before staging data changes. Changes are still reviewed before execution."); return false; }
        if (requireKey && keys.Count == 0) { await Notice("Primary key required", "Row editing and deletion require a declared primary key. You can still write an explicit SQL statement."); return false; }
        return true;
    }
    async void InsertRowClick(object sender, RoutedEventArgs e)
    {
        if (!await CanStage(false)) return;
        var fields = currentColumns.Where(c => !c.Generated).Select(c => new EntryField(c, null, true)).ToList();
        if (!await RowDialog("Insert into " + current!.Name, fields, "Stage insert")) return;
        var values = fields.Where(f => f.Mode.SelectedIndex != 0).ToDictionary(f => f.Column.Name, f => f.Mode.SelectedIndex == 1 ? null : f.Value.Text);
        pendingChanges.Add(new(ChangeKind.Insert, current!, values, [])); UpdateChanges();
    }
    async void EditRowClick(object sender, RoutedEventArgs e)
    {
        if (!await CanStage(true) || tableGrid.Selected is not DataRow row) return;
        var original = PendingChange.Snapshot(row);
        if (AlreadyStaged(original)) { await Notice("Already staged", "This row already has a pending edit or deletion. Apply or discard that batch first."); return; }
        var fields = currentColumns.Where(c => !c.Generated && !keys.Contains(c.Name)).Select(c => new EntryField(c, original.GetValueOrDefault(c.Name), false)).ToList();
        if (!await RowDialog("Edit row / " + current!.Name, fields, "Stage changes")) return;
        var values = fields.Where(f => f.Mode.SelectedIndex != 0).ToDictionary(f => f.Column.Name, f => f.Mode.SelectedIndex == 1 ? null : f.Value.Text);
        values = values.Where(v => v.Value != original.GetValueOrDefault(v.Key)).ToDictionary(v => v.Key, v => v.Value);
        if (values.Count == 0) { status.Text = "No changed values to stage."; return; }
        pendingChanges.Add(new(ChangeKind.Update, current!, values, original)); UpdateChanges();
    }
    async void DeleteRowsClick(object sender, RoutedEventArgs e)
    {
        if (!await CanStage(true)) return; int added = 0;
        foreach (var row in tableGrid.SelectedRows)
        {
            var original = PendingChange.Snapshot(row); if (AlreadyStaged(original)) continue;
            pendingChanges.Add(new(ChangeKind.Delete, current!, [], original)); added++;
        }
        UpdateChanges(); status.Text = $"{added} row deletions staged · nothing has been executed";
    }
    bool AlreadyStaged(Dictionary<string, string?> snapshot) => pendingChanges.Any(c => c.Kind != ChangeKind.Insert && c.Relation == current && keys.All(k => c.Original.GetValueOrDefault(k) == snapshot.GetValueOrDefault(k)));
    void UpdateChanges()
    {
        // Name only the kinds that are staged: "3 pending changes · 2 inserts · 1 delete".
        static string Count(int n, string noun) => n == 0 ? "" : $" · {n} {noun}{(n == 1 ? "" : "s")}";
        int inserts = pendingChanges.Count(c => c.Kind == ChangeKind.Insert), edits = pendingChanges.Count(c => c.Kind == ChangeKind.Update), deletes = pendingChanges.Count(c => c.Kind == ChangeKind.Delete);
        changeSummary.Text = pendingChanges.Count == 0 ? "No pending changes"
            : $"{pendingChanges.Count} pending change{(pendingChanges.Count == 1 ? "" : "s")}" + Count(inserts, "insert") + Count(edits, "edit") + Count(deletes, "delete") + " · nothing has been written yet";
        if (applyChangesButton != null) applyChangesButton.IsEnabled = pendingChanges.Count > 0;
        if (discardChangesButton != null) discardChangesButton.IsEnabled = pendingChanges.Count > 0;
        status.Text = pendingChanges.Count == 0 ? "Changes discarded" : "Changes staged locally · review and apply to commit";
        refreshTableChrome?.Invoke();
    }
    async void DiscardChangesClick(object sender, RoutedEventArgs e)
    {
        if (operation != null || pendingChanges.Count == 0) return;
        if (!await Confirm("Discard staged changes?", $"Discard {pendingChanges.Count} local changes? No database writes have been made.", "Discard")) return; pendingChanges.Clear(); UpdateChanges();
    }
    async void ApplyChangesClick(object sender, RoutedEventArgs e)
    {
        if (operation != null || pendingChanges.Count == 0) return;
        if (readOnly.IsChecked == true) { await Notice("Read-only workspace", "Disable Read-only before applying changes."); return; }
        var batch = pendingChanges.ToArray(); var description = string.Join("\n\n", batch.Select(c => c.Description));
        if (!await Confirm("Apply database changes?", $"{Connected().Profile.Name}{ProductionNote(Connected())}\n\n{description}\n\nAll changes run in one transaction. Any conflict or failure rolls back the batch.", $"Apply {batch.Length} {(batch.Length == 1 ? "change" : "changes")}")) return;
        await Work(async ct => { await Connected().CommitChanges(batch, ct); pendingChanges.Clear(); UpdateChanges(); await Browse(ct); status.Text = $"Committed {batch.Length} staged changes"; });
    }
    sealed class EntryField
    {
        public ColumnInfo Column { get; }
        public ComboBox Mode { get; }
        public TextBox Value { get; }
        public FrameworkElement View { get; }
        public EntryField(ColumnInfo column, string? original, bool insert)
        {
            Column = column;
            Mode = new ComboBox { ItemsSource = new[] { insert ? "Use default / omit" : "Keep original", "SQL NULL", "Set value" }, SelectedIndex = 0, Width = 170 };
            Value = new TextBox { Text = original ?? "", PlaceholderText = column.Type, IsEnabled = false, MinWidth = 230, TextWrapping = TextWrapping.Wrap };
            Mode.SelectionChanged += (_, _) => Value.IsEnabled = Mode.SelectedIndex == 2;
            // One card per column: its name, type and whether it may be NULL, then how to fill it.
            var name = Text(column.Name, 14); name.FontSize = 14; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.VerticalAlignment = VerticalAlignment.Center;
            var facts = Row(name, Pill(column.Type));
            if (!column.Nullable) facts.Children.Add(Pill("Required", Tone.Accent));
            if (column.Default != null) facts.Children.Add(Pill("Has a default"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Mode, column.Name + " · how to fill");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Value, column.Name + " · value");
            var input = new Grid { ColumnSpacing = 8 };
            input.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); input.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            input.Children.Add(Mode); Grid.SetColumn(Value, 1); input.Children.Add(Value);
            View = Surface(Stack(ScrollTools(facts), input), 12, 10);
        }
    }
    async Task<bool> RowDialog(string title, List<EntryField> fields, string action)
    {
        if (dialogOpen) return false; var panel = new StackPanel { Spacing = 10, MinWidth = 520, Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(Muted("This stages the change locally. Nothing is written until you review and apply. Values are cast and validated by PostgreSQL; omitted insert fields use their database defaults.", 13));
        foreach (var field in fields) panel.Children.Add(field.View);
        dialogOpen = true;
        try { return await StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new ScrollViewer { Content = panel, MaxHeight = 550 }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = root.RequestedTheme }).ShowAsync() == ContentDialogResult.Primary; } finally { dialogOpen = false; }
    }
}
