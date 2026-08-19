using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vellume.Core;

namespace Vellume.Windows;

internal sealed class WallpaperRendererWindow : IWallpaperRenderer
{
    private const string VirtualHostName = "vellume.local";
    private readonly string _webViewDataDirectory;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
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
    private bool _webViewFailed;
    private bool _coreConfigured;
    private bool _windowVisibleRequested = true;
    private bool _disposed;

    public WallpaperRendererWindow(string webViewDataDirectory)
    {
        _webViewDataDirectory = webViewDataDirectory;
    }

    public nint Handle => _handle;
    public string BackendDisplayName => _backend switch
    {
        WallpaperRenderBackend.NativeImage => "네이티브 이미지",
        WallpaperRenderBackend.NativeGif => "네이티브 GIF",
        WallpaperRenderBackend.NativeVideo => "Media Foundation",
        WallpaperRenderBackend.WebView => "WebView2",
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
            WallpaperRenderBackend.WebView =>
                !_webViewFailed &&
                _controller is not null &&
                _core is not null,
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
        if (_controller is not null &&
            _controller.IsVisible != visible)
        {
            _controller.IsVisible = visible;
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
                "Vellume Wallpaper Renderer",
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

        if (_controller is not null)
        {
            _controller.ParentWindow = _handle;
            _controller.Bounds = new Rectangle(0, 0, _width, _height);
            _controller.IsVisible = _windowVisibleRequested;
        }

        if (_backend == WallpaperRenderBackend.NativeVideo)
        {
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
            // Keep every Vellume desktop surface hidden while Windows owns
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

    Task IWallpaperRenderer.AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AttachTo(
            hostHandle,
            display,
            raisedDesktop,
            shellViewHandle,
            backgroundWorkerHandle);
        return Task.CompletedTask;
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

        if (wallpaper.Kind == WallpaperKind.Image)
        {
            try
            {
                await CloseWebCoreAsync();
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
                await CloseWebCoreAsync();
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
                await CloseWebCoreAsync();
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

        CloseNativeVideoCore();
        CloseNativeGifCore();
        var keepSafetyFrameUntilReady =
            _nativeBitmapHandle != nint.Zero;
        try
        {
            if (_controller is null || _core is null)
            {
                var environment =
                    await WebViewEnvironmentProvider.GetAsync(
                        _webViewDataDirectory);
                _controller =
                    await environment.CreateCoreWebView2ControllerAsync(
                        _handle);
                _controller.DefaultBackgroundColor = Color.Black;
                _core = _controller.CoreWebView2;
                _core.ProcessFailed += CoreOnProcessFailed;
            }

            _webViewFailed = false;
            _backend = WallpaperRenderBackend.WebView;
            _controller.ParentWindow = _handle;
            _controller.Bounds = new Rectangle(0, 0, _width, _height);
            _controller.IsVisible =
                !keepSafetyFrameUntilReady;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "WebView2를 시작하지 못했습니다. Microsoft Edge WebView2 Runtime을 설치하거나 복구하세요.",
                exception);
        }

        var core = _core;
        if (!_coreConfigured)
        {
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NewWindowRequested += (_, eventArgs) =>
                eventArgs.Handled = true;
            _coreConfigured = true;
        }

        var sourcePath = wallpaper.Kind == WallpaperKind.Web
            ? WallpaperValidator.ResolveWebEntryPoint(wallpaper.SourcePath)
            : wallpaper.SourcePath;
        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException(
                "The wallpaper source has no parent folder.");

        core.ClearVirtualHostNameToFolderMapping(VirtualHostName);
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            sourceDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);

        var fileName = Uri.EscapeDataString(Path.GetFileName(sourcePath));
        var virtualUri = $"https://{VirtualHostName}/{fileName}";
        var navigation = WaitForNavigationAsync(cancellationToken);
        if (wallpaper.Kind == WallpaperKind.Web)
        {
            core.Navigate(virtualUri);
        }
        else
        {
            core.NavigateToString(
                BuildMediaDocument(wallpaper, virtualUri, options));
        }

        var result = await navigation;
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"WebView2가 월페이퍼를 불러오지 못했습니다 ({result.WebErrorStatus}).");
        }

        await EnsureMediaReadyAsync(
            wallpaper.Kind,
            cancellationToken);
        await UpdateWebPlaybackAsync(options);
        await ResumeAsync();
        if (_controller is not null)
        {
            _controller.IsVisible = _windowVisibleRequested;
        }
        // The loaded WebView now covers the parent, so the temporary GDI
        // frame can be released without exposing Windows' own wallpaper.
        CloseNativeImageCore();
    }

    public async Task CapturePreviewAsync(
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
            return;
        }

        if ((_backend is WallpaperRenderBackend.NativeVideo or
             WallpaperRenderBackend.NativeGif) &&
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
            return;
        }

        if (_core is null)
        {
            throw new InvalidOperationException(
                "월페이퍼 렌더러가 미리보기 캡처 준비 상태가 아닙니다.");
        }

        await using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        await _core.CapturePreviewAsync(
            CoreWebView2CapturePreviewImageFormat.Png,
            output);
        await output.FlushAsync(cancellationToken);
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

        if (_backend != WallpaperRenderBackend.WebView)
        {
            return;
        }

        await ExecuteScriptAsync(
            "document.querySelectorAll('video,audio').forEach(x=>x.pause());" +
            "document.getAnimations().forEach(x=>x.pause());");
        if (_core is not null && !_core.IsSuspended)
        {
            try
            {
                // Suspension preserves the last compositor frame while
                // putting Chromium timers and its renderer process to sleep.
                await _core.TrySuspendAsync();
            }
            catch
            {
                // Script-level pause above remains a safe fallback.
            }
        }
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

        if (_backend != WallpaperRenderBackend.WebView)
        {
            return;
        }

        if (_core?.IsSuspended == true)
        {
            _core.Resume();
        }
        await ExecuteScriptAsync(
            "document.querySelectorAll('video,audio').forEach(x=>x.play().catch(()=>{}));" +
            "document.getAnimations().forEach(x=>x.play());");
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

        if (_backend == WallpaperRenderBackend.WebView &&
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

        await UpdateWebPlaybackAsync(_playbackOptions);
    }

    private Task UpdateWebPlaybackAsync(PlaybackOptions options)
    {
        var volume = Math.Clamp(options.Volume, 0, 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var muted = options.IsMuted ? "true" : "false";
        var visualFilter = BuildCssFilter(options);
        var serializedFilter = JsonSerializer.Serialize(visualFilter);
        var objectFit = options.Fit switch
        {
            WallpaperFit.Fit => "contain",
            WallpaperFit.Stretch => "fill",
            _ => "cover"
        };

        return ExecuteScriptAsync(
            $"(()=>{{document.querySelectorAll('video,audio').forEach(x=>{{x.muted={muted};x.volume={volume};}});" +
            $"const vellumeMedia=[...document.querySelectorAll('[data-vellume-media]')];" +
            $"vellumeMedia.forEach(x=>{{x.style.objectFit={JsonSerializer.Serialize(objectFit)};x.style.setProperty('filter',{serializedFilter},'important');}});" +
            $"if(vellumeMedia.length){{document.documentElement.style.removeProperty('filter');}}" +
            $"else{{document.documentElement.style.setProperty('filter',{serializedFilter},'important');}}}})();");
    }

    public Task ApplyWidgetsAsync(WidgetSettings widgets)
    {
        var enabled = widgets.IsEnabled &&
            (widgets.ShowClock ||
             widgets.ShowSystemInfo ||
             widgets.ShowQuickSlots);
        if (!enabled)
        {
            return ExecuteScriptAsync(
                "window.clearInterval(window.__vellumeClockTimer);" +
                "document.getElementById('vellume-widget-layer')?.remove();" +
                "delete window.__vellumeWidgetUpdate;");
        }

        var slotMarkup = widgets.QuickSlots
            .Where(item => item.Slot is >= 1 and <= 9)
            .OrderBy(item => item.Slot)
            .Select(item =>
            {
                var name = WebUtility.HtmlEncode(
                    string.IsNullOrWhiteSpace(item.Name)
                        ? Path.GetFileNameWithoutExtension(item.TargetPath)
                        : item.Name);
                return
                    $"<div class=\"slot\"><span>{item.Slot}</span>" +
                    $"<strong>{name}</strong></div>";
            })
            .ToArray();
        var slots = slotMarkup.Length == 0
            ? "<div class=\"empty\">Add files in Vellume to create quick slots.</div>"
            : string.Join(string.Empty, slotMarkup);
        var showClock = widgets.ShowClock ? "grid" : "none";
        var showSystem = widgets.ShowSystemInfo ? "block" : "none";
        var showSlots = widgets.ShowQuickSlots ? "flex" : "none";

        return ExecuteScriptAsync($$"""
            (() => {
              const id = 'vellume-widget-layer';
              let host = document.getElementById(id);
              if (!host) {
                host = document.createElement('div');
                host.id = id;
                (document.body || document.documentElement).appendChild(host);
              }
              host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;';
              const root = host.shadowRoot || host.attachShadow({ mode: 'open' });
              root.innerHTML = `
                <style>
                  :host { all: initial; }
                  * { box-sizing: border-box; }
                  .layer {
                    position: fixed; inset: 0; pointer-events: none;
                    color: #d9ffff;
                    font-family: "Segoe UI Variable", "Segoe UI", sans-serif;
                    text-shadow: 0 0 16px rgba(54, 238, 255, .48);
                  }
                  .panel {
                    position: absolute;
                    background: linear-gradient(135deg, rgba(2,12,23,.88), rgba(8,26,40,.62));
                    border: 1px solid rgba(89, 229, 255, .72);
                    box-shadow: 0 0 24px rgba(24, 181, 255, .16), inset 0 0 28px rgba(23, 114, 166, .12);
                    backdrop-filter: blur(7px);
                    clip-path: polygon(0 0, calc(100% - 16px) 0, 100% 16px, 100% 100%, 16px 100%, 0 calc(100% - 16px));
                  }
                  .eyebrow {
                    color: #6ef6ff; font-size: 11px; font-weight: 700;
                    letter-spacing: .2em; text-transform: uppercase;
                  }
                  .clock {
                    left: 42px; bottom: 54px; width: 340px;
                    display: {{showClock}}; grid-template-columns: 1fr; padding: 20px 24px 18px;
                  }
                  .clock time {
                    margin-top: 4px; color: #e4ffff; font-size: 72px;
                    font-weight: 250; line-height: .95; letter-spacing: .02em;
                  }
                  .clock .date {
                    margin-top: 10px; color: rgba(213,251,255,.72);
                    font-size: 13px; letter-spacing: .14em; text-transform: uppercase;
                  }
                  .system {
                    display: {{showSystem}}; right: 42px; top: 42px;
                    width: 330px; padding: 19px 20px 18px;
                  }
                  .system h2 {
                    margin: 5px 0 16px; font-size: 18px; font-weight: 650;
                    letter-spacing: .08em;
                  }
                  .metric { margin-top: 12px; }
                  .metric-line {
                    display: flex; justify-content: space-between;
                    color: rgba(225,253,255,.82); font-size: 12px;
                  }
                  .track {
                    height: 5px; margin-top: 6px; overflow: hidden;
                    background: rgba(82,147,170,.22);
                  }
                  .fill {
                    width: 0; height: 100%;
                    background: linear-gradient(90deg, #32d7ff, #7affd5);
                    box-shadow: 0 0 12px #39e5ff;
                    transition: width .5s ease;
                  }
                  .uptime {
                    margin-top: 15px; padding-top: 11px;
                    border-top: 1px solid rgba(91,218,255,.25);
                    color: rgba(174,235,241,.6); font-size: 10px;
                    letter-spacing: .12em;
                  }
                  .slots {
                    display: {{showSlots}}; gap: 8px; align-items: stretch;
                    left: 50%; bottom: 52px; transform: translateX(-50%);
                    min-width: 240px; max-width: 720px; padding: 11px 13px;
                  }
                  .slot {
                    width: 72px; min-height: 58px; padding: 7px 6px;
                    display: grid; align-content: center; justify-items: center;
                    border: 1px solid rgba(102,237,255,.42);
                    background: rgba(7,25,38,.72);
                    clip-path: polygon(11px 0, 100% 0, 100% calc(100% - 11px), calc(100% - 11px) 100%, 0 100%, 0 11px);
                  }
                  .slot span {
                    color: #60f4ff; font-size: 10px; font-weight: 800;
                  }
                  .slot strong {
                    width: 100%; margin-top: 4px; overflow: hidden;
                    color: rgba(232,255,255,.88); font-size: 10px;
                    font-weight: 600; text-align: center;
                    text-overflow: ellipsis; white-space: nowrap;
                  }
                  .empty {
                    padding: 8px 12px; color: rgba(195,239,245,.58);
                    font-size: 11px; letter-spacing: .04em;
                  }
                </style>
                <div class="layer">
                  <section class="panel clock">
                    <span class="eyebrow">Local time</span>
                    <time data-clock>--:--</time>
                    <span class="date" data-date></span>
                  </section>
                  <section class="panel system">
                    <span class="eyebrow">System telemetry</span>
                    <h2>VELLUME // STATUS</h2>
                    <div class="metric">
                      <div class="metric-line"><span>CPU</span><b data-cpu-label>--%</b></div>
                      <div class="track"><div class="fill" data-cpu-bar></div></div>
                    </div>
                    <div class="metric">
                      <div class="metric-line"><span>MEMORY</span><b data-memory-label>--</b></div>
                      <div class="track"><div class="fill" data-memory-bar></div></div>
                    </div>
                    <div class="metric">
                      <div class="metric-line"><span>SYSTEM DISK</span><b data-disk-label>--</b></div>
                      <div class="track"><div class="fill" data-disk-bar></div></div>
                    </div>
                    <div class="uptime" data-uptime>UPTIME --</div>
                  </section>
                  <section class="panel slots">{{slots}}</section>
                </div>`;

              const updateClock = () => {
                const now = new Date();
                const clock = root.querySelector('[data-clock]');
                const date = root.querySelector('[data-date]');
                if (clock) {
                  clock.textContent = new Intl.DateTimeFormat([], {
                    hour: '2-digit', minute: '2-digit', hour12: false
                  }).format(now);
                }
                if (date) {
                  date.textContent = new Intl.DateTimeFormat([], {
                    weekday: 'short', year: 'numeric', month: 'long', day: '2-digit'
                  }).format(now);
                }
              };
              window.clearInterval(window.__vellumeClockTimer);
              updateClock();
              window.__vellumeClockTimer = window.setInterval(updateClock, 1000);

              window.__vellumeWidgetUpdate = metrics => {
                const setText = (selector, value) => {
                  const node = root.querySelector(selector);
                  if (node) node.textContent = value;
                };
                const setBar = (selector, value) => {
                  const node = root.querySelector(selector);
                  if (node) node.style.width = `${Math.max(0, Math.min(100, value))}%`;
                };
                setText('[data-cpu-label]', `${Math.round(metrics.cpu)}%`);
                setText('[data-memory-label]', metrics.memoryLabel);
                setText('[data-disk-label]', metrics.diskLabel);
                setText('[data-uptime]', `UPTIME ${metrics.uptime}`);
                setBar('[data-cpu-bar]', metrics.cpu);
                setBar('[data-memory-bar]', metrics.memory);
                setBar('[data-disk-bar]', metrics.disk);
              };
            })();
            """);
    }

    public Task UpdateSystemMetricsAsync(SystemMetricsSnapshot metrics)
    {
        var payload = JsonSerializer.Serialize(new
        {
            cpu = metrics.CpuPercent,
            memory = metrics.MemoryPercent,
            disk = metrics.DiskPercent,
            memoryLabel =
                $"{FormatBytes(metrics.MemoryUsedBytes)} / " +
                $"{FormatBytes(metrics.MemoryTotalBytes)}",
            diskLabel =
                $"{FormatBytes(metrics.DiskUsedBytes)} / " +
                $"{FormatBytes(metrics.DiskTotalBytes)}",
            uptime = metrics.Uptime.TotalDays >= 1
                ? $"{(int)metrics.Uptime.TotalDays}D {metrics.Uptime.Hours:00}H"
                : $"{metrics.Uptime.Hours:00}H {metrics.Uptime.Minutes:00}M"
        });
        return ExecuteScriptAsync(
            $"window.__vellumeWidgetUpdate?.({payload});");
    }

    public async Task CloseSafelyAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseWebCoreAsync();
        CloseNativeVideoCore();
        CloseNativeGifCore();
        CloseNativeImageCore();
        if (_handle != nint.Zero && NativeMethods.IsWindow(_handle))
        {
            NativeMethods.DestroyWindow(_handle);
        }

        _handle = nint.Zero;
        _backend = WallpaperRenderBackend.None;
    }

    private async Task CloseWebCoreAsync()
    {
        var core = _core;
        _core = null;
        if (core is not null)
        {
            core.ProcessFailed -= CoreOnProcessFailed;
            try
            {
                core.Navigate("about:blank");
                await Task.Yield();
            }
            catch
            {
                // WebView2 may already be gone; native cleanup still continues.
            }
        }

        _controller?.Close();
        _controller = null;
        _coreConfigured = false;
        _webViewFailed = false;
        if (_backend == WallpaperRenderBackend.WebView)
        {
            _backend = WallpaperRenderBackend.None;
        }
    }

    private void PrepareNativeVideoSurface()
    {
        CloseNativeVideoCore();
        CloseNativeGifCore();
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

    private void CoreOnProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        if (!_disposed)
        {
            _webViewFailed = true;
        }
    }

    private Task<CoreWebView2NavigationCompletedEventArgs> WaitForNavigationAsync(
        CancellationToken cancellationToken)
    {
        var completion =
            new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(
            object? _,
            CoreWebView2NavigationCompletedEventArgs eventArgs)
        {
            if (_core is not null)
            {
                _core.NavigationCompleted -= Handler;
            }

            completion.TrySetResult(eventArgs);
        }

        _core!.NavigationCompleted += Handler;
        cancellationToken.Register(() =>
        {
            if (_core is not null)
            {
                _core.NavigationCompleted -= Handler;
            }

            completion.TrySetCanceled(cancellationToken);
        });
        return completion.Task;
    }

    private async Task ExecuteScriptAsync(string script)
    {
        var core = _core;
        if (_disposed || core is null)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
            when (_disposed || !ReferenceEquals(_core, core))
        {
            // A close can finish while an already-issued script is awaiting COM.
        }
        catch (InvalidOperationException)
        {
            _webViewFailed = true;
        }
    }

    private async Task EnsureMediaReadyAsync(
        WallpaperKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == WallpaperKind.Web || _core is null)
        {
            return;
        }

        var isVideo = kind == WallpaperKind.Video;
        var attempts = isVideo ? 80 : 40;
        var script = isVideo
            ? """
              (() => {
                const media = document.querySelector('video');
                if (!media) return JSON.stringify({found:false});
                media.muted = true;
                media.loop = true;
                if (media.readyState === 0) media.load();
                if (media.paused) media.play().catch(() => {});
                return JSON.stringify({
                  found:true,
                  readyState:media.readyState,
                  paused:media.paused,
                  errorCode:media.error?.code || 0,
                  width:media.videoWidth || 0
                });
              })()
              """
            : """
              (() => {
                const media = document.querySelector('img');
                if (!media) return JSON.stringify({found:false});
                return JSON.stringify({
                  found:true,
                  readyState:media.complete ? 4 : 0,
                  paused:false,
                  errorCode:media.complete && media.naturalWidth === 0 ? 4 : 0,
                  width:media.naturalWidth || 0
                });
              })()
              """;

        MediaReadyProbe? lastProbe = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await _core.ExecuteScriptAsync(script);
            try
            {
                var json = JsonSerializer.Deserialize<string>(encoded);
                lastProbe = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<MediaReadyProbe>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
            }
            catch (JsonException)
            {
                lastProbe = null;
            }

            if (lastProbe?.ErrorCode > 0)
            {
                throw new InvalidOperationException(
                    $"미디어 파일을 디코딩하지 못했습니다 " +
                    $"(브라우저 오류 코드 {lastProbe.ErrorCode}).");
            }

            if (lastProbe is
                {
                    Found: true,
                    ReadyState: >= 2,
                    Width: > 0
                })
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            isVideo
                ? "영상이 8초 안에 재생 준비 상태가 되지 않았습니다."
                : "이미지가 4초 안에 로드되지 않았습니다.");
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

    private static string FormatBytes(ulong bytes)
    {
        const double gibibyte = 1024d * 1024d * 1024d;
        return $"{bytes / gibibyte:0.0} GB";
    }

    private static string BuildMediaDocument(
        WallpaperItem wallpaper,
        string virtualUri,
        PlaybackOptions options)
    {
        var objectFit = options.Fit switch
        {
            WallpaperFit.Fit => "contain",
            WallpaperFit.Stretch => "fill",
            _ => "cover"
        };
        var encodedName = WebUtility.HtmlEncode(wallpaper.Name);
        var encodedUri = WebUtility.HtmlEncode(virtualUri);
        var visualFilter = BuildCssFilter(options);
        var media = wallpaper.Kind == WallpaperKind.Video
            ? $"<video data-vellume-media src=\"{encodedUri}\" autoplay muted loop playsinline preload=\"auto\"></video>"
            : $"<img data-vellume-media src=\"{encodedUri}\" alt=\"{encodedName}\" loading=\"eager\">";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{{encodedName}}</title>
              <style>
                html,body { width:100%; height:100%; margin:0; overflow:hidden; background:#05070b; }
                [data-vellume-media] {
                  display:block; width:100%; height:100%; object-fit:{{objectFit}};
                  filter:{{visualFilter}};
                  user-select:none; pointer-events:none;
                }
              </style>
            </head>
            <body>{{media}}</body>
            </html>
            """;
    }

    private static string BuildCssFilter(PlaybackOptions options)
    {
        var brightness = Math.Clamp(
            options.Brightness,
            0.25,
            1.25);
        var contrast = Math.Clamp(
            options.Contrast,
            0.5,
            1.5);
        var saturation = Math.Clamp(
            options.Saturation,
            0,
            1.5);
        var culture =
            System.Globalization.CultureInfo.InvariantCulture;
        return
            $"brightness({brightness.ToString("0.###", culture)}) " +
            $"contrast({contrast.ToString("0.###", culture)}) " +
            $"saturate({saturation.ToString("0.###", culture)})";
    }

    private sealed record MediaReadyProbe(
        bool Found,
        int ReadyState,
        bool Paused,
        int ErrorCode,
        int Width);
}

internal enum WallpaperRenderBackend
{
    None,
    NativeImage,
    NativeGif,
    NativeVideo,
    WebView
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
