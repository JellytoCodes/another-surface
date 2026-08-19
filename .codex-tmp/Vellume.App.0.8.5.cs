using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Resources;
using System.Windows.Threading;
using Microsoft.Win32;
using Vellume.Core;
using Vellume.Windows;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
[assembly: AssemblyAssociatedContentFile("readme.md")]
[assembly: AssemblyAssociatedContentFile("assets/samplewallpaper/index.html")]
[assembly: AssemblyAssociatedContentFile("assets/launcher/quick-slot-base-1024.png")]
[assembly: AssemblyAssociatedContentFile("assets/companion/vellume-companion.png")]
[assembly: AssemblyAssociatedContentFile("webview2loader.dll")]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("Vellume.App")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("0.8.5.0")]
[assembly: AssemblyInformationalVersion("0.8.5")]
[assembly: AssemblyProduct("Vellume.App")]
[assembly: AssemblyTitle("Vellume.App")]
[assembly: TargetPlatform("Windows10.0.17763.0")]
[assembly: SupportedOSPlatform("Windows10.0.17763.0")]
[assembly: AssemblyVersion("0.8.5.0")]
[module: RefSafetyRules(11)]
namespace Vellume.App;

public class App : System.Windows.Application
{
	private const string MutexName = "Local\\Vellume.Desktop.SingleInstance";

	private Mutex? _singleInstanceMutex;

	private NotifyIcon? _notifyIcon;

	private ToolStripMenuItem? _playPauseMenuItem;

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

	private DispatcherTimer? _taskbarMaintainTimer;

	private DispatcherTimer? _trayRevealTimer;

	private DispatcherTimer? _previewTimer;

	private bool _skipSettingsSave;

	private bool _dockSuspendedForExclusiveGpu;

	private bool _contentLoaded;

	protected override async void OnStartup(StartupEventArgs eventArgs)
	{
		bool num = eventArgs.Args.Contains<string>("--wallpaper-render-host", StringComparer.OrdinalIgnoreCase);
		if (!num)
		{
			RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
		}
		base.OnStartup(eventArgs);
		base.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		if (num)
		{
			string stringArgument = GetStringArgument(eventArgs.Args, "--render-pipe");
			string dataDirectory = GetStringArgument(eventArgs.Args, "--render-data");
			int valueOrDefault = GetIntegerArgument(eventArgs.Args, "--render-owner").GetValueOrDefault();
			if (string.IsNullOrWhiteSpace(stringArgument) || string.IsNullOrWhiteSpace(dataDirectory))
			{
				Shutdown(2);
				return;
			}
			try
			{
				await WallpaperRenderHostServer.RunAsync(stringArgument, dataDirectory, valueOrDefault);
				Shutdown();
				return;
			}
			catch (Exception value)
			{
				try
				{
					string text = Path.Combine(Path.GetDirectoryName(dataDirectory) ?? dataDirectory, "Logs");
					Directory.CreateDirectory(text);
					File.AppendAllText(Path.Combine(text, "render-host.log"), $"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
				}
				catch
				{
				}
				Shutdown(3);
				return;
			}
		}
		if (TryGetTaskbarGuardOwner(eventArgs.Args, out var guardOwnerProcessId))
		{
			await Task.Run(delegate
			{
				TaskbarVisibilityController.RunRestoreGuard(guardOwnerProcessId);
			});
			Shutdown();
			return;
		}
		if (TryGetSessionGuardArguments(eventArgs.Args, out int sessionOwnerProcessId, out string sessionMarkerPath, out int recoveryAttempt))
		{
			await Task.Run(() => SessionRecoveryGuard.Run(sessionOwnerProcessId, sessionMarkerPath, recoveryAttempt));
			Shutdown();
			return;
		}
		int exclusiveGpuGuardOwner = GetIntegerArgument(eventArgs.Args, "--exclusive-gpu-guard").GetValueOrDefault();
		if (exclusiveGpuGuardOwner > 0)
		{
			await Task.Run(() => SessionRecoveryGuard.RunExclusiveGpuGuard(exclusiveGpuGuardOwner));
			Shutdown();
			return;
		}
		bool isSmokeTest = eventArgs.Args.Contains<string>("--smoke-test", StringComparer.OrdinalIgnoreCase);
		bool isDockPreview = eventArgs.Args.Contains<string>("--dock-preview", StringComparer.OrdinalIgnoreCase);
		bool isRecoveredSession = eventArgs.Args.Contains<string>("--recovered-session", StringComparer.OrdinalIgnoreCase);
		bool isResumedAfterExclusiveGpu = eventArgs.Args.Contains<string>("--resumed-after-exclusive-gpu", StringComparer.OrdinalIgnoreCase);
		string name = (isSmokeTest ? "Local\\Vellume.Desktop.SingleInstance.SmokeTest" : (isDockPreview ? "Local\\Vellume.Desktop.SingleInstance.DockPreview" : "Local\\Vellume.Desktop.SingleInstance"));
		_singleInstanceMutex = new Mutex(initiallyOwned: true, name, out _ownsMutex);
		if (!_ownsMutex)
		{
			System.Windows.MessageBox.Show("Vellume이 이미 실행 중입니다. 시스템 트레이에서 열어주세요.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			Shutdown();
			return;
		}
		AppPaths paths = new AppPaths();
		paths.EnsureCreated();
		_logger = new FileAppLogger(paths.LogFile);
		await _logger.InfoAsync("Vellume 0.8.5 상주 제어부를 WPF 소프트웨어 렌더링 모드로 시작했습니다. 앱별 완전 정지 규칙에서는 독립 RenderHost를 종료해 GPU 리소스를 해제하고 필요할 때만 새 호스트로 복원합니다.");
		_settingsStore = new JsonSettingsStore(paths.SettingsFile);
		if (!isSmokeTest && !isDockPreview)
		{
			_sessionMarkerPath = paths.SessionMarkerFile;
			SessionRecoveryGuard.MarkActive(_sessionMarkerPath, Environment.ProcessId);
			_sessionRecoveryGuard = SessionRecoveryGuard.Start(Environment.ProcessId, _sessionMarkerPath, isRecoveredSession ? 1 : 0);
			if (_sessionRecoveryGuard == null)
			{
				await _logger.InfoAsync("비정상 종료 복구 가드를 시작하지 못했습니다.");
			}
			else if (isRecoveredSession)
			{
				await _logger.InfoAsync("비정상 종료를 감지해 마지막 세션을 한 번 자동 복구합니다.");
			}
		}
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		try
		{
			WindowsMonitorService monitorService = new WindowsMonitorService();
			WindowsSystemShortcutService systemShortcutService = (WindowsSystemShortcutService)(_systemShortcutService = new WindowsSystemShortcutService());
			_systemEventMonitor = new WindowsSystemEventMonitor(base.Dispatcher);
			_systemEventMonitor.DesktopShellChanged += SystemEventMonitor_DesktopShellChanged;
			await _logger.InfoAsync("Windows 이벤트 모니터를 시작했습니다. 휴지통 Shell 알림=" + (_systemEventMonitor.RecycleBinEventsAvailable ? "사용" : "watchdog 폴백") + ".");
			_wallpaperController = new WallpaperController(paths.WebViewDataDirectory, _logger, systemShortcutService, _systemEventMonitor);
			_wallpaperController.StateChanged += OnWallpaperStateChanged;
			_wallpaperController.ExclusiveGpuIsolationChanged += WallpaperController_ExclusiveGpuIsolationChanged;
			string dataDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "SampleWallpaper", "index.html");
			if (isDockPreview)
			{
				_skipSettingsSave = true;
				_settings = await LoadSettingsSafelyAsync();
				_settings.Dock = _settings.Dock.Copy();
				_settings.Dock.IsEnabled = true;
				_settings.Dock.HideWindowsTaskbar = false;
				_taskbarController = new TaskbarVisibilityController();
				_dockWindow = new DockWindow(_settings, systemShortcutService, _logger, _systemEventMonitor);
				_dockWindow.ExitRequested += async delegate
				{
					await ExitApplicationAsync();
				};
				_dockWindow.ApplySettings(_settings);
				int valueOrDefault2 = GetIntegerArgument(eventArgs.Args, "--preview-seconds").GetValueOrDefault(15);
				_previewTimer = new DispatcherTimer
				{
					Interval = TimeSpan.FromSeconds(Math.Clamp(valueOrDefault2, 3, 120))
				};
				_previewTimer.Tick += async delegate
				{
					_previewTimer?.Stop();
					await ExitApplicationAsync();
				};
				_previewTimer.Start();
				await _logger.InfoAsync("Vellume Dock 프리뷰를 시작했습니다.");
				return;
			}
			if (isSmokeTest)
			{
				DisplayInfo display = monitorService.GetDisplays().FirstOrDefault((DisplayInfo item) => item.IsPrimary) ?? throw new InvalidOperationException("스모크 테스트에서 기본 모니터를 찾지 못했습니다.");
				WallpaperItem wallpaperItem = WallpaperValidator.CreateItem(dataDirectory, isBuiltIn: true);
				wallpaperItem.Name = "Aurora Drift";
				bool diagnosticCapture = Environment.GetEnvironmentVariable("VELLUME_DIAGNOSTIC_CAPTURE") == "1";
				WidgetSettings widgetSettings = new WidgetSettings();
				if (diagnosticCapture)
				{
					_wallpaperController.QuickSlotInvoked += delegate(object? _, int slot)
					{
						_ = _logger.InfoAsync($"Diagnostic quick-slot click received for slot {slot}.");
					};
					_wallpaperController.QuickSlotSecondaryInvoked += delegate(object? _, int slot)
					{
						_ = _logger.InfoAsync($"Diagnostic quick-slot secondary action received for slot {slot}.");
					};
					_wallpaperController.QuickSlotLayoutChanged += delegate(object? _, QuickSlotLayoutChangedEventArgs layout)
					{
						_ = _logger.InfoAsync($"Diagnostic group move saved: {layout.GroupId} at {layout.X},{layout.Y}.");
					};
					string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
					QuickSlotItem[] source = new QuickSlotItem[3]
					{
						new QuickSlotItem
						{
							Slot = 1,
							Name = "메모장",
							TargetPath = Path.Combine(Environment.SystemDirectory, "notepad.exe")
						},
						new QuickSlotItem
						{
							Slot = 2,
							Name = "파일",
							TargetPath = Path.Combine(folderPath, "explorer.exe")
						},
						new QuickSlotItem
						{
							Slot = 3,
							Name = "작업 관리자",
							TargetPath = Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")
						}
					};
					widgetSettings.QuickSlots = source.Where((QuickSlotItem item) => File.Exists(item.TargetPath)).ToList();
					foreach (SystemShortcutSetting systemShortcut in widgetSettings.SystemShortcuts)
					{
						systemShortcut.IsVisible = true;
					}
				}
				await _wallpaperController.ApplyAsync(wallpaperItem, display, new PlaybackOptions
				{
					PauseWhenForegroundAppIsMaximized = false,
					ApplicationRules = new List<ApplicationPlaybackRule>()
				}, widgetSettings);
				await Task.Delay(TimeSpan.FromSeconds(5.0));
				await _wallpaperController.StopAsync();
				await _logger.InfoAsync("Smoke test applied and stopped the sample successfully.");
				if (diagnosticCapture)
				{
					await _logger.InfoAsync("Diagnostic pause: the wallpaper is stopped while the independent bounded widget windows remain active.");
					await Task.Delay(TimeSpan.FromSeconds(4.0));
				}
				await ExitApplicationAsync();
				return;
			}
			_settings = await LoadSettingsSafelyAsync();
			_mainWindow = new MainWindow(_settings, _settingsStore, _wallpaperController, monitorService, systemShortcutService, _logger, _systemEventMonitor, dataDirectory);
			_mainWindow.DockSettingsChanged += MainWindow_DockSettingsChanged;
			base.MainWindow = _mainWindow;
			_taskbarController = new TaskbarVisibilityController();
			CreateDockWindow();
			CreateTrayIcon();
			if (isResumedAfterExclusiveGpu)
			{
				_mainWindow.ShowActivated = false;
				_mainWindow.ShowInTaskbar = false;
				_mainWindow.Opacity = 0.0;
				_mainWindow.InitializationCompleted += MainWindow_ExclusiveGpuResumeCompleted;
			}
			_mainWindow.Show();
			if (!isResumedAfterExclusiveGpu)
			{
				ApplyDockSettings();
			}
			await _logger.InfoAsync("Vellume을 시작했습니다.");
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Vellume 시작에 실패했습니다.", exception);
			System.Windows.MessageBox.Show(exception.Message, "Vellume을 시작할 수 없음", MessageBoxButton.OK, MessageBoxImage.Hand);
			await ExitApplicationAsync();
		}
	}

	public void ShowMainWindow()
	{
		if (_mainWindow != null)
		{
			_mainWindow.Show();
			if (_mainWindow.WindowState == WindowState.Minimized)
			{
				_mainWindow.WindowState = WindowState.Normal;
			}
			_mainWindow.Activate();
		}
	}

	private void MainWindow_ExclusiveGpuResumeCompleted(object? sender, EventArgs eventArgs)
	{
		if (_mainWindow != null)
		{
			_mainWindow.InitializationCompleted -= MainWindow_ExclusiveGpuResumeCompleted;
			_mainWindow.Hide();
			_mainWindow.Opacity = 1.0;
			_mainWindow.ShowActivated = true;
			_mainWindow.ShowInTaskbar = true;
			ApplyDockSettings();
		}
	}

	public async Task TogglePlaybackAsync()
	{
		if (_wallpaperController == null)
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
			if (_logger != null)
			{
				await _logger.ErrorAsync("트레이 재생 명령에 실패했습니다.", exception);
			}
		}
	}

	public Task RequestExitAsync()
	{
		return ExitApplicationAsync();
	}

	private static bool TryGetTaskbarGuardOwner(IReadOnlyList<string> arguments, out int processId)
	{
		processId = 0;
		for (int i = 0; i + 1 < arguments.Count; i++)
		{
			if (arguments[i].Equals("--taskbar-guard", StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[i + 1], out processId) && processId > 0)
			{
				return true;
			}
		}
		processId = 0;
		return false;
	}

	private static int? GetIntegerArgument(IReadOnlyList<string> arguments, string argumentName)
	{
		for (int i = 0; i + 1 < arguments.Count; i++)
		{
			if (arguments[i].Equals(argumentName, StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[i + 1], out var result))
			{
				return result;
			}
		}
		return null;
	}

	private static string? GetStringArgument(IReadOnlyList<string> arguments, string argumentName)
	{
		for (int i = 0; i + 1 < arguments.Count; i++)
		{
			if (arguments[i].Equals(argumentName, StringComparison.OrdinalIgnoreCase))
			{
				return arguments[i + 1];
			}
		}
		return null;
	}

	private static bool TryGetSessionGuardArguments(IReadOnlyList<string> arguments, out int ownerProcessId, out string markerPath, out int recoveryAttempt)
	{
		ownerProcessId = GetIntegerArgument(arguments, "--session-guard").GetValueOrDefault();
		markerPath = GetStringArgument(arguments, "--session-marker") ?? string.Empty;
		recoveryAttempt = GetIntegerArgument(arguments, "--recovery-attempt").GetValueOrDefault();
		if (ownerProcessId > 0)
		{
			return !string.IsNullOrWhiteSpace(markerPath);
		}
		return false;
	}

	private void MainWindow_DockSettingsChanged(object? sender, EventArgs eventArgs)
	{
		ApplyDockSettings();
	}

	private void CreateDockWindow()
	{
		if (_dockWindow == null && !_dockSuspendedForExclusiveGpu && _settings != null && _systemShortcutService != null && _logger != null && _systemEventMonitor != null && !_isExiting)
		{
			_dockWindow = new DockWindow(_settings, _systemShortcutService, _logger, _systemEventMonitor);
			_dockWindow.ShowMainWindowRequested += DockWindow_ShowMainWindowRequested;
			_dockWindow.RevealWindowsTrayRequested += DockWindow_RevealWindowsTrayRequested;
			_dockWindow.RestoreTaskbarRequested += DockWindow_RestoreTaskbarRequested;
			_dockWindow.ExitRequested += DockWindow_ExitRequested;
			_dockWindow.SettingsChanged += DockWindow_SettingsChanged;
		}
	}

	private void WallpaperController_ExclusiveGpuIsolationChanged(object? sender, ExclusiveGpuIsolationChangedEventArgs eventArgs)
	{
		if (_isExiting || _skipSettingsSave)
		{
			return;
		}
		if (eventArgs.IsActive)
		{
			if (!_dockSuspendedForExclusiveGpu)
			{
				_dockSuspendedForExclusiveGpu = true;
				_taskbarMaintainTimer?.Stop();
				_trayRevealTimer?.Stop();
				_taskbarController?.Restore();
				ReleaseDockWindow();
				if (_logger != null)
				{
					_logger.InfoAsync($"GPU 격리 진입: {eventArgs.ProcessName} ({eventArgs.ProcessId}), Dock을 해제했습니다.");
				}
			}
		}
		else if (_dockSuspendedForExclusiveGpu)
		{
			_dockSuspendedForExclusiveGpu = false;
			CreateDockWindow();
			ApplyDockSettings();
			if (_logger != null)
			{
				_logger.InfoAsync("GPU 격리 해제: 월페이퍼·위젯 재생 상태에 맞춰 Dock을 복원했습니다.");
			}
		}
	}

	private void ReleaseDockWindow()
	{
		if (_dockWindow != null)
		{
			_dockWindow.ShowMainWindowRequested -= DockWindow_ShowMainWindowRequested;
			_dockWindow.RevealWindowsTrayRequested -= DockWindow_RevealWindowsTrayRequested;
			_dockWindow.RestoreTaskbarRequested -= DockWindow_RestoreTaskbarRequested;
			_dockWindow.ExitRequested -= DockWindow_ExitRequested;
			_dockWindow.SettingsChanged -= DockWindow_SettingsChanged;
			_dockWindow.Dispose();
			_dockWindow = null;
		}
	}

	private void DockWindow_ShowMainWindowRequested(object? sender, EventArgs eventArgs)
	{
		ShowMainWindow();
	}

	private void DockWindow_RevealWindowsTrayRequested(object? sender, EventArgs eventArgs)
	{
		RevealWindowsTray();
	}

	private void DockWindow_RestoreTaskbarRequested(object? sender, EventArgs eventArgs)
	{
		RestoreWindowsTaskbar();
	}

	private async void DockWindow_ExitRequested(object? sender, EventArgs eventArgs)
	{
		await ExitApplicationAsync();
	}

	private async void DockWindow_SettingsChanged(object? sender, EventArgs eventArgs)
	{
		if (_skipSettingsSave || _settings == null || _settingsStore == null)
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
			if (_logger != null)
			{
				await _logger.ErrorAsync("Dock 고정 항목 저장에 실패했습니다.", exception);
			}
		}
	}

	private void ApplyDockSettings()
	{
		if (_settings == null || _dockWindow == null || _taskbarController == null)
		{
			return;
		}
		_settings.Dock = (_settings.Dock ?? new DockSettings()).Copy();
		_dockWindow.ApplySettings(_settings);
		bool flag = _settings.Dock.IsEnabled && _settings.Dock.HideWindowsTaskbar;
		_taskbarController.SetHidden(flag);
		if (flag)
		{
			if (_taskbarMaintainTimer == null)
			{
				_taskbarMaintainTimer = new DispatcherTimer(DispatcherPriority.Background)
				{
					Interval = TimeSpan.FromSeconds(30.0)
				};
			}
			_taskbarMaintainTimer.Tick -= TaskbarMaintainTimer_Tick;
			_taskbarMaintainTimer.Tick += TaskbarMaintainTimer_Tick;
			_taskbarMaintainTimer.Start();
		}
		else
		{
			_taskbarMaintainTimer?.Stop();
		}
	}

	private void TaskbarMaintainTimer_Tick(object? sender, EventArgs eventArgs)
	{
		_taskbarController?.Maintain();
	}

	private void SystemEventMonitor_DesktopShellChanged(object? sender, EventArgs eventArgs)
	{
		_taskbarController?.Maintain();
		ApplyDockSettings();
	}

	private void RevealWindowsTray()
	{
		if (_settings != null && _taskbarController != null)
		{
			_taskbarController.Restore();
			_taskbarMaintainTimer?.Stop();
			_trayRevealTimer?.Stop();
			_trayRevealTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(10.0)
			};
			_trayRevealTimer.Tick += delegate
			{
				_trayRevealTimer?.Stop();
				ApplyDockSettings();
			};
			_trayRevealTimer.Start();
		}
	}

	private void RestoreWindowsTaskbar()
	{
		if (_settings != null)
		{
			_settings.Dock.HideWindowsTaskbar = false;
			_mainWindow?.SetDockTaskbarHidden(hidden: false);
			ApplyDockSettings();
		}
	}

	private async Task<AppSettings> LoadSettingsSafelyAsync()
	{
		AppSettings result = default(AppSettings);
		object obj;
		int num;
		try
		{
			result = await _settingsStore.LoadAsync();
			return result;
		}
		catch (Exception ex)
		{
			obj = ex;
			num = 1;
		}
		if (num != 1)
		{
			return result;
		}
		Exception exception = (Exception)obj;
		await _logger.ErrorAsync("설정을 불러오지 못해 기본값을 사용합니다.", exception);
		return new AppSettings();
	}

	private void CreateTrayIcon()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("Vellume 열기", null, delegate
		{
			base.Dispatcher.Invoke(ShowMainWindow);
		});
		_playPauseMenuItem = new ToolStripMenuItem("재생 / 일시정지", null, async delegate
		{
			await base.Dispatcher.InvokeAsync((Func<Task>)TogglePlaybackAsync).Task.Unwrap();
		});
		contextMenuStrip.Items.Add(_playPauseMenuItem);
		contextMenuStrip.Items.Add(new ToolStripSeparator());
		contextMenuStrip.Items.Add("종료", null, async delegate
		{
			await base.Dispatcher.InvokeAsync((Func<Task>)ExitApplicationAsync).Task.Unwrap();
		});
		_notifyIcon = new NotifyIcon
		{
			Text = "Vellume",
			Icon = SystemIcons.Application,
			ContextMenuStrip = contextMenuStrip,
			Visible = true
		};
		_notifyIcon.DoubleClick += delegate
		{
			base.Dispatcher.Invoke(ShowMainWindow);
		};
	}

	private void OnWallpaperStateChanged(object? sender, WallpaperStateChangedEventArgs eventArgs)
	{
		if (_playPauseMenuItem != null)
		{
			ToolStripMenuItem playPauseMenuItem = _playPauseMenuItem;
			playPauseMenuItem.Text = eventArgs.State switch
			{
				WallpaperPlaybackState.Playing => "월페이퍼 일시정지", 
				WallpaperPlaybackState.Paused => "월페이퍼 다시 재생", 
				_ => "재생 / 일시정지", 
			};
			playPauseMenuItem = _playPauseMenuItem;
			WallpaperPlaybackState state = eventArgs.State;
			bool enabled = (uint)(state - 1) <= 1u;
			playPauseMenuItem.Enabled = enabled;
		}
	}

	private async Task ExitApplicationAsync()
	{
		if (_isExiting)
		{
			return;
		}
		_isExiting = true;
		bool controllerDisposedCleanly = true;
		_previewTimer?.Stop();
		_trayRevealTimer?.Stop();
		_taskbarMaintainTimer?.Stop();
		_taskbarController?.Restore();
		if (_mainWindow != null)
		{
			_mainWindow.DockSettingsChanged -= MainWindow_DockSettingsChanged;
			_mainWindow.InitializationCompleted -= MainWindow_ExclusiveGpuResumeCompleted;
		}
		ReleaseDockWindow();
		_taskbarController?.Dispose();
		_taskbarController = null;
		if (_mainWindow != null)
		{
			_mainWindow.AllowClose = true;
			_mainWindow.PrepareForExit();
		}
		if (_notifyIcon != null)
		{
			_notifyIcon.Visible = false;
			_notifyIcon.Dispose();
			_notifyIcon = null;
		}
		if (!_skipSettingsSave && _settings != null && _settingsStore != null)
		{
			try
			{
				await _settingsStore.SaveAsync(_settings);
			}
			catch (Exception exception)
			{
				if (_logger != null)
				{
					await _logger.ErrorAsync("마지막 설정 저장에 실패했습니다.", exception);
				}
			}
		}
		if (_wallpaperController != null)
		{
			_wallpaperController.StateChanged -= OnWallpaperStateChanged;
			_wallpaperController.ExclusiveGpuIsolationChanged -= WallpaperController_ExclusiveGpuIsolationChanged;
			try
			{
				await _wallpaperController.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8.0));
			}
			catch (Exception exception2)
			{
				controllerDisposedCleanly = false;
				if (_logger != null)
				{
					await _logger.ErrorAsync("월페이퍼 정리를 완료하지 못했지만 Vellume 종료를 계속합니다.", exception2);
				}
			}
			_wallpaperController = null;
		}
		if (_systemEventMonitor != null)
		{
			_systemEventMonitor.DesktopShellChanged -= SystemEventMonitor_DesktopShellChanged;
			_systemEventMonitor.Dispose();
		}
		_systemEventMonitor = null;
		if (controllerDisposedCleanly && !string.IsNullOrWhiteSpace(_sessionMarkerPath))
		{
			SessionRecoveryGuard.MarkCleanExit(_sessionMarkerPath, Environment.ProcessId);
			_sessionMarkerPath = null;
		}
		else if (!controllerDisposedCleanly && _logger != null)
		{
			await _logger.InfoAsync("정리 실패를 감지해 다음 실행의 데스크톱 복구 마커를 유지합니다.");
		}
		_sessionRecoveryGuard?.Dispose();
		_sessionRecoveryGuard = null;
		if (_logger != null)
		{
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

	private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
	{
		eventArgs.Handled = true;
		if (_logger != null)
		{
			await _logger.ErrorAsync("처리되지 않은 UI 오류입니다.", eventArgs.Exception);
		}
		System.Windows.MessageBox.Show(eventArgs.Exception.Message, "Vellume 오류", MessageBoxButton.OK, MessageBoxImage.Hand);
	}

	private async void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
	{
		eventArgs.SetObserved();
		if (_logger != null)
		{
			await _logger.ErrorAsync("확인되지 않은 백그라운드 작업 오류입니다.", eventArgs.Exception);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Vellume.App;V0.8.5.0;component/app.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocator);
		}
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
public class DockWindow : Window, INotifyPropertyChanged, IDisposable, IComponentConnector, IStyleConnector
{
	private const int WmHotkey = 786;

	private const int DockHotkeyId = 22224;

	private const uint ModAlt = 1u;

	private const uint ModNoRepeat = 16384u;

	private const uint VirtualKeyOem3 = 192u;

	private readonly ISystemShortcutService _systemShortcutService;

	private readonly IAppLogger _logger;

	private readonly WindowsDockRuntimeService _dockRuntime = new WindowsDockRuntimeService();

	private readonly WindowsQuickSlotRuntimeService _quickSlotRuntime = new WindowsQuickSlotRuntimeService();

	private readonly WindowsIconService _iconService = new WindowsIconService();

	private readonly WindowsSystemEventMonitor _systemEventMonitor;

	private readonly DispatcherTimer _refreshTimer;

	private AppSettings _settings;

	private bool _disposed;

	private double _tileSize = 58.0;

	private HwndSource? _windowSource;

	private System.Windows.Point _dragStartPoint;

	private DockItemViewModel? _dragCandidate;

	private bool _dragOccurred;

	private bool _dockHotkeyRegistered;

	private int _runtimeRefreshQueued;

	internal Border DockSurface;

	internal Border RunningDivider;

	private bool _contentLoaded;

	public ObservableCollection<DockItemViewModel> PinnedItems { get; } = new ObservableCollection<DockItemViewModel>();


	public ObservableCollection<DockItemViewModel> RunningItems { get; } = new ObservableCollection<DockItemViewModel>();


	public double TileSize
	{
		get
		{
			return _tileSize;
		}
		private set
		{
			if (!(Math.Abs(_tileSize - value) < 0.1))
			{
				_tileSize = value;
				OnPropertyChanged("TileSize");
			}
		}
	}

	public Visibility RunningSectionVisibility
	{
		get
		{
			if (RunningItems.Count <= 0)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public event EventHandler? ShowMainWindowRequested;

	public event EventHandler? RevealWindowsTrayRequested;

	public event EventHandler? RestoreTaskbarRequested;

	public event EventHandler? ExitRequested;

	public event EventHandler? SettingsChanged;

	public DockWindow(AppSettings settings, ISystemShortcutService systemShortcutService, IAppLogger logger, WindowsSystemEventMonitor systemEventMonitor)
	{
		InitializeComponent();
		base.DataContext = this;
		_settings = settings;
		_systemShortcutService = systemShortcutService;
		_logger = logger;
		_systemEventMonitor = systemEventMonitor;
		_refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_refreshTimer.Tick += RefreshTimer_Tick;
		_dockRuntime.StateChanged += RuntimeStateChanged;
		_systemEventMonitor.ForegroundWindowChanged += SystemEventMonitor_StateChanged;
		_systemEventMonitor.RecycleBinChanged += SystemEventMonitor_StateChanged;
		base.SourceInitialized += Window_SourceInitialized;
		RebuildPinnedItems();
	}

	public void ApplySettings(AppSettings settings)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_settings = settings;
		TileSize = settings.Dock.Copy().IconSize + 4;
		RebuildPinnedItems();
		RefreshRunningItems();
		PositionDock();
		if (settings.Dock.IsEnabled)
		{
			if (!base.IsVisible)
			{
				Show();
			}
			_refreshTimer.Start();
		}
		else
		{
			_refreshTimer.Stop();
			Hide();
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_refreshTimer.Stop();
			_refreshTimer.Tick -= RefreshTimer_Tick;
			_dockRuntime.StateChanged -= RuntimeStateChanged;
			_systemEventMonitor.ForegroundWindowChanged -= SystemEventMonitor_StateChanged;
			_systemEventMonitor.RecycleBinChanged -= SystemEventMonitor_StateChanged;
			_quickSlotRuntime.Dispose();
			_dockRuntime.Dispose();
			if (_dockHotkeyRegistered && _windowSource != null)
			{
				UnregisterHotKey(_windowSource.Handle, 22224);
				_dockHotkeyRegistered = false;
			}
			_windowSource?.RemoveHook(WindowMessageHook);
			_windowSource = null;
			Close();
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs eventArgs)
	{
		TileSize = _settings.Dock.Copy().IconSize + 4;
		PositionDock();
		RefreshRunningItems();
		if (_settings.Dock.IsEnabled)
		{
			_refreshTimer.Start();
		}
	}

	private void Window_SourceInitialized(object? sender, EventArgs eventArgs)
	{
		nint handle = new WindowInteropHelper(this).Handle;
		_windowSource = HwndSource.FromHwnd(handle);
		_windowSource?.AddHook(WindowMessageHook);
		_dockRuntime.Attach(handle);
		_dockHotkeyRegistered = RegisterHotKey(handle, 22224, 16385u, 192u);
		if (!_dockHotkeyRegistered)
		{
			_logger.InfoAsync("Dock 호출 단축키 Alt+`를 등록하지 못했습니다.");
		}
	}

	private nint WindowMessageHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (message == 786 && ((IntPtr)wParam).ToInt32() == 22224)
		{
			BringDockForward();
			handled = true;
			return IntPtr.Zero;
		}
		if (_dockRuntime.TryHandleShellMessage(message, wParam, lParam))
		{
			handled = false;
		}
		return IntPtr.Zero;
	}

	private void BringDockForward()
	{
		if (_settings.Dock.IsEnabled)
		{
			RefreshPinnedRuntimeState();
			RefreshRunningItems();
			if (!base.IsVisible)
			{
				Show();
			}
			base.Topmost = true;
			base.Topmost = false;
			Activate();
		}
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs eventArgs)
	{
		PositionDock();
	}

	private void RefreshTimer_Tick(object? sender, EventArgs eventArgs)
	{
		RefreshRuntimeState();
	}

	private void RuntimeStateChanged(object? sender, EventArgs eventArgs)
	{
		QueueRuntimeRefresh();
	}

	private void SystemEventMonitor_StateChanged(object? sender, EventArgs eventArgs)
	{
		QueueRuntimeRefresh();
	}

	private void QueueRuntimeRefresh()
	{
		if (!_disposed && Interlocked.Exchange(ref _runtimeRefreshQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				Interlocked.Exchange(ref _runtimeRefreshQueued, 0);
				RefreshRuntimeState();
			});
		}
	}

	private void RefreshRuntimeState()
	{
		if (!_disposed && _settings.Dock.IsEnabled)
		{
			RefreshPinnedRuntimeState();
			RefreshRunningItems();
		}
	}

	private void RebuildPinnedItems()
	{
		List<DockItemViewModel> list = new List<DockItemViewModel> { DockItemViewModel.CreateVellume(_iconService.GetIcon(Environment.ProcessPath ?? string.Empty)) };
		List<DockItemViewModel> list2 = new List<DockItemViewModel>();
		foreach (SystemShortcutSetting item in _settings.Widgets.SystemShortcuts.Where((SystemShortcutSetting item) => item.ShowInDock))
		{
			string defaultTargetPath = SystemShortcutSetting.GetDefaultTargetPath(item.Kind);
			bool dynamicState = item.Kind == SystemShortcutKind.RecycleBin && _systemShortcutService.RecycleBinHasItems();
			list2.Add(DockItemViewModel.CreateSystem(item, _iconService.GetIcon(defaultTargetPath, item.CustomIconPath, item.Kind, dynamicState), dynamicState));
		}
		foreach (QuickSlotItem item2 in from item in _settings.Widgets.QuickSlots
			where item.ShowInDock
			orderby (item.DockOrder > 0) ? item.DockOrder : int.MaxValue, item.Slot
			select item)
		{
			list2.Add(DockItemViewModel.CreateQuickSlot(item2, _iconService.GetIcon(item2.TargetPath, item2.IconPath)));
		}
		list.AddRange(list2.OrderBy((DockItemViewModel item) => (item.DockOrder > 0) ? item.DockOrder : int.MaxValue).ThenBy<DockItemViewModel, string>((DockItemViewModel item) => item.Name, StringComparer.CurrentCultureIgnoreCase));
		ApplyDiff(PinnedItems, list);
	}

	private void RefreshPinnedRuntimeState()
	{
		QuickSlotRuntimeSnapshot quickSlotRuntimeSnapshot = _quickSlotRuntime.Capture(_settings.Widgets.QuickSlots, _settings.Widgets.SystemShortcuts);
		bool flag = _systemShortcutService.RecycleBinHasItems();
		foreach (DockItemViewModel pinnedItem in PinnedItems)
		{
			if (pinnedItem.QuickSlot != null && quickSlotRuntimeSnapshot.QuickSlots.TryGetValue(pinnedItem.QuickSlot.Slot, out var value))
			{
				pinnedItem.UpdateRuntime(value.IsRunning, value.HasAttention || _dockRuntime.HasAttention(pinnedItem.ExecutablePath));
				continue;
			}
			SystemShortcutKind? systemKind = pinnedItem.SystemKind;
			if (systemKind.HasValue)
			{
				SystemShortcutKind valueOrDefault = systemKind.GetValueOrDefault();
				if (quickSlotRuntimeSnapshot.SystemShortcuts.TryGetValue(valueOrDefault, out var value2))
				{
					pinnedItem.UpdateRuntime(value2.IsRunning, value2.HasAttention || _dockRuntime.HasAttention(pinnedItem.ExecutablePath));
				}
				if (valueOrDefault == SystemShortcutKind.RecycleBin && pinnedItem.DynamicState != flag)
				{
					pinnedItem.DynamicState = flag;
					pinnedItem.Icon = _iconService.GetIcon(SystemShortcutSetting.GetDefaultTargetPath(valueOrDefault), pinnedItem.SystemShortcut?.CustomIconPath ?? string.Empty, valueOrDefault, flag);
				}
			}
		}
	}

	private void RefreshRunningItems()
	{
		if (!_settings.Dock.ShowRunningApplications)
		{
			if (RunningItems.Count > 0)
			{
				RunningItems.Clear();
				OnPropertyChanged("RunningSectionVisibility");
			}
			return;
		}
		string[] pinnedPaths = (from item in PinnedItems
			select item.ExecutablePath into path
			where !string.IsNullOrWhiteSpace(path)
			select path).ToArray();
		List<DockItemViewModel> desired = (from item in _dockRuntime.Capture()
			where !pinnedPaths.Any((string path) => ExecutablePathIdentity.AreEquivalent(path, item.ExecutablePath))
			select DockItemViewModel.CreateRunning(item, _iconService.GetIcon(item.ExecutablePath))).ToList();
		bool num = RunningItems.Count > 0;
		ApplyDiff(RunningItems, desired);
		if (num != RunningItems.Count > 0)
		{
			OnPropertyChanged("RunningSectionVisibility");
		}
	}

	private static void ApplyDiff(ObservableCollection<DockItemViewModel> target, IReadOnlyList<DockItemViewModel> desired)
	{
		HashSet<string> hashSet = desired.Select((DockItemViewModel item) => item.Key).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int num = target.Count - 1; num >= 0; num--)
		{
			if (!hashSet.Contains(target[num].Key))
			{
				target.RemoveAt(num);
			}
		}
		for (int i = 0; i < desired.Count; i++)
		{
			DockItemViewModel incoming = desired[i];
			DockItemViewModel dockItemViewModel = target.FirstOrDefault((DockItemViewModel item) => item.Key.Equals(incoming.Key, StringComparison.OrdinalIgnoreCase));
			if (dockItemViewModel == null)
			{
				target.Insert(Math.Min(i, target.Count), incoming);
				continue;
			}
			dockItemViewModel.UpdateFrom(incoming);
			int num2 = target.IndexOf(dockItemViewModel);
			if (num2 != i)
			{
				target.Move(num2, i);
			}
		}
	}

	private async void DockItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
	{
		if (!(sender is FrameworkElement { DataContext: var dataContext }) || !(dataContext is DockItemViewModel item))
		{
			return;
		}
		eventArgs.Handled = true;
		if (_dragOccurred)
		{
			_dragOccurred = false;
			return;
		}
		try
		{
			if (item.IsVellume)
			{
				this.ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
				return;
			}
			if ((object)item.RunningApplication != null)
			{
				_dockRuntime.Activate(item.RunningApplication);
				return;
			}
			SystemShortcutKind? systemKind = item.SystemKind;
			if (systemKind.HasValue)
			{
				SystemShortcutKind valueOrDefault = systemKind.GetValueOrDefault();
				if (!_quickSlotRuntime.TryActivateSystemShortcut(valueOrDefault))
				{
					await _systemShortcutService.LaunchAsync(valueOrDefault);
				}
			}
			else if (item.QuickSlot != null)
			{
				await _quickSlotRuntime.ActivateOrLaunchAsync(item.QuickSlot, forceNewInstance: false);
			}
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync(item.Name + " Dock 동작에 실패했습니다.", exception);
		}
	}

	private void DockItem_MouseRightButtonUp(object sender, MouseButtonEventArgs eventArgs)
	{
		if (!(sender is FrameworkElement { DataContext: var dataContext } frameworkElement))
		{
			return;
		}
		DockItemViewModel item = dataContext as DockItemViewModel;
		if (item == null)
		{
			return;
		}
		eventArgs.Handled = true;
		ContextMenu contextMenu = CreateDockContextMenu();
		if (item.IsVellume)
		{
			AddMenuItem(contextMenu, "Vellume 열기", delegate
			{
				this.ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
			});
			AddMenuItem(contextMenu, "Windows 트레이 10초 열기", delegate
			{
				this.RevealWindowsTrayRequested?.Invoke(this, EventArgs.Empty);
			});
			AddMenuItem(contextMenu, "Windows 작업표시줄 복원", delegate
			{
				this.RestoreTaskbarRequested?.Invoke(this, EventArgs.Empty);
			});
			contextMenu.Items.Add(new Separator());
			AddMenuItem(contextMenu, "Vellume 종료", delegate
			{
				this.ExitRequested?.Invoke(this, EventArgs.Empty);
			});
		}
		else if ((object)item.RunningApplication != null)
		{
			foreach (DockWindowInfo window in item.RunningApplication.Windows)
			{
				AddMenuItem(contextMenu, window.Title, delegate
				{
					_dockRuntime.ActivateWindow(window.Handle);
				});
			}
			contextMenu.Items.Add(new Separator());
			AddMenuItem(contextMenu, "새로 실행", delegate
			{
				_dockRuntime.LaunchNew(item.RunningApplication.ExecutablePath);
			});
			AddMenuItem(contextMenu, "Dock에 고정", delegate
			{
				PinRunningApplication(item.RunningApplication);
			});
		}
		else if (item.SystemKind.GetValueOrDefault() == SystemShortcutKind.RecycleBin)
		{
			AddMenuItem(contextMenu, "휴지통 열기", async delegate
			{
				await _systemShortcutService.LaunchAsync(SystemShortcutKind.RecycleBin);
			});
			AddMenuItem(contextMenu, "휴지통 비우기", async delegate
			{
				await _systemShortcutService.EmptyRecycleBinAsync(new WindowInteropHelper(this).Handle);
			});
			contextMenu.Items.Add(new Separator());
			AddMenuItem(contextMenu, "Dock에서 제거", delegate
			{
				UnpinItem(item);
			});
		}
		else
		{
			SystemShortcutKind? systemKind2 = item.SystemKind;
			if (systemKind2.HasValue)
			{
				SystemShortcutKind systemKind = systemKind2.GetValueOrDefault();
				AddMenuItem(contextMenu, "새로 열기", async delegate
				{
					await _systemShortcutService.LaunchAsync(systemKind);
				});
				contextMenu.Items.Add(new Separator());
				AddMenuItem(contextMenu, "Dock에서 제거", delegate
				{
					UnpinItem(item);
				});
			}
			else if (item.QuickSlot != null)
			{
				AddMenuItem(contextMenu, "새로 실행", async delegate
				{
					await _quickSlotRuntime.ActivateOrLaunchAsync(item.QuickSlot, forceNewInstance: true);
				});
				contextMenu.Items.Add(new Separator());
				AddMenuItem(contextMenu, "Dock에서 제거", delegate
				{
					UnpinItem(item);
				});
			}
		}
		if (contextMenu.Items.Count > 0)
		{
			frameworkElement.ContextMenu = contextMenu;
			contextMenu.IsOpen = true;
		}
	}

	private void DockItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
	{
		_dragStartPoint = eventArgs.GetPosition(this);
		_dragCandidate = (sender as FrameworkElement)?.DataContext as DockItemViewModel;
		_dragOccurred = false;
	}

	private void DockItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
	{
		if (eventArgs.LeftButton == MouseButtonState.Pressed && _dragCandidate != null && !_dragCandidate.IsVellume)
		{
			System.Windows.Point position = eventArgs.GetPosition(this);
			if (!(Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance) || !(Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance))
			{
				DockItemViewModel dragCandidate = _dragCandidate;
				_dragCandidate = null;
				_dragOccurred = true;
				DragDrop.DoDragDrop((DependencyObject)sender, dragCandidate, System.Windows.DragDropEffects.Move);
			}
		}
	}

	private void DockItem_Drop(object sender, System.Windows.DragEventArgs eventArgs)
	{
		if (!(sender is FrameworkElement { DataContext: DockItemViewModel { IsVellume: false } dataContext }))
		{
			return;
		}
		DockItemViewModel dockItemViewModel = eventArgs.Data.GetData(typeof(DockItemViewModel)) as DockItemViewModel;
		if (dockItemViewModel == null || dockItemViewModel == dataContext)
		{
			return;
		}
		eventArgs.Handled = true;
		if ((object)dockItemViewModel.RunningApplication != null)
		{
			dockItemViewModel = PinRunningApplication(dockItemViewModel.RunningApplication, notify: false) ?? dockItemViewModel;
		}
		if (PinnedItems.Contains(dockItemViewModel) && PinnedItems.Contains(dataContext))
		{
			int num = PinnedItems.IndexOf(dockItemViewModel);
			int num2 = PinnedItems.IndexOf(dataContext);
			if (num >= 1 && num2 >= 1)
			{
				PinnedItems.Move(num, num2);
				PersistPinnedOrder();
				NotifySettingsChanged();
			}
		}
	}

	private DockItemViewModel? PinRunningApplication(DockApplicationSnapshot application, bool notify = true)
	{
		DockApplicationSnapshot application2 = application;
		QuickSlotItem existing = _settings.Widgets.QuickSlots.FirstOrDefault((QuickSlotItem item) => ExecutablePathIdentity.AreEquivalent(item.TargetPath, application2.ExecutablePath));
		if (existing == null)
		{
			HashSet<int> occupiedSlots = (from item in _settings.Widgets.QuickSlots
				select item.Slot into slot
				where slot >= 1 && slot <= 99
				select slot).ToHashSet();
			int num = Enumerable.Range(1, 99).FirstOrDefault((int candidate) => !occupiedSlots.Contains(candidate));
			if (num == 0)
			{
				return null;
			}
			QuickSlotGroup quickSlotGroup = _settings.Widgets.QuickSlotGroups.FirstOrDefault((QuickSlotGroup item) => item.Id.Equals("apps", StringComparison.OrdinalIgnoreCase)) ?? _settings.Widgets.QuickSlotGroups.FirstOrDefault() ?? new QuickSlotGroup
			{
				Id = "apps",
				Name = "앱"
			};
			if (!_settings.Widgets.QuickSlotGroups.Contains(quickSlotGroup))
			{
				_settings.Widgets.QuickSlotGroups.Add(quickSlotGroup);
			}
			existing = new QuickSlotItem
			{
				Slot = num,
				Name = application2.Name,
				TargetPath = application2.ExecutablePath,
				GroupId = quickSlotGroup.Id,
				GroupName = quickSlotGroup.Name
			};
			_settings.Widgets.QuickSlots.Add(existing);
		}
		existing.ShowInDock = true;
		existing.DockOrder = GetNextDockOrder();
		RebuildPinnedItems();
		RefreshRunningItems();
		DockItemViewModel? result = PinnedItems.FirstOrDefault((DockItemViewModel item) => item.QuickSlot != null && item.QuickSlot == existing);
		if (notify)
		{
			NotifySettingsChanged();
		}
		return result;
	}

	private void UnpinItem(DockItemViewModel item)
	{
		if (item.SystemShortcut != null)
		{
			item.SystemShortcut.ShowInDock = false;
		}
		else
		{
			if (item.QuickSlot == null)
			{
				return;
			}
			item.QuickSlot.ShowInDock = false;
		}
		RebuildPinnedItems();
		RefreshRunningItems();
		PersistPinnedOrder();
		NotifySettingsChanged();
	}

	private int GetNextDockOrder()
	{
		return (from item in PinnedItems
			where !item.IsVellume
			select item.DockOrder).DefaultIfEmpty(0).Max() + 10;
	}

	private void PersistPinnedOrder()
	{
		int num = 10;
		foreach (DockItemViewModel item in PinnedItems.Where((DockItemViewModel item) => !item.IsVellume))
		{
			item.DockOrder = num;
			if (item.SystemShortcut != null)
			{
				item.SystemShortcut.DockOrder = num;
			}
			else if (item.QuickSlot != null)
			{
				item.QuickSlot.DockOrder = num;
			}
			num += 10;
		}
	}

	private void NotifySettingsChanged()
	{
		_settings.SchemaVersion = 12;
		this.SettingsChanged?.Invoke(this, EventArgs.Empty);
	}

	private static void AddMenuItem(ContextMenu menu, string title, Action action)
	{
		Action action2 = action;
		MenuItem menuItem = new MenuItem
		{
			Header = CreateDockMenuHeader(title),
			Foreground = System.Windows.Media.Brushes.Black,
			Background = System.Windows.Media.Brushes.Transparent,
			FontWeight = FontWeights.SemiBold
		};
		menuItem.Click += delegate
		{
			action2();
		};
		menu.Items.Add(menuItem);
	}

	private static void AddMenuItem(ContextMenu menu, string title, Func<Task> action)
	{
		Func<Task> action2 = action;
		MenuItem menuItem = new MenuItem
		{
			Header = CreateDockMenuHeader(title),
			Foreground = System.Windows.Media.Brushes.Black,
			Background = System.Windows.Media.Brushes.Transparent,
			FontWeight = FontWeights.SemiBold
		};
		menuItem.Click += async delegate
		{
			await action2();
		};
		menu.Items.Add(menuItem);
	}

	private static ContextMenu CreateDockContextMenu()
	{
		ContextMenu contextMenu = new ContextMenu
		{
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 248, 248)),
			Foreground = System.Windows.Media.Brushes.Black,
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(146, 146, 146)),
			BorderThickness = new Thickness(1.0),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0
		};
		contextMenu.Resources[System.Windows.SystemColors.MenuBrushKey] = contextMenu.Background;
		contextMenu.Resources[System.Windows.SystemColors.MenuTextBrushKey] = System.Windows.Media.Brushes.Black;
		contextMenu.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = System.Windows.Media.Brushes.Black;
		contextMenu.Resources[System.Windows.SystemColors.GrayTextBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(95, 95, 95));
		Style style = new Style(typeof(TextBlock));
		style.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Black));
		style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
		contextMenu.Resources[typeof(TextBlock)] = style;
		Style style2 = new Style(typeof(MenuItem));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty, FontWeights.SemiBold));
		contextMenu.Resources[typeof(MenuItem)] = style2;
		return contextMenu;
	}

	private static TextBlock CreateDockMenuHeader(string title)
	{
		return new TextBlock
		{
			Text = title,
			Foreground = System.Windows.Media.Brushes.Black,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold
		};
	}

	private void PositionDock()
	{
		if (base.IsLoaded && !(base.ActualWidth <= 0.0) && !(base.ActualHeight <= 0.0))
		{
			base.Left = Math.Max(0.0, (SystemParameters.PrimaryScreenWidth - base.ActualWidth) / 2.0);
			base.Top = Math.Max(0.0, SystemParameters.PrimaryScreenHeight - base.ActualHeight - (double)_settings.Dock.Copy().BottomMargin);
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(nint window, int identifier, uint modifiers, uint virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(nint window, int identifier);

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Vellume.App;V0.8.5.0;component/dockwindow.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((DockWindow)target).Loaded += Window_Loaded;
			((DockWindow)target).SizeChanged += Window_SizeChanged;
			break;
		case 3:
			DockSurface = (Border)target;
			break;
		case 4:
			RunningDivider = (Border)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		if (connectionId == 2)
		{
			((Border)target).PreviewMouseLeftButtonDown += DockItem_PreviewMouseLeftButtonDown;
			((Border)target).PreviewMouseMove += DockItem_PreviewMouseMove;
			((Border)target).MouseLeftButtonUp += DockItem_MouseLeftButtonUp;
			((Border)target).MouseRightButtonUp += DockItem_MouseRightButtonUp;
			((Border)target).Drop += DockItem_Drop;
		}
	}
}
public sealed class DockItemViewModel : INotifyPropertyChanged
{
	private ImageSource? _icon;

	private bool _isRunning;

	private bool _hasAttention;

	private bool _dynamicState;

	private DockApplicationSnapshot? _runningApplication;

	public string Key { get; }

	public string Name { get; private set; }

	public string ExecutablePath { get; private set; } = string.Empty;


	public bool IsVellume { get; private set; }

	public SystemShortcutKind? SystemKind { get; private set; }

	public SystemShortcutSetting? SystemShortcut { get; private set; }

	public QuickSlotItem? QuickSlot { get; private set; }

	public int DockOrder { get; set; }

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			SetField(ref _icon, value, "Icon");
		}
	}

	public bool DynamicState
	{
		get
		{
			return _dynamicState;
		}
		set
		{
			SetField(ref _dynamicState, value, "DynamicState");
		}
	}

	public DockApplicationSnapshot? RunningApplication
	{
		get
		{
			return _runningApplication;
		}
		private set
		{
			SetField(ref _runningApplication, value, "RunningApplication");
		}
	}

	public Visibility RunningVisibility
	{
		get
		{
			if (!_isRunning)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public Visibility AttentionVisibility
	{
		get
		{
			if (!_hasAttention)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private DockItemViewModel(string key, string name)
	{
		Key = key;
		Name = name;
	}

	public static DockItemViewModel CreateVellume(ImageSource? icon)
	{
		return new DockItemViewModel("vellume:control", "Vellume")
		{
			Icon = icon,
			IsVellume = true
		};
	}

	public static DockItemViewModel CreateSystem(SystemShortcutSetting system, ImageSource? icon, bool dynamicState)
	{
		return new DockItemViewModel($"system:{system.Kind}", system.Name)
		{
			Icon = icon,
			SystemKind = system.Kind,
			SystemShortcut = system,
			DockOrder = system.DockOrder,
			ExecutablePath = SystemShortcutSetting.GetDefaultTargetPath(system.Kind),
			DynamicState = dynamicState
		};
	}

	public static DockItemViewModel CreateQuickSlot(QuickSlotItem slot, ImageSource? icon)
	{
		return new DockItemViewModel($"quick:{slot.Slot}:{slot.TargetPath}", slot.Name)
		{
			Icon = icon,
			QuickSlot = slot,
			DockOrder = slot.DockOrder,
			ExecutablePath = slot.TargetPath
		};
	}

	public static DockItemViewModel CreateRunning(DockApplicationSnapshot application, ImageSource? icon)
	{
		return new DockItemViewModel("running:" + application.Key, application.Name)
		{
			Icon = icon,
			ExecutablePath = application.ExecutablePath,
			RunningApplication = application,
			_isRunning = true,
			_hasAttention = application.HasAttention
		};
	}

	public void UpdateRuntime(bool isRunning, bool hasAttention)
	{
		if (_isRunning != isRunning)
		{
			_isRunning = isRunning;
			OnPropertyChanged("RunningVisibility");
		}
		if (_hasAttention != hasAttention)
		{
			_hasAttention = hasAttention;
			OnPropertyChanged("AttentionVisibility");
		}
	}

	public void UpdateFrom(DockItemViewModel incoming)
	{
		if (!string.Equals(Name, incoming.Name, StringComparison.Ordinal))
		{
			Name = incoming.Name;
			OnPropertyChanged("Name");
		}
		ExecutablePath = incoming.ExecutablePath;
		IsVellume = incoming.IsVellume;
		SystemKind = incoming.SystemKind;
		SystemShortcut = incoming.SystemShortcut;
		QuickSlot = incoming.QuickSlot;
		DockOrder = incoming.DockOrder;
		RunningApplication = incoming.RunningApplication;
		DynamicState = incoming.DynamicState;
		if (Icon == null && incoming.Icon != null)
		{
			Icon = incoming.Icon;
		}
		UpdateRuntime(incoming._isRunning, incoming._hasAttention);
	}

	private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (!EqualityComparer<T>.Default.Equals(field, value))
		{
			field = value;
			OnPropertyChanged(propertyName);
		}
	}

	private void OnPropertyChanged(string? propertyName)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
internal sealed record KnownAppMatch(string Name, string GroupName, string TargetPath);
internal static class KnownAppDiscoveryService
{
	private sealed record KnownAppDefinition(string Name, string GroupName, IReadOnlyList<string> Candidates);

	public static IReadOnlyList<KnownAppMatch> Discover()
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		string folderPath3 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		KnownAppDefinition[] obj = new KnownAppDefinition[9]
		{
			Define("Visual Studio 2022", "작업", Path.Combine(folderPath2, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe"), Path.Combine(folderPath2, "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"), Path.Combine(folderPath2, "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"), Path.Combine(folderPath2, "Microsoft Visual Studio", "2022", "Preview", "Common7", "IDE", "devenv.exe")),
			Define("Visual Studio Code", "작업", Path.Combine(folderPath, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(folderPath2, "Microsoft VS Code", "Code.exe"), Path.Combine(folderPath3, "Microsoft VS Code", "Code.exe")),
			Define("GitHub Desktop", "작업", Path.Combine(folderPath, "GitHubDesktop", "GitHubDesktop.exe"), Path.Combine(folderPath2, "GitHub Desktop", "GitHubDesktop.exe")),
			Define("Notion", "작업", Path.Combine(folderPath, "Programs", "Notion", "Notion.exe"), Path.Combine(folderPath, "Notion", "Notion.exe")),
			Define("디스코드", "앱", Path.Combine(folderPath, "Discord", "Discord.exe"), Path.Combine(folderPath2, "Discord", "Discord.exe")),
			Define("카카오톡", "앱", Path.Combine(folderPath3, "Kakao", "KakaoTalk", "KakaoTalk.exe"), Path.Combine(folderPath, "Kakao", "KakaoTalk", "KakaoTalk.exe")),
			Define("네이버 웨일", "앱", Path.Combine(folderPath2, "Naver", "Naver Whale", "Application", "whale.exe"), Path.Combine(folderPath3, "Naver", "Naver Whale", "Application", "whale.exe"), Path.Combine(folderPath, "Naver", "Naver Whale", "Application", "whale.exe")),
			Define("파이어폭스", "앱", Path.Combine(folderPath2, "Mozilla Firefox", "firefox.exe"), Path.Combine(folderPath3, "Mozilla Firefox", "firefox.exe")),
			Define("에픽게임즈 런처", "게임", Path.Combine(folderPath3, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), Path.Combine(folderPath2, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"))
		};
		List<KnownAppMatch> list = new List<KnownAppMatch>();
		KnownAppDefinition[] array = obj;
		foreach (KnownAppDefinition knownAppDefinition in array)
		{
			string text = knownAppDefinition.Candidates.FirstOrDefault(File.Exists);
			if (text == null)
			{
				string name = knownAppDefinition.Name;
				string text2 = ((name == "GitHub Desktop") ? FindNewestSquirrelApp(Path.Combine(folderPath, "GitHubDesktop"), "GitHubDesktop.exe") : ((!(name == "Discord")) ? null : FindNewestSquirrelApp(Path.Combine(folderPath, "Discord"), "Discord.exe")));
				text = text2;
			}
			if (!string.IsNullOrWhiteSpace(text))
			{
				list.Add(new KnownAppMatch(knownAppDefinition.Name, knownAppDefinition.GroupName, Path.GetFullPath(text)));
			}
		}
		return list;
	}

	private static KnownAppDefinition Define(string name, string groupName, params string[] candidates)
	{
		return new KnownAppDefinition(name, groupName, candidates);
	}

	private static string? FindNewestSquirrelApp(string root, string executableName)
	{
		string executableName2 = executableName;
		try
		{
			if (!Directory.Exists(root))
			{
				return null;
			}
			return (from directory in Directory.EnumerateDirectories(root, "app-*", SearchOption.TopDirectoryOnly)
				select new
				{
					Directory = directory,
					Executable = Path.Combine(directory, executableName2)
				} into item
				where File.Exists(item.Executable)
				orderby Directory.GetLastWriteTimeUtc(item.Directory) descending
				select item.Executable).FirstOrDefault();
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}
}
public class MainWindow : Window, IComponentConnector, IStyleConnector
{
	private sealed record SelectionOption<T>(T Value, string Label)
	{
		public override string ToString()
		{
			return Label;
		}
	}

	private readonly AppSettings _settings;

	private readonly ISettingsStore _settingsStore;

	private readonly IWallpaperController _wallpaperController;

	private readonly IMonitorService _monitorService;

	private readonly ISystemShortcutService _systemShortcutService;

	private readonly IAppLogger _logger;

	private readonly string _samplePath;

	private readonly QuickSlotHotkeyManager _hotkeyManager = new QuickSlotHotkeyManager();

	private readonly WindowsQuickSlotRuntimeService _quickSlotRuntimeService = new WindowsQuickSlotRuntimeService();

	private readonly WindowsSystemEventMonitor _systemEventMonitor;

	private readonly DispatcherTimer _quickSlotRuntimeTimer;

	private readonly SemaphoreSlim _wallpaperApplyGate = new SemaphoreSlim(1, 1);

	private CancellationTokenSource? _colorAdjustmentUpdate;

	private CancellationTokenSource? _wallpaperApplyCancellation;

	private HwndSource? _windowSource;

	private WallpaperItem? _sampleWallpaper;

	private bool _initialized;

	private bool _runtimeRefreshInProgress;

	private bool _runtimeClosing;

	private bool _applicationRuleSelectionSync;

	internal TextBlock SidebarStatusText;

	internal Grid HomePage;

	internal TextBlock NowPlayingText;

	internal TextBlock HomeStatusText;

	internal System.Windows.Controls.Button PauseButton;

	internal Grid LibraryPage;

	internal System.Windows.Controls.ListBox LibraryList;

	internal Grid DisplaysPage;

	internal System.Windows.Controls.ListBox DisplayList;

	internal Grid WidgetsPage;

	internal System.Windows.Controls.CheckBox WidgetsEnabledCheckBox;

	internal System.Windows.Controls.CheckBox ClockWidgetCheckBox;

	internal System.Windows.Controls.CheckBox SystemWidgetCheckBox;

	internal System.Windows.Controls.CheckBox QuickSlotsWidgetCheckBox;

	internal System.Windows.Controls.CheckBox CodexCompanionWidgetCheckBox;

	internal System.Windows.Controls.ComboBox ClockSideComboBox;

	internal System.Windows.Controls.ComboBox SystemSideComboBox;

	internal System.Windows.Controls.ComboBox CodexSideComboBox;

	internal TextBlock CodexBridgeStatusText;

	internal System.Windows.Controls.CheckBox DockEnabledCheckBox;

	internal System.Windows.Controls.CheckBox DockRunningAppsCheckBox;

	internal System.Windows.Controls.CheckBox DockHideTaskbarCheckBox;

	internal System.Windows.Controls.ComboBox DockIconSizeComboBox;

	internal System.Windows.Controls.ComboBox DockBottomMarginComboBox;

	internal TextBlock HotkeyStatusText;

	internal TextBlock KnownAppsStatusText;

	internal System.Windows.Controls.CheckBox SnapQuickSlotsCheckBox;

	internal System.Windows.Controls.ComboBox QuickSlotGridSizeComboBox;

	internal System.Windows.Controls.ComboBox QuickSlotGroupComboBox;

	internal TextBlock LayoutStatusText;

	internal ItemsControl SystemShortcutList;

	internal System.Windows.Controls.ListBox QuickSlotList;

	internal Grid SettingsPage;

	internal System.Windows.Controls.ComboBox FitComboBox;

	internal System.Windows.Controls.CheckBox MuteCheckBox;

	internal Run VolumeValueText;

	internal Slider VolumeSlider;

	internal Run BrightnessValueText;

	internal Slider BrightnessSlider;

	internal Run ContrastValueText;

	internal Slider ContrastSlider;

	internal Run SaturationValueText;

	internal Slider SaturationSlider;

	internal System.Windows.Controls.CheckBox AutoPauseForegroundAppCheckBox;

	internal System.Windows.Controls.ListBox ApplicationRuleList;

	internal System.Windows.Controls.ComboBox ApplicationRuleConditionComboBox;

	internal System.Windows.Controls.ComboBox ApplicationRuleActionComboBox;

	internal TextBlock ApplicationRuleStatusText;

	internal System.Windows.Controls.CheckBox RestoreCheckBox;

	private bool _contentLoaded;

	public ObservableCollection<WallpaperItem> Library { get; } = new ObservableCollection<WallpaperItem>();


	public ObservableCollection<DisplayInfo> Displays { get; } = new ObservableCollection<DisplayInfo>();


	public ObservableCollection<QuickSlotItem> QuickSlots { get; } = new ObservableCollection<QuickSlotItem>();


	public ObservableCollection<SystemShortcutSetting> SystemShortcuts { get; } = new ObservableCollection<SystemShortcutSetting>();


	public ObservableCollection<QuickSlotGroup> QuickSlotGroups { get; } = new ObservableCollection<QuickSlotGroup>();


	public ObservableCollection<ApplicationPlaybackRule> ApplicationRules { get; } = new ObservableCollection<ApplicationPlaybackRule>();


	public bool AllowClose { get; set; }

	public event EventHandler? DockSettingsChanged;

	public event EventHandler? InitializationCompleted;

	public MainWindow(AppSettings settings, ISettingsStore settingsStore, IWallpaperController wallpaperController, IMonitorService monitorService, ISystemShortcutService systemShortcutService, IAppLogger logger, WindowsSystemEventMonitor systemEventMonitor, string samplePath)
	{
		InitializeComponent();
		_quickSlotRuntimeTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_quickSlotRuntimeTimer.Tick += QuickSlotRuntimeTimerOnTick;
		_quickSlotRuntimeService.StateChanged += QuickSlotRuntimeService_StateChanged;
		_settings = settings;
		_settingsStore = settingsStore;
		_wallpaperController = wallpaperController;
		_monitorService = monitorService;
		_systemShortcutService = systemShortcutService;
		_logger = logger;
		_systemEventMonitor = systemEventMonitor;
		_samplePath = samplePath;
		_systemEventMonitor.RecycleBinChanged += SystemEventMonitor_RuntimeStateChanged;
		_wallpaperController.StateChanged += WallpaperController_StateChanged;
		_wallpaperController.QuickSlotInvoked += WallpaperController_QuickSlotInvoked;
		_wallpaperController.QuickSlotSecondaryInvoked += WallpaperController_QuickSlotSecondaryInvoked;
		_wallpaperController.QuickSlotLayoutChanged += WallpaperController_QuickSlotLayoutChanged;
		_hotkeyManager.SlotInvoked += QuickSlotHotkeyManager_SlotInvoked;
		base.SourceInitialized += Window_SourceInitialized;
	}

	public void SetDockTaskbarHidden(bool hidden)
	{
		_settings.Dock.HideWindowsTaskbar = hidden;
		DockHideTaskbarCheckBox.IsChecked = hidden;
		if (_initialized)
		{
			this.DockSettingsChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public void SynchronizeDockItems()
	{
		QuickSlots.Clear();
		foreach (QuickSlotItem item in _settings.Widgets.QuickSlots.OrderBy((QuickSlotItem item) => item.Slot))
		{
			QuickSlots.Add(item);
		}
		SystemShortcuts.Clear();
		foreach (SystemShortcutSetting systemShortcut in _settings.Widgets.SystemShortcuts)
		{
			SystemShortcuts.Add(systemShortcut);
		}
		QuickSlotList.Items.Refresh();
		SystemShortcutList.Items.Refresh();
	}

	public void PrepareForExit()
	{
		if (!_runtimeClosing)
		{
			_runtimeClosing = true;
			Interlocked.Exchange(ref _wallpaperApplyCancellation, null)?.Cancel();
			_colorAdjustmentUpdate?.Cancel();
			_quickSlotRuntimeTimer.Stop();
			_quickSlotRuntimeTimer.Tick -= QuickSlotRuntimeTimerOnTick;
			_quickSlotRuntimeService.StateChanged -= QuickSlotRuntimeService_StateChanged;
			_systemEventMonitor.RecycleBinChanged -= SystemEventMonitor_RuntimeStateChanged;
			_quickSlotRuntimeService.Dispose();
		}
	}

	private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			return;
		}
		try
		{
			_sampleWallpaper = WallpaperValidator.CreateItem(_samplePath, isBuiltIn: true);
			_sampleWallpaper.Name = "Aurora Drift";
			_settings.Library.RemoveAll((WallpaperItem item) => item.IsBuiltIn);
			_settings.Library.Insert(0, _sampleWallpaper);
			foreach (WallpaperItem item in _settings.Library)
			{
				Library.Add(item);
			}
			LibraryList.ItemsSource = Library;
			foreach (DisplayInfo display in _monitorService.GetDisplays())
			{
				Displays.Add(display);
			}
			DisplayList.ItemsSource = Displays;
			DisplayList.SelectedItem = Displays.FirstOrDefault((DisplayInfo display) => display.Id == _settings.SelectedMonitorId) ?? Displays.FirstOrDefault((DisplayInfo display) => display.IsPrimary) ?? Displays.FirstOrDefault();
			SelectionOption<WallpaperFit>[] array = new SelectionOption<WallpaperFit>[3]
			{
				new SelectionOption<WallpaperFit>(WallpaperFit.Fill, "채우기"),
				new SelectionOption<WallpaperFit>(WallpaperFit.Fit, "맞춤"),
				new SelectionOption<WallpaperFit>(WallpaperFit.Stretch, "늘이기")
			};
			FitComboBox.ItemsSource = array;
			FitComboBox.SelectedItem = array.First((SelectionOption<WallpaperFit> option) => option.Value == _settings.Playback.Fit);
			MuteCheckBox.IsChecked = _settings.Playback.IsMuted;
			VolumeSlider.Value = _settings.Playback.Volume * 100.0;
			BrightnessSlider.Value = Math.Clamp(_settings.Playback.Brightness, 0.25, 1.25) * 100.0;
			ContrastSlider.Value = Math.Clamp(_settings.Playback.Contrast, 0.5, 1.5) * 100.0;
			SaturationSlider.Value = Math.Clamp(_settings.Playback.Saturation, 0.0, 1.5) * 100.0;
			AutoPauseForegroundAppCheckBox.IsChecked = _settings.Playback.PauseWhenForegroundAppIsMaximized;
			AutoPauseForegroundAppCheckBox.Content = "앱별 자동 재생 제어 사용";
			SelectionOption<ApplicationRuleCondition>[] itemsSource = new SelectionOption<ApplicationRuleCondition>[2]
			{
				new SelectionOption<ApplicationRuleCondition>(ApplicationRuleCondition.IsForeground, "전경 창일 때"),
				new SelectionOption<ApplicationRuleCondition>(ApplicationRuleCondition.IsRunning, "프로세스 실행 중")
			};
			ApplicationRuleConditionComboBox.ItemsSource = itemsSource;
			SelectionOption<ApplicationPlaybackAction>[] itemsSource2 = new SelectionOption<ApplicationPlaybackAction>[3]
			{
				new SelectionOption<ApplicationPlaybackAction>(ApplicationPlaybackAction.KeepRunning, "계속 재생"),
				new SelectionOption<ApplicationPlaybackAction>(ApplicationPlaybackAction.Pause, "일시정지"),
				new SelectionOption<ApplicationPlaybackAction>(ApplicationPlaybackAction.StopFreeMemory, "완전 정지 (GPU 메모리 해제)")
			};
			ApplicationRuleActionComboBox.ItemsSource = itemsSource2;
			PlaybackOptions playback = _settings.Playback;
			if (playback.ApplicationRules == null)
			{
				playback.ApplicationRules = ApplicationPlaybackRule.CreateDefaults();
			}
			foreach (ApplicationPlaybackRule applicationRule in _settings.Playback.ApplicationRules)
			{
				ApplicationRules.Add(applicationRule.Copy());
			}
			ApplicationRuleList.ItemsSource = ApplicationRules;
			ApplicationRuleList.SelectedItem = ApplicationRules.FirstOrDefault();
			SynchronizeApplicationRuleEditor();
			RestoreCheckBox.IsChecked = _settings.RestoreOnStartup;
			AppSettings settings = _settings;
			if (settings.Widgets == null)
			{
				settings.Widgets = new WidgetSettings();
			}
			_settings.Widgets.ShowMediaStatus = false;
			settings = _settings;
			if (settings.Dock == null)
			{
				settings.Dock = new DockSettings();
			}
			SelectionOption<WidgetSide>[] array2 = new SelectionOption<WidgetSide>[2]
			{
				new SelectionOption<WidgetSide>(WidgetSide.Left, "왼쪽"),
				new SelectionOption<WidgetSide>(WidgetSide.Right, "오른쪽")
			};
			ClockSideComboBox.ItemsSource = array2;
			ClockSideComboBox.SelectedItem = array2.First((SelectionOption<WidgetSide> option) => option.Value == _settings.Widgets.ClockSide);
			SystemSideComboBox.ItemsSource = array2;
			SystemSideComboBox.SelectedItem = array2.First((SelectionOption<WidgetSide> option) => option.Value == _settings.Widgets.SystemInfoSide);
			CodexSideComboBox.ItemsSource = array2;
			CodexSideComboBox.SelectedItem = array2.First((SelectionOption<WidgetSide> option) => option.Value == _settings.Widgets.CodexCompanionSide);
			SnapQuickSlotsCheckBox.IsChecked = _settings.Widgets.SnapQuickSlotsToGrid;
			QuickSlotGridSizeComboBox.ItemsSource = new int[3] { 20, 40, 80 };
			QuickSlotGridSizeComboBox.SelectedItem = _settings.Widgets.QuickSlotGridSize;
			if (QuickSlotGridSizeComboBox.SelectedItem == null)
			{
				QuickSlotGridSizeComboBox.SelectedItem = 40;
			}
			foreach (QuickSlotGroup quickSlotGroup in _settings.Widgets.QuickSlotGroups)
			{
				QuickSlotGroups.Add(quickSlotGroup);
			}
			QuickSlotGroupComboBox.ItemsSource = QuickSlotGroups;
			QuickSlotGroupComboBox.SelectedItem = QuickSlotGroups.FirstOrDefault();
			foreach (QuickSlotItem item2 in from item in _settings.Widgets.QuickSlots.Where(delegate(QuickSlotItem item)
				{
					int slot = item.Slot;
					return slot >= 1 && slot <= 99;
				})
				orderby item.Slot
				group item by item.Slot into @group
				select @group.First())
			{
				QuickSlots.Add(item2);
			}
			QuickSlotList.ItemsSource = QuickSlots;
			foreach (SystemShortcutSetting systemShortcut in _settings.Widgets.SystemShortcuts)
			{
				SystemShortcuts.Add(systemShortcut);
			}
			SystemShortcutList.ItemsSource = SystemShortcuts;
			(int, IReadOnlyList<string>) result = DiscoverAndAddKnownApps();
			UpdateKnownAppsStatus(result);
			WidgetsEnabledCheckBox.IsChecked = _settings.Widgets.IsEnabled;
			ClockWidgetCheckBox.IsChecked = _settings.Widgets.ShowClock;
			_settings.Widgets.ShowCalendar = false;
			SystemWidgetCheckBox.IsChecked = _settings.Widgets.ShowSystemInfo;
			QuickSlotsWidgetCheckBox.IsChecked = _settings.Widgets.ShowQuickSlots;
			CodexCompanionWidgetCheckBox.IsChecked = _settings.Widgets.ShowCodexCompanion;
			DockEnabledCheckBox.IsChecked = _settings.Dock.IsEnabled;
			DockRunningAppsCheckBox.IsChecked = _settings.Dock.ShowRunningApplications;
			DockHideTaskbarCheckBox.IsChecked = _settings.Dock.HideWindowsTaskbar;
			DockIconSizeComboBox.ItemsSource = new int[4] { 44, 54, 64, 72 };
			DockIconSizeComboBox.SelectedItem = _settings.Dock.Copy().IconSize;
			if (DockIconSizeComboBox.SelectedItem == null)
			{
				DockIconSizeComboBox.SelectedItem = 54;
			}
			DockBottomMarginComboBox.ItemsSource = new int[5] { 0, 8, 12, 20, 32 };
			DockBottomMarginComboBox.SelectedItem = _settings.Dock.Copy().BottomMargin;
			if (DockBottomMarginComboBox.SelectedItem == null)
			{
				DockBottomMarginComboBox.SelectedItem = 12;
			}
			_initialized = true;
			await SaveAndRefreshWidgetsAsync();
			_quickSlotRuntimeTimer.Start();
			await RefreshQuickSlotRuntimeStateAsync();
			if (_settings.RestoreOnStartup && _settings.ActiveWallpaper != null)
			{
				WallpaperItem wallpaperItem = (_settings.ActiveWallpaper.IsBuiltIn ? _sampleWallpaper : _settings.ActiveWallpaper);
				if (wallpaperItem != null)
				{
					await ApplyWallpaperAsync(wallpaperItem, showDialogOnError: false);
				}
			}
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("Vellume 라이브러리를 초기화하지 못했습니다.", exception);
		}
		finally
		{
			try
			{
				await _wallpaperController.CompleteDesktopWallpaperHandoffAsync();
			}
			catch (Exception exception2)
			{
				await _logger.ErrorAsync("Windows 정지 배경 인계를 마무리하지 못했습니다.", exception2);
			}
			this.InitializationCompleted?.Invoke(this, EventArgs.Empty);
		}
	}

	private void Window_Closing(object? sender, CancelEventArgs eventArgs)
	{
		if (AllowClose)
		{
			PrepareForExit();
			_wallpaperController.StateChanged -= WallpaperController_StateChanged;
			_wallpaperController.QuickSlotInvoked -= WallpaperController_QuickSlotInvoked;
			_wallpaperController.QuickSlotSecondaryInvoked -= WallpaperController_QuickSlotSecondaryInvoked;
			_wallpaperController.QuickSlotLayoutChanged -= WallpaperController_QuickSlotLayoutChanged;
			_windowSource?.RemoveHook(WindowMessageHook);
			_windowSource = null;
			_hotkeyManager.Dispose();
		}
		else
		{
			eventArgs.Cancel = true;
			Hide();
		}
	}

	private async void ExitButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (System.Windows.Application.Current is App app)
		{
			await app.RequestExitAsync();
		}
	}

	private async Task ApplyWallpaperAsync(WallpaperItem wallpaper, bool showDialogOnError = true)
	{
		if (!(await _wallpaperApplyGate.WaitAsync(0)))
		{
			return;
		}
		if (_runtimeClosing)
		{
			_wallpaperApplyGate.Release();
			return;
		}
		using CancellationTokenSource cancellation = new CancellationTokenSource();
		_wallpaperApplyCancellation = cancellation;
		try
		{
			DisplayInfo display = (DisplayList.SelectedItem as DisplayInfo) ?? Displays.FirstOrDefault((DisplayInfo item) => item.IsPrimary);
			if (display == null)
			{
				if (showDialogOnError)
				{
					System.Windows.MessageBox.Show("연결된 디스플레이를 찾지 못했습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				}
				return;
			}
			try
			{
				HomeStatusText.Text = "WebView2를 준비하고 바탕화면에 연결하는 중…";
				SidebarStatusText.Text = "적용 중…";
				await _wallpaperController.ApplyAsync(wallpaper, display, _settings.Playback.Copy(), _settings.Widgets.Copy(), cancellation.Token);
				_settings.ActiveWallpaper = wallpaper;
				_settings.SelectedMonitorId = display.Id;
				NowPlayingText.Text = wallpaper.Name;
				LibraryList.SelectedItem = wallpaper;
				await SaveSettingsAsync();
			}
			catch (OperationCanceledException) when (_runtimeClosing || cancellation.IsCancellationRequested)
			{
			}
			catch (Exception exception)
			{
				await _logger.ErrorAsync("UI에서 월페이퍼 적용에 실패했습니다.", exception);
				HomeStatusText.Text = exception.Message;
				if (showDialogOnError)
				{
					System.Windows.MessageBox.Show(exception.Message, "월페이퍼를 적용할 수 없음", MessageBoxButton.OK, MessageBoxImage.Hand);
				}
			}
		}
		finally
		{
			Interlocked.CompareExchange(ref _wallpaperApplyCancellation, null, cancellation);
			_wallpaperApplyGate.Release();
		}
	}

	private async Task SaveSettingsAsync()
	{
		_settings.Library = Library.ToList();
		await _settingsStore.SaveAsync(_settings);
	}

	private async Task ReportErrorAsync(string message, Exception exception)
	{
		await _logger.ErrorAsync(message, exception);
		System.Windows.MessageBox.Show(message + "\n\n" + exception.Message, "Vellume", MessageBoxButton.OK, MessageBoxImage.Hand);
	}

	private void WallpaperController_StateChanged(object? sender, WallpaperStateChangedEventArgs eventArgs)
	{
		WallpaperStateChangedEventArgs eventArgs2 = eventArgs;
		base.Dispatcher.Invoke(delegate
		{
			SidebarStatusText.Text = LocalizePlaybackState(eventArgs2.State);
			HomeStatusText.Text = eventArgs2.Message;
			System.Windows.Controls.Button pauseButton = PauseButton;
			WallpaperPlaybackState state = eventArgs2.State;
			bool isEnabled = (uint)(state - 1) <= 1u;
			pauseButton.IsEnabled = isEnabled;
			PauseButton.Content = ((eventArgs2.State == WallpaperPlaybackState.Paused) ? "다시 재생" : "일시정지");
			if (eventArgs2.State == WallpaperPlaybackState.Stopped)
			{
				NowPlayingText.Text = "활성 월페이퍼 없음";
			}
		});
	}

	private void ShowPage(Grid page)
	{
		HomePage.Visibility = Visibility.Collapsed;
		LibraryPage.Visibility = Visibility.Collapsed;
		DisplaysPage.Visibility = Visibility.Collapsed;
		WidgetsPage.Visibility = Visibility.Collapsed;
		SettingsPage.Visibility = Visibility.Collapsed;
		page.Visibility = Visibility.Visible;
	}

	private void HomeNavigation_Click(object sender, RoutedEventArgs eventArgs)
	{
		ShowPage(HomePage);
	}

	private void LibraryNavigation_Click(object sender, RoutedEventArgs eventArgs)
	{
		ShowPage(LibraryPage);
	}

	private void DisplaysNavigation_Click(object sender, RoutedEventArgs eventArgs)
	{
		ShowPage(DisplaysPage);
	}

	private void WidgetsNavigation_Click(object sender, RoutedEventArgs eventArgs)
	{
		ShowPage(WidgetsPage);
	}

	private void SettingsNavigation_Click(object sender, RoutedEventArgs eventArgs)
	{
		ShowPage(SettingsPage);
	}

	private async void ApplySampleButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (_sampleWallpaper != null)
		{
			await ApplyWallpaperAsync(_sampleWallpaper);
		}
	}

	private async void ApplyCardButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (sender is System.Windows.Controls.Button { Tag: WallpaperItem tag })
		{
			await ApplyWallpaperAsync(tag);
		}
	}

	private async void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
	{
		for (DependencyObject dependencyObject = eventArgs.OriginalSource as DependencyObject; dependencyObject != null; dependencyObject = VisualTreeHelper.GetParent(dependencyObject))
		{
			if (dependencyObject is System.Windows.Controls.Button)
			{
				return;
			}
		}
		if (LibraryList.SelectedItem is WallpaperItem wallpaper)
		{
			await ApplyWallpaperAsync(wallpaper);
		}
	}

	private async void RemoveCardButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: var tag }) || !(tag is WallpaperItem wallpaper))
		{
			return;
		}
		if (wallpaper.IsBuiltIn)
		{
			System.Windows.MessageBox.Show("포함된 샘플은 라이브러리에서 삭제할 수 없습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		if (_settings.ActiveWallpaper?.Id == wallpaper.Id)
		{
			await _wallpaperController.StopAsync();
			_settings.ActiveWallpaper = null;
		}
		Library.Remove(wallpaper);
		await SaveSettingsAsync();
	}

	private async void AddFileButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "월페이퍼 추가",
			Filter = "지원 월페이퍼|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.webm;*.html;*.htm|이미지|*.jpg;*.jpeg;*.png;*.bmp;*.gif|영상|*.mp4;*.webm|로컬 HTML|*.html;*.htm",
			CheckFileExists = true
		};
		if (openFileDialog.ShowDialog(this).GetValueOrDefault())
		{
			await AddWallpaperPathAsync(openFileDialog.FileName);
		}
	}

	private async void AddFolderButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		using FolderBrowserDialog dialog = new FolderBrowserDialog
		{
			Description = "index.html이 있는 폴더를 선택하세요",
			UseDescriptionForTitle = true,
			ShowNewFolderButton = false
		};
		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			await AddWallpaperPathAsync(dialog.SelectedPath);
		}
	}

	private async Task AddWallpaperPathAsync(string path)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			WallpaperItem wallpaperItem = Library.FirstOrDefault((WallpaperItem item) => item.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
			if (wallpaperItem != null)
			{
				LibraryList.SelectedItem = wallpaperItem;
				return;
			}
			WallpaperItem wallpaperItem2 = WallpaperValidator.CreateItem(fullPath);
			Library.Add(wallpaperItem2);
			LibraryList.SelectedItem = wallpaperItem2;
			await SaveSettingsAsync();
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("해당 소스를 추가할 수 없습니다.", exception);
		}
	}

	private async void StopButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		try
		{
			await _wallpaperController.StopAsync();
			_settings.ActiveWallpaper = null;
			await SaveSettingsAsync();
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("월페이퍼를 안전하게 중지하지 못했습니다.", exception);
		}
	}

	private async void PauseButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		try
		{
			if (_wallpaperController.State != WallpaperPlaybackState.Paused)
			{
				await _wallpaperController.PauseAsync();
			}
			else
			{
				await _wallpaperController.ResumeAsync();
			}
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("재생 상태를 변경하지 못했습니다.", exception);
		}
	}

	private async void DisplayList_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
	{
		if (_initialized && DisplayList.SelectedItem is DisplayInfo displayInfo)
		{
			_settings.SelectedMonitorId = displayInfo.Id;
			await _wallpaperController.UpdateWidgetsAsync(_settings.Widgets.Copy(), displayInfo);
			await SaveSettingsAsync();
		}
	}

	private async void FitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
	{
		if (_initialized && FitComboBox.SelectedItem is SelectionOption<WallpaperFit> selectionOption)
		{
			_settings.Playback.Fit = selectionOption.Value;
			await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy());
			await SaveSettingsAsync();
		}
	}

	private async void MuteCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			_settings.Playback.IsMuted = MuteCheckBox.IsChecked.GetValueOrDefault();
			await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy());
			await SaveSettingsAsync();
		}
	}

	private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
	{
		VolumeValueText.Text = $"{Math.Round(VolumeSlider.Value):0}%";
		if (_initialized)
		{
			_settings.Playback.Volume = VolumeSlider.Value / 100.0;
			await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy());
			await SaveSettingsAsync();
		}
	}

	private void ColorAdjustmentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
	{
		if (BrightnessSlider != null && ContrastSlider != null && SaturationSlider != null && BrightnessValueText != null && ContrastValueText != null && SaturationValueText != null)
		{
			BrightnessValueText.Text = $"{Math.Round(BrightnessSlider.Value):0}%";
			ContrastValueText.Text = $"{Math.Round(ContrastSlider.Value):0}%";
			SaturationValueText.Text = $"{Math.Round(SaturationSlider.Value):0}%";
			if (_initialized)
			{
				_settings.Playback.Brightness = BrightnessSlider.Value / 100.0;
				_settings.Playback.Contrast = ContrastSlider.Value / 100.0;
				_settings.Playback.Saturation = SaturationSlider.Value / 100.0;
				_colorAdjustmentUpdate?.Cancel();
				ApplyColorAdjustmentAfterDelayAsync(_colorAdjustmentUpdate = new CancellationTokenSource());
			}
		}
	}

	private async Task ApplyColorAdjustmentAfterDelayAsync(CancellationTokenSource cancellation)
	{
		_ = 3;
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(120.0), cancellation.Token);
			await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy(), cancellation.Token);
			await SaveSettingsAsync();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("월페이퍼 화면 보정을 적용하지 못했습니다.", exception);
		}
		finally
		{
			if (_colorAdjustmentUpdate == cancellation)
			{
				_colorAdjustmentUpdate = null;
			}
			cancellation.Dispose();
		}
	}

	private void ResetColorAdjustmentButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		BrightnessSlider.Value = 100.0;
		ContrastSlider.Value = 100.0;
		SaturationSlider.Value = 100.0;
		if (_initialized)
		{
			_settings.Playback.Brightness = 1.0;
			_settings.Playback.Contrast = 1.0;
			_settings.Playback.Saturation = 1.0;
			_colorAdjustmentUpdate?.Cancel();
			ApplyColorAdjustmentAfterDelayAsync(_colorAdjustmentUpdate = new CancellationTokenSource());
		}
	}

	private async void RestoreCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			_settings.RestoreOnStartup = RestoreCheckBox.IsChecked.GetValueOrDefault();
			await SaveSettingsAsync();
		}
	}

	private async void AutoPauseForegroundAppCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			_settings.Playback.PauseWhenForegroundAppIsMaximized = AutoPauseForegroundAppCheckBox.IsChecked.GetValueOrDefault();
			await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy());
			await SaveSettingsAsync();
		}
	}

	private void ApplicationRuleList_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
	{
		SynchronizeApplicationRuleEditor();
	}

	private async void ApplicationRuleEditor_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
	{
		if (_initialized && !_applicationRuleSelectionSync)
		{
			object selectedItem = ApplicationRuleList.SelectedItem;
			if (selectedItem is ApplicationPlaybackRule rule && ApplicationRuleConditionComboBox.SelectedItem is SelectionOption<ApplicationRuleCondition> selectionOption && ApplicationRuleActionComboBox.SelectedItem is SelectionOption<ApplicationPlaybackAction> selectionOption2 && (rule.Condition != selectionOption.Value || rule.Action != selectionOption2.Value))
			{
				rule.Condition = selectionOption.Value;
				rule.Action = selectionOption2.Value;
				await PersistApplicationRulesAsync();
				UpdateApplicationRuleStatus(rule);
			}
		}
	}

	private async void AddApplicationRuleButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "자동 재생 규칙에 추가할 실행 파일 선택",
			Filter = "실행 파일|*.exe",
			CheckFileExists = true,
			Multiselect = false
		};
		if (!openFileDialog.ShowDialog(this).GetValueOrDefault())
		{
			return;
		}
		string processName = Path.GetFileNameWithoutExtension(openFileDialog.FileName).Trim();
		if (!string.IsNullOrWhiteSpace(processName))
		{
			ApplicationPlaybackRule applicationPlaybackRule = ApplicationRules.FirstOrDefault((ApplicationPlaybackRule rule) => string.Equals(Path.GetFileNameWithoutExtension(rule.ProcessName), processName, StringComparison.OrdinalIgnoreCase));
			if (applicationPlaybackRule != null)
			{
				ApplicationRuleList.SelectedItem = applicationPlaybackRule;
				ApplicationRuleList.ScrollIntoView(applicationPlaybackRule);
				UpdateApplicationRuleStatus(applicationPlaybackRule);
				return;
			}
			ApplicationPlaybackRule applicationPlaybackRule2 = new ApplicationPlaybackRule
			{
				ProcessName = processName,
				Condition = ApplicationRuleCondition.IsRunning,
				Action = ApplicationPlaybackAction.StopFreeMemory
			};
			ApplicationRules.Add(applicationPlaybackRule2);
			ApplicationRuleList.SelectedItem = applicationPlaybackRule2;
			ApplicationRuleList.ScrollIntoView(applicationPlaybackRule2);
			await PersistApplicationRulesAsync();
		}
	}

	private async void RemoveApplicationRuleButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (ApplicationRuleList.SelectedItem is ApplicationPlaybackRule item)
		{
			int selectedIndex = ApplicationRuleList.SelectedIndex;
			ApplicationRules.Remove(item);
			ApplicationRuleList.SelectedItem = ApplicationRules.ElementAtOrDefault(Math.Clamp(selectedIndex, 0, Math.Max(0, ApplicationRules.Count - 1)));
			await PersistApplicationRulesAsync();
			SynchronizeApplicationRuleEditor();
		}
	}

	private void SynchronizeApplicationRuleEditor()
	{
		_applicationRuleSelectionSync = true;
		try
		{
			object selectedItem = ApplicationRuleList.SelectedItem;
			ApplicationPlaybackRule rule = selectedItem as ApplicationPlaybackRule;
			if (rule == null)
			{
				ApplicationRuleConditionComboBox.SelectedItem = null;
				ApplicationRuleActionComboBox.SelectedItem = null;
				ApplicationRuleStatusText.Text = "규칙을 선택하거나 실행 파일을 추가하세요.";
				return;
			}
			ApplicationRuleConditionComboBox.SelectedItem = ApplicationRuleConditionComboBox.Items.OfType<SelectionOption<ApplicationRuleCondition>>().First((SelectionOption<ApplicationRuleCondition> option) => option.Value == rule.Condition);
			ApplicationRuleActionComboBox.SelectedItem = ApplicationRuleActionComboBox.Items.OfType<SelectionOption<ApplicationPlaybackAction>>().First((SelectionOption<ApplicationPlaybackAction> option) => option.Value == rule.Action);
			UpdateApplicationRuleStatus(rule);
		}
		finally
		{
			_applicationRuleSelectionSync = false;
		}
	}

	private void UpdateApplicationRuleStatus(ApplicationPlaybackRule rule)
	{
		string value = ((rule.Condition == ApplicationRuleCondition.IsRunning) ? "실행되는 동안" : "전경 창일 때");
		string value2 = rule.Action switch
		{
			ApplicationPlaybackAction.KeepRunning => "계속 재생", 
			ApplicationPlaybackAction.Pause => "일시정지", 
			ApplicationPlaybackAction.StopFreeMemory => "렌더러 완전 정지 및 GPU 메모리 해제", 
			_ => rule.Action.ToString(), 
		};
		ApplicationRuleStatusText.Text = $"{rule.ProcessName}: {value} · {value2}";
	}

	private async Task PersistApplicationRulesAsync()
	{
		_settings.Playback.ApplicationRules = ApplicationRules.Select((ApplicationPlaybackRule rule) => rule.Copy()).ToList();
		await _wallpaperController.UpdatePlaybackAsync(_settings.Playback.Copy());
		await SaveSettingsAsync();
	}

	private void Window_SourceInitialized(object? sender, EventArgs eventArgs)
	{
		nint handle = new WindowInteropHelper(this).Handle;
		_windowSource = HwndSource.FromHwnd(handle);
		_windowSource?.AddHook(WindowMessageHook);
		_quickSlotRuntimeService.Attach(handle);
		int num = _hotkeyManager.Register(handle);
		_logger.InfoAsync($"Registered {num}/9 quick-slot shortcuts.");
		if (num < 9)
		{
			HotkeyStatusText.Text = $"{num}/9개 단축키를 등록했습니다. 나머지 키는 다른 앱이 사용 중입니다.";
		}
	}

	private nint WindowMessageHook(nint windowHandle, int message, nint wParam, nint lParam, ref bool handled)
	{
		handled = _quickSlotRuntimeService.TryHandleShellMessage(message, wParam, lParam) || _hotkeyManager.TryHandleMessage(message, wParam);
		return IntPtr.Zero;
	}

	private async void QuickSlotRuntimeTimerOnTick(object? sender, EventArgs eventArgs)
	{
		await RefreshQuickSlotRuntimeStateAsync();
	}

	private void QuickSlotRuntimeService_StateChanged(object? sender, EventArgs eventArgs)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			RefreshQuickSlotRuntimeStateAsync();
		});
	}

	private void SystemEventMonitor_RuntimeStateChanged(object? sender, EventArgs eventArgs)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			RefreshQuickSlotRuntimeStateAsync();
		});
	}

	private async Task RefreshQuickSlotRuntimeStateAsync()
	{
		if (!_initialized || _runtimeClosing || _runtimeRefreshInProgress)
		{
			return;
		}
		_runtimeRefreshInProgress = true;
		try
		{
			QuickSlotItem[] quickSlotSnapshot = QuickSlots.Select((QuickSlotItem item) => item.Copy()).ToArray();
			SystemShortcutSetting[] systemSnapshot = SystemShortcuts.Select((SystemShortcutSetting item) => item.Copy()).ToArray();
			QuickSlotRuntimeSnapshot quickSlotRuntimeSnapshot = await Task.Run(() => _quickSlotRuntimeService.Capture(quickSlotSnapshot, systemSnapshot));
			if (_runtimeClosing)
			{
				return;
			}
			bool flag = false;
			foreach (QuickSlotItem quickSlot in QuickSlots)
			{
				if (!quickSlotRuntimeSnapshot.QuickSlots.TryGetValue(quickSlot.Slot, out var value))
				{
					value = default(QuickSlotRuntimeState);
				}
				if (quickSlot.IsRunning != value.IsRunning || quickSlot.HasAttention != value.HasAttention)
				{
					quickSlot.IsRunning = value.IsRunning;
					quickSlot.HasAttention = value.HasAttention;
					flag = true;
				}
			}
			foreach (SystemShortcutSetting systemShortcut in SystemShortcuts)
			{
				if (!quickSlotRuntimeSnapshot.SystemShortcuts.TryGetValue(systemShortcut.Kind, out var value2))
				{
					value2 = default(QuickSlotRuntimeState);
				}
				if (systemShortcut.IsRunning != value2.IsRunning || systemShortcut.HasAttention != value2.HasAttention)
				{
					systemShortcut.IsRunning = value2.IsRunning;
					systemShortcut.HasAttention = value2.HasAttention;
					flag = true;
				}
			}
			if (!flag)
			{
				return;
			}
			QuickSlotList.Items.Refresh();
			SystemShortcutList.Items.Refresh();
			_settings.Widgets.QuickSlots = (from item in QuickSlots
				orderby item.Slot
				select item.Copy()).ToList();
			_settings.Widgets.SystemShortcuts = SystemShortcuts.Select((SystemShortcutSetting item) => item.Copy()).ToList();
			await _wallpaperController.UpdateQuickSlotRuntimeAsync(_settings.Widgets.Copy());
		}
		catch (ObjectDisposedException) when (_runtimeClosing)
		{
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Quick-slot runtime state refresh failed.", exception);
		}
		finally
		{
			_runtimeRefreshInProgress = false;
		}
	}

	private void QuickSlotHotkeyManager_SlotInvoked(object? sender, int slot)
	{
		LaunchQuickSlotAsync(slot);
	}

	private void WallpaperController_QuickSlotInvoked(object? sender, int slot)
	{
		base.Dispatcher.Invoke(() => LaunchQuickSlotAsync(slot));
	}

	private void WallpaperController_QuickSlotSecondaryInvoked(object? sender, int slot)
	{
		base.Dispatcher.Invoke(() => HandleQuickSlotSecondaryActionAsync(slot));
	}

	private void WallpaperController_QuickSlotLayoutChanged(object? sender, QuickSlotLayoutChangedEventArgs eventArgs)
	{
		QuickSlotLayoutChangedEventArgs eventArgs2 = eventArgs;
		base.Dispatcher.Invoke(delegate
		{
			QuickSlotGroup quickSlotGroup = QuickSlotGroups.FirstOrDefault((QuickSlotGroup candidate) => candidate.Id.Equals(eventArgs2.GroupId, StringComparison.OrdinalIgnoreCase));
			if (quickSlotGroup != null)
			{
				quickSlotGroup.X = eventArgs2.X;
				quickSlotGroup.Y = eventArgs2.Y;
				QuickSlotGroup quickSlotGroup2 = _settings.Widgets.QuickSlotGroups.FirstOrDefault((QuickSlotGroup candidate) => candidate.Id.Equals(eventArgs2.GroupId, StringComparison.OrdinalIgnoreCase));
				if (quickSlotGroup2 != null)
				{
					quickSlotGroup2.X = eventArgs2.X;
					quickSlotGroup2.Y = eventArgs2.Y;
				}
				LayoutStatusText.Text = $"{quickSlotGroup.Name} 위치 저장됨: {quickSlotGroup.X}, {quickSlotGroup.Y}";
				SaveSettingsAsync();
			}
		});
	}

	private async Task LaunchQuickSlotAsync(int slot)
	{
		QuickSlotItem item = QuickSlots.FirstOrDefault((QuickSlotItem candidate) => candidate.Slot == slot);
		SystemShortcutSetting systemItem = SystemShortcuts.FirstOrDefault((SystemShortcutSetting candidate) => candidate.IsVisible && candidate.Slot == slot);
		if (!_settings.Widgets.IsEnabled || !_settings.Widgets.ShowQuickSlots || (item == null && systemItem == null))
		{
			return;
		}
		try
		{
			if (systemItem != null)
			{
				bool activated = _quickSlotRuntimeService.TryActivateSystemShortcut(systemItem.Kind);
				if (!activated)
				{
					await _systemShortcutService.LaunchAsync(systemItem.Kind);
				}
				await _logger.InfoAsync($"System quick slot {slot} {(activated ? "activated" : "launched")} '{systemItem.Kind}'.");
			}
			else
			{
				QuickSlotActivationResult quickSlotActivationResult = await _quickSlotRuntimeService.ActivateOrLaunchAsync(item, forceNewInstance: false);
				await _logger.InfoAsync($"Quick slot {slot} {((quickSlotActivationResult == QuickSlotActivationResult.Activated) ? "activated" : "launched")} '{item.TargetPath}'.");
			}
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			await _logger.ErrorAsync($"Quick slot {slot} could not be launched.", exception);
			base.Dispatcher.Invoke(delegate
			{
				HotkeyStatusText.Text = exception.Message;
			});
		}
	}

	private async Task HandleQuickSlotSecondaryActionAsync(int slot)
	{
		QuickSlotItem item = QuickSlots.FirstOrDefault((QuickSlotItem candidate) => candidate.Slot == slot);
		SystemShortcutSetting systemItem = SystemShortcuts.FirstOrDefault((SystemShortcutSetting candidate) => candidate.IsVisible && candidate.Slot == slot);
		if (item == null && systemItem == null)
		{
			return;
		}
		try
		{
			if (systemItem != null && systemItem.Kind == SystemShortcutKind.RecycleBin)
			{
				await _systemShortcutService.EmptyRecycleBinAsync(new WindowInteropHelper(this).Handle);
				await _logger.InfoAsync("Recycle Bin empty action completed or was cancelled.");
				await SaveAndRefreshWidgetsAsync();
				return;
			}
			if (systemItem != null)
			{
				await _systemShortcutService.LaunchAsync(systemItem.Kind);
				await _logger.InfoAsync($"System quick slot {slot} launched a new '{systemItem.Kind}' window.");
			}
			else if (item != null)
			{
				await _quickSlotRuntimeService.ActivateOrLaunchAsync(item, forceNewInstance: true);
				await _logger.InfoAsync($"Quick slot {slot} requested a new instance of '{item.TargetPath}'.");
			}
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("퀵슬롯 보조 동작에 실패했습니다.", exception);
		}
	}

	private async void AddQuickSlotButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (QuickSlots.Count >= 99)
		{
			System.Windows.MessageBox.Show("퀵슬롯 최대 개수 99개에 도달했습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "퀵슬롯에 넣을 앱 또는 파일 선택",
			Filter = "앱과 파일|*.*",
			CheckFileExists = true
		};
		if (openFileDialog.ShowDialog(this).GetValueOrDefault())
		{
			await AddQuickSlotItemAsync(Path.GetFileNameWithoutExtension(openFileDialog.FileName), Path.GetFullPath(openFileDialog.FileName));
		}
	}

	private async Task AddQuickSlotItemAsync(string name, string targetPath)
	{
		HashSet<int> occupiedSlots = (from slot in QuickSlots.Select((QuickSlotItem item) => item.Slot).Concat(from item in SystemShortcuts
				where item.IsVisible
				select item.Slot)
			where slot >= 1 && slot <= 99
			select slot).ToHashSet();
		if (occupiedSlots.Count >= 99)
		{
			System.Windows.MessageBox.Show("퀵슬롯 최대 개수 99개에 도달했습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		QuickSlotGroup quickSlotGroup = (QuickSlotGroupComboBox.SelectedItem as QuickSlotGroup) ?? QuickSlotGroups.First();
		int slot2 = Enumerable.Range(1, 99).First((int candidate) => !occupiedSlots.Contains(candidate));
		QuickSlots.Add(new QuickSlotItem
		{
			Slot = slot2,
			Name = name,
			TargetPath = targetPath,
			GroupId = quickSlotGroup.Id,
			GroupName = quickSlotGroup.Name,
			ShowInDock = true,
			DockOrder = GetNextDockOrder()
		});
		SortQuickSlots();
		await SaveAndRefreshWidgetsAsync();
	}

	private async void DiscoverKnownAppsButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			try
			{
				(int, IReadOnlyList<string>) result = DiscoverAndAddKnownApps();
				UpdateKnownAppsStatus(result);
				await SaveAndRefreshWidgetsAsync();
			}
			catch (Exception exception)
			{
				await ReportErrorAsync("설치된 앱을 찾지 못했습니다.", exception);
			}
		}
	}

	private (int Found, IReadOnlyList<string> Added) DiscoverAndAddKnownApps()
	{
		EnsurePresetQuickSlotGroups();
		IReadOnlyList<KnownAppMatch> readOnlyList = KnownAppDiscoveryService.Discover();
		List<string> list = new List<string>();
		HashSet<int> occupiedSlots = (from slot in QuickSlots.Select((QuickSlotItem item) => item.Slot).Concat(from item in SystemShortcuts
				where item.IsVisible
				select item.Slot)
			where slot >= 1 && slot <= 99
			select slot).ToHashSet();
		foreach (KnownAppMatch match in readOnlyList)
		{
			QuickSlotItem quickSlotItem = QuickSlots.FirstOrDefault((QuickSlotItem item) => IsKnownAppMatch(item, match));
			if (quickSlotItem != null)
			{
				if (!File.Exists(quickSlotItem.TargetPath) && File.Exists(match.TargetPath))
				{
					quickSlotItem.TargetPath = match.TargetPath;
				}
				continue;
			}
			int num = Enumerable.Range(1, 99).FirstOrDefault((int candidate) => !occupiedSlots.Contains(candidate));
			if (num == 0)
			{
				break;
			}
			QuickSlotGroup quickSlotGroup = FindPresetGroup(match.GroupName);
			if (quickSlotGroup != null)
			{
				QuickSlots.Add(new QuickSlotItem
				{
					Slot = num,
					Name = match.Name,
					TargetPath = match.TargetPath,
					GroupId = quickSlotGroup.Id,
					GroupName = quickSlotGroup.Name,
					ShowInDock = true,
					DockOrder = GetNextDockOrder()
				});
				occupiedSlots.Add(num);
				list.Add(match.Name);
			}
		}
		if (list.Count > 0)
		{
			SortQuickSlots();
		}
		return (Found: readOnlyList.Count, Added: list);
	}

	private int GetNextDockOrder()
	{
		return (from item in SystemShortcuts
			where item.ShowInDock
			select item.DockOrder).Concat(from item in QuickSlots
			where item.ShowInDock
			select item.DockOrder).DefaultIfEmpty(0).Max() + 10;
	}

	private void EnsurePresetQuickSlotGroups()
	{
		foreach (QuickSlotGroup item in QuickSlotGroup.CreatePresetGroups())
		{
			if (FindPresetGroup(item.Name) == null)
			{
				QuickSlotGroups.Add(item);
			}
		}
	}

	private QuickSlotGroup? FindPresetGroup(string name)
	{
		string name2 = name;
		QuickSlotGroup preset = QuickSlotGroup.CreatePresetGroups().FirstOrDefault((QuickSlotGroup candidate) => candidate.Name.Equals(name2, StringComparison.OrdinalIgnoreCase));
		return QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => group.Name.Equals(name2, StringComparison.OrdinalIgnoreCase) || (preset != null && group.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase)));
	}

	private static bool IsKnownAppMatch(QuickSlotItem item, KnownAppMatch match)
	{
		if (item.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		try
		{
			if (Path.GetFullPath(item.TargetPath).Equals(match.TargetPath, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		catch (Exception ex) when (((ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) ? 1 : 0) != 0)
		{
		}
		return Path.GetFileName(item.TargetPath).Equals(Path.GetFileName(match.TargetPath), StringComparison.OrdinalIgnoreCase);
	}

	private void UpdateKnownAppsStatus((int Found, IReadOnlyList<string> Added) result)
	{
		TextBlock knownAppsStatusText = KnownAppsStatusText;
		string text = ((result.Added.Count > 0) ? ($"설치된 앱 {result.Added.Count}개 추가: " + string.Join(", ", result.Added)) : ((result.Found <= 0) ? "기본 설치 경로에서 대상 앱을 찾지 못했습니다." : $"확인된 앱 {result.Found}개가 이미 정리되어 있습니다."));
		knownAppsStatusText.Text = text;
	}

	private async void SystemShortcutVisibility_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (!_initialized || !(sender is System.Windows.Controls.CheckBox { DataContext: var dataContext } checkBox))
		{
			return;
		}
		SystemShortcutSetting item = dataContext as SystemShortcutSetting;
		if (item == null)
		{
			return;
		}
		item.IsVisible = checkBox.IsChecked.GetValueOrDefault();
		if (item.IsVisible)
		{
			HashSet<int> occupiedSlots = (from slot in QuickSlots.Select((QuickSlotItem candidate) => candidate.Slot).Concat(from candidate in SystemShortcuts
					where candidate.IsVisible && candidate != item
					select candidate.Slot)
				where slot >= 1 && slot <= 99
				select slot).ToHashSet();
			int slot2 = item.Slot;
			if (slot2 < 1 || slot2 > 99 || occupiedSlots.Contains(item.Slot))
			{
				item.Slot = Enumerable.Range(1, 99).FirstOrDefault((int slot) => !occupiedSlots.Contains(slot));
			}
			if (item.Slot == 0)
			{
				item.IsVisible = false;
				checkBox.IsChecked = false;
				System.Windows.MessageBox.Show("사용 가능한 퀵슬롯 번호가 없습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
		}
		SystemShortcutList.Items.Refresh();
		await SaveAndRefreshWidgetsAsync();
	}

	private async void ChooseSystemShortcutIconButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (sender is System.Windows.Controls.Button { Tag: SystemShortcutSetting tag })
		{
			Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
			{
				Title = tag.Name + " 아이콘 선택",
				Filter = "아이콘 이미지|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.exe;*.dll|모든 파일|*.*",
				CheckFileExists = true
			};
			if (openFileDialog.ShowDialog(this).GetValueOrDefault())
			{
				tag.CustomIconPath = Path.GetFullPath(openFileDialog.FileName);
				SystemShortcutList.Items.Refresh();
				await SaveAndRefreshWidgetsAsync();
			}
		}
	}

	private async void ResetSystemShortcutIconButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (sender is System.Windows.Controls.Button { Tag: SystemShortcutSetting tag })
		{
			tag.CustomIconPath = string.Empty;
			SystemShortcutList.Items.Refresh();
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void RemoveQuickSlotButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (sender is System.Windows.Controls.Button { Tag: QuickSlotItem tag })
		{
			QuickSlots.Remove(tag);
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void WidgetCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			_settings.Widgets.IsEnabled = WidgetsEnabledCheckBox.IsChecked.GetValueOrDefault();
			_settings.Widgets.ShowClock = ClockWidgetCheckBox.IsChecked.GetValueOrDefault();
			_settings.Widgets.ShowCalendar = false;
			_settings.Widgets.ShowSystemInfo = SystemWidgetCheckBox.IsChecked.GetValueOrDefault();
			_settings.Widgets.ShowQuickSlots = false;
			_settings.Widgets.ShowMediaStatus = false;
			_settings.Widgets.ShowCodexCompanion = CodexCompanionWidgetCheckBox.IsChecked.GetValueOrDefault();
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void WidgetPlacement_Changed(object sender, SelectionChangedEventArgs eventArgs)
	{
		if (_initialized)
		{
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void GridSettings_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			await SaveAndRefreshWidgetsAsync();
			LayoutStatusText.Text = ((SnapQuickSlotsCheckBox.IsChecked.GetValueOrDefault() && QuickSlotGridSizeComboBox.SelectedItem is int value) ? $"그리드 맞춤 사용: {value}px" : "그리드 맞춤을 사용하지 않습니다.");
		}
	}

	private async Task SaveAndRefreshWidgetsAsync()
	{
		if (ClockSideComboBox.SelectedItem is SelectionOption<WidgetSide> selectionOption)
		{
			_settings.Widgets.ClockSide = selectionOption.Value;
		}
		if (SystemSideComboBox.SelectedItem is SelectionOption<WidgetSide> selectionOption2)
		{
			_settings.Widgets.SystemInfoSide = selectionOption2.Value;
		}
		if (CodexSideComboBox.SelectedItem is SelectionOption<WidgetSide> selectionOption3)
		{
			_settings.Widgets.CodexCompanionSide = selectionOption3.Value;
		}
		_settings.Widgets.SnapQuickSlotsToGrid = SnapQuickSlotsCheckBox.IsChecked.GetValueOrDefault();
		if (QuickSlotGridSizeComboBox.SelectedItem is int quickSlotGridSize)
		{
			_settings.Widgets.QuickSlotGridSize = quickSlotGridSize;
		}
		if (_settings.Widgets.SnapQuickSlotsToGrid)
		{
			foreach (QuickSlotGroup quickSlotGroup in QuickSlotGroups)
			{
				quickSlotGroup.X = SnapCoordinate(quickSlotGroup.X, _settings.Widgets.QuickSlotGridSize);
				quickSlotGroup.Y = SnapCoordinate(quickSlotGroup.Y, _settings.Widgets.QuickSlotGridSize);
			}
		}
		_settings.Widgets.QuickSlotGroups = QuickSlotGroups.Select((QuickSlotGroup group) => group.Copy()).ToList();
		_settings.Widgets.QuickSlots = (from item in QuickSlots
			orderby item.Slot
			select item.Copy()).ToList();
		_settings.Widgets.SystemShortcuts = SystemShortcuts.Select((SystemShortcutSetting item) => item.Copy()).ToList();
		await _wallpaperController.UpdateWidgetsAsync(_settings.Widgets.Copy(), DisplayList.SelectedItem as DisplayInfo);
		await SaveSettingsAsync();
		this.DockSettingsChanged?.Invoke(this, EventArgs.Empty);
	}

	private async void DockSettings_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			_settings.Dock.IsEnabled = DockEnabledCheckBox.IsChecked.GetValueOrDefault();
			_settings.Dock.ShowRunningApplications = DockRunningAppsCheckBox.IsChecked.GetValueOrDefault();
			_settings.Dock.HideWindowsTaskbar = DockHideTaskbarCheckBox.IsChecked.GetValueOrDefault();
			if (DockIconSizeComboBox.SelectedItem is int iconSize)
			{
				_settings.Dock.IconSize = iconSize;
			}
			if (DockBottomMarginComboBox.SelectedItem is int bottomMargin)
			{
				_settings.Dock.BottomMargin = bottomMargin;
			}
			_settings.Dock = _settings.Dock.Copy();
			await SaveSettingsAsync();
			this.DockSettingsChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private async void DockItemVisibility_Changed(object sender, RoutedEventArgs eventArgs)
	{
		if (_initialized)
		{
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void InstallCodexBridgeButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		try
		{
			string text = await _wallpaperController.InstallCodexCompanionBridgeAsync();
			CodexBridgeStatusText.Text = text + "에 설치했습니다. Codex에서 /hooks를 한 번 열고 Vellume 훅을 신뢰하도록 설정하세요.";
		}
		catch (Exception exception)
		{
			await ReportErrorAsync("Codex 컴패니언 연결을 설치하지 못했습니다.", exception);
		}
	}

	private static int SnapCoordinate(int value, int gridSize)
	{
		return Math.Max(0, (int)Math.Round((double)value / (double)Math.Clamp(gridSize, 10, 200), MidpointRounding.AwayFromZero) * Math.Clamp(gridSize, 10, 200));
	}

	private void SortQuickSlots()
	{
		QuickSlotItem[] array = QuickSlots.OrderBy((QuickSlotItem item) => item.Slot).ToArray();
		QuickSlots.Clear();
		QuickSlotItem[] array2 = array;
		foreach (QuickSlotItem item2 in array2)
		{
			QuickSlots.Add(item2);
		}
	}

	private async void AddQuickSlotGroupButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		QuickSlotGroupDialog quickSlotGroupDialog = new QuickSlotGroupDialog
		{
			Owner = this
		};
		if (quickSlotGroupDialog.ShowDialog().GetValueOrDefault())
		{
			string name = quickSlotGroupDialog.GroupName.Trim();
			if (QuickSlotGroups.Any((QuickSlotGroup group) => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
			{
				System.Windows.MessageBox.Show("같은 이름의 퀵슬롯 그룹이 이미 있습니다.", "Vellume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			int count = QuickSlotGroups.Count;
			QuickSlotGroup quickSlotGroup = new QuickSlotGroup
			{
				Name = name,
				X = 340 + count % 3 * 320,
				Y = 60 + count / 3 * 250
			};
			QuickSlotGroups.Add(quickSlotGroup);
			QuickSlotGroupComboBox.SelectedItem = quickSlotGroup;
			await SaveAndRefreshWidgetsAsync();
		}
	}

	private async void MoveQuickSlotToGroupButton_Click(object sender, RoutedEventArgs eventArgs)
	{
		if (!(QuickSlotList.SelectedItem is QuickSlotItem quickSlotItem) || !(QuickSlotGroupComboBox.SelectedItem is QuickSlotGroup quickSlotGroup))
		{
			LayoutStatusText.Text = "퀵슬롯과 이동할 그룹을 먼저 선택하세요.";
			return;
		}
		quickSlotItem.GroupId = quickSlotGroup.Id;
		quickSlotItem.GroupName = quickSlotGroup.Name;
		QuickSlotList.Items.Refresh();
		LayoutStatusText.Text = quickSlotItem.Name + "을(를) " + quickSlotGroup.Name + " 그룹으로 이동했습니다.";
		await SaveAndRefreshWidgetsAsync();
	}

	private static string LocalizePlaybackState(WallpaperPlaybackState state)
	{
		return state switch
		{
			WallpaperPlaybackState.Stopped => "중지됨", 
			WallpaperPlaybackState.Playing => "재생 중", 
			WallpaperPlaybackState.Paused => "일시정지됨", 
			WallpaperPlaybackState.Error => "오류", 
			_ => state.ToString(), 
		};
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Vellume.App;V0.8.5.0;component/mainwindow.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((MainWindow)target).Loaded += Window_Loaded;
			((MainWindow)target).Closing += Window_Closing;
			break;
		case 2:
			((System.Windows.Controls.Button)target).Click += HomeNavigation_Click;
			break;
		case 3:
			((System.Windows.Controls.Button)target).Click += LibraryNavigation_Click;
			break;
		case 4:
			((System.Windows.Controls.Button)target).Click += DisplaysNavigation_Click;
			break;
		case 5:
			((System.Windows.Controls.Button)target).Click += WidgetsNavigation_Click;
			break;
		case 6:
			((System.Windows.Controls.Button)target).Click += SettingsNavigation_Click;
			break;
		case 7:
			SidebarStatusText = (TextBlock)target;
			break;
		case 8:
			((System.Windows.Controls.Button)target).Click += ExitButton_Click;
			break;
		case 9:
			HomePage = (Grid)target;
			break;
		case 10:
			NowPlayingText = (TextBlock)target;
			break;
		case 11:
			HomeStatusText = (TextBlock)target;
			break;
		case 12:
			PauseButton = (System.Windows.Controls.Button)target;
			PauseButton.Click += PauseButton_Click;
			break;
		case 13:
			((System.Windows.Controls.Button)target).Click += StopButton_Click;
			break;
		case 14:
			((System.Windows.Controls.Button)target).Click += ApplySampleButton_Click;
			break;
		case 15:
			LibraryPage = (Grid)target;
			break;
		case 16:
			((System.Windows.Controls.Button)target).Click += AddFileButton_Click;
			break;
		case 17:
			((System.Windows.Controls.Button)target).Click += AddFolderButton_Click;
			break;
		case 18:
			LibraryList = (System.Windows.Controls.ListBox)target;
			LibraryList.MouseDoubleClick += LibraryList_MouseDoubleClick;
			break;
		case 21:
			DisplaysPage = (Grid)target;
			break;
		case 22:
			DisplayList = (System.Windows.Controls.ListBox)target;
			DisplayList.SelectionChanged += DisplayList_SelectionChanged;
			break;
		case 23:
			WidgetsPage = (Grid)target;
			break;
		case 24:
			WidgetsEnabledCheckBox = (System.Windows.Controls.CheckBox)target;
			WidgetsEnabledCheckBox.Checked += WidgetCheckBox_Changed;
			WidgetsEnabledCheckBox.Unchecked += WidgetCheckBox_Changed;
			break;
		case 25:
			ClockWidgetCheckBox = (System.Windows.Controls.CheckBox)target;
			ClockWidgetCheckBox.Checked += WidgetCheckBox_Changed;
			ClockWidgetCheckBox.Unchecked += WidgetCheckBox_Changed;
			break;
		case 26:
			SystemWidgetCheckBox = (System.Windows.Controls.CheckBox)target;
			SystemWidgetCheckBox.Checked += WidgetCheckBox_Changed;
			SystemWidgetCheckBox.Unchecked += WidgetCheckBox_Changed;
			break;
		case 27:
			QuickSlotsWidgetCheckBox = (System.Windows.Controls.CheckBox)target;
			QuickSlotsWidgetCheckBox.Checked += WidgetCheckBox_Changed;
			QuickSlotsWidgetCheckBox.Unchecked += WidgetCheckBox_Changed;
			break;
		case 28:
			CodexCompanionWidgetCheckBox = (System.Windows.Controls.CheckBox)target;
			CodexCompanionWidgetCheckBox.Checked += WidgetCheckBox_Changed;
			CodexCompanionWidgetCheckBox.Unchecked += WidgetCheckBox_Changed;
			break;
		case 29:
			ClockSideComboBox = (System.Windows.Controls.ComboBox)target;
			ClockSideComboBox.SelectionChanged += WidgetPlacement_Changed;
			break;
		case 30:
			SystemSideComboBox = (System.Windows.Controls.ComboBox)target;
			SystemSideComboBox.SelectionChanged += WidgetPlacement_Changed;
			break;
		case 31:
			CodexSideComboBox = (System.Windows.Controls.ComboBox)target;
			CodexSideComboBox.SelectionChanged += WidgetPlacement_Changed;
			break;
		case 32:
			CodexBridgeStatusText = (TextBlock)target;
			break;
		case 33:
			((System.Windows.Controls.Button)target).Click += InstallCodexBridgeButton_Click;
			break;
		case 34:
			DockEnabledCheckBox = (System.Windows.Controls.CheckBox)target;
			DockEnabledCheckBox.Checked += DockSettings_Changed;
			DockEnabledCheckBox.Unchecked += DockSettings_Changed;
			break;
		case 35:
			DockRunningAppsCheckBox = (System.Windows.Controls.CheckBox)target;
			DockRunningAppsCheckBox.Checked += DockSettings_Changed;
			DockRunningAppsCheckBox.Unchecked += DockSettings_Changed;
			break;
		case 36:
			DockHideTaskbarCheckBox = (System.Windows.Controls.CheckBox)target;
			DockHideTaskbarCheckBox.Checked += DockSettings_Changed;
			DockHideTaskbarCheckBox.Unchecked += DockSettings_Changed;
			break;
		case 37:
			DockIconSizeComboBox = (System.Windows.Controls.ComboBox)target;
			DockIconSizeComboBox.SelectionChanged += DockSettings_Changed;
			break;
		case 38:
			DockBottomMarginComboBox = (System.Windows.Controls.ComboBox)target;
			DockBottomMarginComboBox.SelectionChanged += DockSettings_Changed;
			break;
		case 39:
			HotkeyStatusText = (TextBlock)target;
			break;
		case 40:
			((System.Windows.Controls.Button)target).Click += DiscoverKnownAppsButton_Click;
			break;
		case 41:
			((System.Windows.Controls.Button)target).Click += AddQuickSlotGroupButton_Click;
			break;
		case 42:
			((System.Windows.Controls.Button)target).Click += AddQuickSlotButton_Click;
			break;
		case 43:
			KnownAppsStatusText = (TextBlock)target;
			break;
		case 44:
			SnapQuickSlotsCheckBox = (System.Windows.Controls.CheckBox)target;
			SnapQuickSlotsCheckBox.Checked += GridSettings_Changed;
			SnapQuickSlotsCheckBox.Unchecked += GridSettings_Changed;
			break;
		case 45:
			QuickSlotGridSizeComboBox = (System.Windows.Controls.ComboBox)target;
			QuickSlotGridSizeComboBox.SelectionChanged += GridSettings_Changed;
			break;
		case 46:
			QuickSlotGroupComboBox = (System.Windows.Controls.ComboBox)target;
			break;
		case 47:
			((System.Windows.Controls.Button)target).Click += MoveQuickSlotToGroupButton_Click;
			break;
		case 48:
			LayoutStatusText = (TextBlock)target;
			break;
		case 49:
			SystemShortcutList = (ItemsControl)target;
			break;
		case 53:
			QuickSlotList = (System.Windows.Controls.ListBox)target;
			break;
		case 56:
			SettingsPage = (Grid)target;
			break;
		case 57:
			FitComboBox = (System.Windows.Controls.ComboBox)target;
			FitComboBox.SelectionChanged += FitComboBox_SelectionChanged;
			break;
		case 58:
			MuteCheckBox = (System.Windows.Controls.CheckBox)target;
			MuteCheckBox.Checked += MuteCheckBox_Changed;
			MuteCheckBox.Unchecked += MuteCheckBox_Changed;
			break;
		case 59:
			VolumeValueText = (Run)target;
			break;
		case 60:
			VolumeSlider = (Slider)target;
			VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
			break;
		case 61:
			BrightnessValueText = (Run)target;
			break;
		case 62:
			BrightnessSlider = (Slider)target;
			BrightnessSlider.ValueChanged += ColorAdjustmentSlider_ValueChanged;
			break;
		case 63:
			ContrastValueText = (Run)target;
			break;
		case 64:
			ContrastSlider = (Slider)target;
			ContrastSlider.ValueChanged += ColorAdjustmentSlider_ValueChanged;
			break;
		case 65:
			SaturationValueText = (Run)target;
			break;
		case 66:
			SaturationSlider = (Slider)target;
			SaturationSlider.ValueChanged += ColorAdjustmentSlider_ValueChanged;
			break;
		case 67:
			((System.Windows.Controls.Button)target).Click += ResetColorAdjustmentButton_Click;
			break;
		case 68:
			AutoPauseForegroundAppCheckBox = (System.Windows.Controls.CheckBox)target;
			AutoPauseForegroundAppCheckBox.Checked += AutoPauseForegroundAppCheckBox_Changed;
			AutoPauseForegroundAppCheckBox.Unchecked += AutoPauseForegroundAppCheckBox_Changed;
			break;
		case 69:
			ApplicationRuleList = (System.Windows.Controls.ListBox)target;
			ApplicationRuleList.SelectionChanged += ApplicationRuleList_SelectionChanged;
			break;
		case 70:
			ApplicationRuleConditionComboBox = (System.Windows.Controls.ComboBox)target;
			ApplicationRuleConditionComboBox.SelectionChanged += ApplicationRuleEditor_SelectionChanged;
			break;
		case 71:
			ApplicationRuleActionComboBox = (System.Windows.Controls.ComboBox)target;
			ApplicationRuleActionComboBox.SelectionChanged += ApplicationRuleEditor_SelectionChanged;
			break;
		case 72:
			((System.Windows.Controls.Button)target).Click += AddApplicationRuleButton_Click;
			break;
		case 73:
			((System.Windows.Controls.Button)target).Click += RemoveApplicationRuleButton_Click;
			break;
		case 74:
			ApplicationRuleStatusText = (TextBlock)target;
			break;
		case 75:
			RestoreCheckBox = (System.Windows.Controls.CheckBox)target;
			RestoreCheckBox.Checked += RestoreCheckBox_Changed;
			RestoreCheckBox.Unchecked += RestoreCheckBox_Changed;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "9.0.11.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 19:
			((System.Windows.Controls.Button)target).Click += ApplyCardButton_Click;
			break;
		case 20:
			((System.Windows.Controls.Button)target).Click += RemoveCardButton_Click;
			break;
		case 50:
			((System.Windows.Controls.CheckBox)target).Checked += DockItemVisibility_Changed;
			((System.Windows.Controls.CheckBox)target).Unchecked += DockItemVisibility_Changed;
			break;
		case 51:
			((System.Windows.Controls.Button)target).Click += ChooseSystemShortcutIconButton_Click;
			break;
		case 52:
			((System.Windows.Controls.Button)target).Click += ResetSystemShortcutIconButton_Click;
			break;
		case 54:
			((System.Windows.Controls.CheckBox)target).Checked += DockItemVisibility_Changed;
			((System.Windows.Controls.CheckBox)target).Unchecked += DockItemVisibility_Changed;
			break;
		case 55:
			((System.Windows.Controls.Button)target).Click += RemoveQuickSlotButton_Click;
			break;
		}
	}
}
internal sealed class QuickSlotGroupDialog : Window
{
	private readonly System.Windows.Controls.TextBox _nameTextBox = new System.Windows.Controls.TextBox();

	public string GroupName => _nameTextBox.Text;

	public QuickSlotGroupDialog()
	{
		base.Title = "새 퀵슬롯 그룹";
		base.Width = 390.0;
		base.Height = 190.0;
		base.ResizeMode = ResizeMode.NoResize;
		base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		base.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 17, 26));
		base.Foreground = System.Windows.Media.Brushes.White;
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "그룹 이름",
			FontSize = 16.0,
			FontWeight = FontWeights.SemiBold
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "예: 자주 사용, 게임, 개발",
			Margin = new Thickness(0.0, 5.0, 0.0, 10.0),
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 158, 181))
		});
		_nameTextBox.Height = 32.0;
		_nameTextBox.MaxLength = 32;
		_nameTextBox.Padding = new Thickness(8.0, 4.0, 8.0, 4.0);
		stackPanel.Children.Add(_nameTextBox);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		};
		System.Windows.Controls.Button element = new System.Windows.Controls.Button
		{
			Content = "취소",
			IsCancel = true,
			MinWidth = 78.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		System.Windows.Controls.Button button = new System.Windows.Controls.Button
		{
			Content = "만들기",
			IsDefault = true,
			MinWidth = 78.0
		};
		button.Click += delegate
		{
			if (!string.IsNullOrWhiteSpace(_nameTextBox.Text))
			{
				base.DialogResult = true;
			}
		};
		stackPanel2.Children.Add(element);
		stackPanel2.Children.Add(button);
		stackPanel.Children.Add(stackPanel2);
		base.Content = stackPanel;
		base.Loaded += delegate
		{
			_nameTextBox.Focus();
		};
	}
}
internal sealed class QuickSlotHotkeyManager : IDisposable
{
	private const int WmHotkey = 786;

	private const uint ModAlt = 1u;

	private const uint ModControl = 2u;

	private const uint ModNoRepeat = 16384u;

	private const int HotkeyIdBase = 22016;

	private readonly List<int> _registeredIds = new List<int>();

	private nint _windowHandle;

	public event EventHandler<int>? SlotInvoked;

	public int Register(nint windowHandle)
	{
		DisposeRegistrations();
		_windowHandle = windowHandle;
		for (int i = 1; i <= 9; i++)
		{
			int num = 22016 + i;
			uint virtualKey = (uint)(48 + i);
			if (RegisterHotKey(_windowHandle, num, 16387u, virtualKey))
			{
				_registeredIds.Add(num);
			}
		}
		return _registeredIds.Count;
	}

	public bool TryHandleMessage(int message, nint wParam)
	{
		if (message != 786)
		{
			return false;
		}
		int num = ((IntPtr)wParam).ToInt32() - 22016;
		if (num < 1 || num > 9)
		{
			return false;
		}
		this.SlotInvoked?.Invoke(this, num);
		return true;
	}

	public void Dispose()
	{
		DisposeRegistrations();
		this.SlotInvoked = null;
	}

	private void DisposeRegistrations()
	{
		if (_windowHandle != IntPtr.Zero)
		{
			foreach (int registeredId in _registeredIds)
			{
				UnregisterHotKey(_windowHandle, registeredId);
			}
		}
		_registeredIds.Clear();
		_windowHandle = IntPtr.Zero;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(nint windowHandle, int identifier, uint modifiers, uint virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(nint windowHandle, int identifier);
}
