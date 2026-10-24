using System.Runtime.ExceptionServices;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class StickerTests
{
    [Fact]
    public async Task SaveLoad_RestoresMultipleInstancesTransformsVisibilityAndLock()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Vellume-Stickers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new JsonSettingsStore(Path.Combine(folder, "settings.json"));
            var first = new StickerItem
            {
                AssetName = StickerCatalog.All[0].FileName, Size = 348, Rotation = -37,
                Placement = new WidgetPlacement { Anchor = WidgetAnchor.Custom, X = 0.23, Y = 0.81,
                    DisplayId = "second-monitor", IsLocked = true }
            };
            var second = first.Copy();
            second.Id = Guid.NewGuid().ToString("N");
            second.IsVisible = false;
            second.Placement.IsLocked = false;
            await store.SaveAsync(new AppSettings { Widgets = new WidgetSettings { Stickers = [first, second] } });
            var restored = (await store.LoadAsync()).Widgets.Stickers;
            Assert.Equal(2, restored.Count);
            Assert.Equal(first.Id, restored[0].Id);
            Assert.Equal(348, restored[0].Size);
            Assert.Equal(-37, restored[0].Rotation);
            Assert.Equal(0.23, restored[0].Placement.X);
            Assert.Equal(0.81, restored[0].Placement.Y);
            Assert.Equal("second-monitor", restored[0].Placement.DisplayId);
            Assert.True(restored[0].Placement.IsLocked);
            Assert.False(restored[1].Placement.IsLocked);
            Assert.False(restored[1].IsVisible);

            await store.SaveAsync(new AppSettings { Widgets = new WidgetSettings { Stickers = [second, first] } });
            var reordered = (await store.LoadAsync()).Widgets.Stickers;
            Assert.Equal(new[] { second.Id, first.Id }, reordered.Select(item => item.Id));
            Assert.False(reordered[0].IsVisible);
            Assert.True(reordered[1].Placement.IsLocked);

            await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "{\"Widgets\":{\"Stickers\":null}}");
            Assert.Empty((await store.LoadAsync()).Widgets.Stickers);
            await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "{\"Widgets\":{\"ShowClock\":false}}");
            Assert.Empty((await store.LoadAsync()).Widgets.Stickers);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Copy_RejectsUnknownPathsDeduplicatesIdsAndDoesNotSharePlacement()
    {
        var original = new StickerItem { AssetName = StickerCatalog.All[0].FileName, Size = double.NaN,
            Rotation = double.PositiveInfinity, Placement = new WidgetPlacement { X = -5, Y = 9 } };
        var settings = new WidgetSettings
        {
            Stickers = [original, original.Copy(), new StickerItem { AssetName = "../outside.png" }]
        }.Copy();
        var copy = Assert.Single(settings.Stickers);
        Assert.Equal(240, copy.Size);
        Assert.Equal(0, copy.Rotation);
        Assert.Equal(0, copy.Placement.X);
        Assert.Equal(1, copy.Placement.Y);
        copy.Placement.X = 0.7;
        Assert.Equal(-5, original.Placement.X);
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void All25BundledPngs_PresentAlphaPixels_AndDisposeTheirWindows()
        => RunOnSta(() =>
        {
            Assert.Equal(25, StickerCatalog.All.Count);
            Assert.All(StickerCatalog.All, asset => Assert.True(File.Exists(asset.ImagePath), asset.ImagePath));
            WithRenderer((renderer, display) =>
            {
                var settings = EmptyWidgets();
                settings.Stickers = StickerCatalog.All.Select(asset => new StickerItem
                {
                    AssetName = asset.FileName,
                    Placement = WidgetPlacement.FromAnchor(WidgetAnchor.Center)
                }).ToList();
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                Assert.Equal(25, renderer.PanelHandles.Count);
                Assert.True(renderer.AllPanelsPresented);
                Assert.All(renderer.PanelNonTransparentPixelCounts, count => Assert.InRange(count, 1, 242 * 242 - 1));
                Assert.Empty(renderer.StickerEditingHandles);
                var handles = renderer.PanelHandles.ToArray();
                settings.IsEnabled = false;
                Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
                Assert.Empty(renderer.PanelHandles);
                Assert.All(handles, handle => Assert.False(NativeMethods.IsWindow(handle)));
            });
        });

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void DragAndTransform_CommitOnRelease_LockRemovesInput_AndHideRemoveDispose()
        => RunOnSta(() => WithRenderer((renderer, display) =>
        {
            var settings = EmptyWidgets();
            var sticker = new StickerItem { AssetName = StickerCatalog.All[1].FileName };
            settings.Stickers = [sticker];
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            var surface = Assert.Single(renderer.PanelHandles);
            var editor = Assert.Single(renderer.StickerEditingHandles);
            var style = NativeMethods.GetWindowLongPtr(surface, NativeMethods.GwlExtendedStyle).ToInt64();
            Assert.NotEqual(0, style & NativeMethods.WsExTransparent);
            var generation = Assert.Single(renderer.PanelPresentationGenerations);
            var changes = new List<StickerChangedEventArgs>();
            renderer.StickerChanged += (_, args) => changes.Add(args);
            renderer.MoveSticker(sticker.Id, display.X + 110, display.Y + 250, false);
            Assert.Empty(changes);
            Assert.Equal(generation, Assert.Single(renderer.PanelPresentationGenerations));
            Assert.True(NativeMethods.GetWindowRect(surface, out var dragged));
            Assert.Equal(display.X + 110, dragged.Left);
            renderer.MoveSticker(sticker.Id, display.X + 110, display.Y + 250, true);
            Assert.Single(changes);
            renderer.TransformSticker(sticker.Id, 360, 45, false);
            Assert.Single(changes);
            Assert.True(NativeMethods.GetWindowRect(surface, out var rotated));
            Assert.Equal((int)Math.Ceiling(360 * Math.Sqrt(2)) + 2, rotated.Right - rotated.Left);
            Assert.True(renderer.AllPanelsPresented);
            renderer.TransformSticker(sticker.Id, 360, 45, true);
            Assert.Equal(2, changes.Count);
            settings.Stickers[0] = changes[^1].Sticker.Copy();
            settings.Stickers[0].Placement.IsLocked = true;
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.False(NativeMethods.IsWindow(editor));
            Assert.Empty(renderer.StickerEditingHandles);
            renderer.TransformSticker(sticker.Id, 500, 120, true);
            renderer.MoveSticker(sticker.Id, display.X, display.Y, true);
            Assert.Equal(2, changes.Count);
            settings.Stickers[0].IsVisible = false;
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.Empty(renderer.PanelHandles);
            settings.Stickers.Clear();
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.Empty(renderer.PanelHandles);
        }));

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void PriorityOrder_UpdatesActualWindowStackForLockedStickers()
        => RunOnSta(() => WithRenderer((renderer, display) =>
        {
            var front = new StickerItem { AssetName = StickerCatalog.All[0].FileName, Size = 160,
                Placement = WidgetPlacement.FromAnchor(WidgetAnchor.Center, true) };
            var back = new StickerItem { AssetName = StickerCatalog.All[1].FileName, Size = 240,
                Placement = WidgetPlacement.FromAnchor(WidgetAnchor.Center, true) };
            var settings = EmptyWidgets();
            settings.Stickers = [front, back];
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            var handles = renderer.PanelHandles.ToArray();
            Assert.Equal(2, handles.Length);
            AssertStickerStack(handles[0], handles[1]);
            Assert.True(NativeMethods.GetWindowRect(handles[0], out var firstBounds));
            Assert.Equal(162, firstBounds.Right - firstBounds.Left);
            Assert.Empty(renderer.StickerEditingHandles);

            settings.Stickers = [back, front];
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.All(handles, handle => Assert.False(NativeMethods.IsWindow(handle)));
            var reordered = renderer.PanelHandles.ToArray();
            Assert.Equal(2, reordered.Length);
            AssertStickerStack(reordered[0], reordered[1]);
            Assert.True(NativeMethods.GetWindowRect(reordered[0], out var reorderedBounds));
            Assert.Equal(242, reorderedBounds.Right - reorderedBounds.Left);
            Assert.True(renderer.AllPanelsPresented);
            Assert.Empty(renderer.StickerEditingHandles);
        }));

    private static void AssertStickerStack(nint front, nint back)
    {
        // Other integration tests and real apps may insert unrelated HWNDs
        // between these windows. Only the two stickers' relative order matters.
        var order = new List<nint>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == front || window == back) order.Add(window);
            return true;
        }, nint.Zero);
        Assert.Equal(new[] { front, back }, order);
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void ShortcutLock_AndUnlock_PreserveStickerTransformAndAllowEditingAgain()
        => RunOnSta(() => WithRenderer((renderer, display) =>
        {
            var sticker = new StickerItem { AssetName = StickerCatalog.All[1].FileName, Size = 321, Rotation = 27 };
            var settings = EmptyWidgets();
            settings.Stickers = [sticker];
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            var surface = Assert.Single(renderer.PanelHandles);
            var changes = new List<StickerChangedEventArgs>();
            renderer.StickerChanged += (_, args) => changes.Add(args);
            renderer.SetStickerLocked(sticker.Id, true);
            Assert.Empty(renderer.StickerEditingHandles);
            Assert.True(Assert.Single(changes).IsLockChange);
            Assert.True(changes[0].Sticker.Placement.IsLocked);
            renderer.TransformSticker(sticker.Id, 555, 90, true);
            Assert.Single(changes);
            renderer.SetStickerLocked(sticker.Id, false);
            Assert.Single(renderer.StickerEditingHandles);
            Assert.Equal(surface, Assert.Single(renderer.PanelHandles));
            Assert.False(changes[^1].Sticker.Placement.IsLocked);
            Assert.Equal(321, changes[^1].Sticker.Size);
            Assert.Equal(27, changes[^1].Sticker.Rotation);
            renderer.TransformSticker(sticker.Id, 333, 30, true);
            Assert.False(changes[^1].IsLockChange);
            Assert.Equal(333, changes[^1].Sticker.Size);
        }));

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void PointerShortcut_OnlyTargetsDesktopPixelsOrTheMatchingEditor()
        => RunOnSta(() => WithRenderer((renderer, display) =>
        {
            using var hook = new DesktopWidgetPointerHook(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            // This test's renderer is empty, so use a real offscreen HWND as the desktop root.
            var root = NativeMethods.CreateWindowExW(0, "Static", "Shortcut hit test", unchecked((uint)NativeMethods.WsPopup),
                -30000, -30000, 200, 200, nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            try
            {
                var top = new DesktopWidgetPointerHook.Target(0, 0, 100, 100, new nint(123), (x, y) => x == 10 && y == 10, _ => { });
                var back = new DesktopWidgetPointerHook.Target(0, 0, 100, 100, nint.Zero, (x, y) => x == 20 && y == 20, _ => { });
                hook.Update([top, back], root);
                Assert.Same(top, hook.FindTarget(10, 10, true, root));
                Assert.Same(back, hook.FindTarget(20, 20, true, root));
                Assert.Null(hook.FindTarget(30, 30, true, root));
                Assert.Null(hook.FindTarget(10, 10, false, root));
                Assert.Null(hook.FindTarget(10, 10, true, new nint(456))); // A covering application.
                Assert.Same(top, hook.FindTarget(30, 30, false, top.Editor));
                Assert.Null(hook.FindTarget(120, 120, true, top.Editor));
                var clicked = false;
                var companion = back with { Click = () => clicked = true };
                hook.Update([top, companion], root);
                Assert.Same(companion, hook.FindClickTarget(20, 20, root));
                Assert.Null(hook.FindClickTarget(10, 10, root));
                Assert.Null(hook.FindClickTarget(30, 30, root));
                Assert.Null(hook.FindClickTarget(20, 20, new nint(456)));
                Assert.True(hook.HandlePrimaryButton(true, 20, 20, root));
                Assert.True(hook.HandlePrimaryButton(false, 26, 20, root)); // Dragging cannot open details.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.False(clicked);
                Assert.True(hook.HandlePrimaryButton(true, 20, 20, root));
                Assert.True(hook.HandlePrimaryButton(false, 20, 20, root));
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.True(clicked);
                hook.Update([]);
                Assert.Null(hook.FindTarget(10, 10, true, root));
            }
            finally { NativeMethods.DestroyWindow(root); }
        }));

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void HideSticker_PreservesRecordTransformAndLayer_AndCanBeRestored()
        => RunOnSta(() => WithRenderer((renderer, display) =>
        {
            var sticker = new StickerItem { AssetName = StickerCatalog.All[0].FileName, Size = 279, Rotation = -28,
                Placement = WidgetPlacement.FromAnchor(WidgetAnchor.Center, true) };
            var settings = EmptyWidgets();
            settings.Stickers = [sticker];
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            StickerChangedEventArgs? hidden = null;
            renderer.StickerChanged += (_, args) => { if (args.IsVisibilityChange) hidden = args; };
            renderer.HideSticker(sticker.Id);
            Assert.Null(hidden);
            Assert.Single(renderer.PanelHandles);
            renderer.SetStickerLocked(sticker.Id, false);
            renderer.MoveSticker(sticker.Id, display.X + 80, display.Y + 100, false);
            renderer.HideSticker(sticker.Id);
            Assert.NotNull(hidden);
            Assert.False(hidden!.Sticker.IsVisible);
            Assert.Equal(sticker.Id, hidden.Sticker.Id);
            Assert.Equal(279, hidden.Sticker.Size);
            Assert.Equal(-28, hidden.Sticker.Rotation);
            Assert.Empty(renderer.PanelHandles);
            settings.Stickers[0] = hidden.Sticker.Copy();
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.Empty(renderer.PanelHandles);
            settings.Stickers[0].IsVisible = true;
            Assert.True(renderer.ApplySettingsAsync(settings).IsCompletedSuccessfully);
            Assert.Single(renderer.PanelHandles);
        }));

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void DeleteKey_IsScopedToAnActiveLeftDrag_AndConsumesRepeatsOnlyOnce()
        => RunOnSta(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var hook = new DesktopWidgetPointerHook(dispatcher);
            var hidden = 0;
            Assert.False(hook.HandleDeleteKey(true, true));
            hook.SetDeleteGesture(() => hidden++);
            Assert.False(hook.HandleDeleteKey(true, false));
            Assert.True(hook.HandleDeleteKey(true, true));
            Assert.True(hook.HandleDeleteKey(true, true));
            Assert.True(hook.HandleDeleteKey(false, false));
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal(1, hidden);
            Assert.False(hook.HandleDeleteKey(true, true));
            hook.SetDeleteGesture(() => hidden++);
            hook.SetDeleteGesture(null); // Releasing the mouse or losing capture cancels the shortcut.
            Assert.False(hook.HandleDeleteKey(true, true));
            Assert.False(hook.HandleDeleteKey(false, false));
        });

    private static WidgetSettings EmptyWidgets() => new()
        { ShowClock = false, ShowSystemInfo = false, ShowCodexCompanion = false };

    private static void WithRenderer(Action<NativeWidgetRendererWindow, DisplayInfo> action)
    {
        var parent = NativeMethods.CreateWindowExW(0, "Static", "Vellume Sticker Test",
            unchecked((uint)NativeMethods.WsPopup), -30000, -30000, 1920, 1080,
            nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
        Assert.NotEqual(nint.Zero, parent);
        var display = new DisplayInfo { Id = "sticker-test", Name = "Test", IsPrimary = true,
            X = -30000, Y = -30000, Width = 1920, Height = 1080 };
        var renderer = new NativeWidgetRendererWindow(new MonitorStub([display]));
        try
        {
            renderer.AttachTo(parent, display);
            renderer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            action(renderer, display);
        }
        finally
        {
            renderer.CloseSafelyAsync().GetAwaiter().GetResult();
            NativeMethods.DestroyWindow(parent);
        }
    }

    private sealed class MonitorStub(IReadOnlyList<DisplayInfo> displays) : IMonitorService
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
