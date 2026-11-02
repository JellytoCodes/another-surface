using System.Runtime.ExceptionServices;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

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
                    Assert.Equal(2, renderer.PanelHandles.Count);
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
                    AssertScaled(bounds[1].Right - bounds[1].Left, SystemInfoVisual.NaturalWidth, resolution.Scale, tolerance);
                    AssertScaled(bounds[1].Bottom - bounds[1].Top, SystemInfoVisual.DefaultHeight, resolution.Scale, tolerance);
                    AssertScaled(display.X + display.Width - bounds[1].Right, 24, resolution.Scale, tolerance);
                    Assert.InRange(Math.Abs(bounds[1].Top - display.Y -
                        (display.Height - (bounds[1].Bottom - bounds[1].Top)) / 2d), 0, 1);
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
                    // Updated information panels must retain their HWND and bounds;
                    // the clock can reuse its frame within the same displayed second.
                    Assert.True(renderer.AllPanelsPresented);
                    Assert.All(renderer.PanelPresentationGenerations.Zip(
                        generations,
                        (current, previous) => current >= previous), Assert.True);
                    Assert.True(renderer.PanelPresentationGenerations[1] > generations[1]);
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
                    AssertScaled(panel.Right - panel.Left, SystemInfoVisual.NaturalWidth, scale, tolerance);
                    AssertScaled(panel.Bottom - panel.Top, SystemInfoVisual.DefaultHeight, scale, tolerance);
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

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void SystemWidget_AppearanceChangesResizeSurfaceAndAllHiddenLeavesNoWindow()
        => RunOnStaThread(() => RunWithOffscreenParent(parent =>
        {
            var renderer = new NativeWidgetRendererWindow();
            renderer.SetWindowVisible(false);
            try
            {
                var settings = new WidgetSettings { ShowClock = false, };
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                var display = CreateDisplay(1920, 1080);
                renderer.AttachTo(parent, display);
                Assert.True(renderer.InitializeAsync(CancellationToken.None).IsCompletedSuccessfully);
                Assert.True(renderer.IsReady);
                settings.SystemInfoAppearance = new SystemInfoAppearance { ShowGpu = false, ShowDriveD = false, Scale = 1.5, BackgroundOpacity = 0 };
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                var bounds = ReadPhysicalBounds(Assert.Single(renderer.PanelHandles));
                Assert.InRange(bounds.Right - bounds.Left, 475, 476);
                Assert.Equal(360, bounds.Bottom - bounds.Top);
                AssertInsideDisplay(bounds, display);
                AssertTransparentFrames(renderer, [bounds]);
                settings.SystemInfoAppearance.ShowCpu = false;
                settings.SystemInfoAppearance.ShowMemory = false;
                settings.SystemInfoAppearance.ShowDriveC = false;
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                Assert.True(renderer.IsReady);
                Assert.Empty(renderer.PanelHandles);
            }
            finally { Assert.True(renderer.CloseSafelyAsync().IsCompletedSuccessfully); }
        }));

    [Theory]
    [InlineData(WidgetKind.Clock, 1920, 1080)]
    [InlineData(WidgetKind.SystemInfo, 1920, 1080)]
    [InlineData(WidgetKind.Clock, 3840, 2160)]
    [InlineData(WidgetKind.SystemInfo, 3840, 2160)]
    [Trait("Category", "LocalIntegration")]
    public void FreeResize_CommitsContinuousScaleOnRelease_AndLockCanBeReversed(WidgetKind kind, int width, int height)
        => RunOnStaThread(() => RunWithOffscreenParent(parent =>
        {
            var renderer = new NativeWidgetRendererWindow();
            try
            {
                var settings = new WidgetSettings { ShowClock = kind == WidgetKind.Clock,
                    ShowSystemInfo = kind == WidgetKind.SystemInfo, };
                settings.SetPlacement(kind, new WidgetPlacement { Anchor = WidgetAnchor.Custom, X = 0.2, Y = 0.25, IsLocked = false });
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                var display = CreateDisplay(width, height);
                renderer.AttachTo(parent, display);
                Assert.True(renderer.InitializeAsync(CancellationToken.None).IsCompletedSuccessfully);
                var handle = Assert.Single(renderer.PanelHandles);
                var editor = Assert.Single(renderer.EditingHandles);
                Assert.True(NativeMethods.IsWindow(editor));
                var original = ReadPhysicalBounds(handle);
                var changes = new List<WidgetPlacementChangedEventArgs>();
                renderer.PlacementChanged += (_, args) => changes.Add(args);
                renderer.ResizeWidget(kind, 1.137, false);
                Assert.Empty(changes);
                Assert.Equal(handle, Assert.Single(renderer.PanelHandles));
                renderer.ResizeWidget(kind, 1.313, true);
                var resized = ReadPhysicalBounds(handle);
                Assert.InRange(Math.Abs((resized.Right - resized.Left) - (original.Right - original.Left) * 1.313), 0, 2);
                Assert.InRange(Math.Abs((resized.Bottom - resized.Top) - (original.Bottom - original.Top) * 1.313), 0, 2);
                Assert.Equal(original.Left, resized.Left);
                Assert.Equal(original.Top, resized.Top);
                Assert.Equal(1.313, Assert.Single(changes).Scale);
                Assert.False(changes[0].IsLockChange);
                renderer.SetWidgetLocked(kind, true);
                Assert.False(NativeMethods.IsWindow(editor));
                Assert.Empty(renderer.EditingHandles);
                Assert.True(changes[^1].IsLockChange);
                Assert.True(changes[^1].Placement.IsLocked);
                var count = changes.Count;
                renderer.ResizeWidget(kind, 1.9, true);
                renderer.MoveWidget(kind, display.X, display.Y, true);
                Assert.Equal(count, changes.Count);
                renderer.SetWidgetLocked(kind, false);
                Assert.True(changes[^1].IsLockChange);
                Assert.False(changes[^1].Placement.IsLocked);
                Assert.True(NativeMethods.IsWindow(Assert.Single(renderer.EditingHandles)));
                renderer.MoveWidget(kind, resized.Left + 20, resized.Top + 20, true);
                var moved = ReadPhysicalBounds(Assert.Single(renderer.PanelHandles));
                Assert.Equal(resized.Right - resized.Left, moved.Right - moved.Left);
                Assert.Equal(resized.Bottom - resized.Top, moved.Bottom - moved.Top);
                Assert.True(renderer.AllPanelsPresented);
            }
            finally { Assert.True(renderer.CloseSafelyAsync().IsCompletedSuccessfully); }
        }));

    [Theory]
    [InlineData(WidgetKind.Clock)]
    [InlineData(WidgetKind.SystemInfo)]
    [Trait("Category", "LocalIntegration")]
    public void HideWidget_PreservesDraggedPlacementAndScale_AndCanBeRestored(WidgetKind kind)
        => RunOnStaThread(() => RunWithOffscreenParent(parent =>
        {
            var renderer = new NativeWidgetRendererWindow();
            renderer.SetWindowVisible(false);
            try
            {
                var settings = new WidgetSettings { ShowClock = kind == WidgetKind.Clock,
                    ShowSystemInfo = kind == WidgetKind.SystemInfo, };
                settings.SetScale(kind, 1.137);
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                var display = CreateDisplay(1920, 1080);
                renderer.AttachTo(parent, display);
                Assert.True(renderer.InitializeAsync(CancellationToken.None).IsCompletedSuccessfully);
                WidgetPlacementChangedEventArgs? hidden = null;
                renderer.PlacementChanged += (_, args) => { if (args.IsVisible == false) hidden = args; };
                renderer.HideWidget(kind); // Locked widgets cannot be hidden by this gesture.
                Assert.Null(hidden);
                Assert.Single(renderer.PanelHandles);
                renderer.SetWidgetLocked(kind, false);
                renderer.MoveWidget(kind, display.X + 120, display.Y + 140, false);
                renderer.HideWidget(kind);
                Assert.NotNull(hidden);
                Assert.Equal(1.137, hidden!.Scale);
                Assert.Equal(WidgetAnchor.Custom, hidden.Placement.Anchor);
                Assert.Empty(renderer.PanelHandles);
                settings.SetVisible(kind, false);
                settings.SetPlacement(kind, hidden.Placement);
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                Assert.Empty(renderer.PanelHandles);
                settings.SetVisible(kind, true);
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                var bounds = ReadPhysicalBounds(Assert.Single(renderer.PanelHandles));
                Assert.InRange(bounds.Left - display.X, 119, 121);
                Assert.InRange(bounds.Top - display.Y, 139, 141);
            }
            finally { Assert.True(renderer.CloseSafelyAsync().IsCompletedSuccessfully); }
        }));

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
                "Another Surface Offscreen Widget Resolution Test",
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
