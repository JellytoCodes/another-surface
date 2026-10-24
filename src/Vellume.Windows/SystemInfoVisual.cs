using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vellume.Core;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using ShapePath = System.Windows.Shapes.Path;

namespace Vellume.Windows;

/// <summary>SysDash-inspired presentation shared by the desktop surface and settings preview.</summary>
public sealed class SystemInfoVisual : Border
{
    public const double NaturalWidth = 317;
    public const double DefaultHeight = 374;
    private const double ChartWidth = 285;
    private const double ChartHeight = 26;
    private const int HistoryLength = 60;
    private readonly Dictionary<string, MetricRow> _rows = [];
    private readonly Color _accent;

    public SystemInfoVisual(SystemInfoAppearance appearance)
    {
        var settings = appearance.Copy();
        _accent = (Color)ColorConverter.ConvertFromString(settings.AccentColor);
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(8);
        Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(settings.BackgroundOpacity * 255), 16, 19, 24));
        IsHitTestVisible = false;
        var rows = new StackPanel { Width = ChartWidth };
        var foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.TextColor));
        var detailBrush = new SolidColorBrush(foreground.Color) { Opacity = 0.65 };
        foreach (var (key, title, show, history) in new[]
        {
            ("cpu", "CPU", settings.ShowCpu, true),
            ("gpu", "GPU", settings.ShowGpu, true),
            ("memory", "RAM", settings.ShowMemory, true),
            ("driveC", "DISK C:", settings.ShowDriveC, false),
            ("driveD", "DISK D:", settings.ShowDriveD, false)
        })
        {
            if (!show) continue;
            var row = new Canvas { Width = ChartWidth, Height = history ? 74 : 60 };
            var heading = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = foreground };
            var value = new TextBlock { Text = "--", Width = ChartWidth, TextAlignment = TextAlignment.Right, FontSize = 14, Foreground = foreground };
            var detail = new TextBlock { Text = "측정 대기 중", Width = ChartWidth, FontSize = 10, Foreground = detailBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetTop(detail, 23);
            row.Children.Add(heading);
            row.Children.Add(value);
            row.Children.Add(detail);
            var chart = new Canvas { Width = ChartWidth, Height = ChartHeight, ClipToBounds = true };
            Canvas.SetTop(chart, 39);
            ShapePath? line = null;
            ShapePath? area = null;
            Border? bar = null;
            if (history)
            {
                area = new ShapePath { Fill = new LinearGradientBrush(
                    Color.FromArgb(90, _accent.R, _accent.G, _accent.B),
                    Color.FromArgb(0, _accent.R, _accent.G, _accent.B), 90) };
                line = new ShapePath { Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
                chart.Children.Add(area);
                chart.Children.Add(line);
            }
            else
            {
                chart.Children.Add(new Border { Width = ChartWidth, Height = 3, Background = new SolidColorBrush(foreground.Color) { Opacity = 0.15 } });
                bar = new Border { Width = 0, Height = 3, Background = new SolidColorBrush(_accent) };
                chart.Children.Add(bar);
            }
            row.Children.Add(chart);
            rows.Children.Add(row);
            _rows[key] = new MetricRow(value, detail, line, area, bar);
        }
        // A Viewbox makes the user scale part of the measured desktop bounds, so
        // the existing monitor-relative placement and drag logic stays accurate.
        Child = new Viewbox { Width = ChartWidth * settings.Scale, Stretch = Stretch.Uniform, Child = rows };
        Padding = new Thickness(16 * settings.Scale);
        Width = NaturalWidth * settings.Scale;
        System.Windows.Documents.TextElement.SetFontFamily(this, new FontFamily("Segoe UI"));
        UseLayoutRounding = true;
    }

    internal void Update(SystemMetricsSnapshot metrics)
    {
        SetMetric("cpu", metrics.CpuPercent, $"{Environment.ProcessorCount} logical processors");
        SetMetric("gpu", metrics.GpuPercent, metrics.GpuPercent is null ? "측정할 수 없음" : "GPU 엔진 사용량");
        SetMetric("memory", metrics.MemoryPercent, $"{FormatBytes(metrics.MemoryUsedBytes)} / {FormatBytes(metrics.MemoryTotalBytes)}");
        SetDrive("driveC", metrics.DriveC);
        SetDrive("driveD", metrics.DriveD);
    }

    private void SetDrive(string key, DriveMetricsSnapshot drive)
        => SetMetric(key, drive.IsAvailable ? drive.Percent : null, drive.IsAvailable
            ? $"{FormatBytes(drive.UsedBytes)} / {FormatBytes(drive.TotalBytes)} 사용 중" : "드라이브 없음");

    private void SetMetric(string key, double? percent, string detail)
    {
        if (!_rows.TryGetValue(key, out var row)) return;
        var value = percent is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : (double?)null;
        row.Value.Text = value is { } usage ? $"{usage:0}%" : "N/A";
        row.Detail.Text = detail;
        if (row.Bar is { } bar) bar.Width = ChartWidth * (value ?? 0) / 100;
        else
        {
            row.History.Enqueue(value);
            while (row.History.Count > HistoryLength) row.History.Dequeue();
            DrawHistory(row);
        }
    }

    private static void DrawHistory(MetricRow row)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var stroke = line.Open())
        using (var fill = area.Open())
        {
            var values = row.History.ToArray();
            var connected = false;
            var lastX = 0d;
            for (var index = 0; index < values.Length; index++)
            {
                var x = ChartWidth * (HistoryLength - values.Length + index) / (HistoryLength - 1);
                if (values[index] is not { } value)
                {
                    if (connected) fill.LineTo(new Point(lastX, ChartHeight), true, false);
                    connected = false;
                    continue;
                }
                var point = new Point(x, 1 + (ChartHeight - 2) * (1 - value / 100));
                if (!connected)
                {
                    stroke.BeginFigure(point, false, false);
                    fill.BeginFigure(new Point(x, ChartHeight), true, true);
                    fill.LineTo(point, true, false);
                    connected = true;
                }
                else { stroke.LineTo(point, true, false); fill.LineTo(point, true, false); }
                lastX = x;
            }
            if (connected) fill.LineTo(new Point(lastX, ChartHeight), true, false);
        }
        line.Freeze();
        area.Freeze();
        row.Line!.Data = line;
        row.Area!.Data = area;
    }

    public static FrameworkElement CreatePreview(SystemInfoAppearance appearance)
    {
        var visual = new SystemInfoVisual(appearance with { Scale = 1 });
        for (var index = 0; index < HistoryLength; index++)
            visual.Update(new SystemMetricsSnapshot(
                27 + 12 * Math.Sin(index * 0.31), 42 + 22 * Math.Sin(index * 0.17), 48 + 3 * Math.Sin(index * 0.13),
                16UL << 30, 32UL << 30,
                new DriveMetricsSnapshot("C", true, 65, 325UL << 30, 500UL << 30),
                new DriveMetricsSnapshot("D", true, 32, 320UL << 30, 1000UL << 30), TimeSpan.Zero));
        return visual;
    }

    private static string FormatBytes(ulong bytes)
        => (bytes / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";

    private sealed record MetricRow(TextBlock Value, TextBlock Detail, ShapePath? Line, ShapePath? Area, Border? Bar)
    {
        internal Queue<double?> History { get; } = new();
    }
}
