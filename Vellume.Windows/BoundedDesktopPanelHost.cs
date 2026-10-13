using System.Windows;

namespace Vellume.Windows;

/// <summary>
/// Owns one bounded premultiplied-alpha top-level desktop widget surface.
/// It deliberately has no Explorer or wallpaper parent because Windows 11 can
/// silently omit layered child windows from the final desktop composition.
/// </summary>
internal sealed class BoundedDesktopPanelHost : IDisposable
{
    private readonly LayeredWpfPanelHost _surface;
    private bool _disposed;

    internal BoundedDesktopPanelHost(
        string name,
        nint desktopHandle,
        int x,
        int y,
        int width,
        int height)
    {
        _surface = new LayeredWpfPanelHost(
            $"{name} Alpha Surface",
            desktopHandle,
            x,
            y,
            width,
            height);
    }

    /// <summary>
    /// The single bounded top-level HWND associated with the desktop host.
    /// </summary>
    internal nint Handle => _surface.Handle;

    /// <summary>
    /// Compatibility alias retained for diagnostics.
    /// </summary>
    internal nint SurfaceHandle => _surface.Handle;

    internal int Width => _surface.Width;

    internal int Height => _surface.Height;

    internal long PresentationGeneration =>
        _surface.PresentationGeneration;

    internal bool LastPresentSucceeded =>
        _surface.LastPresentSucceeded;

    internal int LastNonTransparentPixelCount =>
        _surface.LastNonTransparentPixelCount;

    internal bool IsReady =>
        !_disposed &&
        _surface.IsReady;

    internal bool IsAttachedTo(nint desktopHandle)
        => IsReady &&
           _surface.IsAttachedTo(desktopHandle);

    internal void Render(FrameworkElement visual)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.Render(visual);
    }

    internal void RenderRegion(
        FrameworkElement visual,
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.RenderRegion(visual, x, y, width, height);
    }

    internal void SetWindowVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady ||
            NativeMethods.IsWindowVisible(_surface.Handle) == visible)
        {
            return;
        }

        if (visible &&
            !_surface.PresentCachedFrame())
        {
            return;
        }

        _surface.SetWindowVisible(visible);
    }

    internal void AttachTo(
        nint desktopHandle,
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.AttachTo(
            desktopHandle,
            x,
            y,
            width,
            height);
        if (!IsAttachedTo(desktopHandle))
        {
            throw new InvalidOperationException(
                "Windows가 단일 투명 위젯 창을 다시 연결하지 못했습니다.");
        }

        // A cached submit keeps the DIB alive across Explorer host changes
        // without hiding or recreating the visible top-level surface.
        _surface.PresentCachedFrame();
    }

    internal void ApplyDesktopZOrder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.ApplyDesktopZOrder();
    }

    internal void MoveTo(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.MoveTo(x, y);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.Dispose();
    }
}
