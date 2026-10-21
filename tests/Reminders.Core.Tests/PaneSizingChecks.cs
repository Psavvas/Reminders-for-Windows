using Reminders.Windows.Services;

internal static class PaneSizingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var balanced = PaneSizing.Calculate(1008, null);
        check(!balanced.Compact && balanced.List == 500 && balanced.Details == 500, "Automatic panes share usable width equally");
        var custom = PaneSizing.Calculate(1008, 0.65);
        check(custom.List == 350 && custom.Details == 650, "Custom detail share applies to usable width");
        var smaller = PaneSizing.Calculate(808, 0.65);
        check(smaller.List == 320 && smaller.Details == 480, "Shrinking the window preserves a readable list despite its saved custom share");
        check(PaneSizing.Calculate(1008, 0.65) == custom, "Custom share returns when window grows");
        var narrow = PaneSizing.Calculate(647, null);
        check(narrow.Compact && narrow.List == 647 && narrow.Details == 647, "Automatic sizing uses full-width single panes in narrow windows");
        var boundary = PaneSizing.Calculate(648, null);
        check(!boundary.Compact && boundary.List == 320 && boundary.Details == 320, "Automatic panes fit exactly at minimum width");
        foreach (var fraction in new double?[] { null, 0, 0.001, 0.05, 0.2, 0.8, 0.999, 1, -1, 2, double.NaN, double.PositiveInfinity })
        {
            foreach (var width in new[] { 648d, 649, 808, 1008, 2008 })
            {
                var sizes = PaneSizing.Calculate(width, fraction);
                check(!sizes.Compact && sizes.List >= 320 && sizes.Details >= 320 && Math.Abs(sizes.List + sizes.Details + 8 - width) < 0.001, "Every custom split fits and keeps both panes readable");
            }
            foreach (var width in new[] { 0d, 8, 300, 400, 647 })
            {
                var sizes = PaneSizing.Calculate(width, fraction);
                check(sizes.Compact && sizes.List == width && sizes.Details == width, "Narrow windows use a full-width single pane even with a custom split");
            }
        }
        var formerFullPreview = PaneSizing.Calculate(1008, 1);
        check(formerFullPreview.List == 320 && formerFullPreview.Details == 680, "An old fully expanded preview preference cannot hide the reminder list");
        var formerSliver = PaneSizing.Calculate(1008, 0.01);
        check(formerSliver.List == 680 && formerSliver.Details == 320, "An old sliver preference cannot squash the placeholder or editor");
        check(PaneSizing.Calculate(1008, double.NaN) == balanced, "Invalid saved share falls back to automatic sizing");
        check(PaneSizing.Calculate(647, double.NaN).Compact, "Invalid custom preference uses compact automatic sizing");
        check(PaneSizing.Calculate(double.NaN, null).Compact, "Invalid or unmeasured width stays compact");
        check(PaneSizing.Calculate(8, 0.5).Compact, "A window smaller than the divider cannot split");

        var minimumDetails = PaneSizing.Resize(1008, null, 1000);
        var minimumList = PaneSizing.Resize(1008, null, -1000);
        check(Math.Abs(minimumDetails!.Value - 0.32) < 0.000001, "Dragging past the right edge stops at the minimum editor width");
        check(Math.Abs(minimumList!.Value - 0.68) < 0.000001, "Dragging past the left edge stops at the minimum list width");
        check(Math.Abs(PaneSizing.Resize(1008, minimumDetails, -24)!.Value - 0.344) < 0.000001, "Dragging away from the minimum expands details immediately");
        check(Math.Abs(PaneSizing.Resize(1008, minimumList, 24)!.Value - 0.656) < 0.000001, "Dragging away from the maximum expands the list immediately");
        check(Math.Abs(PaneSizing.Resize(1008, 0.01, -24)!.Value - 0.344) < 0.000001, "An old tiny saved share resizes from its visible width without a dead zone");
        check(Math.Abs(PaneSizing.Resize(1008, 0.99, 24)!.Value - 0.656) < 0.000001, "An old expanded saved share resizes from its visible width without a dead zone");
        check(PaneSizing.Resize(1008, 0.5, double.NaN) == 0.5, "An invalid drag delta leaves the split unchanged");
        check(PaneSizing.Resize(647, null, 24) is null, "Resizing does not change a compact automatic layout");
        check(PaneSizing.Resize(400, 0.65, -24) == 0.65, "Compact resizing leaves the saved preference intact for a wider window");
    }
}
