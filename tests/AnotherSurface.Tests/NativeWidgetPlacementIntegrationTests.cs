using System.Runtime.ExceptionServices;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class NativeWidgetPlacementIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void Drag_MovesCachedPixels_PersistsOnlyOnRelease_AndLockRemovesInputWindow()
    {
        RunOnSta(VerifyDragAndLock);
    }

    private static void VerifyDragAndLock()
    {
            var parent = NativeMethods.CreateWindowExW(0, "Static", "Another Surface Placement Test",
                unchecked((uint)NativeMethods.WsPopup), -32000, -32000, 1920, 1080,
                nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            Assert.NotEqual(nint.Zero, parent);
            var renderer = new NativeWidgetRendererWindow();
            var display = new DisplayInfo
            {
                Id = "synthetic-placement", Name = "Test", IsPrimary = true,
                X = -32000, Y = -32000, Width = 1920, Height = 1080
            };
            try
            {
                renderer.AttachTo(parent, display);
                renderer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                var settings = new WidgetSettings
                {
                    ShowClock = true, ShowSystemInfo = false, ClockStyle = ClockStyle.DigitalStacked,
                    ClockPlacement = WidgetPlacement.FromAnchor(WidgetAnchor.Center, false)
                };
                renderer.ApplySettingsAsync(settings).GetAwaiter().GetResult();
                var widget = Assert.Single(renderer.PanelHandles);
                var editor = Assert.Single(renderer.EditingHandles);
                Assert.NotEqual(nint.Zero, editor);
                var editorStyle = NativeMethods.GetWindowLongPtr(editor, NativeMethods.GwlExtendedStyle).ToInt64();
                Assert.NotEqual(0, editorStyle & NativeMethods.WsExNoActivate);
                Assert.Equal(0, editorStyle & NativeMethods.WsExTransparent);
                var pixelCount = Assert.Single(renderer.PanelNonTransparentPixelCounts);
                var frame = Assert.Single(renderer.PanelPresentationGenerations);
                var changes = new List<WidgetPlacementChangedEventArgs>();
                renderer.PlacementChanged += (_, args) => changes.Add(args);

                renderer.MoveWidget(WidgetKind.Clock, display.X + 400, display.Y + 250, false);
                Assert.Empty(changes);
                Assert.Equal(widget, Assert.Single(renderer.PanelHandles));
                Assert.Equal(frame, Assert.Single(renderer.PanelPresentationGenerations));
                Assert.Equal(pixelCount, Assert.Single(renderer.PanelNonTransparentPixelCounts));
                Assert.True(NativeMethods.GetWindowRect(widget, out var moved));
                Assert.Equal(display.X + 400, moved.Left);
                Assert.Equal(display.Y + 250, moved.Top);

                renderer.MoveWidget(WidgetKind.Clock, display.X + 400, display.Y + 250, true);
                var change = Assert.Single(changes);
                Assert.Equal(WidgetKind.Clock, change.Kind);
                Assert.Equal(WidgetAnchor.Custom, change.Placement.Anchor);
                Assert.Equal(display.Id, change.Placement.DisplayId);
                Assert.False(change.Placement.IsLocked);
                Assert.InRange(change.Placement.X, 0, 1);
                Assert.InRange(change.Placement.Y, 0, 1);
                Assert.False(NativeMethods.IsWindow(editor));

                settings.SetPlacement(WidgetKind.Clock, change.Placement);
                settings.ClockPlacement.IsLocked = true;
                var oldEditor = Assert.Single(renderer.EditingHandles);
                renderer.ApplySettingsAsync(settings).GetAwaiter().GetResult();
                Assert.Empty(renderer.EditingHandles);
                Assert.False(NativeMethods.IsWindow(oldEditor));
                var locked = Assert.Single(renderer.PanelHandles);
                Assert.True(NativeMethods.GetWindowRect(locked, out var beforeLockedDrag));
                renderer.MoveWidget(WidgetKind.Clock, display.X + 900, display.Y + 800, true);
                Assert.Single(changes);
                Assert.True(NativeMethods.GetWindowRect(locked, out var afterLockedDrag));
                Assert.Equal(beforeLockedDrag.Left, afterLockedDrag.Left);
                Assert.Equal(beforeLockedDrag.Top, afterLockedDrag.Top);
                var widgetStyle = NativeMethods.GetWindowLongPtr(locked, NativeMethods.GwlExtendedStyle).ToInt64();
                Assert.NotEqual(0, widgetStyle & NativeMethods.WsExTransparent);
                renderer.CloseSafelyAsync().GetAwaiter().GetResult();
                Assert.False(NativeMethods.IsWindow(locked));
            }
            finally
            {
                renderer.CloseSafelyAsync().GetAwaiter().GetResult();
                NativeMethods.DestroyWindow(parent);
            }
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1920, 1080, 3840, 2160)]
    [Trait("Category", "LocalIntegration")]
    public void DropOnDifferentResolution_PreservesTheRequestedTopLeft(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        => RunOnSta(() => VerifyCrossDisplayDrop(sourceWidth, sourceHeight, targetWidth, targetHeight));

    private static void VerifyCrossDisplayDrop(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var source = new DisplayInfo
        {
            Id = "test-source", Name = "Source", IsPrimary = true,
            X = -28000, Y = -28000, Width = sourceWidth, Height = sourceHeight
        };
        var destination = new DisplayInfo
        {
            Id = "test-destination", Name = "Destination",
            X = source.X + sourceWidth, Y = -28000, Width = targetWidth, Height = targetHeight
        };
        var parent = NativeMethods.CreateWindowExW(0, "Static", "Another Surface Cross Display Test",
            unchecked((uint)NativeMethods.WsPopup), source.X, source.Y, sourceWidth, sourceHeight,
            nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
        Assert.NotEqual(nint.Zero, parent);
        var renderer = new NativeWidgetRendererWindow(new FixedMonitorService([source, destination]));
        try
        {
            renderer.SetWindowVisible(false);
            renderer.AttachTo(parent, source);
            renderer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var settings = new WidgetSettings
            {
                ShowClock = true, ShowSystemInfo = false, ClockPlacement = WidgetPlacement.FromAnchor(WidgetAnchor.Center, false)
            };
            renderer.ApplySettingsAsync(settings).GetAwaiter().GetResult();
            WidgetPlacementChangedEventArgs? saved = null;
            renderer.PlacementChanged += (_, args) => saved = args;
            var dropX = destination.X + 400;
            var dropY = destination.Y + 250;
            renderer.MoveWidget(WidgetKind.Clock, dropX, dropY, false);
            renderer.MoveWidget(WidgetKind.Clock, dropX, dropY, true);
            Assert.NotNull(saved);
            Assert.Equal(destination.Id, saved.Placement.DisplayId);
            var widget = Assert.Single(renderer.PanelHandles);
            Assert.True(NativeMethods.GetWindowRect(widget, out var rectangle));
            Assert.Equal(dropX, rectangle.Left);
            Assert.Equal(dropY, rectangle.Top);
            var scale = NativeWidgetRendererWindow.CalculateResolutionScale(targetWidth, targetHeight);
            Assert.Equal((int)Math.Ceiling(SimpleClockVisual.NaturalWidth * scale), rectangle.Right - rectangle.Left);
            Assert.Equal((int)Math.Ceiling(SimpleClockVisual.NaturalHeight * scale), rectangle.Bottom - rectangle.Top);
        }
        finally
        {
            renderer.CloseSafelyAsync().GetAwaiter().GetResult();
            NativeMethods.DestroyWindow(parent);
        }
    }

    private sealed class FixedMonitorService(IReadOnlyList<DisplayInfo> displays) : IMonitorService
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void SecondaryDisplayChanges_ReflowAndRecoverWithoutPrimaryChanges()
        => RunOnSta(VerifySecondaryTopologyChanges);

    private static void VerifySecondaryTopologyChanges()
    {
        var primary = new DisplayInfo
        {
            Id = "test-primary", Name = "Primary", IsPrimary = true,
            X = -30000, Y = -30000, Width = 1920, Height = 1080
        };
        var secondary = new DisplayInfo
        {
            Id = "test-secondary", Name = "Secondary",
            X = -27000, Y = -30000, Width = 1920, Height = 1080
        };
        var displays = new List<DisplayInfo> { primary, secondary };
        var parent = NativeMethods.CreateWindowExW(0, "Static", "Another Surface Monitor Reflow Test",
            unchecked((uint)NativeMethods.WsPopup), primary.X, primary.Y, primary.Width, primary.Height,
            nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
        Assert.NotEqual(nint.Zero, parent);
        var renderer = new NativeWidgetRendererWindow(new FixedMonitorService(displays));
        try
        {
            renderer.SetWindowVisible(false);
            renderer.AttachTo(parent, primary);
            renderer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var placement = WidgetPlacement.FromAnchor(WidgetAnchor.Center);
            placement.DisplayId = secondary.Id;
            renderer.ApplySettingsAsync(new WidgetSettings
            {
                ShowClock = true, ShowSystemInfo = false, ClockPlacement = placement
            }).GetAwaiter().GetResult();
            secondary = new DisplayInfo
            {
                Id = secondary.Id, Name = "Secondary",
                X = -26000, Y = -29500, Width = 3840, Height = 2160
            };
            displays[1] = secondary;
            renderer.AttachTo(parent, primary);
            Assert.True(NativeMethods.GetWindowRect(Assert.Single(renderer.PanelHandles), out var resized));
            Assert.Equal(secondary.X + (3840 - 640) / 2, resized.Left);
            Assert.Equal(secondary.Y + (2160 - 400) / 2, resized.Top);
            Assert.Equal(640, resized.Right - resized.Left);

            displays.RemoveAt(1);
            renderer.AttachTo(parent, primary);
            Assert.True(NativeMethods.GetWindowRect(Assert.Single(renderer.PanelHandles), out var recovered));
            Assert.Equal(primary.X + (1920 - 320) / 2, recovered.Left);
            Assert.Equal(primary.Y + (1080 - 200) / 2, recovered.Top);
            Assert.Equal(320, recovered.Right - recovered.Left);
        }
        finally
        {
            renderer.CloseSafelyAsync().GetAwaiter().GetResult();
            NativeMethods.DestroyWindow(parent);
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
