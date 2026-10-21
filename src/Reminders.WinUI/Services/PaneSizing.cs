namespace Reminders.Windows.Services;

internal readonly record struct PaneWidths(bool Compact, double List, double Details);

internal static class PaneSizing
{
    public const double MinimumWidth = 320;
    public const double DividerWidth = 8;

    public static PaneWidths Calculate(double width, double? detailFraction, bool detailSelected = false)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        var custom = detailFraction is { } value && double.IsFinite(value);
        if (width < MinimumWidth * 2 + DividerWidth) return new(true, width, width);
        var available = width - DividerWidth;
        var fraction = custom ? Math.Clamp(detailFraction!.Value, 0, 1) : 0.5;
        // Either endpoint uses list -> full-width task navigation. Keep an edge
        // divider on the list so dragging inward can restore a readable split.
        if (fraction is 0 or 1)
            return detailSelected ? new(true, width, width) : new(false, available, 0);
        // Intermediate shares stay readable rather than creating a sliver.
        var details = Math.Clamp(available * fraction, MinimumWidth, available - MinimumWidth);
        return new(false, available - details, details);
    }

    public static double? Resize(double width, double? detailFraction, double delta)
    {
        var sizes = Calculate(width, detailFraction);
        if (sizes.Compact || !double.IsFinite(delta) || delta == 0) return detailFraction;
        var available = sizes.List + sizes.Details;
        var requested = sizes.Details - delta;
        if (requested <= 0) return sizes.Details == 0 ? Math.Clamp(detailFraction ?? 0, 0, 1) : 0;
        if (requested >= available) return 1;
        var minimumFraction = MinimumWidth / available;
        return Math.Clamp(requested / available, minimumFraction, 1 - minimumFraction);
    }
}
