using Vellume.Core;

namespace Vellume.Windows;

/// <summary>
/// Prefers the C++ renderer for image/video wallpapers and retains the managed
/// renderer as a compatibility path for GIF, local HTML, or unsupported codecs.
/// </summary>
internal sealed class AdaptiveWallpaperRenderer : IWallpaperRenderer
{
    private readonly string _webViewDataDirectory;
    private readonly Func<IWallpaperRenderer?> _nativeRendererFactory;
    private readonly Func<IWallpaperRenderer> _managedRendererFactory;
    private IWallpaperRenderer? _active;
    private bool _usingNativeRenderer;
    private WallpaperItem? _wallpaper;
    private PlaybackOptions? _options;
    private RenderAttachment? _attachment;
    private bool _visible = true;
    private bool _paused;
    private bool _closed;
    private string _fallbackReason = string.Empty;

    internal AdaptiveWallpaperRenderer(
        string webViewDataDirectory,
        Func<IWallpaperRenderer?>? nativeRendererFactory = null,
        Func<IWallpaperRenderer>? managedRendererFactory = null)
    {
        _webViewDataDirectory = webViewDataDirectory;
        _nativeRendererFactory = nativeRendererFactory ?? (() =>
        {
            var path = NativeWallpaperRendererClient.ResolveExecutablePath();
            return path is null ? null : new NativeWallpaperRendererClient(path);
        });
        _managedRendererFactory = managedRendererFactory ??
            (() => new WallpaperRenderHostClient(_webViewDataDirectory));
    }

    public nint Handle => _active?.Handle ?? nint.Zero;

    public string BackendDisplayName =>
        _active?.BackendDisplayName ?? "렌더러 준비 중";

    public bool IsReady => _active?.IsReady == true;

    public string GetAttachmentDiagnostics()
    {
        var diagnostics = _active?.GetAttachmentDiagnostics() ??
            "backend=adaptive, state=waiting-for-source";
        return string.IsNullOrWhiteSpace(_fallbackReason)
            ? diagnostics
            : $"{diagnostics}, native-fallback={_fallbackReason}";
    }

    public async Task AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _attachment = new RenderAttachment(
            hostHandle,
            display,
            raisedDesktop,
            shellViewHandle,
            backgroundWorkerHandle);
        if (_active is null)
        {
            return;
        }

        await AttachActiveAsync(_active, cancellationToken)
            .ConfigureAwait(false);
        // Geometry-only attachment preserves a live renderer's decoded content.
        // Reload only if its HWND/content was actually lost (for example Explorer restart).
        if (!_active.IsReady && _wallpaper is not null && _options is not null)
        {
            await _active.InitializeAndNavigateAsync(
                    _wallpaper,
                    _options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_paused)
            {
                await _active.PauseAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task InitializeAndNavigateAsync(
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _wallpaper = wallpaper.Copy();
        _options = options.Copy();
        await CloseActiveAsync().ConfigureAwait(false);

        var native = ShouldUseNativeRenderer(wallpaper, options)
            ? _nativeRendererFactory()
            : null;
        if (native is not null)
        {
            try
            {
                await StartRendererAsync(
                        native,
                        wallpaper,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                _active = native;
                _usingNativeRenderer = true;
                _fallbackReason = string.Empty;
                return;
            }
            catch (OperationCanceledException)
            {
                await native.CloseSafelyAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                _fallbackReason = SanitizeDiagnostic(exception.Message);
                await native.CloseSafelyAsync().ConfigureAwait(false);
            }
        }

        var managed = _managedRendererFactory();
        try
        {
            await StartRendererAsync(
                    managed,
                    wallpaper,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            _active = managed;
            _usingNativeRenderer = false;
        }
        catch
        {
            await managed.CloseSafelyAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CapturePreviewAsync(
        string outputPath,
        CancellationToken cancellationToken)
        => GetActive().CapturePreviewAsync(
            outputPath,
            cancellationToken);

    public bool IsAttachedTo(nint hostHandle)
        => _active?.IsAttachedTo(hostHandle) == true;

    public async Task SetWindowVisibleAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        _visible = visible;
        if (_active is not null)
        {
            await _active.SetWindowVisibleAsync(
                    visible,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task PauseAsync()
    {
        _paused = true;
        if (_active is not null)
        {
            await _active.PauseAsync().ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync()
    {
        _paused = false;
        if (_active is not null)
        {
            await _active.ResumeAsync().ConfigureAwait(false);
        }
    }

    public async Task UpdatePlaybackAsync(PlaybackOptions options)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_active is not null && _wallpaper?.Kind == WallpaperKind.Video)
        {
            var wantsNative = ShouldUseNativeRenderer(_wallpaper, options);
            if (_usingNativeRenderer && !wantsNative)
            {
                // The C++ HWND Media Engine path does not apply color filters.
                // Keep the current frame visible until the compatible backend is ready.
                await ReplaceRendererAsync(
                    _managedRendererFactory(), options)
                    .ConfigureAwait(false);
                _options = options.Copy();
                return;
            }
            // Once migrated, keep the compatible process until wallpaper reapply.
            // Its existing WebView/MediaEngine policy handles returning to neutral
            // colors without repeatedly creating C++ and managed child processes.
        }
        if (_active is not null)
        {
            await _active.UpdatePlaybackAsync(options)
                .ConfigureAwait(false);
            if (_paused)
            {
                // The managed backend may internally switch WebView/MediaEngine.
                await _active.PauseAsync().ConfigureAwait(false);
            }
        }
        _options = options.Copy();
    }

    private async Task ReplaceRendererAsync(
        IWallpaperRenderer replacement,
        PlaybackOptions options)
    {
        var previous = GetActive();
        var pausedForSwap = false;
        try
        {
            await AttachRendererAsync(replacement, CancellationToken.None)
                .ConfigureAwait(false);
            await replacement.SetWindowVisibleAsync(false, CancellationToken.None)
                .ConfigureAwait(false);
            var preparationOptions = options.Copy();
            preparationOptions.IsMuted = true;
            await replacement.InitializeAndNavigateAsync(
                _wallpaper!, preparationOptions, CancellationToken.None)
                .ConfigureAwait(false);
            if (_paused)
            {
                await replacement.PauseAsync().ConfigureAwait(false);
            }
            else
            {
                await previous.PauseAsync().ConfigureAwait(false);
                pausedForSwap = true;
            }
            await replacement.UpdatePlaybackAsync(options).ConfigureAwait(false);
            await replacement.SetWindowVisibleAsync(_visible, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            await replacement.CloseSafelyAsync().ConfigureAwait(false);
            if (pausedForSwap)
            {
                await previous.ResumeAsync().ConfigureAwait(false);
            }
            throw;
        }

        _active = replacement;
        _usingNativeRenderer = false;
        // IWallpaperRenderer has no seek contract: backend changes restart the
        // video. Hidden, muted preparation avoids an empty surface/audio overlap,
        // but does not promise frame-perfect continuity or playback-position retention.
        await previous.CloseSafelyAsync().ConfigureAwait(false);
    }

    public async Task CloseSafelyAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await CloseActiveAsync().ConfigureAwait(false);
        _wallpaper = null;
        _options = null;
        _attachment = null;
    }

    private async Task StartRendererAsync(
        IWallpaperRenderer renderer,
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        await AttachRendererAsync(renderer, cancellationToken)
            .ConfigureAwait(false);
        await renderer.SetWindowVisibleAsync(
                _visible,
                cancellationToken)
            .ConfigureAwait(false);
        await renderer.InitializeAndNavigateAsync(
                wallpaper,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        if (_paused)
        {
            await renderer.PauseAsync().ConfigureAwait(false);
        }
    }

    private Task AttachActiveAsync(
        IWallpaperRenderer renderer,
        CancellationToken cancellationToken)
        => AttachRendererAsync(renderer, cancellationToken);

    private Task AttachRendererAsync(
        IWallpaperRenderer renderer,
        CancellationToken cancellationToken)
    {
        var attachment = _attachment ??
            throw new InvalidOperationException(
                "월페이퍼 렌더러 연결 정보가 없습니다.");
        return renderer.AttachToAsync(
            attachment.HostHandle,
            attachment.Display,
            attachment.RaisedDesktop,
            attachment.ShellViewHandle,
            attachment.BackgroundWorkerHandle,
            cancellationToken);
    }

    private async Task CloseActiveAsync()
    {
        var active = _active;
        _active = null;
        _usingNativeRenderer = false;
        if (active is not null)
        {
            await active.CloseSafelyAsync().ConfigureAwait(false);
        }
    }

    private IWallpaperRenderer GetActive()
        => _active ?? throw new InvalidOperationException(
            "월페이퍼 렌더러가 아직 초기화되지 않았습니다.");

    private static string SanitizeDiagnostic(string message)
        => string.Join(
            ' ',
            (message ?? string.Empty)
                .Split(
                    ['\r', '\n', ','],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries))
            .Trim();

    private static bool ShouldUseNativeRenderer(
        WallpaperItem wallpaper,
        PlaybackOptions options)
        => wallpaper.Kind == WallpaperKind.Image ||
           (wallpaper.Kind == WallpaperKind.Video &&
            Math.Abs(options.Brightness - 1) < 0.001 &&
            Math.Abs(options.Contrast - 1) < 0.001 &&
            Math.Abs(options.Saturation - 1) < 0.001);

    private sealed record RenderAttachment(
        nint HostHandle,
        DisplayInfo Display,
        bool RaisedDesktop,
        nint ShellViewHandle,
        nint BackgroundWorkerHandle);
}

internal static class WallpaperRendererFactory
{
    internal static IWallpaperRenderer Create(
        string webViewDataDirectory)
        => new AdaptiveWallpaperRenderer(webViewDataDirectory);
}
