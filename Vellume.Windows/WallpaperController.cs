using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vellume.Core;

namespace Vellume.Windows;

public sealed class WallpaperController : IWallpaperController, IAsyncDisposable
{
	private readonly DesktopHostLocator _hostLocator = new();

	private readonly string _webViewDataDirectory;

	private readonly string _gpuSafetyDirectory;

	private readonly IAppLogger _logger;

	private readonly ISystemShortcutService _systemShortcutService;

	private readonly DispatcherTimer _reconnectTimer;

	private readonly DispatcherTimer _systemMetricsTimer;

	private readonly DispatcherTimer _recycleBinTimer;

	private readonly DispatcherTimer _codexTimer;

	private readonly DispatcherTimer _foregroundAppTimer;

	private readonly ForegroundAppPauseDetector _foregroundAppPauseDetector = new ForegroundAppPauseDetector();

	private readonly WindowsSystemEventMonitor _systemEventMonitor;

	private readonly Dispatcher _dispatcher;

	private readonly SystemMetricsSampler _metricsSampler = new SystemMetricsSampler();

	private readonly CodexCompanionStatusReader _codexCompanionStatusReader;

	private readonly CodexCompanionPipeServer _codexCompanionPipeServer;

	private readonly CodexCompanionBridgeInstaller _codexCompanionBridgeInstaller;

	private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _metricsRefreshLock = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _codexRefreshLock = new SemaphoreSlim(1, 1);

	private readonly Dictionary<string, NativeQuickSlotGroupWindow> _quickSlotWindows = new Dictionary<string, NativeQuickSlotGroupWindow>(StringComparer.OrdinalIgnoreCase);

	private IWallpaperRenderer? _renderer;

	private NativeWidgetRendererWindow? _widgetRenderer;

	private DisplayInfo? _display;

	private DisplayInfo? _widgetDisplay;

	private WallpaperItem? _wallpaper;

	private PlaybackOptions? _playbackOptions;

	private WidgetSettings _widgetSettings = new WidgetSettings();

	private nint _hostHandle;

	private nint _interactiveHostHandle;

	private bool? _lastRecycleBinHasItems;

	private CodexCompanionSnapshot _lastCodexSnapshot = CodexCompanionSnapshot.Disconnected();

	private int _codexStatusDirty = 1;

	private int _codexRefreshQueued;

	private bool _manualPauseRequested;

	private bool _displayPauseRequested;

	private ApplicationPlaybackAction _automaticAction;

	private CancellationTokenSource? _foregroundEventDebounce;

	private bool _foregroundCheckInProgress;

	private bool _foregroundCheckQueued;

	private bool? _desktopOverlaysVisible;

	private bool _desktopWallpaperRecoveryPending;

	private bool _disposed;

	public WallpaperPlaybackState State { get; private set; }

	private bool IsRenderingPaused
	{
		get
		{
			if (!_manualPauseRequested)
			{
				return _automaticAction != ApplicationPlaybackAction.KeepRunning;
			}
			return true;
		}
	}

	public event EventHandler<WallpaperStateChangedEventArgs>? StateChanged;

	internal event EventHandler<AutomaticPlaybackActionChangedEventArgs>? AutomaticPlaybackActionChanged;

	public event EventHandler<int>? QuickSlotInvoked;

	public event EventHandler<int>? QuickSlotSecondaryInvoked;

	public event EventHandler<QuickSlotLayoutChangedEventArgs>? QuickSlotLayoutChanged;

	public WallpaperController(string webViewDataDirectory, IAppLogger logger, ISystemShortcutService systemShortcutService, WindowsSystemEventMonitor systemEventMonitor)
	{
		_webViewDataDirectory = webViewDataDirectory;
		_logger = logger;
		_systemShortcutService = systemShortcutService;
		_systemEventMonitor = systemEventMonitor;
		_dispatcher = Dispatcher.CurrentDispatcher;
		string path = Path.GetDirectoryName(_webViewDataDirectory) ?? _webViewDataDirectory;
		_gpuSafetyDirectory = Path.Combine(path, "GpuSafety");
		_desktopWallpaperRecoveryPending = WindowsDesktopWallpaperLease.HasPendingRecovery(_gpuSafetyDirectory);
		string text = Path.Combine(path, "Codex");
		string statusPath = Path.Combine(text, "status.json");
		_codexCompanionStatusReader = new CodexCompanionStatusReader(statusPath);
		_codexCompanionPipeServer = new CodexCompanionPipeServer(statusPath, _logger);
		_codexCompanionBridgeInstaller = new CodexCompanionBridgeInstaller(text, statusPath, "Vellume.Codex.Status.v1");
		_reconnectTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_reconnectTimer.Tick += ReconnectTimerOnTick;
		_systemMetricsTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_systemMetricsTimer.Tick += SystemMetricsTimerOnTick;
		_recycleBinTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_recycleBinTimer.Tick += RecycleBinTimerOnTick;
		_codexTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_codexTimer.Tick += CodexTimerOnTick;
		_foregroundAppTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(10.0)
		};
		_foregroundAppTimer.Tick += ForegroundAppTimerOnTick;
		_systemEventMonitor.DesktopShellChanged += SystemEventMonitor_DesktopShellChanged;
		_systemEventMonitor.RecycleBinChanged += SystemEventMonitor_RecycleBinChanged;
		_codexCompanionStatusReader.Changed += CodexCompanionStatusReader_Changed;
	}

	public async Task ApplyAsync(WallpaperItem wallpaper, DisplayInfo display, PlaybackOptions options, WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			WallpaperValidator.DetectKind(wallpaper.SourcePath);
			nint hostHandle = FindWallpaperHostOrThrow();
			IWallpaperRenderer? renderer = _renderer;
			if (renderer == null || !renderer.IsReady)
			{
				await CloseWallpaperCoreAsync();
				renderer = (_renderer = WallpaperRendererFactory.Create(_webViewDataDirectory));
			}
			await AttachWallpaperRendererAsync(renderer, hostHandle, display, cancellationToken);
			bool keepDesktopHandoffUntilReady = _desktopWallpaperRecoveryPending;
			if (keepDesktopHandoffUntilReady)
			{
				await renderer.SetWindowVisibleAsync(visible: false, cancellationToken);
			}
			bool captureDiagnostics = Environment.GetEnvironmentVariable("VELLUME_DIAGNOSTIC_CAPTURE") == "1";
			string diagnosticDirectory = Path.GetDirectoryName(_webViewDataDirectory) ?? _webViewDataDirectory;
			await _logger.InfoAsync("Desktop attachment (" + (_hostLocator.IsRaisedDesktop ? "raised" : "classic") + "): " + renderer.GetAttachmentDiagnostics());
			PlaybackOptions continuousPlayback = CreateContinuousPlaybackOptions(options);
			await renderer.InitializeAndNavigateAsync(wallpaper, continuousPlayback, cancellationToken);
			_hostHandle = hostHandle;
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			_display = display;
			_wallpaper = wallpaper;
			_playbackOptions = continuousPlayback;
			_widgetSettings = widgets.Copy();
			_widgetDisplay = display;
			_manualPauseRequested = false;
			_displayPauseRequested = false;
			SetAutomaticAction(ApplicationPlaybackAction.KeepRunning);
			if (!HasInformationWidgets(_widgetSettings))
			{
				await CloseWidgetCoreAsync(clearDisplay: false);
			}
			else
			{
				await EnsureWidgetRendererAsync(hostHandle, display, _widgetSettings, cancellationToken);
				NativeWidgetRendererWindow widgetRenderer = _widgetRenderer ?? throw new InvalidOperationException("위젯 렌더러가 준비되지 않았습니다.");
				await FinalizeDesktopLayerOrderAsync(renderer, widgetRenderer, hostHandle, display, cancellationToken);
				await _logger.InfoAsync("Final desktop layer order: wallpaper=(" + renderer.GetAttachmentDiagnostics() + "), widgets=(" + widgetRenderer.GetAttachmentDiagnostics() + ").");
			}
			await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, display, _widgetSettings, cancellationToken);
			if (keepDesktopHandoffUntilReady)
			{
				SetDesktopOverlayVisibility(visible: false);
				await renderer.SetWindowVisibleAsync(visible: true, cancellationToken);
				await Task.Delay(TimeSpan.FromMilliseconds(40.0), cancellationToken);
				if (!(await CompleteDesktopWallpaperHandoffCoreAsync()))
				{
					SetDesktopOverlayVisibility(visible: true);
				}
			}
			if (captureDiagnostics)
			{
				await Task.Delay(250, cancellationToken);
				string afterPath = Path.Combine(diagnosticDirectory, "renderer-after-attach.png");
				await renderer.CapturePreviewAsync(afterPath, cancellationToken);
				await _logger.InfoAsync("Captured post-attachment renderer preview to '" + afterPath + "'.");
				await _logger.InfoAsync("Post-capture attachment: " + renderer.GetAttachmentDiagnostics());
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Playing, wallpaper.Name + " 재생 중 · " + renderer.BackendDisplayName);
			await _logger.InfoAsync($"Applied '{wallpaper.SourcePath}' to {display.Id}.");
			if (_playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
				{
					ForegroundAppTimerOnTick(this, EventArgs.Empty);
				});
			}
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			_manualPauseRequested = false;
			_displayPauseRequested = false;
			SetAutomaticAction(ApplicationPlaybackAction.KeepRunning);
			await CloseWallpaperCoreAsync();
			UpdateTimers();
			SetState(WallpaperPlaybackState.Error, exception.Message);
			await _logger.ErrorAsync("월페이퍼 적용에 실패했습니다.", exception);
			throw;
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_manualPauseRequested = false;
			SetAutomaticAction(ApplicationPlaybackAction.KeepRunning);
			await CloseWallpaperCoreAsync();
			SetDesktopOverlayVisibility(visible: true);
			UpdateTimers();
			string text = ((_widgetRenderer == null && _quickSlotWindows.Count == 0) ? "월페이퍼를 중지했습니다" : "월페이퍼를 중지했습니다. 데스크톱 위젯은 유지됩니다");
			SetState(WallpaperPlaybackState.Stopped, text);
			await _logger.InfoAsync(text + ".");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public Task StopDisplayAsync(string displayId, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_display == null || !_display.Id.Equals(displayId, StringComparison.OrdinalIgnoreCase))
		{
			return Task.CompletedTask;
		}
		return StopAsync(cancellationToken);
	}

	public async Task PauseDisplayAsync(string displayId, CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer == null || _display == null || !_display.Id.Equals(displayId, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			_displayPauseRequested = true;
			await _renderer.PauseAsync();
			SetState(WallpaperPlaybackState.Paused, $"{_display.Name} 월페이퍼 일시정지");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task ResumeDisplayAsync(string displayId, CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer == null || _display == null || !_display.Id.Equals(displayId, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			_displayPauseRequested = false;
			if (_manualPauseRequested || _automaticAction != ApplicationPlaybackAction.KeepRunning)
			{
				SetState(WallpaperPlaybackState.Paused, "전역 또는 자동 절전 상태를 유지합니다.");
				return;
			}
			await _renderer.ResumeAsync();
			SetState(WallpaperPlaybackState.Playing, $"{_display.Name} 월페이퍼 재생 중");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task PauseAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer != null)
			{
				_manualPauseRequested = true;
				await _renderer.PauseAsync();
				if (_widgetRenderer != null)
				{
					await _widgetRenderer.PauseAsync();
				}
				UpdateTimers();
				SetState(WallpaperPlaybackState.Paused, "월페이퍼 일시정지");
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task ResumeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer == null)
			{
				return;
			}
			_manualPauseRequested = false;
			_displayPauseRequested = false;
			if (_automaticAction != 0)
			{
				SetState(WallpaperPlaybackState.Paused, "집중 작업 앱 감지 · 렌더링 절전");
				return;
			}
			await _renderer.ResumeAsync();
			SetDesktopOverlayVisibility(visible: true);
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.ResumeAsync();
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Playing, "월페이퍼 재생 중");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task UpdatePlaybackAsync(PlaybackOptions options, CancellationToken cancellationToken = default(CancellationToken))
	{
		bool evaluateRulesAfterUpdate = false;
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_playbackOptions = CreateContinuousPlaybackOptions(options);
			if (_renderer != null)
			{
				await _renderer.UpdatePlaybackAsync(_playbackOptions);
			}
			if (!_playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				if (_automaticAction != 0)
				{
					await SetAutomaticPlaybackCoreAsync(ApplicationPlaybackAction.KeepRunning);
				}
				else
				{
					UpdateTimers();
				}
			}
			else
			{
				UpdateTimers();
				evaluateRulesAfterUpdate = _renderer != null;
			}
		}
		finally
		{
			_operationLock.Release();
		}
		if (evaluateRulesAfterUpdate)
		{
			_ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				ForegroundAppTimerOnTick(this, EventArgs.Empty);
			});
		}
	}

	public Task UpdateDisplayPlaybackAsync(string displayId, PlaybackOptions options, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_display == null || !_display.Id.Equals(displayId, StringComparison.OrdinalIgnoreCase))
		{
			return Task.CompletedTask;
		}
		return UpdatePlaybackAsync(options, cancellationToken);
	}

	public async Task UpdateWidgetsAsync(WidgetSettings widgets, DisplayInfo? display = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_widgetSettings = widgets.Copy();
			_widgetDisplay = display ?? _widgetDisplay ?? _display;
			if (!_widgetSettings.IsEnabled)
			{
				await CloseWidgetCoreAsync(clearDisplay: false);
				await CloseQuickSlotWindowsAsync();
				UpdateTimers();
			}
			else
			{
				if (_widgetDisplay == null)
				{
					return;
				}
				nint hostHandle = _hostHandle;
				if (hostHandle == IntPtr.Zero || !NativeMethods.IsWindow(hostHandle))
				{
					hostHandle = FindWallpaperHostOrThrow();
					_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
				}
				if (!HasInformationWidgets(_widgetSettings))
				{
					await CloseWidgetCoreAsync(clearDisplay: false);
				}
				else
				{
					await EnsureWidgetRendererAsync(hostHandle, _widgetDisplay, _widgetSettings, cancellationToken);
					if (_renderer != null && _display != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
					{
						await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, hostHandle, _display, cancellationToken);
					}
				}
				await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, cancellationToken);
				if (_desktopWallpaperRecoveryPending)
				{
					SetDesktopOverlayVisibility(visible: false);
				}
				_hostHandle = hostHandle;
				UpdateTimers();
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task UpdateQuickSlotRuntimeAsync(WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_widgetSettings = widgets.Copy();
			if (_quickSlotWindows.Count == 0)
			{
				return;
			}
			IReadOnlyList<QuickSlotItem> desktopItems = _widgetSettings.GetDesktopQuickSlots(_lastRecycleBinHasItems.GetValueOrDefault());
			foreach (KeyValuePair<string, NativeQuickSlotGroupWindow> quickSlotWindow in _quickSlotWindows)
			{
				quickSlotWindow.Deconstruct(out var key, out var value);
				string groupId = key;
				NativeQuickSlotGroupWindow nativeQuickSlotGroupWindow = value;
				QuickSlotItem[] items = (from item in desktopItems
					where item.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)
					orderby item.Slot
					select item).ToArray();
				await nativeQuickSlotGroupWindow.UpdateRuntimeStatesAsync(items, cancellationToken);
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public Task<string> InstallCodexCompanionBridgeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		_codexCompanionPipeServer.Start();
		return _codexCompanionBridgeInstaller.InstallAsync(cancellationToken);
	}

	public async Task CompleteDesktopWallpaperHandoffAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			await CompleteDesktopWallpaperHandoffCoreAsync();
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async Task<bool> CompleteDesktopWallpaperHandoffCoreAsync()
	{
		if (!_desktopWallpaperRecoveryPending)
		{
			return false;
		}
		if (!WindowsDesktopWallpaperLease.TryRecover(_gpuSafetyDirectory))
		{
			return false;
		}
		_desktopWallpaperRecoveryPending = false;
		SetDesktopOverlayVisibility(visible: true);
		await _logger.InfoAsync("새 월페이퍼 렌더러가 준비된 뒤 Windows 정지 배경 인계를 완료했습니다.");
		return true;
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_foregroundEventDebounce?.Cancel();
		_foregroundEventDebounce?.Dispose();
		_foregroundEventDebounce = null;
		_systemEventMonitor.DesktopShellChanged -= SystemEventMonitor_DesktopShellChanged;
		_systemEventMonitor.RecycleBinChanged -= SystemEventMonitor_RecycleBinChanged;
		_codexCompanionStatusReader.Changed -= CodexCompanionStatusReader_Changed;
		_reconnectTimer.Stop();
		_reconnectTimer.Tick -= ReconnectTimerOnTick;
		_systemMetricsTimer.Stop();
		_systemMetricsTimer.Tick -= SystemMetricsTimerOnTick;
		_recycleBinTimer.Stop();
		_recycleBinTimer.Tick -= RecycleBinTimerOnTick;
		_codexTimer.Stop();
		_codexTimer.Tick -= CodexTimerOnTick;
		_foregroundAppTimer.Stop();
		_foregroundAppTimer.Tick -= ForegroundAppTimerOnTick;
		await _codexCompanionPipeServer.DisposeAsync();
		_codexCompanionStatusReader.Dispose();
		await _operationLock.WaitAsync();
		await _metricsRefreshLock.WaitAsync();
		await _codexRefreshLock.WaitAsync();
		try
		{
			await CloseWallpaperCoreAsync();
			await CloseWidgetCoreAsync(clearDisplay: true);
			await CloseQuickSlotWindowsAsync();
			_hostHandle = IntPtr.Zero;
			_interactiveHostHandle = IntPtr.Zero;
		}
		finally
		{
			_metricsSampler.Dispose();
			_codexRefreshLock.Release();
			_metricsRefreshLock.Release();
			_operationLock.Release();
			_codexRefreshLock.Dispose();
			_metricsRefreshLock.Dispose();
			_operationLock.Dispose();
		}
	}

	private async Task EnsureWidgetRendererAsync(nint hostHandle, DisplayInfo display, WidgetSettings settings, CancellationToken cancellationToken)
	{
		try
		{
			if (_widgetRenderer == null || !_widgetRenderer.IsReady)
			{
				if (_widgetRenderer != null)
				{
					await _widgetRenderer.CloseSafelyAsync();
				}
				_widgetRenderer = new NativeWidgetRendererWindow();
				AttachWidgetRenderer(_widgetRenderer, hostHandle, display);
				await _widgetRenderer.InitializeAsync(cancellationToken);
			}
			else
			{
				AttachWidgetRenderer(_widgetRenderer, hostHandle, display);
			}
			await _widgetRenderer.ApplySettingsAsync(settings, cancellationToken);
			if (settings.ShowSystemInfo)
			{
				await _metricsRefreshLock.WaitAsync(cancellationToken);
				try
				{
					SystemMetricsSnapshot metrics = await Task.Run((Func<SystemMetricsSnapshot>)_metricsSampler.Sample, cancellationToken);
					await _widgetRenderer.UpdateSystemMetricsAsync(metrics);
				}
				finally
				{
					_metricsRefreshLock.Release();
				}
			}
			await _codexRefreshLock.WaitAsync(cancellationToken);
			try
			{
				await UpdateCodexCoreAsync(settings, cancellationToken);
			}
			finally
			{
				_codexRefreshLock.Release();
			}
			if (IsRenderingPaused)
			{
				await _widgetRenderer.PauseAsync();
			}
			_widgetRenderer.SetWindowVisible(_desktopOverlaysVisible.GetValueOrDefault(true));
			_widgetDisplay = display;
			await _logger.InfoAsync("Independent widget attachment: " + _widgetRenderer.GetAttachmentDiagnostics());
		}
		catch
		{
			await CloseWidgetCoreAsync(clearDisplay: false);
			throw;
		}
	}

	private async Task EnsureQuickSlotWindowsAsync(nint interactiveHostHandle, DisplayInfo display, WidgetSettings settings, CancellationToken cancellationToken)
	{
		bool flag = settings.IsEnabled && settings.ShowQuickSlots && settings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin);
		bool flag2 = flag && _systemShortcutService.RecycleBinHasItems();
		_lastRecycleBinHasItems = (flag ? new bool?(flag2) : null);
		IReadOnlyList<QuickSlotItem> desktopItems = settings.GetDesktopQuickSlots(flag2);
		if (!settings.IsEnabled || !settings.ShowQuickSlots || desktopItems.Count == 0 || interactiveHostHandle == IntPtr.Zero)
		{
			await CloseQuickSlotWindowsAsync();
			return;
		}
		Dictionary<string, QuickSlotGroup> groups = settings.QuickSlotGroups.ToDictionary<QuickSlotGroup, string>((QuickSlotGroup group) => group.Id, StringComparer.OrdinalIgnoreCase);
		HashSet<string> desiredGroupIds = desktopItems.Select((QuickSlotItem item) => item.GroupId).Where(groups.ContainsKey).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] array = _quickSlotWindows.Keys.Where((string id) => !desiredGroupIds.Contains(id)).ToArray();
		foreach (string groupId2 in array)
		{
			await CloseQuickSlotWindowAsync(groupId2);
		}
		foreach (string groupId in desiredGroupIds)
		{
			QuickSlotGroup group2 = groups[groupId];
			QuickSlotItem[] items = (from item in desktopItems
				where item.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)
				orderby item.Slot
				select item).ToArray();
			if (!_quickSlotWindows.TryGetValue(groupId, out var renderer) || !renderer.IsReady)
			{
				if (renderer != null)
				{
					await CloseQuickSlotWindowAsync(groupId);
				}
				renderer = new NativeQuickSlotGroupWindow(groupId);
				renderer.SlotInvoked += QuickSlotWindowOnSlotInvoked;
				renderer.SlotSecondaryInvoked += QuickSlotWindowOnSlotSecondaryInvoked;
				renderer.LayoutChanged += QuickSlotWindowOnLayoutChanged;
				_quickSlotWindows[groupId] = renderer;
				renderer.AttachTo(interactiveHostHandle, display, group2, items.Length, settings.SnapQuickSlotsToGrid, settings.QuickSlotGridSize);
				await renderer.InitializeAsync(cancellationToken);
			}
			else
			{
				renderer.AttachTo(interactiveHostHandle, display, group2, items.Length, settings.SnapQuickSlotsToGrid, settings.QuickSlotGridSize);
			}
			await renderer.RenderAsync(group2, items, cancellationToken);
			renderer.SetWindowVisible(_desktopOverlaysVisible.GetValueOrDefault(true));
			await _logger.InfoAsync("Quick-slot attachment: " + renderer.GetAttachmentDiagnostics());
		}
		_interactiveHostHandle = interactiveHostHandle;
		_widgetDisplay = display;
		await _logger.InfoAsync($"Attached {_quickSlotWindows.Count} interactive quick-slot group(s).");
	}

	private async Task CloseQuickSlotWindowsAsync()
	{
		string[] array = _quickSlotWindows.Keys.ToArray();
		foreach (string groupId in array)
		{
			await CloseQuickSlotWindowAsync(groupId);
		}
	}

	private async Task CloseQuickSlotWindowAsync(string groupId)
	{
		if (_quickSlotWindows.Remove(groupId, out var value))
		{
			value.SlotInvoked -= QuickSlotWindowOnSlotInvoked;
			value.SlotSecondaryInvoked -= QuickSlotWindowOnSlotSecondaryInvoked;
			value.LayoutChanged -= QuickSlotWindowOnLayoutChanged;
			await value.CloseSafelyAsync();
		}
	}

	private void QuickSlotWindowOnSlotInvoked(object? sender, int slot)
	{
		this.QuickSlotInvoked?.Invoke(this, slot);
	}

	private void QuickSlotWindowOnSlotSecondaryInvoked(object? sender, int slot)
	{
		this.QuickSlotSecondaryInvoked?.Invoke(this, slot);
	}

	private void QuickSlotWindowOnLayoutChanged(object? sender, QuickSlotLayoutChangedEventArgs eventArgs)
	{
		QuickSlotLayoutChangedEventArgs eventArgs2 = eventArgs;
		QuickSlotGroup? quickSlotGroup = _widgetSettings.QuickSlotGroups.FirstOrDefault((QuickSlotGroup candidate) => candidate.Id.Equals(eventArgs2.GroupId, StringComparison.OrdinalIgnoreCase));
		if (quickSlotGroup != null)
		{
			quickSlotGroup.X = eventArgs2.X;
			quickSlotGroup.Y = eventArgs2.Y;
		}
		this.QuickSlotLayoutChanged?.Invoke(this, eventArgs2);
	}

	private async Task CloseWallpaperCoreAsync()
	{
		IWallpaperRenderer? renderer = _renderer;
		_renderer = null;
		_display = null;
		_wallpaper = null;
		_playbackOptions = null;
		_displayPauseRequested = false;
		if (renderer != null)
		{
			await renderer.CloseSafelyAsync();
		}
	}

	private async Task CloseWidgetCoreAsync(bool clearDisplay)
	{
		_systemMetricsTimer.Stop();
		_codexTimer.Stop();
		NativeWidgetRendererWindow? widgetRenderer = _widgetRenderer;
		_widgetRenderer = null;
		if (clearDisplay)
		{
			_widgetDisplay = null;
		}
		if (widgetRenderer != null)
		{
			await widgetRenderer.CloseSafelyAsync();
		}
	}

	private async void ReconnectTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool wallpaperNeedsReconnect = _renderer != null && !_renderer.IsAttachedTo(_hostHandle);
		bool widgetNeedsReconnect = _widgetRenderer != null && !_widgetRenderer.IsAttachedTo(_hostHandle);
		bool quickSlotsNeedReconnect2 = _quickSlotWindows.Values.Any((NativeQuickSlotGroupWindow window) => !window.IsAttachedTo(_interactiveHostHandle));
		bool flag = !wallpaperNeedsReconnect && !widgetNeedsReconnect && !quickSlotsNeedReconnect2;
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			wallpaperNeedsReconnect = _renderer != null && !_renderer.IsAttachedTo(_hostHandle);
			widgetNeedsReconnect = _widgetRenderer != null && !_widgetRenderer.IsAttachedTo(_hostHandle);
			quickSlotsNeedReconnect2 = _quickSlotWindows.Values.Any((NativeQuickSlotGroupWindow window) => !window.IsAttachedTo(_interactiveHostHandle));
			if (!wallpaperNeedsReconnect && !widgetNeedsReconnect && !quickSlotsNeedReconnect2)
			{
				return;
			}
			nint replacementHost = _hostLocator.FindWallpaperHost();
			if (replacementHost == IntPtr.Zero)
			{
				return;
			}
			if (wallpaperNeedsReconnect && _renderer != null && _display != null && _wallpaper != null && _playbackOptions != null)
			{
				bool wasPaused = State == WallpaperPlaybackState.Paused;
				if (_renderer.IsReady)
				{
					await AttachWallpaperRendererAsync(_renderer, replacementHost, _display, CancellationToken.None);
				}
				else
				{
					await _renderer.CloseSafelyAsync();
					_renderer = WallpaperRendererFactory.Create(_webViewDataDirectory);
					await AttachWallpaperRendererAsync(_renderer, replacementHost, _display, CancellationToken.None);
					await _renderer.InitializeAndNavigateAsync(_wallpaper, _playbackOptions, CancellationToken.None);
					if (wasPaused)
					{
						await _renderer.PauseAsync();
					}
				}
			}
			if (_widgetRenderer != null && _widgetDisplay != null && HasInformationWidgets(_widgetSettings) && (widgetNeedsReconnect || wallpaperNeedsReconnect))
			{
				await EnsureWidgetRendererAsync(replacementHost, _widgetDisplay, _widgetSettings, CancellationToken.None);
			}
			if (_renderer != null && _widgetRenderer != null && _display != null && _widgetDisplay != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
			{
				await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, replacementHost, _display, CancellationToken.None);
			}
			_hostHandle = replacementHost;
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			if (_widgetDisplay != null && (quickSlotsNeedReconnect2 || wallpaperNeedsReconnect || widgetNeedsReconnect))
			{
				await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			}
			await _logger.InfoAsync("Reattached independent desktop layers after WorkerW recreation.");
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Desktop layer reattachment failed.", exception);
			SetState(WallpaperPlaybackState.Error, "Explorer changed and Vellume could not reattach. Use Apply to retry.");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private void SystemEventMonitor_DesktopShellChanged(object? sender, EventArgs eventArgs)
	{
		ReconnectTimerOnTick(sender, eventArgs);
	}

	private void SystemEventMonitor_RecycleBinChanged(object? sender, EventArgs eventArgs)
	{
		RecycleBinTimerOnTick(sender, eventArgs);
	}

	private async void RecycleBinTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool flag = _widgetDisplay == null || !_widgetSettings.IsEnabled || !_widgetSettings.ShowQuickSlots || !_widgetSettings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin);
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			DisplayInfo? display = _widgetDisplay;
			if (display is null)
			{
				return;
			}
			bool currentState = await Task.Run((Func<bool>)_systemShortcutService.RecycleBinHasItems);
			bool num = !_lastRecycleBinHasItems.HasValue || _lastRecycleBinHasItems.Value != currentState;
			_lastRecycleBinHasItems = currentState;
			if (!num)
			{
				return;
			}
			await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, display, _widgetSettings, CancellationToken.None);
			await _logger.InfoAsync("Recycle Bin icon changed to " + (currentState ? "full" : "empty") + ".");
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Recycle Bin icon refresh failed.", exception);
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async void SystemMetricsTimerOnTick(object? sender, EventArgs eventArgs)
	{
		if (_disposed || _widgetRenderer == null ||
			!_widgetSettings.IsEnabled ||
			!_widgetSettings.ShowSystemInfo ||
			!await _metricsRefreshLock.WaitAsync(0))
		{
			return;
		}
		try
		{
			NativeWidgetRendererWindow? renderer = _widgetRenderer;
			if (renderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowSystemInfo)
			{
				long metricsStarted = Stopwatch.GetTimestamp();
				SystemMetricsSnapshot metrics = await Task.Run((Func<SystemMetricsSnapshot>)_metricsSampler.Sample);
				await renderer.UpdateSystemMetricsAsync(metrics);
				LocalPerformanceDiagnostics.Record("system.metrics-and-render", Stopwatch.GetElapsedTime(metricsStarted));
				if (Environment.GetEnvironmentVariable("VELLUME_DIAGNOSTIC_CAPTURE") == "1")
				{
					await _logger.InfoAsync($"Diagnostic metrics: CPU={metrics.CpuPercent:0.0}%, GPU={((!metrics.GpuPercent.HasValue) ? "N/A" : $"{metrics.GpuPercent:0.0}%")}, C:={(metrics.DriveC.IsAvailable ? $"{metrics.DriveC.Percent:0.0}%" : "N/A")}, D:={(metrics.DriveD.IsAvailable ? $"{metrics.DriveD.Percent:0.0}%" : "N/A")}.");
				}
			}
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("System widget metrics update failed.", exception);
		}
		finally
		{
			_metricsRefreshLock.Release();
		}
	}

	private async void CodexTimerOnTick(object? sender, EventArgs eventArgs)
	{
		if (_disposed || _widgetRenderer == null ||
			!_widgetSettings.IsEnabled ||
			!_widgetSettings.ShowCodexCompanion ||
			!await _codexRefreshLock.WaitAsync(0))
		{
			return;
		}
		try
		{
			await UpdateCodexCoreAsync(_widgetSettings.Copy(), CancellationToken.None);
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Codex companion status update failed.", exception);
		}
		finally
		{
			_codexRefreshLock.Release();
		}
	}

	private void CodexCompanionStatusReader_Changed(object? sender, EventArgs eventArgs)
	{
		QueueCodexRefresh();
	}

	private void QueueCodexRefresh()
	{
		if (_disposed)
		{
			return;
		}
		Interlocked.Exchange(ref _codexStatusDirty, 1);
		if (Interlocked.Exchange(ref _codexRefreshQueued, 1) == 0)
		{
			_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				_ = RefreshCodexFromEventsAsync();
			});
		}
	}

	private async Task RefreshCodexFromEventsAsync()
	{
		try
		{
			Interlocked.Exchange(ref _codexStatusDirty, 0);
			if (_disposed || IsRenderingPaused || _widgetRenderer == null || !_widgetSettings.IsEnabled || !_widgetSettings.ShowCodexCompanion)
			{
				return;
			}
			await _codexRefreshLock.WaitAsync();
			try
			{
				if (!_disposed)
				{
					await UpdateCodexCoreAsync(_widgetSettings.Copy(), CancellationToken.None);
				}
			}
			finally
			{
				_codexRefreshLock.Release();
			}
		}
		catch (ObjectDisposedException) when (_disposed)
		{
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("이벤트 기반 Codex 컴패니언 갱신에 실패했습니다.", exception);
		}
		finally
		{
			Interlocked.Exchange(ref _codexRefreshQueued, 0);
			if (!_disposed && Volatile.Read(ref _codexStatusDirty) != 0)
			{
				QueueCodexRefresh();
			}
		}
	}

	private async void ForegroundAppTimerOnTick(object? sender, EventArgs eventArgs)
	{
		if (!_disposed && _renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_foregroundCheckQueued = true;
				if (_foregroundCheckInProgress)
				{
					return;
				}
				_foregroundCheckInProgress = true;
				try
				{
					try
					{
						do
						{
							_foregroundCheckQueued = false;
							await EvaluateForegroundPauseAsync();
						}
						while (_foregroundCheckQueued && !_disposed);
					}
					catch (Exception exception)
					{
						await _logger.ErrorAsync("전경 앱의 창 상태를 확인하지 못했습니다.", exception);
					}
					return;
				}
				finally
				{
					_foregroundCheckInProgress = false;
				}
			}
		}
		_foregroundCheckQueued = false;
	}

	private void SystemEventMonitor_ForegroundWindowChanged(object? sender, EventArgs eventArgs)
	{
		if (!_disposed && _renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_foregroundEventDebounce?.Cancel();
				_foregroundEventDebounce?.Dispose();
				_foregroundEventDebounce = new CancellationTokenSource();
				_ = EvaluateForegroundAfterEventAsync(_foregroundEventDebounce.Token);
			}
		}
	}

	private async Task EvaluateForegroundAfterEventAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(40, cancellationToken);
			ForegroundAppTimerOnTick(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task EvaluateForegroundPauseAsync()
	{
		PlaybackOptions? playbackOptions = _playbackOptions;
		if (_renderer == null || playbackOptions == null || !playbackOptions.PauseWhenForegroundAppIsMaximized)
		{
			return;
		}
		ForegroundWindowSnapshot foregroundSnapshot = _foregroundAppPauseDetector.Capture();
		ApplicationPlaybackAction requestedAction =
			ForegroundAppPausePolicy.ResolveAction(
				foregroundSnapshot,
				playbackOptions);
		if (requestedAction == ApplicationPlaybackAction.StopFreeMemory)
		{
			requestedAction = ApplicationPlaybackAction.Pause;
		}
		if (requestedAction == _automaticAction)
		{
			return;
		}
		await _operationLock.WaitAsync();
		try
		{
			if (_renderer != null)
			{
				PlaybackOptions? playbackOptions2 = _playbackOptions;
				if (playbackOptions2 != null && playbackOptions2.PauseWhenForegroundAppIsMaximized)
				{
					await SetAutomaticPlaybackCoreAsync(requestedAction, foregroundSnapshot);
				}
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async Task SetAutomaticPlaybackCoreAsync(
		ApplicationPlaybackAction requestedAction,
		ForegroundWindowSnapshot? trigger = null)
	{
		SetDesktopOverlayVisibility(visible: true);
		SetAutomaticAction(requestedAction);
		if (requestedAction == ApplicationPlaybackAction.Pause)
		{
			if (_renderer != null)
			{
				await _renderer.PauseAsync();
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Paused, "최대화 창 감지 · 월페이퍼 일시정지");
			await _logger.InfoAsync("화면을 채운 최대화 창에 따라 월페이퍼를 일시정지했습니다." + FormatForegroundPauseTrigger(trigger));
			return;
		}
		if (!_manualPauseRequested)
		{
			if (_renderer != null)
			{
				if (_displayPauseRequested)
				{
					await _renderer.PauseAsync();
				}
				else
				{
					await _renderer.ResumeAsync();
				}
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.ResumeAsync();
			}
			if (_renderer != null && _widgetRenderer != null && _display != null && _widgetDisplay != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
			{
				await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, _hostHandle, _display, CancellationToken.None);
			}
			SetState(
				_displayPauseRequested
					? WallpaperPlaybackState.Paused
					: WallpaperPlaybackState.Playing,
				_displayPauseRequested
					? "선택 화면 일시정지 유지"
					: "월페이퍼 재생 중");
		}
		else
		{
			if (_renderer != null)
			{
				await _renderer.PauseAsync();
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
			}
			SetState(WallpaperPlaybackState.Paused, "수동 일시정지 유지");
		}
		UpdateTimers();
		await _logger.InfoAsync(_manualPauseRequested ? "자동 규칙은 해제됐지만 수동 일시정지를 유지합니다." : "자동 규칙이 해제되어 유지 중이던 월페이퍼 재생을 다시 시작했습니다.");
	}

	private void SetDesktopOverlayVisibility(bool visible)
	{
		if (_desktopOverlaysVisible == visible)
		{
			return;
		}
		_desktopOverlaysVisible = visible;
		_widgetRenderer?.SetWindowVisible(visible);
		foreach (NativeQuickSlotGroupWindow value in _quickSlotWindows.Values)
		{
			value.SetWindowVisible(visible);
		}
	}

	private static string FormatForegroundPauseTrigger(ForegroundWindowSnapshot? trigger)
	{
		if (trigger.HasValue)
		{
			ForegroundWindowSnapshot valueOrDefault = trigger.GetValueOrDefault();
			if (valueOrDefault.IsValid)
			{
				return " process=" + valueOrDefault.ProcessName + ", " + $"pid={valueOrDefault.ProcessId}, " + "class=" + valueOrDefault.WindowClass + ", " + $"maximized={valueOrDefault.IsMaximized}, " + $"window={valueOrDefault.WindowBounds.Left}," + $"{valueOrDefault.WindowBounds.Top}," + $"{valueOrDefault.WindowBounds.Right}," + $"{valueOrDefault.WindowBounds.Bottom}, " + $"workArea={valueOrDefault.WorkAreaBounds.Left}," + $"{valueOrDefault.WorkAreaBounds.Top}," + $"{valueOrDefault.WorkAreaBounds.Right}," + $"{valueOrDefault.WorkAreaBounds.Bottom}";
			}
		}
		return string.Empty;
	}

	private async Task UpdateCodexCoreAsync(WidgetSettings settings, CancellationToken cancellationToken)
	{
		NativeWidgetRendererWindow? renderer = _widgetRenderer;
		if (renderer == null)
		{
			return;
		}
		if (settings.ShowCodexCompanion)
		{
			_codexCompanionPipeServer.Start();
			_lastCodexSnapshot = await _codexCompanionStatusReader.ReadAsync(cancellationToken);
			await renderer.UpdateCodexAsync(_lastCodexSnapshot);
		}
	}

	private void UpdateTimers()
	{
		if (_renderer != null || _widgetRenderer != null || _quickSlotWindows.Count > 0)
		{
			_reconnectTimer.Start();
		}
		else
		{
			_reconnectTimer.Stop();
			_hostHandle = IntPtr.Zero;
			_interactiveHostHandle = IntPtr.Zero;
		}
		if (!IsRenderingPaused && _widgetRenderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowSystemInfo)
		{
			_systemMetricsTimer.Start();
		}
		else
		{
			_systemMetricsTimer.Stop();
		}
		if (!IsRenderingPaused && _quickSlotWindows.Count > 0 && _widgetSettings.IsEnabled && _widgetSettings.ShowQuickSlots && _widgetSettings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin))
		{
			_recycleBinTimer.Start();
		}
		else
		{
			_recycleBinTimer.Stop();
			_lastRecycleBinHasItems = null;
		}
		bool num = !IsRenderingPaused && _widgetRenderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowCodexCompanion;
		bool isEnabled = _codexTimer.IsEnabled;
		if (num)
		{
			_codexTimer.Start();
			if (!isEnabled)
			{
				QueueCodexRefresh();
			}
		}
		else
		{
			_codexTimer.Stop();
		}
		if (_renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_foregroundAppTimer.Start();
				return;
			}
		}
		_foregroundAppTimer.Stop();
		_foregroundEventDebounce?.Cancel();
	}

	private nint FindWallpaperHostOrThrow()
	{
		nint num = _hostLocator.FindWallpaperHost();
		if (num == IntPtr.Zero)
		{
			throw new InvalidOperationException("Explorer의 WorkerW 월페이퍼 호스트를 찾지 못했습니다. Vellume은 Explorer를 재시작하지 않았습니다. Explorer가 준비된 뒤 다시 적용해 주세요.");
		}
		return num;
	}

	private static bool HasInformationWidgets(WidgetSettings settings)
	{
		if (settings.IsEnabled)
		{
			if (!settings.ShowClock && !settings.ShowSystemInfo)
			{
				return settings.ShowCodexCompanion;
			}
			return true;
		}
		return false;
	}

	private async Task AttachWallpaperRendererAsync(IWallpaperRenderer renderer, nint hostHandle, DisplayInfo display, CancellationToken cancellationToken)
	{
		await renderer.AttachToAsync(hostHandle, display, _hostLocator.IsRaisedDesktop, _hostLocator.ShellViewHandle, _hostLocator.BackgroundWorkerHandle, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
	}

	private void AttachWidgetRenderer(NativeWidgetRendererWindow renderer, nint hostHandle, DisplayInfo display)
	{
		renderer.AttachTo(
			hostHandle,
			display);
	}

	private async Task FinalizeDesktopLayerOrderAsync(IWallpaperRenderer wallpaper, NativeWidgetRendererWindow? widgets, nint hostHandle, DisplayInfo display, CancellationToken cancellationToken)
	{
		if (!wallpaper.IsAttachedTo(hostHandle))
		{
			await AttachWallpaperRendererAsync(wallpaper, hostHandle, display, cancellationToken);
		}
		if (widgets != null)
		{
			AttachWidgetRenderer(widgets, hostHandle, display);
		}
	}

	private void SetState(WallpaperPlaybackState state, string message)
	{
		State = state;
		this.StateChanged?.Invoke(this, new WallpaperStateChangedEventArgs(state, message));
	}

	private static PlaybackOptions CreateContinuousPlaybackOptions(
		PlaybackOptions options)
	{
		PlaybackOptions normalized = options.Copy();
		normalized.PauseWhenForegroundAppIsMaximized = false;
		normalized.ApplicationRules = [];
		return normalized;
	}

	private void SetAutomaticAction(ApplicationPlaybackAction action)
	{
		if (_automaticAction == action)
		{
			return;
		}

		_automaticAction = action;
		AutomaticPlaybackActionChanged?.Invoke(
			this,
			new AutomaticPlaybackActionChangedEventArgs(action));
	}
}
