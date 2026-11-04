using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using AnotherSurface.Core;

namespace AnotherSurface.Windows;

internal sealed class WallpaperRendererWindow : IWallpaperRenderer
{
    private ManagedVideoPlayer? _managedVideo;
    private NativeMediaEnginePlayer? _nativeVideoPlayer;
    private NativeGifPlayer? _nativeGifPlayer;
    private nint _handle;
    private nint _nativeVideoHandle;
    private nint _nativeBitmapHandle;
    private int _width;
    private int _height;
    private int _displayX;
    private int _displayY;
    private string _nativeSourcePath = string.Empty;
    private PlaybackOptions _playbackOptions = new();
    private WallpaperItem? _wallpaper;
    private string _nativeFallbackReason = string.Empty;
    private WallpaperRenderBackend _backend;
    private bool _windowVisibleRequested = true;
    private bool _isRaisedDesktop;
    private bool _disposed;

    public nint Handle => _handle;
    public string BackendDisplayName => _backend switch
    {
        WallpaperRenderBackend.NativeImage => "네이티브 이미지",
        WallpaperRenderBackend.NativeGif => "네이티브 GIF",
        WallpaperRenderBackend.NativeVideo => "Media Foundation",
        WallpaperRenderBackend.ManagedVideo => "Windows 영상 (색상 조절)",
        _ => "준비 중"
    };
    public bool IsReady =>
        !_disposed &&
        _handle != nint.Zero &&
        NativeMethods.IsWindow(_handle) &&
        (_backend switch
        {
            WallpaperRenderBackend.NativeImage =>
                _nativeBitmapHandle != nint.Zero,
            WallpaperRenderBackend.NativeGif =>
                _nativeGifPlayer?.IsReady == true,
            WallpaperRenderBackend.NativeVideo =>
                _nativeVideoPlayer?.IsReady == true &&
                _nativeVideoHandle == _handle &&
                NativeMethods.IsWindow(_handle),
            WallpaperRenderBackend.ManagedVideo => _managedVideo?.IsReady == true,
            _ => false
        });

    public string GetAttachmentDiagnostics()
    {
        var parent = NativeMethods.GetParent(_handle);
        var above = NativeMethods.GetWindow(
            _handle,
            NativeMethods.GwHwndPrevious);
        var below = NativeMethods.GetWindow(
            _handle,
            NativeMethods.GwHwndNext);
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            _handle,
            NativeMethods.GwlExtendedStyle).ToInt64();
        NativeMethods.GetWindowRect(_handle, out var rendererRectangle);
        var hasLayeredAttributes = NativeMethods.GetLayeredWindowAttributes(
            _handle,
            out _,
            out var alpha,
            out var layeredFlags);
        return $"backend={_backend}" +
               $"{(string.IsNullOrWhiteSpace(_nativeFallbackReason) ? string.Empty : $" (fallback={_nativeFallbackReason})")}, " +
               $"parent=0x{parent:X}, renderer=0x{_handle:X}, " +
               $"nativeVideoTarget=" +
               $"{(_backend == WallpaperRenderBackend.NativeVideo ? "wallpaper-hwnd" : "none")}, " +
               $"nestedVideoWindow=false, " +
               $"class={DescribeWindow(_handle)}, " +
               $"above={DescribeWindow(above)}, below={DescribeWindow(below)}, " +
               $"visible={NativeMethods.IsWindowVisible(_handle)}, " +
               $"rect={rendererRectangle.Left},{rendererRectangle.Top}-" +
               $"{rendererRectangle.Right},{rendererRectangle.Bottom}, " +
               $"ex-style=0x{extendedStyle:X8}, " +
               $"layered-alpha={(hasLayeredAttributes ? alpha : -1)}, " +
               $"layered-flags=0x{layeredFlags:X}";
    }

    public void SetWindowVisible(bool visible)
    {
        _windowVisibleRequested = visible;
        if (_handle == nint.Zero ||
            !NativeMethods.IsWindow(_handle))
        {
            return;
        }

        if (NativeMethods.IsWindowVisible(_handle) != visible)
        {
            NativeMethods.ShowWindow(
                _handle,
                visible
                    ? NativeMethods.SwShowNoActivate
                    : NativeMethods.SwHide);
        }
        _nativeGifPlayer?.SetVisible(visible);
    }

    Task IWallpaperRenderer.SetWindowVisibleAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetWindowVisible(visible);
        return Task.CompletedTask;
    }

    public void AttachTo(
        nint hostHandle,
        DisplayInfo display,
        bool isRaisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!NativeMethods.GetWindowRect(hostHandle, out var hostRectangle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 데스크톱 호스트 영역을 읽지 못했습니다.");
        }

        if (isRaisedDesktop &&
            (shellViewHandle == nint.Zero ||
             backgroundWorkerHandle == nint.Zero))
        {
            throw new InvalidOperationException(
                "확장 데스크톱에 ShellView 또는 WorkerW 레이어가 없습니다.");
        }

        var relativeX = display.X - hostRectangle.Left;
        var relativeY = display.Y - hostRectangle.Top;
        _width = display.Width;
        _height = display.Height;
        _displayX = display.X;
        _displayY = display.Y;
        _isRaisedDesktop = isRaisedDesktop;

        if (_handle == nint.Zero || !NativeMethods.IsWindow(_handle))
        {
            var style =
                NativeMethods.WsChild |
                NativeMethods.WsClipChildren |
                NativeMethods.WsClipSiblings |
                NativeMethods.SsBitmap |
                (_windowVisibleRequested
                    ? NativeMethods.WsVisible
                    : 0);
            var extendedStyle =
                NativeMethods.WsExNoActivate |
                NativeMethods.WsExToolWindow |
                (isRaisedDesktop ? NativeMethods.WsExLayered : 0);

            _handle = NativeMethods.CreateWindowExW(
                unchecked((uint)extendedStyle),
                "Static",
                "Another Surface Wallpaper Renderer",
                unchecked((uint)style),
                relativeX,
                relativeY,
                _width,
                _height,
                hostHandle,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            if (_handle == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 네이티브 월페이퍼 호스트를 만들지 못했습니다.");
            }
        }
        else
        {
            NativeMethods.SetParent(_handle, hostHandle);
        }

        if (NativeMethods.GetParent(_handle) != hostHandle)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows가 데스크톱 렌더러 연결을 거부했습니다.");
        }

        if (isRaisedDesktop)
        {
            var extendedStyle = NativeMethods.GetWindowLongPtr(
                _handle,
                NativeMethods.GwlExtendedStyle).ToInt64();
            if ((extendedStyle & NativeMethods.WsExLayered) == 0)
            {
                NativeMethods.SetWindowLongPtr(
                    _handle,
                    NativeMethods.GwlExtendedStyle,
                    new nint(extendedStyle | NativeMethods.WsExLayered));
            }

            // The raised desktop only composites opaque layered children between
            // ShellView and its background WorkerW.
            if (!NativeMethods.SetLayeredWindowAttributes(
                    _handle,
                    0,
                    255,
                    NativeMethods.LwaAlpha))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 확장 데스크톱 합성을 켜지 못했습니다.");
            }

            var zOrderFlags =
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate;
            if (!NativeMethods.SetWindowPos(
                    _handle,
                    shellViewHandle,
                    0,
                    0,
                    0,
                    0,
                    zOrderFlags))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 확장 데스크톱의 Z 순서를 설정하지 못했습니다.");
            }
        }

        if (!NativeMethods.SetWindowPos(
                _handle,
                nint.Zero,
                relativeX,
                relativeY,
                _width,
                _height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpFrameChanged))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 월페이퍼 렌더러 위치를 지정하지 못했습니다.");
        }

        _managedVideo?.Resize(_width, _height);

        if (_backend == WallpaperRenderBackend.NativeVideo)
        {
            UseVideoRedirectionSurface();
            LayoutNativeVideo();
        }
        else if (_backend == WallpaperRenderBackend.NativeGif &&
                 _nativeGifPlayer is not null)
        {
            _nativeGifPlayer.UpdateLayout(
                _width,
                _height,
                _playbackOptions.Fit);
        }

        if (!_windowVisibleRequested)
        {
            // SetWindowPos above may reveal the host after Explorer rebuilds.
            // Keep every Another Surface desktop surface hidden while Windows owns
            // the temporary safety wallpaper.
            NativeMethods.ShowWindow(
                _handle,
                NativeMethods.SwHide);
        }
        else
        {
            NativeMethods.ShowWindow(
                _handle,
                NativeMethods.SwShowNoActivate);
        }
    }

    async Task IWallpaperRenderer.AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var imageNeedsResize =
            _backend == WallpaperRenderBackend.NativeImage &&
            (_width != display.Width || _height != display.Height ||
             !NativeMethods.IsWindow(_handle));
        AttachTo(
            hostHandle,
            display,
            raisedDesktop,
            shellViewHandle,
            backgroundWorkerHandle);
        if (imageNeedsResize &&
            !string.IsNullOrWhiteSpace(_nativeSourcePath))
        {
            var sourcePath = _nativeSourcePath;
            var options = _playbackOptions.Copy();
            var bitmap = await Task.Run(
                () => NativeWallpaperImageRenderer.Render(
                    sourcePath, display.Width, display.Height, options),
                cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                SetNativeBitmap(bitmap);
                bitmap = nint.Zero;
            }
            finally
            {
                if (bitmap != nint.Zero)
                {
                    NativeMethods.DeleteObject(bitmap);
                }
            }
        }
    }

    public async Task InitializeAndNavigateAsync(
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        if (_handle == nint.Zero || !NativeMethods.IsWindow(_handle))
        {
            throw new InvalidOperationException(
                "네이티브 월페이퍼 호스트가 연결되지 않았습니다.");
        }

        _wallpaper = wallpaper.Copy();
        _playbackOptions = options.Copy();
        _nativeSourcePath = wallpaper.SourcePath;
        _nativeFallbackReason = string.Empty;

        // The same HWND is reused when changing from video to an image/GIF
        // or the WPF color-adjustment path. Restore its bitmap composition.
        if (_isRaisedDesktop)
        {
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle, new nint(style | NativeMethods.WsExLayered));
            if (!NativeMethods.SetLayeredWindowAttributes(_handle, 0, 255, NativeMethods.LwaAlpha))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 확장 데스크톱 합성을 켜지 못했습니다.");
        }

        if (wallpaper.Kind == WallpaperKind.Image)
        {
            try
            {
                CloseManagedVideo();
                CloseNativeVideoCore();
                CloseNativeGifCore();
                SetNativeBitmap(
                    NativeWallpaperImageRenderer.Render(
                        wallpaper.SourcePath,
                        _width,
                        _height,
                        _playbackOptions));
                _backend = WallpaperRenderBackend.NativeImage;
                return;
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                _nativeFallbackReason =
                    $"native image: {exception.GetType().Name}";
                CloseNativeImageCore();
            }
        }

        if (WallpaperRendererBackendPolicy.ShouldUseNativeGif(wallpaper))
        {
            try
            {
                CloseManagedVideo();
                CloseNativeVideoCore();
                CloseNativeGifCore();
                _nativeGifPlayer = new NativeGifPlayer(
                    _handle,
                    wallpaper.SourcePath,
                    _width,
                    _height,
                    options.Fit);
                _backend = WallpaperRenderBackend.NativeGif;
                CloseNativeImageCore();
                return;
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                _nativeFallbackReason =
                    $"native GIF: {exception.GetType().Name}";
                CloseNativeGifCore();
            }
        }

        if (WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                options))
        {
            try
            {
                CloseManagedVideo();
                CloseNativeGifCore();
                PrepareNativeVideoSurface();
                _nativeVideoPlayer =
                    new NativeMediaEnginePlayer();
                await _nativeVideoPlayer.InitializeAsync(
                    _nativeVideoHandle,
                    wallpaper.SourcePath,
                    options.IsMuted,
                    options.Volume,
                    cancellationToken);
                // Retain the previous safety bitmap until Media Foundation
                // reports its first decoded frame. Native video now renders
                // directly into the wallpaper HWND instead of allocating a
                // second monitor-sized child composition surface.
                CloseNativeImageCore();
                SetRendererStaticStyle(NativeMethods.SsBlackRect);
                _backend = WallpaperRenderBackend.NativeVideo;
                LayoutNativeVideo();
                return;
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                _nativeFallbackReason =
                    $"Media Foundation: {exception.GetType().Name}";
                CloseNativeVideoCore();
            }
        }

        if (wallpaper.Kind != WallpaperKind.Video)
        {
            throw new NotSupportedException(
                $"지원하지 않거나 불러올 수 없는 월페이퍼입니다: {wallpaper.Kind}. {_nativeFallbackReason}");
        }

        CloseNativeVideoCore();
        CloseNativeGifCore();
        CloseManagedVideo();
        _managedVideo = new ManagedVideoPlayer(_handle, _width, _height);
        try
        {
            await _managedVideo.OpenAsync(wallpaper.SourcePath, options, cancellationToken);
            CloseNativeImageCore();
            SetRendererStaticStyle(NativeMethods.SsBlackRect);
            _backend = WallpaperRenderBackend.ManagedVideo;
        }
        catch
        {
            CloseManagedVideo();
            throw;
        }
    }

    public Task CapturePreviewAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (_backend == WallpaperRenderBackend.NativeImage &&
            _nativeBitmapHandle != nint.Zero)
        {
            using var image = Image.FromHbitmap(_nativeBitmapHandle);
            image.Save(
                outputPath,
                System.Drawing.Imaging.ImageFormat.Png);
            return Task.CompletedTask;
        }

        if ((_backend is WallpaperRenderBackend.NativeVideo or
             WallpaperRenderBackend.NativeGif or WallpaperRenderBackend.ManagedVideo) &&
            NativeMethods.GetWindowRect(
                _handle,
                out var nativeRectangle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = new Bitmap(_width, _height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                nativeRectangle.Left,
                nativeRectangle.Top,
                0,
                0,
                new Size(_width, _height));
            bitmap.Save(
                outputPath,
                System.Drawing.Imaging.ImageFormat.Png);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("월페이퍼 렌더러가 미리보기 캡처 준비 상태가 아닙니다.");
    }

    public bool IsAttachedTo(nint hostHandle)
        => IsReady &&
           NativeMethods.IsWindow(hostHandle) &&
           NativeMethods.GetParent(_handle) == hostHandle;

    public async Task PauseAsync()
    {
        if (_backend == WallpaperRenderBackend.NativeGif &&
            _nativeGifPlayer is not null)
        {
            _nativeGifPlayer.Pause();
            return;
        }

        if (_backend == WallpaperRenderBackend.NativeVideo &&
            _nativeVideoPlayer is not null)
        {
            // IMFMediaEngine keeps its most recent video frame presented
            // while Pause stops decode and frame delivery.
            await _nativeVideoPlayer.PauseAsync();
            return;
        }

        _managedVideo?.Pause();
    }

    public async Task ResumeAsync()
    {
        if (_backend == WallpaperRenderBackend.NativeGif &&
            _nativeGifPlayer is not null)
        {
            _nativeGifPlayer.Resume();
            return;
        }

        if (_backend == WallpaperRenderBackend.NativeVideo &&
            _nativeVideoPlayer is not null)
        {
            await _nativeVideoPlayer.ResumeAsync();
            return;
        }

        _managedVideo?.Resume();
    }

    public async Task UpdatePlaybackAsync(PlaybackOptions options)
    {
        _playbackOptions = options.Copy();
        if (_backend == WallpaperRenderBackend.NativeGif &&
            _nativeGifPlayer is not null)
        {
            _nativeGifPlayer.UpdateLayout(
                _width,
                _height,
                _playbackOptions.Fit);
            return;
        }

        if (_backend == WallpaperRenderBackend.NativeImage &&
            !string.IsNullOrWhiteSpace(_nativeSourcePath))
        {
            SetNativeBitmap(
                NativeWallpaperImageRenderer.Render(
                    _nativeSourcePath,
                    _width,
                    _height,
                    _playbackOptions));
            return;
        }

        if (_backend == WallpaperRenderBackend.NativeVideo &&
            _nativeVideoPlayer is not null)
        {
            if (_wallpaper is not null &&
                !WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                    _wallpaper,
                    _playbackOptions))
            {
                await InitializeAndNavigateAsync(
                    _wallpaper,
                    _playbackOptions,
                    CancellationToken.None);
                return;
            }

            await _nativeVideoPlayer.UpdateAudioAsync(
                _playbackOptions.IsMuted,
                _playbackOptions.Volume);
            LayoutNativeVideo();
            return;
        }

        if (_backend == WallpaperRenderBackend.ManagedVideo &&
            _wallpaper is not null &&
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                _wallpaper,
                _playbackOptions))
        {
            await InitializeAndNavigateAsync(
                _wallpaper,
                _playbackOptions,
                CancellationToken.None);
            return;
        }

        _managedVideo?.UpdatePlayback(_playbackOptions);
    }

    public Task CloseSafelyAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _disposed = true;
        CloseManagedVideo();
        CloseNativeVideoCore();
        CloseNativeGifCore();
        CloseNativeImageCore();
        if (_handle != nint.Zero && NativeMethods.IsWindow(_handle))
        {
            NativeMethods.DestroyWindow(_handle);
        }

        _handle = nint.Zero;
        _backend = WallpaperRenderBackend.None;
        return Task.CompletedTask;
    }

    private void CloseManagedVideo()
    {
        _managedVideo?.Dispose();
        _managedVideo = null;
        if (_backend == WallpaperRenderBackend.ManagedVideo)
            _backend = WallpaperRenderBackend.None;
    }

    private void PrepareNativeVideoSurface()
    {
        CloseNativeVideoCore();
        CloseNativeGifCore();
        UseVideoRedirectionSurface();
        _nativeVideoHandle = _handle;
        if (_nativeVideoHandle == nint.Zero ||
            !NativeMethods.IsWindow(_nativeVideoHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Media Foundation 영상 출력 창을 만들지 못했습니다.");
        }

        // Media Engine draws directly into the existing wallpaper HWND.
        // Avoid creating another monitor-sized child composition surface.
    }

    private void UseVideoRedirectionSurface()
    {
        // Media Engine presents an opaque video surface. Retain ordinary DWM
        // redirection, as the previous native backend did, without the bitmap
        // renderer's additional layered-window composition path.
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle).ToInt64();
        if ((style & NativeMethods.WsExLayered) == 0) return;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle, new nint(style & ~NativeMethods.WsExLayered));
        NativeMethods.SetWindowPos(_handle, nint.Zero, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void LayoutNativeVideo()
    {
        if (_nativeVideoHandle == nint.Zero ||
            !NativeMethods.IsWindow(_nativeVideoHandle))
        {
            return;
        }

        var sourceWidth =
            (int)(_nativeVideoPlayer?.NativeWidth ?? (uint)_width);
        var sourceHeight =
            (int)(_nativeVideoPlayer?.NativeHeight ?? (uint)_height);
        var destination = WallpaperLayout.Calculate(
            sourceWidth,
            sourceHeight,
            _width,
            _height,
            _playbackOptions.Fit);
        _nativeVideoPlayer?.UpdateVideoLayout(
            destination.X,
            destination.Y,
            destination.Width,
            destination.Height);
    }

    private void SetNativeBitmap(nint bitmapHandle)
    {
        if (bitmapHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                "네이티브 월페이퍼 비트맵을 만들지 못했습니다.");
        }

        SetRendererStaticStyle(NativeMethods.SsBitmap);
        var previous = NativeMethods.SendMessageW(
            _handle,
            NativeMethods.StmSetImage,
            NativeMethods.ImageBitmap,
            bitmapHandle);
        if (previous != nint.Zero &&
            previous != bitmapHandle)
        {
            NativeMethods.DeleteObject(previous);
        }
        if (_nativeBitmapHandle != nint.Zero &&
            _nativeBitmapHandle != previous &&
            _nativeBitmapHandle != bitmapHandle)
        {
            NativeMethods.DeleteObject(_nativeBitmapHandle);
        }

        _nativeBitmapHandle = bitmapHandle;
    }

    private void SetRendererStaticStyle(long staticStyle)
    {
        if (_handle == nint.Zero ||
            !NativeMethods.IsWindow(_handle))
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(
            _handle,
            NativeMethods.GwlStyle).ToInt64();
        var updatedStyle =
            (style & ~NativeMethods.SsTypeMask) |
            (staticStyle & NativeMethods.SsTypeMask);
        if (updatedStyle == style)
        {
            return;
        }

        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlStyle,
            new nint(updatedStyle));
        NativeMethods.SetWindowPos(
            _handle,
            nint.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged);
    }

    private void CloseNativeImageCore()
    {
        if (_nativeBitmapHandle == nint.Zero)
        {
            return;
        }

        NativeMethods.SendMessageW(
            _handle,
            NativeMethods.StmSetImage,
            NativeMethods.ImageBitmap,
            nint.Zero);
        NativeMethods.DeleteObject(_nativeBitmapHandle);
        _nativeBitmapHandle = nint.Zero;
        if (_backend == WallpaperRenderBackend.NativeImage)
        {
            _backend = WallpaperRenderBackend.None;
        }
    }

    private void CloseNativeVideoCore()
    {
        _nativeVideoPlayer?.Dispose();
        _nativeVideoPlayer = null;
        if (_nativeVideoHandle != nint.Zero &&
            _nativeVideoHandle != _handle &&
            NativeMethods.IsWindow(_nativeVideoHandle))
        {
            NativeMethods.DestroyWindow(_nativeVideoHandle);
        }

        _nativeVideoHandle = nint.Zero;
        if (_backend == WallpaperRenderBackend.NativeVideo)
        {
            _backend = WallpaperRenderBackend.None;
        }
    }

    private void CloseNativeGifCore()
    {
        _nativeGifPlayer?.Dispose();
        _nativeGifPlayer = null;
        if (_backend == WallpaperRenderBackend.NativeGif)
        {
            _backend = WallpaperRenderBackend.None;
        }
    }

    private static string DescribeWindow(nint handle)
    {
        if (handle == nint.Zero)
        {
            return "none";
        }

        var className = new System.Text.StringBuilder(128);
        NativeMethods.GetClassNameW(
            handle,
            className,
            className.Capacity);
        return $"0x{handle:X}({className})";
    }

}

internal enum WallpaperRenderBackend
{
    None,
    NativeImage,
    NativeGif,
    NativeVideo,
    ManagedVideo
}

internal static class WallpaperRendererBackendPolicy
{
    internal static bool ShouldUseNativeGif(
        WallpaperItem wallpaper)
        => wallpaper.Kind == WallpaperKind.Gif &&
           Path.GetExtension(wallpaper.SourcePath).Equals(
               ".gif",
               StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldUseNativeVideo(
        WallpaperItem wallpaper,
        PlaybackOptions options)
        => wallpaper.Kind == WallpaperKind.Video &&
           Path.GetExtension(wallpaper.SourcePath) is var extension &&
           (extension.Equals(
                ".mp4",
                StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(
                ".webm",
                StringComparison.OrdinalIgnoreCase)) &&
           options.Brightness <= 1.001 &&
           Math.Abs(options.Contrast - 1) < 0.001 &&
           Math.Abs(options.Saturation - 1) < 0.001;
}
