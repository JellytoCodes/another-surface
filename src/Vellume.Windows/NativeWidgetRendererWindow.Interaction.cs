using System.Windows.Controls;
using Vellume.Core;

namespace Vellume.Windows;

internal sealed partial class NativeWidgetRendererWindow
{
    private DesktopWidgetPointerHook? _pointerHook;

    private void CreateWidgetEditor(WidgetHostState state)
    {
        var initialScale = _settings.GetScale(state.Kind);
        state.Editor = new WidgetEditingOverlayWindow(state.Kind);
        var editor = state.Editor;
        editor.MoveDragStateChanged += active => _pointerHook?.SetDeleteGesture(active ? editor.DeleteDuringMove : null);
        editor.DeleteRequested += () => HideWidget(state.Kind);
        state.Editor.DragPositionChanged += MoveWidget;
        state.Editor.TransformStarted += () =>
        {
            initialScale = _settings.GetScale(state.Kind);
        };
        state.Editor.TransformChanged += (factor, _, completed) => ResizeWidget(state.Kind, initialScale * factor, completed);
        state.Editor.SetBounds(_interactiveHostHandle, state.X, state.Y, state.Host.Width, state.Host.Height, _isWindowVisible);
    }

    private void CreateStickerEditor(StickerHostState state)
    {
        var initialSize = state.Item.Size;
        var initialRotation = state.Item.Rotation;
        var id = state.Item.Id;
        state.Editor = new WidgetEditingOverlayWindow($"Vellume Sticker {StickerCatalog.Find(state.Item.AssetName)?.Name}", true);
        var editor = state.Editor;
        editor.MoveDragStateChanged += active => _pointerHook?.SetDeleteGesture(active ? editor.DeleteDuringMove : null);
        editor.DeleteRequested += () => HideSticker(id);
        state.Editor.PositionChanged += (x, y, completed) => MoveSticker(id, x, y, completed);
        state.Editor.TransformStarted += () => { initialSize = state.Item.Size; initialRotation = state.Item.Rotation; };
        state.Editor.TransformChanged += (factor, angle, completed) => TransformSticker(id, initialSize * factor, initialRotation + angle, completed);
        state.Editor.SetBounds(_interactiveHostHandle, state.X, state.Y, state.Host.Width, state.Host.Height, _isWindowVisible);
    }

    internal void ResizeWidget(WidgetKind kind, double scale, bool completed)
    {
        if (_disposed || !_widgetHosts.TryGetValue(kind, out var state) || _settings.GetPlacement(kind).IsLocked) return;
        _settings.SetScale(kind, scale);
        var target = kind switch
        {
            WidgetKind.Clock => _clockRenderTarget,
            WidgetKind.SystemInfo => _systemRenderTarget,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (target?.Visual is Viewbox previous) previous.Child = null;
        var display = state.Display;
        var resolutionScale = CalculateResolutionScale(display.Width, display.Height);
        var prepared = PrepareLayeredPanel(state.Content, resolutionScale * _settings.GetScale(kind));
        var placement = WidgetPlacementLayout.FromPosition(state.X - display.X, state.Y - display.Y,
            display.Width, display.Height, prepared.Width, prepared.Height, 24 * resolutionScale,
            isLocked: false, displayId: display.Id);
        var position = WidgetPlacementLayout.Resolve(placement, display.Width, display.Height,
            prepared.Width, prepared.Height, 24 * resolutionScale);
        state.X = display.X + (int)Math.Round(position.X);
        state.Y = display.Y + (int)Math.Round(position.Y);
        _settings.SetPlacement(kind, placement);
        state.Host.AttachTo(_hostHandle, state.X, state.Y, prepared.Width, prepared.Height);
        state.Host.Render(prepared.Visual);
        var resized = new WidgetPanelRenderTarget(kind, prepared.Visual, prepared.Width, prepared.Height);
        switch (kind)
        {
            case WidgetKind.Clock: _clockRenderTarget = resized; break;
            case WidgetKind.SystemInfo: _systemRenderTarget = resized; break;
        }
        state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y, prepared.Width, prepared.Height, _isWindowVisible);
        RefreshPointerTargets();
        if (completed) PlacementChanged?.Invoke(this, new WidgetPlacementChangedEventArgs(kind, placement, _settings.GetScale(kind)));
    }

    internal void HideWidget(WidgetKind kind)
    {
        if (_disposed || !_widgetHosts.TryGetValue(kind, out var state) || _settings.GetPlacement(kind).IsLocked) return;
        var display = state.Display;
        var resolutionScale = CalculateResolutionScale(display.Width, display.Height);
        var scale = _settings.GetScale(kind);
        var placement = WidgetPlacementLayout.FromPosition(state.X - display.X, state.Y - display.Y,
            display.Width, display.Height, Math.Ceiling(state.NaturalWidth * resolutionScale * scale),
            Math.Ceiling(state.NaturalHeight * resolutionScale * scale), 24 * resolutionScale, isLocked: false, displayId: display.Id);
        _settings.SetPlacement(kind, placement);
        _settings.SetVisible(kind, false);
        RebuildAlphaPanels();
        ApplyLastMetrics();
        UpdateClockTimer();
        PlacementChanged?.Invoke(this, new WidgetPlacementChangedEventArgs(kind, placement, scale, isVisible: false));
    }

    internal void HideSticker(string id)
    {
        if (_disposed || !_stickerHosts.TryGetValue(id, out var state) || state.Item.Placement.IsLocked) return;
        var index = _settings.Stickers.FindIndex(item => item.Id == id);
        if (index < 0) return;
        var item = state.Item.Copy();
        var side = GetStickerPixelSize(item, state.Display);
        item.Placement = WidgetPlacementLayout.FromPosition(state.X - state.Display.X, state.Y - state.Display.Y,
            state.Display.Width, state.Display.Height, side, side, isLocked: false, displayId: state.Display.Id);
        item.IsVisible = false;
        _settings.Stickers[index] = item;
        RebuildAlphaPanels();
        ApplyLastMetrics();
        UpdateClockTimer();
        StickerChanged?.Invoke(this, new StickerChangedEventArgs(item, isVisibilityChange: true));
    }

    internal void SetWidgetLocked(WidgetKind kind, bool locked)
    {
        if (_disposed || !_widgetHosts.TryGetValue(kind, out var state)) return;
        var placement = _settings.GetPlacement(kind).Copy();
        if (placement.IsLocked == locked) return;
        placement.IsLocked = locked;
        _settings.SetPlacement(kind, placement);
        if (locked) { state.Editor?.CloseSafely(); state.Editor = null; }
        else CreateWidgetEditor(state);
        RefreshPointerTargets();
        PlacementChanged?.Invoke(this, new WidgetPlacementChangedEventArgs(kind, placement, _settings.GetScale(kind), isLockChange: true));
    }

    internal void SetStickerLocked(string id, bool locked)
    {
        if (_disposed || !_stickerHosts.TryGetValue(id, out var state) || state.Item.Placement.IsLocked == locked) return;
        state.Item.Placement.IsLocked = locked;
        var index = _settings.Stickers.FindIndex(item => item.Id == id);
        if (index >= 0) _settings.Stickers[index] = state.Item.Copy();
        if (locked) { state.Editor?.CloseSafely(); state.Editor = null; }
        else CreateStickerEditor(state);
        RefreshPointerTargets();
        StickerChanged?.Invoke(this, new StickerChangedEventArgs(state.Item, isLockChange: true));
    }

    private void RefreshPointerTargets()
    {
        if (_disposed || !_isWindowVisible || !_settings.IsEnabled || !_initialized)
        {
            _pointerHook?.Update([]);
            return;
        }
        var targets = new List<DesktopWidgetPointerHook.Target>();
        foreach (var state in _widgetHosts.Values)
            targets.Add(new(state.X, state.Y, state.Host.Width, state.Host.Height,
                state.Editor?.Handle ?? nint.Zero, state.Host.HitTest, locked => SetWidgetLocked(state.Kind, locked)));
        foreach (var item in _settings.Stickers)
            if (_stickerHosts.TryGetValue(item.Id, out var state))
                targets.Add(new(state.X, state.Y, state.Host.Width, state.Host.Height,
                    state.Editor?.Handle ?? nint.Zero, state.Host.HitTest, locked => SetStickerLocked(item.Id, locked)));
        if (targets.Count == 0) { _pointerHook?.Update([]); return; }
        _pointerHook ??= new DesktopWidgetPointerHook(_clockTimer.Dispatcher);
        _pointerHook.Update(targets.ToArray(), _hostHandle, _interactiveHostHandle, _compositionParentHandle);
    }
}
