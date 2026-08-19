using System.IO;
using System.Text;
using Vellume.Core;

namespace Vellume.Windows;

/// <summary>
/// Keeps one primary-display controller for all desktop widgets and adds
/// independent wallpaper renderer processes for secondary displays.
/// </summary>
public sealed class MultiDisplayWallpaperController :
    IWallpaperController,
    IAsyncDisposable
{
    private sealed class SecondarySession
    {
        public required DisplayInfo Display { get; init; }
        public required WallpaperItem Wallpaper { get; init; }
        public required PlaybackOptions Playback { get; set; }
        public required IWallpaperRenderer Renderer { get; set; }
        public nint HostHandle { get; set; }
        public bool IsPaused { get; set; }
    }

    private readonly WallpaperController _primaryController;
    private readonly IMonitorService _monitorService;
    private readonly WindowsSystemEventMonitor _systemEventMonitor;
    private readonly IAppLogger _logger;
    private readonly string _webViewDataDirectory;
    private readonly DesktopHostLocator _hostLocator = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, SecondarySession> _secondarySessions =
        new(StringComparer.OrdinalIgnoreCase);
    private DisplayInfo? _primaryDisplay;
    private string? _selectedDisplayId;
    private bool _manualPauseRequested;
    private ApplicationPlaybackAction _automaticAction;
    private bool _disposed;

    public MultiDisplayWallpaperController(
        string webViewDataDirectory,
        IAppLogger logger,
        ISystemShortcutService systemShortcutService,
        WindowsSystemEventMonitor systemEventMonitor,
        IMonitorService monitorService)
    {
        _webViewDataDirectory = webViewDataDirectory;
        _logger = logger;
        _monitorService = monitorService;
        _systemEventMonitor = systemEventMonitor;
        _primaryController = new WallpaperController(
            webViewDataDirectory,
            logger,
            systemShortcutService,
            systemEventMonitor);

        _primaryDisplay = TryGetPrimaryDisplay();
        _primaryController.StateChanged += PrimaryController_StateChanged;
        _primaryController.AutomaticPlaybackActionChanged +=
            PrimaryController_AutomaticPlaybackActionChanged;
        _primaryController.QuickSlotInvoked +=
            PrimaryController_QuickSlotInvoked;
        _primaryController.QuickSlotSecondaryInvoked +=
            PrimaryController_QuickSlotSecondaryInvoked;
        _primaryController.QuickSlotLayoutChanged +=
            PrimaryController_QuickSlotLayoutChanged;
        _systemEventMonitor.DesktopShellChanged +=
            SystemEventMonitor_DesktopShellChanged;
    }

    public WallpaperPlaybackState State { get; private set; }

    public event EventHandler<WallpaperStateChangedEventArgs>? StateChanged;
    public event EventHandler<int>? QuickSlotInvoked;
    public event EventHandler<int>? QuickSlotSecondaryInvoked;
    public event EventHandler<QuickSlotLayoutChangedEventArgs>?
        QuickSlotLayoutChanged;

    public async Task ApplyAsync(
        WallpaperItem wallpaper,
        DisplayInfo display,
        PlaybackOptions options,
        WidgetSettings widgets,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _selectedDisplayId = display.Id;

        if (display.IsPrimary)
        {
            _primaryDisplay = display;
            await _primaryController.ApplyAsync(
                wallpaper,
                display,
                options,
                widgets,
                cancellationToken);
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        IWallpaperRenderer? replacement = null;
        try
        {
            WallpaperValidator.DetectKind(wallpaper.SourcePath);
            var hostHandle = FindWallpaperHostOrThrow();
            replacement = WallpaperRendererFactory.Create(
                GetRendererDataDirectory(display.Id));
            await replacement.SetWindowVisibleAsync(
                false,
                cancellationToken);
            await AttachRendererAsync(
                replacement,
                hostHandle,
                display,
                cancellationToken);
            await replacement.InitializeAndNavigateAsync(
                wallpaper,
                options,
                cancellationToken);

            if (_manualPauseRequested ||
                _automaticAction !=
                    ApplicationPlaybackAction.KeepRunning)
            {
                await replacement.PauseAsync();
            }

            if (_secondarySessions.Remove(
                    display.Id,
                    out var previous))
            {
                await previous.Renderer.CloseSafelyAsync();
            }

            _secondarySessions[display.Id] = new SecondarySession
            {
                Display = display,
                Wallpaper = wallpaper.Copy(),
                Playback = options.Copy(),
                Renderer = replacement,
                HostHandle = hostHandle,
                IsPaused = _manualPauseRequested
            };
            await replacement.SetWindowVisibleAsync(
                true,
                cancellationToken);
            replacement = null;

            SetState(
                _manualPauseRequested ||
                _automaticAction !=
                    ApplicationPlaybackAction.KeepRunning
                    ? WallpaperPlaybackState.Paused
                    : WallpaperPlaybackState.Playing,
                $"{display.Name}에 {wallpaper.Name} 적용 완료");
            await _logger.InfoAsync(
                $"Applied '{wallpaper.SourcePath}' to secondary display " +
                $"{display.Id}. Active display renderers: " +
                $"{_secondarySessions.Count + 1}.");
        }
        catch (Exception exception)
        {
            if (replacement is not null)
            {
                await replacement.CloseSafelyAsync();
            }
            SetState(WallpaperPlaybackState.Error, exception.Message);
            await _logger.ErrorAsync(
                $"보조 모니터 {display.Id} 월페이퍼 적용에 실패했습니다.",
                exception);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _primaryController.StopAsync(cancellationToken);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var session in _secondarySessions.Values.ToArray())
            {
                await session.Renderer.CloseSafelyAsync();
            }
            _secondarySessions.Clear();
            _manualPauseRequested = false;
            SetState(
                WallpaperPlaybackState.Stopped,
                "모든 모니터의 월페이퍼를 중지했습니다.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimaryDisplay(displayId))
        {
            await _primaryController.StopDisplayAsync(
                displayId,
                cancellationToken);
        }
        else
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                if (_secondarySessions.Remove(
                        displayId,
                        out var session))
                {
                    await session.Renderer.CloseSafelyAsync();
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        var anyWallpaper = _secondarySessions.Count > 0 ||
                           _primaryController.State is
                               WallpaperPlaybackState.Playing or
                               WallpaperPlaybackState.Paused;
        SetState(
            anyWallpaper
                ? (_manualPauseRequested ||
                   _automaticAction !=
                       ApplicationPlaybackAction.KeepRunning
                    ? WallpaperPlaybackState.Paused
                    : WallpaperPlaybackState.Playing)
                : WallpaperPlaybackState.Stopped,
            anyWallpaper
                ? "선택한 모니터의 월페이퍼를 중지했습니다."
                : "모든 월페이퍼가 중지되었습니다.");
    }

    public async Task PauseAsync(
        CancellationToken cancellationToken = default)
    {
        _manualPauseRequested = true;
        await _primaryController.PauseAsync(cancellationToken);
        await ForEachSecondaryRendererAsync(
            async session =>
            {
                session.IsPaused = true;
                await session.Renderer.PauseAsync();
            },
            cancellationToken);
        SetState(WallpaperPlaybackState.Paused, "모든 월페이퍼 일시정지");
    }

    public async Task ResumeAsync(
        CancellationToken cancellationToken = default)
    {
        _manualPauseRequested = false;
        await _primaryController.ResumeAsync(cancellationToken);
        await ForEachSecondaryRendererAsync(
            async session =>
            {
                var renderer = session.Renderer;
                if (_automaticAction !=
                    ApplicationPlaybackAction.KeepRunning)
                {
                    await renderer.PauseAsync();
                }
                else
                {
                    await renderer.ResumeAsync();
                }
                session.IsPaused = false;
            },
            cancellationToken);
        SetState(
            _automaticAction ==
                ApplicationPlaybackAction.KeepRunning
                ? WallpaperPlaybackState.Playing
                : WallpaperPlaybackState.Paused,
            _automaticAction ==
                ApplicationPlaybackAction.KeepRunning
                ? "모든 월페이퍼 재생 중"
                : "자동 전체화면 정지 상태 유지");
    }

    public Task UpdatePlaybackAsync(
        PlaybackOptions options,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(_selectedDisplayId)
            ? _primaryController.UpdatePlaybackAsync(
                options,
                cancellationToken)
            : UpdateDisplayPlaybackAsync(
                _selectedDisplayId,
                options,
                cancellationToken);

    public async Task PauseDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimaryDisplay(displayId))
        {
            await _primaryController.PauseDisplayAsync(
                displayId,
                cancellationToken);
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_secondarySessions.TryGetValue(
                    displayId,
                    out var session))
            {
                session.IsPaused = true;
                await session.Renderer.PauseAsync();
                SetState(
                    WallpaperPlaybackState.Paused,
                    $"{session.Display.Name} 월페이퍼 일시정지");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ResumeDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default)
    {
        if (IsPrimaryDisplay(displayId))
        {
            await _primaryController.ResumeDisplayAsync(
                displayId,
                cancellationToken);
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_secondarySessions.TryGetValue(
                    displayId,
                    out var session))
            {
                session.IsPaused = false;
                if (_manualPauseRequested ||
                    _automaticAction !=
                        ApplicationPlaybackAction.KeepRunning)
                {
                    return;
                }
                await session.Renderer.ResumeAsync();
                SetState(
                    WallpaperPlaybackState.Playing,
                    $"{session.Display.Name} 월페이퍼 재생 중");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task UpdateDisplayPlaybackAsync(
        string displayId,
        PlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        _selectedDisplayId = displayId;
        if (IsPrimaryDisplay(displayId))
        {
            await _primaryController.UpdateDisplayPlaybackAsync(
                displayId,
                options,
                cancellationToken);
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_secondarySessions.TryGetValue(
                    displayId,
                    out var session))
            {
                session.Playback = options.Copy();
                await session.Renderer.UpdatePlaybackAsync(
                    session.Playback);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task UpdateWidgetsAsync(
        WidgetSettings widgets,
        DisplayInfo? display = null,
        CancellationToken cancellationToken = default)
    {
        if (display?.IsPrimary == true)
        {
            _primaryDisplay = display;
        }

        return _primaryController.UpdateWidgetsAsync(
            widgets,
            _primaryDisplay ?? TryGetPrimaryDisplay(),
            cancellationToken);
    }

    public Task UpdateQuickSlotRuntimeAsync(
        WidgetSettings widgets,
        CancellationToken cancellationToken = default)
        => _primaryController.UpdateQuickSlotRuntimeAsync(
            widgets,
            cancellationToken);

    public Task<string> InstallCodexCompanionBridgeAsync(
        CancellationToken cancellationToken = default)
        => _primaryController.InstallCodexCompanionBridgeAsync(
            cancellationToken);

    public Task CompleteDesktopWallpaperHandoffAsync(
        CancellationToken cancellationToken = default)
        => _primaryController.CompleteDesktopWallpaperHandoffAsync(
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _systemEventMonitor.DesktopShellChanged -=
            SystemEventMonitor_DesktopShellChanged;
        _primaryController.StateChanged -= PrimaryController_StateChanged;
        _primaryController.AutomaticPlaybackActionChanged -=
            PrimaryController_AutomaticPlaybackActionChanged;
        _primaryController.QuickSlotInvoked -=
            PrimaryController_QuickSlotInvoked;
        _primaryController.QuickSlotSecondaryInvoked -=
            PrimaryController_QuickSlotSecondaryInvoked;
        _primaryController.QuickSlotLayoutChanged -=
            PrimaryController_QuickSlotLayoutChanged;

        await _operationLock.WaitAsync();
        try
        {
            foreach (var session in _secondarySessions.Values.ToArray())
            {
                await session.Renderer.CloseSafelyAsync();
            }
            _secondarySessions.Clear();
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }

        await _primaryController.DisposeAsync();
    }

    private async Task ForEachSecondaryRendererAsync(
        Func<SecondarySession, Task> operation,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var session in _secondarySessions.Values)
            {
                await operation(session);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async void SystemEventMonitor_DesktopShellChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (_disposed || !await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var hostHandle = _hostLocator.FindWallpaperHost();
            if (hostHandle == nint.Zero)
            {
                return;
            }

            foreach (var pair in _secondarySessions.ToArray())
            {
                var session = pair.Value;
                if (session.Renderer.IsAttachedTo(hostHandle))
                {
                    session.HostHandle = hostHandle;
                    continue;
                }

                if (session.Renderer.IsReady)
                {
                    await AttachRendererAsync(
                        session.Renderer,
                        hostHandle,
                        session.Display,
                        CancellationToken.None);
                }
                else
                {
                    await session.Renderer.CloseSafelyAsync();
                    var replacement = WallpaperRendererFactory.Create(
                        GetRendererDataDirectory(session.Display.Id));
                    await replacement.SetWindowVisibleAsync(
                        false,
                        CancellationToken.None);
                    await AttachRendererAsync(
                        replacement,
                        hostHandle,
                        session.Display,
                        CancellationToken.None);
                    await replacement.InitializeAndNavigateAsync(
                        session.Wallpaper,
                        session.Playback,
                        CancellationToken.None);
                    if (_manualPauseRequested ||
                        session.IsPaused ||
                        _automaticAction !=
                            ApplicationPlaybackAction.KeepRunning)
                    {
                        await replacement.PauseAsync();
                    }
                    await replacement.SetWindowVisibleAsync(
                        true,
                        CancellationToken.None);
                    session.Renderer = replacement;
                }

                session.HostHandle = hostHandle;
            }

            await _logger.InfoAsync(
                "Explorer 변경 후 보조 모니터 월페이퍼를 다시 연결했습니다.");
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                "보조 모니터 월페이퍼 재연결에 실패했습니다.",
                exception);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void PrimaryController_StateChanged(
        object? sender,
        WallpaperStateChangedEventArgs eventArgs)
    {
        var state = eventArgs.State;
        var message = eventArgs.Message;
        if (state == WallpaperPlaybackState.Stopped &&
            _secondarySessions.Count > 0)
        {
            state = _manualPauseRequested ||
                    _automaticAction !=
                        ApplicationPlaybackAction.KeepRunning
                ? WallpaperPlaybackState.Paused
                : WallpaperPlaybackState.Playing;
            message = state == WallpaperPlaybackState.Paused
                ? "보조 모니터 월페이퍼 자동 정지 중"
                : "보조 모니터 월페이퍼 재생 중";
        }
        SetState(state, message);
    }

    private async void PrimaryController_AutomaticPlaybackActionChanged(
        object? sender,
        AutomaticPlaybackActionChangedEventArgs eventArgs)
    {
        _automaticAction = eventArgs.Action;
        if (_disposed)
        {
            return;
        }

        try
        {
            await _operationLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_disposed)
            {
                return;
            }
            foreach (var session in _secondarySessions.Values)
            {
                if (eventArgs.Action !=
                    ApplicationPlaybackAction.KeepRunning)
                {
                    await session.Renderer.PauseAsync();
                    continue;
                }

                if (_manualPauseRequested ||
                    session.IsPaused)
                {
                    continue;
                }

                await session.Renderer.ResumeAsync();
            }

            await _logger.InfoAsync(
                eventArgs.Action ==
                    ApplicationPlaybackAction.KeepRunning
                    ? "자동 정지 해제를 모든 보조 모니터에 반영했습니다."
                    : "자동 정지를 모든 보조 모니터에 반영했습니다.");
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                "보조 모니터 자동 정지 상태 동기화에 실패했습니다.",
                exception);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void PrimaryController_QuickSlotInvoked(
        object? sender,
        int slot)
        => QuickSlotInvoked?.Invoke(this, slot);

    private void PrimaryController_QuickSlotSecondaryInvoked(
        object? sender,
        int slot)
        => QuickSlotSecondaryInvoked?.Invoke(this, slot);

    private void PrimaryController_QuickSlotLayoutChanged(
        object? sender,
        QuickSlotLayoutChangedEventArgs eventArgs)
        => QuickSlotLayoutChanged?.Invoke(this, eventArgs);

    private async Task AttachRendererAsync(
        IWallpaperRenderer renderer,
        nint hostHandle,
        DisplayInfo display,
        CancellationToken cancellationToken)
    {
        await renderer.AttachToAsync(
            hostHandle,
            display,
            _hostLocator.IsRaisedDesktop,
            _hostLocator.ShellViewHandle,
            _hostLocator.BackgroundWorkerHandle,
            cancellationToken);
        await _logger.InfoAsync(
            $"Secondary desktop attachment {display.Id}: " +
            renderer.GetAttachmentDiagnostics());
    }

    private nint FindWallpaperHostOrThrow()
    {
        var hostHandle = _hostLocator.FindWallpaperHost();
        if (hostHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                "Windows 바탕화면 호스트를 찾지 못했습니다. " +
                "Explorer를 다시 시작하지 않고 적용을 중단했습니다.");
        }
        return hostHandle;
    }

    private DisplayInfo? TryGetPrimaryDisplay()
    {
        try
        {
            return _monitorService.GetDisplays().FirstOrDefault(
                display => display.IsPrimary);
        }
        catch
        {
            return null;
        }
    }

    private bool IsPrimaryDisplay(string displayId)
    {
        _primaryDisplay ??= TryGetPrimaryDisplay();
        return _primaryDisplay?.Id.Equals(
                   displayId,
                   StringComparison.OrdinalIgnoreCase) == true;
    }

    private string GetRendererDataDirectory(string displayId)
    {
        var builder = new StringBuilder();
        foreach (var character in displayId)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            if (builder.Length >= 40)
            {
                break;
            }
        }
        if (builder.Length == 0)
        {
            builder.Append("secondary");
        }
        return Path.Combine(
            _webViewDataDirectory,
            "Displays",
            builder.ToString());
    }

    private void SetState(
        WallpaperPlaybackState state,
        string message)
    {
        State = state;
        StateChanged?.Invoke(
            this,
            new WallpaperStateChangedEventArgs(state, message));
    }

}
