using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vellume.Windows;

/// <summary>
/// Hosts an interactive WPF surface inside a redirected desktop child.
/// Information widgets no longer use this monitor-sized bridge; it remains
/// only for the small quick-slot and media-control interaction windows.
/// </summary>
internal sealed class NativeWpfChildHost : IDisposable
{
    private nint _parentHandle;
    private bool _disposed;

    internal NativeWpfChildHost(
        string name,
        nint parentHandle,
        int x,
        int y,
        int width,
        int height,
        bool clickThrough)
    {
        _parentHandle = parentHandle;
        var extendedStyle =
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExLayered;
        if (clickThrough)
        {
            extendedStyle |= NativeMethods.WsExTransparent;
        }

        Handle = NativeMethods.CreateWindowExW(
            unchecked((uint)extendedStyle),
            "Static",
            name,
            unchecked((uint)(
                NativeMethods.WsChild |
                NativeMethods.WsVisible |
                NativeMethods.WsClipChildren |
                NativeMethods.WsClipSiblings |
                NativeMethods.SsBlackRect)),
            x,
            y,
            width,
            height,
            parentHandle,
            nint.Zero,
            NativeMethods.GetModuleHandleW(null),
            nint.Zero);
        if (Handle == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 WPF 데스크톱 브리지 창을 만들지 못했습니다.");
        }

        try
        {
            // A child HwndSource composites transparent pixels as black in
            // this redirected bridge. Color-key the matching Static backing
            // so only the interactive WPF pixels remain visible.
            if (!NativeMethods.SetLayeredWindowAttributes(
                    Handle,
                    0,
                    255,
                    NativeMethods.LwaColorKey |
                    NativeMethods.LwaAlpha))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 WPF 데스크톱 브리지 투명도를 설정하지 못했습니다.");
            }

            var parameters = new HwndSourceParameters(name)
            {
                ParentWindow = Handle,
                PositionX = 0,
                PositionY = 0,
                Width = width,
                Height = height,
                WindowStyle = unchecked((int)(
                    NativeMethods.WsChild |
                    NativeMethods.WsVisible |
                    NativeMethods.WsClipChildren |
                    NativeMethods.WsClipSiblings)),
                ExtendedWindowStyle = unchecked((int)(
                    NativeMethods.WsExNoActivate |
                    NativeMethods.WsExToolWindow)),
                UsesPerPixelOpacity = true
            };
            Source = new HwndSource(parameters);
            Source.CompositionTarget.BackgroundColor =
                Colors.Transparent;
        }
        catch
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = nint.Zero;
            throw;
        }
    }

    internal nint Handle { get; private set; }

    internal HwndSource Source { get; }

    internal bool IsReady =>
        !_disposed &&
        Handle != nint.Zero &&
        NativeMethods.IsWindow(Handle) &&
        Source.Handle != nint.Zero &&
        NativeMethods.IsWindow(Source.Handle);

    internal void AttachTo(
        nint parentHandle,
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _parentHandle = parentHandle;
        NativeMethods.SetParent(Handle, parentHandle);
        Position(x, y, width, height);
        if (NativeMethods.GetParent(Handle) != parentHandle)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows가 WPF 데스크톱 브리지 창을 다시 연결하지 못했습니다.");
        }
    }

    internal void Position(
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.SetWindowPos(
                Handle,
                nint.Zero,
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpShowWindow |
                NativeMethods.SwpFrameChanged) ||
            !NativeMethods.SetWindowPos(
                Source.Handle,
                nint.Zero,
                0,
                0,
                width,
                height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpShowWindow |
                NativeMethods.SwpFrameChanged))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 WPF 데스크톱 브리지 위치를 갱신하지 못했습니다.");
        }
    }

    internal bool IsAttachedTo(nint parentHandle)
        => IsReady &&
           NativeMethods.IsWindow(parentHandle) &&
           NativeMethods.GetParent(Handle) == parentHandle &&
           NativeMethods.GetParent(Source.Handle) == Handle;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Source.Dispose();
        if (Handle != nint.Zero &&
            NativeMethods.IsWindow(Handle))
        {
            NativeMethods.DestroyWindow(Handle);
        }

        Handle = nint.Zero;
        _parentHandle = nint.Zero;
    }
}
