using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnotherSurface.Core;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AnotherSurface.Windows;

internal sealed partial class NativeWidgetRendererWindow
{
    private readonly Dictionary<string, StickerHostState> _stickerHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _stickerImages = new(StringComparer.Ordinal);
    internal event EventHandler<StickerChangedEventArgs>? StickerChanged;
    internal IReadOnlyList<nint> StickerEditingHandles => _stickerHosts.Values
        .Where(state => state.Editor is not null).Select(state => state.Editor!.Handle).ToArray();

    private static bool HasSameStickers(WidgetSettings current, WidgetSettings next)
        => current.Stickers.Count == next.Stickers.Count && current.Stickers.Zip(next.Stickers).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.AssetName == pair.Second.AssetName &&
            pair.First.IsVisible == pair.Second.IsVisible && pair.First.Size == pair.Second.Size &&
            pair.First.Rotation == pair.Second.Rotation && HasSamePlacement(pair.First.Placement, pair.Second.Placement));

    private void CreateStickerHosts()
    {
        foreach (var item in _settings.Stickers.Where(item => item.IsVisible))
        {
            var asset = StickerCatalog.Find(item.AssetName);
            if (asset is null || !File.Exists(asset.ImagePath)) continue;
            if (!_stickerImages.TryGetValue(asset.FileName, out var image))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(asset.ImagePath);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                    _stickerImages.Add(asset.FileName, image);
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
                {
                    continue;
                }
            }
            var display = ResolveDisplay(item.Placement);
            var side = GetStickerPixelSize(item, display);
            var position = WidgetPlacementLayout.Resolve(item.Placement, display.Width, display.Height, side, side);
            var x = display.X + (int)Math.Round(position.X);
            var y = display.Y + (int)Math.Round(position.Y);
            var host = new BoundedDesktopPanelHost($"Another Surface Sticker {item.Id}", _hostHandle, x, y, side, side);
            var state = new StickerHostState(item, host, image, display, x, y);
            try
            {
                RenderSticker(state);
                _stickerHosts.Add(item.Id, state);
                if (!item.Placement.IsLocked) CreateStickerEditor(state);
            }
            catch
            {
                state.Editor?.CloseSafely();
                host.Dispose();
                _stickerHosts.Remove(item.Id);
                throw;
            }
        }
    }

    private static int GetStickerPixelSize(StickerItem item, DisplayInfo display)
        => Math.Max(1, (int)Math.Ceiling(item.GetBoundsSize() *
            CalculateResolutionScale(display.Width, display.Height)) + 2);

    private static void RenderSticker(StickerHostState state)
    {
        var size = state.Item.Size * CalculateResolutionScale(state.Display.Width, state.Display.Height);
        var image = new Image
        {
            Source = state.Image, Width = size, Height = size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(state.Item.Rotation)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var root = new Grid { Width = state.Host.Width, Height = state.Host.Height };
        root.Children.Add(image);
        state.Host.Render(root);
    }

    internal void MoveSticker(string id, int requestedX, int requestedY, bool completed)
    {
        if (_disposed || !_stickerHosts.TryGetValue(id, out var state) || state.Item.Placement.IsLocked) return;
        var cx = requestedX + state.Host.Width / 2;
        var cy = requestedY + state.Host.Height / 2;
        var display = _availableDisplays.FirstOrDefault(candidate =>
            cx >= candidate.X && cx < candidate.X + candidate.Width &&
            cy >= candidate.Y && cy < candidate.Y + candidate.Height) ?? state.Display;
        var side = completed ? GetStickerPixelSize(state.Item, display) : state.Host.Width;
        state.Item.Placement = WidgetPlacementLayout.FromPosition(requestedX - display.X, requestedY - display.Y,
            display.Width, display.Height, side, side, isLocked: false, displayId: display.Id);
        var position = WidgetPlacementLayout.Resolve(state.Item.Placement, display.Width, display.Height, side, side);
        state.X = display.X + (int)Math.Round(position.X);
        state.Y = display.Y + (int)Math.Round(position.Y);
        state.Display = display;
        if (completed)
        {
            state.Host.AttachTo(_hostHandle, state.X, state.Y, side, side);
            RenderSticker(state);
        }
        else state.Host.MoveTo(state.X, state.Y);
        state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y, side, side, _isWindowVisible);
        RefreshPointerTargets();
        if (completed) CompleteStickerEdit(state);
    }

    internal void TransformSticker(string id, double size, double rotation, bool completed)
    {
        if (_disposed || !_stickerHosts.TryGetValue(id, out var state) || state.Item.Placement.IsLocked) return;
        var centerX = state.X + state.Host.Width / 2d;
        var centerY = state.Y + state.Host.Height / 2d;
        state.Item.Size = size;
        state.Item.Rotation = rotation;
        state.Item = state.Item.Copy();
        var side = GetStickerPixelSize(state.Item, state.Display);
        state.Item.Placement = WidgetPlacementLayout.FromPosition(centerX - side / 2d - state.Display.X,
            centerY - side / 2d - state.Display.Y, state.Display.Width, state.Display.Height, side, side,
            isLocked: false, displayId: state.Display.Id);
        var position = WidgetPlacementLayout.Resolve(state.Item.Placement,
            state.Display.Width, state.Display.Height, side, side);
        state.X = state.Display.X + (int)Math.Round(position.X);
        state.Y = state.Display.Y + (int)Math.Round(position.Y);
        state.Host.AttachTo(_hostHandle, state.X, state.Y, side, side);
        RenderSticker(state);
        state.Host.SetWindowVisible(_isWindowVisible);
        state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y, side, side, _isWindowVisible);
        RefreshPointerTargets();
        if (completed) CompleteStickerEdit(state);
    }

    private void CompleteStickerEdit(StickerHostState state)
    {
        var index = _settings.Stickers.FindIndex(item => item.Id == state.Item.Id);
        if (index >= 0) _settings.Stickers[index] = state.Item.Copy();
        StickerChanged?.Invoke(this, new StickerChangedEventArgs(state.Item));
    }

    private void DisposeStickerHosts()
    {
        foreach (var state in _stickerHosts.Values)
        {
            state.Editor?.CloseSafely();
            state.Host.Dispose();
        }
        _stickerHosts.Clear();
    }

    private void ReattachStickerHosts()
    {
        foreach (var state in _stickerHosts.Values)
        {
            state.Host.AttachTo(_hostHandle, state.X, state.Y, state.Host.Width, state.Host.Height);
            state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y, state.Host.Width, state.Host.Height, _isWindowVisible);
        }
    }

    private sealed class StickerHostState(StickerItem item, BoundedDesktopPanelHost host,
        BitmapSource image, DisplayInfo display, int x, int y)
    {
        internal StickerItem Item { get; set; } = item.Copy();
        internal BoundedDesktopPanelHost Host { get; } = host;
        internal BitmapSource Image { get; } = image;
        internal DisplayInfo Display { get; set; } = display;
        internal int X { get; set; } = x;
        internal int Y { get; set; } = y;
        internal WidgetEditingOverlayWindow? Editor { get; set; }
    }
}
