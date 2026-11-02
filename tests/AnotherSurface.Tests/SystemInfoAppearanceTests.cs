using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class SystemInfoAppearanceTests
{
    [Theory]
    [InlineData(-4, -1, 0.5, 0)]
    [InlineData(4, 2, 2, 1)]
    [InlineData(double.NaN, double.PositiveInfinity, 1, 0.75)]
    public void Copy_NormalizesAppearanceWithoutChangingOriginal(double scale, double opacity, double expectedScale, double expectedOpacity)
    {
        var original = new WidgetSettings
        {
            SystemInfoAppearance = new SystemInfoAppearance
            {
                ShowCpu = false, ShowGpu = false, ShowMemory = false, ShowDriveC = false, ShowDriveD = false,
                AccentColor = " ff8a65 ", TextColor = "invalid", Scale = scale, BackgroundOpacity = opacity
            }
        };
        var copy = original.Copy().SystemInfoAppearance;
        Assert.False(copy.HasVisibleItems);
        Assert.Equal("#FF8A65", copy.AccentColor);
        Assert.Equal("#FFFFFF", copy.TextColor);
        Assert.Equal(expectedScale, copy.Scale);
        Assert.Equal(expectedOpacity, copy.BackgroundOpacity);
        copy.ShowCpu = true;
        Assert.False(original.SystemInfoAppearance.ShowCpu);
        Assert.Equal(" ff8a65 ", original.SystemInfoAppearance.AccentColor);
    }

    [Fact]
    public async Task Store_LoadsLegacyDefaultsAndRoundTripsCustomAppearance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AnotherSurfaceSysDashTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"SchemaVersion":18,"Widgets":{"ShowSystemInfo":true}}""");
            var store = new JsonSettingsStore(path);
            var settings = await store.LoadAsync();
            Assert.Equal(new SystemInfoAppearance(), settings.Widgets.SystemInfoAppearance);
            var custom = new SystemInfoAppearance
            {
                ShowGpu = false, ShowDriveD = false, AccentColor = "#FF8A65", TextColor = "#DDEEFF", Scale = 1.25, BackgroundOpacity = 0
            };
            settings.Widgets.SystemInfoAppearance = custom;
            await store.SaveAsync(settings);
            Assert.Equal(custom, (await store.LoadAsync()).Widgets.SystemInfoAppearance);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Visual_SelectedRowsScaleAndColorsMatchTheDesktopBounds()
        => RunOnStaThread(() =>
        {
            var appearance = new SystemInfoAppearance { ShowGpu = false, ShowDriveD = false, Scale = 1.5, BackgroundOpacity = 0, AccentColor = "#FF8A65", TextColor = "#DDEEFF" };
            var visual = new SystemInfoVisual(appearance);
            visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.InRange(Math.Abs(317 * 1.5 - visual.DesiredSize.Width), 0, 1);
            // WPF layout rounding can move either dimension by a fraction of
            // a DIP at the test host's display scale.
            Assert.InRange(Math.Abs((74 * 2 + 60 + 32) * 1.5 - visual.DesiredSize.Height), 0, 1);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(visual.Background).Color.A);
            var rows = Assert.IsType<StackPanel>(Assert.IsType<Viewbox>(visual.Child).Child);
            Assert.Equal(3, rows.Children.Count);
            Assert.All(rows.Children.Cast<Canvas>().SelectMany(row => row.Children.OfType<TextBlock>()).Where(text => text.FontSize == 14),
                text => Assert.Equal(Color.FromRgb(0xDD, 0xEE, 0xFF), Assert.IsType<SolidColorBrush>(text.Foreground).Color));
            var disk = rows.Children.Cast<Canvas>().Last();
            var chart = Assert.Single(disk.Children.OfType<Canvas>());
            Assert.Equal(Color.FromRgb(0xFF, 0x8A, 0x65), Assert.IsType<SolidColorBrush>(chart.Children.OfType<Border>().Last().Background).Color);
        });

    [Fact]
    public void History_KeepsSixtySamplesAndDoesNotShowUnavailableGpuAsZero()
        => RunOnStaThread(() =>
        {
            var visual = new SystemInfoVisual(new SystemInfoAppearance());
            var rows = Assert.IsType<StackPanel>(Assert.IsType<Viewbox>(visual.Child).Child);
            var cpu = Assert.IsType<Canvas>(rows.Children[0]);
            var gpu = Assert.IsType<Canvas>(rows.Children[1]);
            var drive = Assert.IsType<Canvas>(rows.Children[4]);
            var metrics = new SystemMetricsSnapshot(10, 50, 40, 4UL << 30, 10UL << 30,
                DriveMetricsSnapshot.Unavailable("C"), DriveMetricsSnapshot.Unavailable("D"), TimeSpan.Zero);
            for (var index = 0; index < 75; index++)
                visual.Update(metrics with { CpuPercent = index % 2 == 0 ? 10 : 90, GpuPercent = index > 10 ? null : 50 });
            var cpuLine = Assert.Single(cpu.Children.OfType<Canvas>()).Children.OfType<System.Windows.Shapes.Path>().Last();
            Assert.InRange(cpuLine.Data.Bounds.Left, 0, 0.001);
            Assert.InRange(cpuLine.Data.Bounds.Width, 284.999, 285.001);
            Assert.InRange(cpuLine.Data.Bounds.Height, 19, 20);
            var gpuLine = Assert.Single(gpu.Children.OfType<Canvas>()).Children.OfType<System.Windows.Shapes.Path>().Last();
            Assert.True(gpuLine.Data.IsEmpty());
            Assert.Contains(gpu.Children.OfType<TextBlock>(), text => text.Text == "N/A");
            Assert.Contains(drive.Children.OfType<TextBlock>(), text => text.Text == "드라이브 없음");
            Assert.Equal(0, Assert.Single(drive.Children.OfType<Canvas>()).Children.OfType<Border>().Last().Width);
        });

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
