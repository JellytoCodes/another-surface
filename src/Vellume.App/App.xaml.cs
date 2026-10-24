using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\Vellume.Desktop.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _playPauseMenuItem;
    private bool _ownsMutex;
    private bool _isExiting;
    private FileAppLogger? _logger;
    private ISettingsStore? _settingsStore;
    private IWallpaperController? _wallpaperController;
    private ISystemShortcutService? _systemShortcutService;
    private WindowsSystemEventMonitor? _systemEventMonitor;
    private AppSettings? _settings;
    private MainWindow? _mainWindow;
    private DockWindow? _dockWindow;
    private TaskbarVisibilityController? _taskbarController;
    private Process? _sessionRecoveryGuard;
    private string? _sessionMarkerPath;
    private string? _crashLogPath;
    private DispatcherTimer? _taskbarMaintainTimer;
    private DispatcherTimer? _previewTimer;
    private bool _skipSettingsSave;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        var isWallpaperRenderHost = eventArgs.Args.Contains(
            "--wallpaper-render-host",
            StringComparer.OrdinalIgnoreCase);
        if (!isWallpaperRenderHost)
        {
            // Keep the long-lived settings, Dock and widget shell off the GPU.
            // The disposable render-host child retains hardware acceleration.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        base.OnStartup(eventArgs);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (eventArgs.Args.Contains(
                "--install-codex-bridge",
                StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await CodexCompanionBridgeMaintenance
                    .InstallForCurrentUserAsync();
                Shutdown();
            }
            catch
            {
                Shutdown(4);
            }
            return;
        }

        if (isWallpaperRenderHost)
        {
            var pipeName = GetStringArgument(
                eventArgs.Args,
                "--render-pipe");
            var dataDirectory = GetStringArgument(
                eventArgs.Args,
                "--render-data");
            var ownerProcessId =
                GetIntegerArgument(
                    eventArgs.Args,
                    "--render-owner") ?? 0;
            if (string.IsNullOrWhiteSpace(pipeName) ||
                string.IsNullOrWhiteSpace(dataDirectory))
            {
                Shutdown(2);
                return;
            }

            try
            {
                await WallpaperRenderHostServer.RunAsync(
                    pipeName,
                    dataDirectory,
                    ownerProcessId);
                Shutdown();
            }
            catch (Exception exception)
            {
                try
                {
                    var applicationDataDirectory =
                        Path.GetDirectoryName(dataDirectory) ??
                        dataDirectory;
                    var logDirectory = Path.Combine(
                        applicationDataDirectory,
                        "Logs");
                    Directory.CreateDirectory(logDirectory);
                    File.AppendAllText(
                        Path.Combine(
                            logDirectory,
                            "render-host.log"),
                        $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
                }
                catch
                {
                    // The parent observes the pipe failure even when the
                    // emergency local log cannot be written.
                }
                Shutdown(3);
            }
            return;
        }

        if (TryGetSessionGuardArguments(
                eventArgs.Args,
                out var sessionOwnerProcessId,
                out var sessionMarkerPath,
                out var recoveryAttempt))
        {
            await Task.Run(() =>
                SessionRecoveryGuard.Run(
                    sessionOwnerProcessId,
                    sessionMarkerPath,
                    recoveryAttempt));
            Shutdown();
            return;
        }

        var isNativeVideoSmokeTest = eventArgs.Args.Contains(
            "--smoke-test-active-video",
            StringComparer.OrdinalIgnoreCase);
        var isSmokeTest = isNativeVideoSmokeTest ||
            eventArgs.Args.Contains(
                "--smoke-test",
                StringComparer.OrdinalIgnoreCase);
        var isDockPreview = eventArgs.Args.Contains(
            "--dock-preview",
            StringComparer.OrdinalIgnoreCase);
        var isRecoveredSession = eventArgs.Args.Contains(
            "--recovered-session",
            StringComparer.OrdinalIgnoreCase);
        var isWindowsStartup = eventArgs.Args.Contains(
            "--windows-startup",
            StringComparer.OrdinalIgnoreCase);
        var mutexName = isSmokeTest
            ? MutexName + ".SmokeTest"
            : isDockPreview
                ? MutexName + ".DockPreview"
                : MutexName;
        _singleInstanceMutex = new Mutex(true, mutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            System.Windows.MessageBox.Show(
                "Vellume이 이미 실행 중입니다. 시스템 트레이에서 열어주세요.",
                "Vellume",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();
        _logger = new FileAppLogger(paths.LogFile);
        _crashLogPath = Path.Combine(paths.LogDirectory, "crash.log");
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        LocalPerformanceDiagnostics.Initialize(_logger);
        await _logger.InfoAsync(
            $"Vellume {typeof(App).Assembly.GetName().Version?.ToString(3)}을 시작했습니다. " +
            "상주 제어부는 WPF 소프트웨어 렌더링을 사용하고 월페이퍼는 별도 프로세스로 재생합니다.");
        _settingsStore = new JsonSettingsStore(paths.SettingsFile);
        if (!isSmokeTest && !isDockPreview)
        {
            _sessionMarkerPath = paths.SessionMarkerFile;
            if (File.Exists(_sessionMarkerPath))
                await _logger.InfoAsync("이전 세션의 비정상 종료 표식이 남아 있습니다.");
            SessionRecoveryGuard.MarkActive(
                _sessionMarkerPath,
                Environment.ProcessId);
            _sessionRecoveryGuard = SessionRecoveryGuard.Start(
                Environment.ProcessId,
                _sessionMarkerPath,
                isRecoveredSession ? 1 : 0);
            if (_sessionRecoveryGuard is null)
            {
                await _logger.InfoAsync(
                    "비정상 종료 복구 가드를 시작하지 못했습니다.");
            }
            else if (isRecoveredSession)
            {
                await _logger.InfoAsync(
                    "비정상 종료를 감지해 마지막 세션을 한 번 자동 복구합니다.");
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var monitorService = new WindowsMonitorService();
            var systemShortcutService =
                new WindowsSystemShortcutService();
            _systemShortcutService = systemShortcutService;
            _systemEventMonitor =
                new WindowsSystemEventMonitor(Dispatcher);
            _systemEventMonitor.DesktopShellChanged +=
                SystemEventMonitor_DesktopShellChanged;
            await _logger.InfoAsync(
                "Windows 이벤트 모니터를 시작했습니다. " +
                $"휴지통 Shell 알림=" +
                $"{(_systemEventMonitor.RecycleBinEventsAvailable ? "사용" : "watchdog 폴백")}.");
            _wallpaperController = new MultiDisplayWallpaperController(
                paths.RenderDataDirectory,
                _logger,
                _systemEventMonitor,
                monitorService);
            _wallpaperController.StateChanged += OnWallpaperStateChanged;

            if (isDockPreview)
            {
                _skipSettingsSave = true;
                _settings = await LoadSettingsSafelyAsync();
                _settings.Dock = _settings.Dock.Copy();
                _settings.Dock.IsEnabled = true;
                _taskbarController =
                    new TaskbarVisibilityController();
                _dockWindow = new DockWindow(
                    _settings,
                    systemShortcutService,
                    _logger,
                    _systemEventMonitor);
                _dockWindow.ExitRequested += async (_, _) =>
                    await ExitApplicationAsync();
                _dockWindow.ApplySettings(_settings);
                var previewSeconds =
                    GetIntegerArgument(
                        eventArgs.Args,
                        "--preview-seconds") ?? 15;
                _previewTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(
                        Math.Clamp(previewSeconds, 3, 120))
                };
                _previewTimer.Tick += async (_, _) =>
                {
                    _previewTimer?.Stop();
                    await ExitApplicationAsync();
                };
                _previewTimer.Start();
                await _logger.InfoAsync(
                    "Vellume Dock 프리뷰를 시작했습니다.");
                return;
            }

            if (isSmokeTest)
            {
                var display = monitorService.GetDisplays()
                    .FirstOrDefault(item => item.IsPrimary)
                    ?? throw new InvalidOperationException(
                        "스모크 테스트에서 기본 모니터를 찾지 못했습니다.");
                var persisted = await LoadSettingsSafelyAsync();
                var primaryProfile =
                    persisted.FindDisplayProfile(display.Id);
                var wallpaperCandidates =
                    new[]
                    {
                        primaryProfile?.Wallpaper,
                        persisted.ActiveWallpaper
                    }
                    .Concat(
                        persisted.DisplayWallpapers
                            .Select(profile => profile.Wallpaper))
                    .Concat(persisted.Library)
                    .Where(item =>
                        item is not null &&
                        !item.IsBuiltIn &&
                        IsWallpaperSourceAvailable(item))
                    .Cast<WallpaperItem>();
                if (isNativeVideoSmokeTest)
                {
                    wallpaperCandidates = wallpaperCandidates.Where(
                        item => item.Kind == WallpaperKind.Video);
                }

                var smokeWallpaper = wallpaperCandidates.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        isNativeVideoSmokeTest
                            ? "저장된 사용자 영상 월페이퍼를 찾을 수 없습니다."
                            : "스모크 테스트에 사용할 저장된 사용자 월페이퍼가 없습니다.");
                var matchingProfile = persisted.DisplayWallpapers
                    .FirstOrDefault(profile =>
                        profile.Wallpaper is not null &&
                        profile.Wallpaper.SourcePath.Equals(
                            smokeWallpaper.SourcePath,
                            StringComparison.OrdinalIgnoreCase));
                var smokePlayback =
                    (matchingProfile?.Playback ?? persisted.Playback).Copy();
                smokePlayback.PauseWhenForegroundAppIsMaximized = false;
                smokePlayback.ApplicationRules = [];
                var diagnosticCapture =
                    Environment.GetEnvironmentVariable(
                        "VELLUME_DIAGNOSTIC_CAPTURE") == "1";
                var smokeWidgets = persisted.Widgets.Copy();

                await _wallpaperController.ApplyAsync(
                    smokeWallpaper,
                    display,
                    smokePlayback,
                    smokeWidgets);
                await Task.Delay(TimeSpan.FromSeconds(5));
                await _wallpaperController.StopAsync();
                await _logger.InfoAsync(
                    "Smoke test applied and stopped the persisted user wallpaper successfully.");
                if (diagnosticCapture)
                {
                    await _logger.InfoAsync(
                        "Diagnostic pause: the wallpaper is stopped while the independent bounded widget windows remain active.");
                    await Task.Delay(TimeSpan.FromSeconds(4));
                }
                await ExitApplicationAsync();
                return;
            }

            _settings = await LoadSettingsSafelyAsync();
            var startupRegistrationService =
                new WindowsStartupRegistrationService();
            try
            {
                startupRegistrationService.SetEnabled(
                    _settings.LaunchAtWindowsLogin);
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync(
                    "Windows 자동 실행 등록을 동기화하지 못했습니다.",
                    exception);
            }
            _mainWindow = new MainWindow(
                _settings,
                _settingsStore,
                _wallpaperController,
                monitorService,
                systemShortcutService,
                new WallpaperLibraryStore(paths.LibraryDirectory),
                _logger,
                _systemEventMonitor,
                startupRegistrationService);
            _mainWindow.DockSettingsChanged +=
                MainWindow_DockSettingsChanged;
            MainWindow = _mainWindow;
            _taskbarController = new TaskbarVisibilityController();
            ApplyTaskbarVisibility();
            CreateDockWindow();
            CreateTrayIcon();
            if (isWindowsStartup)
            {
                _mainWindow.ShowActivated = false;
                _mainWindow.ShowInTaskbar = false;
                _mainWindow.Opacity = 0;
                _mainWindow.InitializationCompleted +=
                    MainWindow_BackgroundStartupCompleted;
            }
            _mainWindow.Show();
            if (!isWindowsStartup)
            {
                ApplyDockSettings();
            }
            await _logger.InfoAsync("Vellume을 시작했습니다.");
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("Vellume 시작에 실패했습니다.", exception);
            System.Windows.MessageBox.Show(
                exception.Message,
                "Vellume을 시작할 수 없음",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void MainWindow_BackgroundStartupCompleted(
        object? sender,
        EventArgs eventArgs)
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.InitializationCompleted -=
            MainWindow_BackgroundStartupCompleted;
        _mainWindow.Hide();
        _mainWindow.Opacity = 1;
        _mainWindow.ShowActivated = true;
        _mainWindow.ShowInTaskbar = true;
        ApplyDockSettings();
    }

    public async Task TogglePlaybackAsync()
    {
        if (_wallpaperController is null)
        {
            return;
        }

        try
        {
            if (_wallpaperController.State == WallpaperPlaybackState.Paused)
            {
                await _wallpaperController.ResumeAsync();
            }
            else if (_wallpaperController.State == WallpaperPlaybackState.Playing)
            {
                await _wallpaperController.PauseAsync();
            }
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                await _logger.ErrorAsync("트레이 재생 명령에 실패했습니다.", exception);
            }
        }
    }

    public Task RequestExitAsync() => ExitApplicationAsync();

    private static int? GetIntegerArgument(
        IReadOnlyList<string> arguments,
        string argumentName)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index].Equals(
                    argumentName,
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arguments[index + 1], out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetStringArgument(
        IReadOnlyList<string> arguments,
        string argumentName)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index].Equals(
                    argumentName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static bool TryGetSessionGuardArguments(
        IReadOnlyList<string> arguments,
        out int ownerProcessId,
        out string markerPath,
        out int recoveryAttempt)
    {
        ownerProcessId =
            GetIntegerArgument(arguments, "--session-guard") ?? 0;
        markerPath =
            GetStringArgument(arguments, "--session-marker") ??
            string.Empty;
        recoveryAttempt =
            GetIntegerArgument(arguments, "--recovery-attempt") ?? 0;
        return ownerProcessId > 0 &&
               !string.IsNullOrWhiteSpace(markerPath);
    }

    private void MainWindow_DockSettingsChanged(
        object? sender,
        EventArgs eventArgs)
        => ApplyDockSettings();

    private void CreateDockWindow()
    {
        if (_dockWindow is not null ||
            _settings is null ||
            _systemShortcutService is null ||
            _logger is null ||
            _systemEventMonitor is null ||
            _isExiting)
        {
            return;
        }

        _dockWindow = new DockWindow(
            _settings,
            _systemShortcutService,
            _logger,
            _systemEventMonitor);
        _dockWindow.ShowMainWindowRequested +=
            DockWindow_ShowMainWindowRequested;
        _dockWindow.ExitRequested += DockWindow_ExitRequested;
        _dockWindow.SettingsChanged += DockWindow_SettingsChanged;
    }

    private void ReleaseDockWindow()
    {
        if (_dockWindow is null)
        {
            return;
        }

        _dockWindow.ShowMainWindowRequested -=
            DockWindow_ShowMainWindowRequested;
        _dockWindow.ExitRequested -= DockWindow_ExitRequested;
        _dockWindow.SettingsChanged -= DockWindow_SettingsChanged;
        _dockWindow.Dispose();
        _dockWindow = null;
    }

    private void DockWindow_ShowMainWindowRequested(
        object? sender,
        EventArgs eventArgs)
        => ShowMainWindow();

    private async void DockWindow_ExitRequested(
        object? sender,
        EventArgs eventArgs)
        => await ExitApplicationAsync();

    private async void DockWindow_SettingsChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (_skipSettingsSave ||
            _settings is null ||
            _settingsStore is null)
        {
            return;
        }

        try
        {
            await _settingsStore.SaveAsync(_settings);
            _mainWindow?.SynchronizeDockItems();
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                await _logger.ErrorAsync(
                    "Dock 고정 항목 저장에 실패했습니다.",
                    exception);
            }
        }
    }

    private void ApplyDockSettings()
    {
        if (_settings is null ||
            _dockWindow is null ||
            _taskbarController is null)
        {
            return;
        }

        _settings.Dock = (_settings.Dock ?? new DockSettings()).Copy();
        _dockWindow.ApplySettings(_settings);
        ApplyTaskbarVisibility();
    }

    private void ApplyTaskbarVisibility()
    {
        if (_taskbarController is null ||
            _settings is null ||
            _isExiting)
        {
            return;
        }

        var hideTaskbar = !_settings.Dock.ShowWindowsTaskbar;
        _taskbarController.SetHidden(hideTaskbar);
        if (!hideTaskbar)
        {
            _taskbarMaintainTimer?.Stop();
            return;
        }

        _taskbarMaintainTimer ??= new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _taskbarMaintainTimer.Tick -=
            TaskbarMaintainTimer_Tick;
        _taskbarMaintainTimer.Tick +=
            TaskbarMaintainTimer_Tick;
        _taskbarMaintainTimer.Start();
    }

    private void TaskbarMaintainTimer_Tick(
        object? sender,
        EventArgs eventArgs)
        => _taskbarController?.Maintain();

    private void SystemEventMonitor_DesktopShellChanged(
        object? sender,
        EventArgs eventArgs)
    {
        _taskbarController?.Maintain();
        ApplyDockSettings();
    }

    private async Task<AppSettings> LoadSettingsSafelyAsync()
    {
        try
        {
            return await _settingsStore!.LoadAsync();
        }
        catch (Exception exception)
        {
            await _logger!.ErrorAsync(
                "설정을 불러오지 못해 기본값을 사용합니다.",
                exception);
            return new AppSettings();
        }
    }

    private void CreateTrayIcon()
    {
        // Clone the chosen small frame so the owned GDI icon outlives the resource stream.
        using (var stream = GetResourceStream(new Uri(
                   "pack://application:,,,/Assets/Brand/vellume-app.ico")).Stream)
        using (var sourceIcon = new System.Drawing.Icon(
                   stream, Forms.SystemInformation.SmallIconSize))
        {
            _trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Vellume 열기", null, (_, _) =>
            Dispatcher.Invoke(ShowMainWindow));
        _playPauseMenuItem = new Forms.ToolStripMenuItem(
            "재생 / 일시정지",
            null,
            async (_, _) => await Dispatcher.InvokeAsync(TogglePlaybackAsync).Task.Unwrap());
        menu.Items.Add(_playPauseMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, async (_, _) =>
            await Dispatcher.InvokeAsync(ExitApplicationAsync).Task.Unwrap());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Vellume",
            Icon = _trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void OnWallpaperStateChanged(
        object? sender,
        WallpaperStateChangedEventArgs eventArgs)
    {
        if (_playPauseMenuItem is not null)
        {
            _playPauseMenuItem.Text = eventArgs.State switch
            {
                WallpaperPlaybackState.Playing => "월페이퍼 일시정지",
                WallpaperPlaybackState.Paused => "월페이퍼 다시 재생",
                _ => "재생 / 일시정지"
            };
            _playPauseMenuItem.Enabled =
                eventArgs.State is WallpaperPlaybackState.Playing or
                    WallpaperPlaybackState.Paused;
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        var controllerDisposedCleanly = true;
        _previewTimer?.Stop();
        _taskbarMaintainTimer?.Stop();
        _taskbarController?.Restore();
        if (_mainWindow is not null)
        {
            _mainWindow.DockSettingsChanged -=
                MainWindow_DockSettingsChanged;
            _mainWindow.InitializationCompleted -=
                MainWindow_BackgroundStartupCompleted;
        }
        ReleaseDockWindow();
        _taskbarController?.Dispose();
        _taskbarController = null;
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.PrepareForExit();
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (!_skipSettingsSave &&
            _settings is not null &&
            _settingsStore is not null)
        {
            try
            {
                await _settingsStore.SaveAsync(_settings);
            }
            catch (Exception exception)
            {
                if (_logger is not null)
                {
                    await _logger.ErrorAsync("마지막 설정 저장에 실패했습니다.", exception);
                }
            }
        }

        if (_wallpaperController is not null)
        {
            _wallpaperController.StateChanged -= OnWallpaperStateChanged;
            try
            {
                await _wallpaperController
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (Exception exception)
            {
                controllerDisposedCleanly = false;
                if (_logger is not null)
                {
                    await _logger.ErrorAsync(
                        "월페이퍼 정리를 완료하지 못했지만 Vellume 종료를 계속합니다.",
                        exception);
                }
            }
            _wallpaperController = null;
        }
        if (_systemEventMonitor is not null)
        {
            _systemEventMonitor.DesktopShellChanged -=
                SystemEventMonitor_DesktopShellChanged;
            _systemEventMonitor.Dispose();
        }
        _systemEventMonitor = null;

        if (controllerDisposedCleanly &&
            !string.IsNullOrWhiteSpace(_sessionMarkerPath))
        {
            SessionRecoveryGuard.MarkCleanExit(
                _sessionMarkerPath,
                Environment.ProcessId);
            _sessionMarkerPath = null;
        }
        else if (!controllerDisposedCleanly &&
                 _logger is not null)
        {
            await _logger.InfoAsync(
                "정리 실패를 감지해 다음 실행의 데스크톱 복구 마커를 유지합니다.");
        }
        _sessionRecoveryGuard?.Dispose();
        _sessionRecoveryGuard = null;

        if (_logger is not null)
        {
            await LocalPerformanceDiagnostics.FlushAsync();
            await _logger.InfoAsync("Vellume을 안전하게 종료했습니다.");
            _logger.Dispose();
        }

        _mainWindow?.Close();
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        Shutdown();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        // A background-thread exception can terminate the process without
        // reaching WPF's dispatcher handler. Do not depend on an async logger.
        try
        {
            if (_crashLogPath is not null)
                File.AppendAllText(_crashLogPath,
                    $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} " +
                    $"terminating={args.IsTerminating}{Environment.NewLine}{args.ExceptionObject}{Environment.NewLine}");
        }
        catch { } // Emergency logging must not throw during process termination.
    }

    private async void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (_logger is not null)
        {
            await _logger.ErrorAsync("처리되지 않은 UI 오류입니다.", eventArgs.Exception);
        }

        System.Windows.MessageBox.Show(
            eventArgs.Exception.Message,
            "Vellume 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs eventArgs)
    {
        eventArgs.SetObserved();
        if (_logger is not null)
        {
            await _logger.ErrorAsync(
                "확인되지 않은 백그라운드 작업 오류입니다.",
                eventArgs.Exception);
        }
    }

    private static bool IsWallpaperSourceAvailable(WallpaperItem item)
        => item.Kind != WallpaperKind.Web &&
           !string.IsNullOrWhiteSpace(item.SourcePath) && File.Exists(item.SourcePath);
}
