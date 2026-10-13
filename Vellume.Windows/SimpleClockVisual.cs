using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Vellume.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Vellume.Windows;

/// <summary>A transparent clock shared by the desktop renderer and the style gallery.</summary>
public sealed class SimpleClockVisual : Canvas
{
    public const double NaturalWidth = 320;
    public const double NaturalHeight = 200;

    private static readonly SolidColorBrush White = CreateBrush(255, 255, 255, 255);
    private static readonly SolidColorBrush SoftWhite = CreateBrush(190, 255, 255, 255);
    private static readonly SolidColorBrush FaintWhite = CreateBrush(82, 255, 255, 255);
    private readonly TextBlock? _primary;
    private readonly TextBlock? _secondary;
    private readonly TextBlock? _date;
    private readonly RotateTransform? _hourRotation;
    private readonly RotateTransform? _minuteRotation;
    private readonly RotateTransform? _secondRotation;
    private DateTime? _displayedTime;

    public SimpleClockVisual(ClockStyle style)
    {
        SelectedStyle = Enum.IsDefined(style) ? style : ClockStyle.DigitalClean;
        Width = NaturalWidth;
        Height = NaturalHeight;
        ClipToBounds = false;
        IsHitTestVisible = false;
        Effect = new DropShadowEffect
        {
            Color = Color.FromRgb(10, 14, 24),
            BlurRadius = 5,
            ShadowDepth = 1,
            Opacity = 0.3,
            RenderingBias = RenderingBias.Performance
        };

        switch (SelectedStyle)
        {
            case ClockStyle.AnalogMinimal:
            case ClockStyle.AnalogNumerals:
                CreateFace(SelectedStyle == ClockStyle.AnalogNumerals);
                _hourRotation = AddHand(47, 4, White);
                _minuteRotation = AddHand(66, 2.5, White);
                _secondRotation = AddHand(71, 1, SoftWhite);
                var center = new Ellipse { Width = 6, Height = 6, Fill = White };
                Place(center, 157, 97);
                break;
            case ClockStyle.DigitalClean:
                _primary = AddText(64, FontWeights.Light, 26, 57, 220, 82);
                _primary.TextAlignment = TextAlignment.Right;
                _secondary = AddText(34, FontWeights.Light, 244, 86, 68, 48);
                _date = AddText(12, FontWeights.Normal, 20, 139, 280, 24, SoftWhite);
                break;
            case ClockStyle.DigitalStacked:
                _primary = AddText(88, FontWeights.Bold, 70, -11, 180, 110);
                _secondary = AddText(88, FontWeights.Bold, 70, 72, 180, 110);
                _date = AddText(10, FontWeights.Normal, 20, 183, 280, 17, SoftWhite);
                break;
            case ClockStyle.DigitalMono:
                _primary = AddText(69, FontWeights.Normal, 5, 55, 310, 93);
                _primary.FontFamily = new FontFamily("Consolas");
                _date = AddText(11, FontWeights.Normal, 20, 139, 280, 22, SoftWhite);
                break;
        }

        Update(DateTime.Now);
    }

    public ClockStyle SelectedStyle { get; }

    public bool NeedsSeconds => SelectedStyle is ClockStyle.AnalogMinimal
        or ClockStyle.AnalogNumerals or ClockStyle.DigitalClean;

    /// <summary>Updates existing elements only when the displayed time can change.</summary>
    public bool Update(DateTime now)
    {
        var precision = NeedsSeconds ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerMinute;
        if (_displayedTime is { } previous && previous.Ticks / precision == now.Ticks / precision)
        {
            return false;
        }

        _displayedTime = now;
        if (_hourRotation is not null)
        {
            _hourRotation.Angle = (now.Hour % 12 + now.Minute / 60d + now.Second / 3600d) * 30;
            _minuteRotation!.Angle = (now.Minute + now.Second / 60d) * 6;
            _secondRotation!.Angle = now.Second * 6;
        }
        else
        {
            _primary!.Text = now.ToString(
                SelectedStyle == ClockStyle.DigitalStacked ? "HH" : "HH:mm",
                CultureInfo.InvariantCulture);
            if (_secondary is not null)
            {
                _secondary.Text = now.ToString(
                    SelectedStyle == ClockStyle.DigitalStacked ? "mm" : ":ss",
                    CultureInfo.InvariantCulture);
            }
            _date!.Text = now.ToString("yyyy.MM.dd  ddd", CultureInfo.CurrentCulture);
        }

        return true;
    }

    /// <summary>Creates a uniformly scaled preview of the same clock used on the desktop.</summary>
    public static Viewbox CreatePreview(
        ClockStyle style,
        double width = 240,
        double height = 150,
        DateTime? now = null)
    {
        var clock = new SimpleClockVisual(style);
        clock.Update(now ?? DateTime.Now);
        return new Viewbox
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Child = clock
        };
    }

    private void CreateFace(bool numerals)
    {
        const double centerX = 160;
        const double centerY = 100;
        if (numerals)
        {
            AddText(43, FontWeights.Bold, 126, 8, 68, 56, FaintWhite).Text = "12";
            AddText(43, FontWeights.Bold, 222, 69, 45, 58, FaintWhite).Text = "3";
            AddText(43, FontWeights.Bold, 137, 130, 46, 58, FaintWhite).Text = "6";
            AddText(43, FontWeights.Bold, 52, 69, 45, 58, FaintWhite).Text = "9";
            return;
        }

        for (var index = 0; index < 60; index++)
        {
            var angle = index * Math.PI / 30;
            var major = index % 5 == 0;
            var innerRadius = major ? 78 : 83;
            Children.Add(new Line
            {
                X1 = centerX + Math.Sin(angle) * innerRadius,
                Y1 = centerY - Math.Cos(angle) * innerRadius,
                X2 = centerX + Math.Sin(angle) * 87,
                Y2 = centerY - Math.Cos(angle) * 87,
                Stroke = major ? White : SoftWhite,
                StrokeThickness = major ? 1.6 : 1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
    }

    private RotateTransform AddHand(double length, double thickness, Brush brush)
    {
        var rotation = new RotateTransform(0, 160, 100);
        Children.Add(new Line
        {
            X1 = 160,
            Y1 = 106,
            X2 = 160,
            Y2 = 100 - length,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotation
        });
        return rotation;
    }

    private TextBlock AddText(
        double size,
        FontWeight weight,
        double x,
        double y,
        double width,
        double height,
        Brush? foreground = null)
    {
        var text = new TextBlock
        {
            Width = width,
            Height = height,
            FontSize = size,
            FontWeight = weight,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = foreground ?? White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
        Place(text, x, y);
        return text;
    }

    private void Place(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
        Children.Add(element);
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
