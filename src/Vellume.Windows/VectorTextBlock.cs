using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace Vellume.Windows;

/// <summary>
/// Draws glyph outlines as WPF vector geometry. This avoids ClearType color
/// fringes when text is rotated inside the transparent desktop child window.
/// </summary>
internal sealed class VectorTextBlock : FrameworkElement
{
    private string _text = string.Empty;

    internal string Text
    {
        get => _text;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_text, next, StringComparison.Ordinal))
            {
                return;
            }

            _text = next;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    internal FontFamily FontFamily { get; set; } =
        new("Pretendard, Malgun Gothic, Segoe UI");

    internal double FontSize { get; set; } = 12;

    internal FontWeight FontWeight { get; set; } =
        FontWeights.Normal;

    internal Color FillColor { get; set; } = Colors.White;

    internal Color? OutlineColor { get; set; }

    internal double OutlineThickness { get; set; }

    internal TextAlignment TextAlignment { get; set; } =
        TextAlignment.Left;

    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = CreateFormattedText();
        var width = Math.Ceiling(
            formatted.WidthIncludingTrailingWhitespace +
            (OutlineThickness * 2));
        var height = Math.Ceiling(
            formatted.Height +
            (OutlineThickness * 2));
        if (!double.IsInfinity(availableSize.Width))
        {
            width = Math.Min(width, availableSize.Width);
        }

        if (!double.IsInfinity(availableSize.Height))
        {
            height = Math.Min(height, availableSize.Height);
        }

        return new Size(
            Math.Max(0, width),
            Math.Max(0, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text) ||
            RenderSize.Width <= 0 ||
            RenderSize.Height <= 0)
        {
            return;
        }

        var formatted = CreateFormattedText();
        var textWidth =
            formatted.WidthIncludingTrailingWhitespace;
        var x = TextAlignment switch
        {
            TextAlignment.Center =>
                Math.Max(0, (RenderSize.Width - textWidth) / 2),
            TextAlignment.Right =>
                Math.Max(0, RenderSize.Width - textWidth),
            _ => 0
        };
        var geometry = formatted.BuildGeometry(
            new Point(x, 0));
        var fill = new SolidColorBrush(FillColor);
        fill.Freeze();
        Pen? outline = null;
        if (OutlineColor is { } outlineColor &&
            OutlineThickness > 0)
        {
            var outlineBrush = new SolidColorBrush(
                outlineColor);
            outlineBrush.Freeze();
            outline = new Pen(
                outlineBrush,
                OutlineThickness)
            {
                LineJoin = PenLineJoin.Round
            };
            outline.Freeze();
        }

        drawingContext.DrawGeometry(
            fill,
            outline,
            geometry);
    }

    private FormattedText CreateFormattedText()
    {
        var pixelsPerDip =
            VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            WpfFlowDirection.LeftToRight,
            new Typeface(
                FontFamily,
                FontStyles.Normal,
                FontWeight,
                FontStretches.Normal),
            FontSize,
            Brushes.White,
            pixelsPerDip);
    }
}
