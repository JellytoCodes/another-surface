using System.Runtime.ExceptionServices;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WidgetResolutionScaleTests
{
    [Theory]
    [InlineData(1920, 1080, 1d)]
    [InlineData(2560, 1440, 1.3333333333333333)]
    [InlineData(3840, 2160, 2d)]
    [InlineData(3440, 1440, 1.3333333333333333)]
    [InlineData(2560, 1080, 1d)]
    [InlineData(1280, 720, 0.6666666666666666)]
    [InlineData(800, 600, 0.4166666666666667)]
    [InlineData(1080, 1920, 0.5625)]
    public void ResolutionScale_UniformlyFitsFullHdReference(
        int width,
        int height,
        double expected)
    {
        Assert.Equal(
            expected,
            NativeWidgetRendererWindow.CalculateResolutionScale(
                width,
                height),
            precision: 10);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(-1920, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1920, -1080)]
    public void ResolutionScale_RejectsNonPositiveBounds(
        int width,
        int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeWidgetRendererWindow.CalculateResolutionScale(
                width,
                height));
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void InformationWidgets_ResizeInPhysicalPixelsAndKeepAlphaFramesReady()
    {
        RunOnStaThread(() => RunWithOffscreenParent(parent =>
        {
            var renderer = new NativeWidgetRendererWindow();
            renderer.SetWindowVisible(false);
            try
            {
                renderer.ApplySettingsAsync(new WidgetSettings
                    {
                        IsEnabled = true,
                        ShowClock = true,
                        ShowSystemInfo = true,
                        ShowCodexCompanion = true,
                        ClockSide = WidgetSide.Right,
                        SystemInfoSide = WidgetSide.Right
                    })
                    .GetAwaiter().GetResult();
                var resolutions = new (int Width, int Height, double Scale)[]
                {
                    (1920, 1080, 1),
                    (2560, 1440, 4d / 3),
                    (3840, 2160, 2),
                    (3440, 1440, 4d / 3),
                    (1280, 720, 2d / 3),
                    (1080, 1920, 0.5625),
                    (1920, 1080, 1)
                };
                for (var index = 0; index < resolutions.Length; index++)
                {
                    var resolution = resolutions[index];
                    var display = CreateDisplay(
                        resolution.Width,
                        resolution.Height);
                    renderer.AttachTo(parent, display);
                    renderer.InitializeAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert.True(renderer.IsReady);
                    Assert.True(renderer.IsAttachedTo(parent));
                    Assert.Equal(3, renderer.PanelHandles.Count);
                    Assert.True(renderer.AllPanelsPresented);
                    Assert.All(renderer.PanelHandles, handle =>
                        Assert.False(NativeMethods.IsWindowVisible(handle)));

                    var bounds = renderer.PanelHandles
                        .Select(ReadPhysicalBounds).ToArray();
                    var tolerance = resolution.Scale == 1 ? 0d : 2d;
                    // Each widget now owns an independent bounded surface.
                    AssertScaled(bounds[0].Right - bounds[0].Left, 320, resolution.Scale, tolerance);
                    AssertScaled(bounds[0].Bottom - bounds[0].Top, 200, resolution.Scale, tolerance);
                    AssertScaled(display.X + display.Width - bounds[0].Right, 24, resolution.Scale, tolerance);
                    AssertScaled(bounds[0].Top - display.Y, 24, resolution.Scale, tolerance);
                    AssertScaled(bounds[1].Right - bounds[1].Left, 255, resolution.Scale, tolerance);
                    AssertScaled(bounds[1].Bottom - bounds[1].Top, 285, resolution.Scale, tolerance);
                    AssertScaled(display.X + display.Width - bounds[1].Right, 24, resolution.Scale, tolerance);
                    Assert.InRange(Math.Abs(bounds[1].Top - display.Y -
                        (display.Height - (bounds[1].Bottom - bounds[1].Top)) / 2d), 0, 1);
                    AssertScaled(bounds[2].Right - bounds[2].Left, 520, resolution.Scale, tolerance);
                    AssertScaled(bounds[2].Bottom - bounds[2].Top, 420, resolution.Scale, tolerance);
                    AssertScaled(bounds[2].Left - display.X, 24, resolution.Scale, tolerance);
                    AssertScaled(bounds[2].Top - display.Y, 24, resolution.Scale, tolerance);
                    foreach (var panel in bounds) AssertInsideDisplay(panel, display);
                    AssertTransparentFrames(renderer, bounds);

                    var generations = renderer.PanelPresentationGenerations
                        .ToArray();
                    renderer.UpdateSystemMetricsAsync(new SystemMetricsSnapshot(
                            20 + index,
                            35,
                            50,
                            8UL * 1024 * 1024 * 1024,
                            16UL * 1024 * 1024 * 1024,
                            DriveMetricsSnapshot.Unavailable("C"),
                            DriveMetricsSnapshot.Unavailable("D"),
                            TimeSpan.FromHours(1)))
                        .GetAwaiter().GetResult();
                    renderer.UpdateCodexAsync(
                            CodexCompanionSnapshot.Disconnected() with
                            {
                                Headline = $"해상도 검증 {index}"
                            })
                        .GetAwaiter().GetResult();
                    // Updated information panels must retain their HWND and bounds;
                    // the clock can reuse its frame within the same displayed second.
                    Assert.True(renderer.AllPanelsPresented);
                    Assert.All(renderer.PanelPresentationGenerations.Zip(
                        generations,
                        (current, previous) => current >= previous), Assert.True);
                    Assert.True(renderer.PanelPresentationGenerations[1] > generations[1]);
                    Assert.True(renderer.PanelPresentationGenerations[2] > generations[2]);
                    Assert.Equal(bounds, renderer.PanelHandles
                        .Select(ReadPhysicalBounds).ToArray());
                    AssertTransparentFrames(renderer, bounds);

                    renderer.SetWindowVisible(true);
                    Assert.All(renderer.PanelHandles, handle =>
                        Assert.True(NativeMethods.IsWindowVisible(handle)));
                    renderer.SetWindowVisible(false);
                }
            }
            finally
            {
                renderer.CloseSafelyAsync().GetAwaiter().GetResult();
            }
        }));
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void SystemWidget_WithoutClockRemainsCenteredAtEveryResolution()
    {
        RunOnStaThread(() => RunWithOffscreenParent(parent =>
        {
            var renderer = new NativeWidgetRendererWindow();
            renderer.SetWindowVisible(false);
            try
            {
                renderer.ApplySettingsAsync(new WidgetSettings
                    {
                        IsEnabled = true,
                        ShowClock = false,
                        ShowSystemInfo = true,
                        ShowCodexCompanion = false,
                        SystemInfoSide = WidgetSide.Right
                    })
                    .GetAwaiter().GetResult();
                foreach (var (width, height, scale) in new[]
                         {
                             (1920, 1080, 1d),
                             (1280, 720, 2d / 3),
                             (3840, 2160, 2d)
                         })
                {
                    var display = CreateDisplay(width, height);
                    renderer.AttachTo(parent, display);
                    renderer.InitializeAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var panel = ReadPhysicalBounds(
                        Assert.Single(renderer.PanelHandles));
                    var tolerance = scale == 1 ? 0d : 2d;
                    Assert.InRange(Math.Abs(panel.Top - display.Y -
                        (display.Height - (panel.Bottom - panel.Top)) / 2d), 0, 1);
                    AssertScaled(display.X + display.Width - panel.Right,
                        24, scale, tolerance);
                    AssertScaled(panel.Right - panel.Left, 255, scale, tolerance);
                    AssertScaled(panel.Bottom - panel.Top, 285, scale, tolerance);
                    AssertInsideDisplay(panel, display);
                    AssertTransparentFrames(renderer, [panel]);
                }
            }
            finally
            {
                renderer.CloseSafelyAsync().GetAwaiter().GetResult();
            }
        }));
    }

    private static void AssertScaled(
        int actual,
        double reference,
        double scale,
        double tolerance)
        => Assert.InRange(Math.Abs(actual - reference * scale), 0, tolerance);

    private static void AssertInsideDisplay(
        NativeMethods.Rect panel,
        DisplayInfo display)
    {
        Assert.InRange(panel.Left, display.X, display.X + display.Width - 1);
        Assert.InRange(panel.Top, display.Y, display.Y + display.Height - 1);
        Assert.InRange(panel.Right, panel.Left + 1, display.X + display.Width);
        Assert.InRange(panel.Bottom, panel.Top + 1, display.Y + display.Height);
    }

    private static void AssertTransparentFrames(
        NativeWidgetRendererWindow renderer,
        IReadOnlyList<NativeMethods.Rect> bounds)
    {
        Assert.True(renderer.AllPanelsPresented);
        for (var index = 0; index < bounds.Count; index++)
        {
            var panel = bounds[index];
            var pixelCount = (panel.Right - panel.Left) *
                             (panel.Bottom - panel.Top);
            Assert.InRange(renderer.PanelNonTransparentPixelCounts[index],
                1, pixelCount - 1);
        }
    }

    private static NativeMethods.Rect ReadPhysicalBounds(nint handle)
    {
        Assert.True(NativeMethods.GetWindowRect(handle, out var bounds));
        return bounds;
    }

    private static DisplayInfo CreateDisplay(int width, int height)
        => new()
        {
            Id = "WIDGET-RESOLUTION-TEST",
            Name = "Offscreen physical pixels",
            X = -32000,
            Y = -32000,
            Width = width,
            Height = height,
            IsPrimary = true
        };

    private static void RunWithOffscreenParent(Action<nint> action)
    {
        // Match the application's PerMonitorV2 contract: HWND bounds must
        // be measured in physical pixels, without caller DPI virtualization.
        var originalDpi = NativeMethods.SetThreadDpiAwarenessContext(new nint(-4));
        Assert.NotEqual(nint.Zero, originalDpi);
        nint parent = nint.Zero;
        try
        {
            parent = NativeMethods.CreateWindowExW(
                0,
                "Static",
                "Vellume Offscreen Widget Resolution Test",
                unchecked((uint)NativeMethods.WsPopup),
                -32000,
                -32000,
                3840,
                2160,
                nint.Zero,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, parent);
            action(parent);
        }
        finally
        {
            if (parent != nint.Zero)
            {
                NativeMethods.DestroyWindow(parent);
            }
            NativeMethods.SetThreadDpiAwarenessContext(originalDpi);
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
