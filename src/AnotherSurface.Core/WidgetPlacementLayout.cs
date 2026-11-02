namespace AnotherSurface.Core;

public readonly record struct WidgetPosition(double X, double Y);

public static class WidgetPlacementLayout
{
    // All dimensions and the returned top-left position use the same unit.
    // Coordinates are local to the selected display, not the virtual desktop.
    public static WidgetPosition Resolve(
        WidgetPlacement placement,
        double displayWidth,
        double displayHeight,
        double widgetWidth,
        double widgetHeight,
        double margin = 0)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var normalized = placement.Copy();
        var (x, y) = normalized.Anchor switch
        {
            WidgetAnchor.TopLeft => (0d, 0d),
            WidgetAnchor.TopCenter => (0.5, 0d),
            WidgetAnchor.TopRight => (1d, 0d),
            WidgetAnchor.CenterLeft => (0d, 0.5),
            WidgetAnchor.Center => (0.5, 0.5),
            WidgetAnchor.CenterRight => (1d, 0.5),
            WidgetAnchor.BottomLeft => (0d, 1d),
            WidgetAnchor.BottomCenter => (0.5, 1d),
            WidgetAnchor.BottomRight => (1d, 1d),
            _ => (normalized.X, normalized.Y)
        };
        var horizontal = GetAxis(displayWidth, widgetWidth, margin);
        var vertical = GetAxis(displayHeight, widgetHeight, margin);
        return new WidgetPosition(
            horizontal.Margin + horizontal.Travel * x,
            vertical.Margin + vertical.Travel * y);
    }

    public static WidgetPlacement FromPosition(
        double x,
        double y,
        double displayWidth,
        double displayHeight,
        double widgetWidth,
        double widgetHeight,
        double margin = 0,
        bool isLocked = true,
        string? displayId = null)
    {
        var horizontal = GetAxis(displayWidth, widgetWidth, margin);
        var vertical = GetAxis(displayHeight, widgetHeight, margin);
        return new WidgetPlacement
        {
            Anchor = WidgetAnchor.Custom,
            IsLocked = isLocked,
            X = NormalizePosition(x, horizontal),
            Y = NormalizePosition(y, vertical),
            DisplayId = displayId
        }.Copy();
    }

    private static (double Margin, double Travel) GetAxis(
        double displaySize,
        double widgetSize,
        double margin)
    {
        var available = Math.Max(0, Nonnegative(displaySize) - Nonnegative(widgetSize));
        var inset = Math.Min(Nonnegative(margin), available / 2);
        return (inset, available - inset * 2);
    }

    private static double NormalizePosition(
        double position,
        (double Margin, double Travel) axis)
        => axis.Travel > 0
            ? WidgetPlacement.NormalizeCoordinate((position - axis.Margin) / axis.Travel)
            : 0.5;

    private static double Nonnegative(double value)
        => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
