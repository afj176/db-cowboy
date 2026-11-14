using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

/// <summary>
/// An EXPLAIN plan as a tree of nodes beside the detail of the selected node. The plan comes from
/// EXPLAIN (FORMAT JSON) without ANALYZE, so every number is a planner estimate.
/// </summary>
public sealed class QueryPlanView : Grid
{
    readonly TreeView tree = new();
    readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0) };
    readonly TextBlock detailTitle = new() { Text = "Whole plan", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("ms-appx:///Assets/Manrope.ttf#Manrope") };
    string wholePlan = "";

    sealed record PlanLabel(PlanNode Plan, double RootCost)
    {
        public override string ToString() =>
            $"{Plan.Label}{(Plan.Relation == null ? "" : " / " + Plan.Relation)}\n{Plan.Rows:N0} estimated rows · {Plan.Cost:N2} cost · {(RootCost > 0 ? Plan.Cost / RootCost : 0):P0} of root inclusive cost";
    }

    public QueryPlanView()
    {
        Padding = new Thickness(12, 4, 12, 12);
        ColumnSpacing = 12;
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tree, "Plan nodes");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(detail, "Plan node detail");

        var note = BrandUi.MutedText("Estimates, not timings. Costs are planner units and a parent's cost includes its children. EXPLAIN does not run the query.", 12);
        var treeLayout = new Grid { RowSpacing = 6 };
        treeLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        treeLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        treeLayout.Children.Add(note);
        SetRow(tree, 1); treeLayout.Children.Add(tree);
        Children.Add(treeLayout);

        var showAll = new Button { Content = "Show whole plan", Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(showAll, "Show the detail of every node again");
        showAll.Click += (_, _) => { detailTitle.Text = "Whole plan"; detail.Text = wholePlan; };
        var detailHeader = new Grid { Margin = new Thickness(14, 10, 10, 6) };
        detailTitle.VerticalAlignment = VerticalAlignment.Center;
        detailHeader.Children.Add(detailTitle); detailHeader.Children.Add(showAll);
        var detailLayout = new Grid();
        detailLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        detailLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        detailLayout.Children.Add(detailHeader);
        SetRow(detail, 1); detailLayout.Children.Add(detail);
        var detailCard = BrandUi.Card(detailLayout, 0, 10);
        // The detail sits one step back from the results card it lives in.
        BrandUi.Themed(detailCard, (card, dark) => { if (!BrandUi.HighContrast) card.Background = BrandUi.HeaderBrush(dark); });
        SetColumn(detailCard, 1); Children.Add(detailCard);

        tree.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItem is not TreeViewNode { Content: PlanLabel label }) return;
            detailTitle.Text = label.Plan.Label + (label.Plan.Relation == null ? "" : " / " + label.Plan.Relation);
            detail.Text = label.Plan.Detail;
        };
    }

    public void Show(string json)
    {
        var plan = QueryPlans.Parse(json);
        tree.RootNodes.Clear();
        TreeViewNode Node(PlanNode node)
        {
            var item = new TreeViewNode { Content = new PlanLabel(node, plan.Cost), IsExpanded = true };
            foreach (var child in node.Children) item.Children.Add(Node(child));
            return item;
        }
        tree.RootNodes.Add(Node(plan));
        wholePlan = plan.Detail; detailTitle.Text = "Whole plan"; detail.Text = wholePlan;
    }
}
