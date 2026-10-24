using System.ComponentModel;
using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormats = System.Windows.Media.PixelFormats;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace Vellume.Windows;

/// <summary>
/// Hosts one small WPF panel in a Rainmeter-style top-level layered window
/// using premultiplied BGRA pixels. The window is never shown before its
/// first complete frame.
/// </summary>
internal sealed class LayeredWpfPanelHost : IDisposable
{
    private nint _memoryDc;
    private nint _dib;
    private nint _previousBitmap;
    private nint _bits;
    private byte[] _pixelBuffer = [];
    private int _surfaceWidth;
    private int _surfaceHeight;
    private nint _desktopHandle;
    private bool _disposed;

    internal LayeredWpfPanelHost(
        string name,
        nint parentHandle,
        int x,
        int y,
        int width,
        int height)
    {
        Width = width;
        Height = height;
        _desktopHandle = parentHandle;
        Handle = NativeMethods.CreateWindowExW(
            unchecked((uint)(
                NativeMethods.WsExLayered |
                NativeMethods.WsExTransparent |
                NativeMethods.WsExNoActivate |
                NativeMethods.WsExToolWindow)),
            NativeAlphaPanelWindowClass.EnsureRegistered(),
            name,
            unchecked((uint)(
                NativeMethods.WsPopup |
                NativeMethods.WsClipChildren)),
            x,
            y,
            width,
            height,
            nint.Zero,
            nint.Zero,
            NativeMethods.GetModuleHandleW(null),
            nint.Zero);
        if (Handle == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 알파 위젯 패널을 만들지 못했습니다.");
        }

        // Keep the independent desktop widget out of Aero Peek. Parenting a
        // layered surface to Explorer is intentionally avoided because Windows
        // 11 can omit layered child windows from the final composition.
        _ = NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmwaExcludedFromPeek,
            ref NativeMethods.TrueValue,
            sizeof(int));
        Position(x, y, width, height);
    }

    internal nint Handle { get; private set; }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal long PresentationGeneration { get; private set; }

    internal bool LastPresentSucceeded { get; private set; }

    internal int LastNonTransparentPixelCount { get; private set; }

    internal bool HasPresentedFrame =>
        LastPresentSucceeded &&
        _pixelBuffer.Length == checked(Width * Height * 4);

    internal bool IsReady =>
        !_disposed &&
        Handle != nint.Zero &&
        NativeMethods.IsWindow(Handle);

    internal bool HitTest(int x, int y)
    {
        var pixels = _pixelBuffer;
        var width = Width;
        var height = Height;
        if (x < 0 || y < 0 || x >= width || y >= height) return false;
        var index = ((long)y * width + x) * 4 + 3;
        return index < pixels.Length && pixels[index] > 16;
    }

    internal bool IsAttachedTo(nint parentHandle)
        => IsReady &&
           _desktopHandle == parentHandle &&
           NativeMethods.GetParent(Handle) == nint.Zero;

    internal void SetWindowVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Handle == nint.Zero ||
            !NativeMethods.IsWindow(Handle))
        {
            return;
        }

        if (visible && !HasPresentedFrame)
        {
            return;
        }

        _ = NativeMethods.ShowWindow(
            Handle,
            visible
                ? NativeMethods.SwShowNoActivate
                : NativeMethods.SwHide);
        if (visible)
        {
            ApplyDesktopZOrder();
        }
    }

    internal void AttachTo(
        nint parentHandle,
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _desktopHandle = parentHandle;
        Position(x, y, width, height);
        if (!IsAttachedTo(parentHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "알파 위젯 패널을 데스크톱에 다시 연결하지 못했습니다.");
        }
    }

    internal void Position(
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sizeChanged =
            Width != width ||
            Height != height;
        Width = width;
        Height = height;
        if (!NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndBottom,
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpFrameChanged))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "알파 위젯 패널의 위치를 갱신하지 못했습니다.");
        }

        if (sizeChanged)
        {
            ReleasePresentationSurface();
        }
    }

    internal void MoveTo(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Windows retains the layered bitmap when only the HWND origin moves.
        // Dragging does not measure WPF, copy pixels, or submit another frame.
        if (!NativeMethods.SetWindowPos(Handle, nint.Zero, x, y, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "알파 위젯 패널의 위치를 갱신하지 못했습니다.");
        }
    }

    internal void Render(FrameworkElement visual)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var renderStarted = Stopwatch.GetTimestamp();

        var bitmap = RenderVisual(visual, Width, Height);

        EnsurePresentationSurface();
        var stride = checked(Width * 4);
        bitmap.CopyPixels(
            _pixelBuffer,
            stride,
            0);
        UpdateNonTransparentPixelCountWhenNeeded();
        Present();
        LocalPerformanceDiagnostics.Record(
            "widget.alpha-render",
            Stopwatch.GetElapsedTime(renderStarted));
    }

    internal void RenderRegion(
        FrameworkElement visual,
        int x,
        int y,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
        {
            return;
        }
        if (x < 0 || y < 0 ||
            x + width > Width ||
            y + height > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "부분 위젯 렌더링 영역이 통합 표면을 벗어났습니다.");
        }

        EnsurePresentationSurface();
        if (!HasPresentedFrame)
        {
            throw new InvalidOperationException(
                "부분 렌더링 전에 완성된 위젯 프레임이 필요합니다.");
        }

        var renderStarted = Stopwatch.GetTimestamp();
        var bitmap = RenderVisual(visual, width, height);
        var sourceStride = checked(width * 4);
        var byteCount = checked(sourceStride * height);
        var regionPixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            bitmap.CopyPixels(
                new Int32Rect(0, 0, width, height),
                regionPixels,
                sourceStride,
                0);
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(
                    regionPixels,
                    row * sourceStride,
                    _pixelBuffer,
                    checked(((y + row) * Width + x) * 4),
                    sourceStride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(regionPixels);
        }

        UpdateNonTransparentPixelCountWhenNeeded();
        Present();
        LocalPerformanceDiagnostics.Record(
            "widget.alpha-region-render",
            Stopwatch.GetElapsedTime(renderStarted));
    }

    private static RenderTargetBitmap RenderVisual(
        FrameworkElement visual,
        int width,
        int height)
    {
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        const int supersampling = 2;
        var highResolutionBitmap = new RenderTargetBitmap(
            width * supersampling,
            height * supersampling,
            96 * supersampling,
            96 * supersampling,
            PixelFormats.Pbgra32);
        highResolutionBitmap.Render(visual);

        var downsampledVisual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(
            downsampledVisual,
            BitmapScalingMode.Fant);
        using (var context = downsampledVisual.RenderOpen())
        {
            context.DrawImage(
                highResolutionBitmap,
                new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(downsampledVisual);
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateNonTransparentPixelCountWhenNeeded()
    {
        if (LastNonTransparentPixelCount != 0 &&
            Environment.GetEnvironmentVariable(
                "VELLUME_DIAGNOSTIC_CAPTURE") != "1")
        {
            return;
        }

        var nonTransparentPixelCount = 0;
        for (var index = 3;
             index < _pixelBuffer.Length;
             index += 4)
        {
            if (_pixelBuffer[index] != 0)
            {
                nonTransparentPixelCount++;
            }
        }
        LastNonTransparentPixelCount = nonTransparentPixelCount;
    }

    internal bool PresentCachedFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pixelBuffer.Length != checked(Width * Height * 4))
        {
            return false;
        }

        Present();
        return true;
    }

    private void EnsurePresentationSurface()
    {
        if (_memoryDc != nint.Zero &&
            _dib != nint.Zero &&
            _bits != nint.Zero &&
            _surfaceWidth == Width &&
            _surfaceHeight == Height)
        {
            return;
        }

        ReleasePresentationSurface();
        var pixelCount = checked(Width * Height * 4);
        var memoryDc = NativeMethods.CreateCompatibleDC(nint.Zero);
        if (memoryDc == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "알파 위젯용 메모리 DC를 만들지 못했습니다.");
        }

        nint dib = nint.Zero;
        nint previousBitmap = nint.Zero;
        try
        {
            var bitmapInfo = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = unchecked((uint)Marshal.SizeOf<
                        NativeMethods.BitmapInfoHeader>()),
                    Width = Width,
                    Height = -Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb,
                    SizeImage = unchecked((uint)pixelCount)
                }
            };
            dib = NativeMethods.CreateDIBSection(
                memoryDc,
                ref bitmapInfo,
                NativeMethods.DibRgbColors,
                out var bits,
                nint.Zero,
                0);
            if (dib == nint.Zero || bits == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "알파 위젯용 32비트 화면을 만들지 못했습니다.");
            }

            previousBitmap = NativeMethods.SelectObject(
                memoryDc,
                dib);
            if (previousBitmap == nint.Zero ||
                previousBitmap == new nint(-1))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "알파 위젯용 32비트 화면을 선택하지 못했습니다.");
            }

            _memoryDc = memoryDc;
            _dib = dib;
            _previousBitmap = previousBitmap;
            _bits = bits;
            _pixelBuffer = new byte[pixelCount];
            _surfaceWidth = Width;
            _surfaceHeight = Height;
            memoryDc = nint.Zero;
            dib = nint.Zero;
            previousBitmap = nint.Zero;
        }
        finally
        {
            if (memoryDc != nint.Zero &&
                previousBitmap != nint.Zero &&
                previousBitmap != new nint(-1))
            {
                NativeMethods.SelectObject(
                    memoryDc,
                    previousBitmap);
            }
            if (dib != nint.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }
            if (memoryDc != nint.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }
        }
    }

    private void Present()
    {
        LastPresentSucceeded = false;
        EnsurePresentationSurface();
        Marshal.Copy(
            _pixelBuffer,
            0,
            _bits,
            _pixelBuffer.Length);
        var size = new NativeMethods.NativeSize
        {
            Width = Width,
            Height = Height
        };
        var source = new NativeMethods.NativePoint();
        var blend = new NativeMethods.BlendFunction
        {
            BlendOp = NativeMethods.AcSrcOver,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AcSrcAlpha
        };
        if (!NativeMethods.UpdateLayeredWindow(
                Handle,
                nint.Zero,
                nint.Zero,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.UlwAlpha))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 알파 위젯 프레임을 표시하지 못했습니다.");
        }

        LastPresentSucceeded = true;
        PresentationGeneration++;
    }

    internal void ApplyDesktopZOrder()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady)
        {
            return;
        }

        if (!NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndBottom,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "독립 알파 위젯 창을 데스크톱 Z-order에 배치하지 못했습니다.");
        }
    }

    private void ReleasePresentationSurface()
    {
        if (_memoryDc != nint.Zero &&
            _previousBitmap != nint.Zero &&
            _previousBitmap != new nint(-1))
        {
            NativeMethods.SelectObject(
                _memoryDc,
                _previousBitmap);
        }
        if (_dib != nint.Zero)
        {
            NativeMethods.DeleteObject(_dib);
        }
        if (_memoryDc != nint.Zero)
        {
            NativeMethods.DeleteDC(_memoryDc);
        }

        _memoryDc = nint.Zero;
        _dib = nint.Zero;
        _previousBitmap = nint.Zero;
        _bits = nint.Zero;
        _pixelBuffer = [];
        _surfaceWidth = 0;
        _surfaceHeight = 0;
        LastPresentSucceeded = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleasePresentationSurface();
        if (Handle != nint.Zero &&
            NativeMethods.IsWindow(Handle))
        {
            NativeMethods.DestroyWindow(Handle);
        }

        Handle = nint.Zero;
        _desktopHandle = nint.Zero;
    }
}
