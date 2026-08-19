using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vellume.Core;
using Vellume.Windows;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSystemColors = System.Windows.SystemColors;

namespace Vellume.App;

public partial class DockWindow : Window, INotifyPropertyChanged, IDisposable
{
    private enum DockSection
    {
        System,
        Development,
        Tools,
        Games
    }

    private const int WmHotkey = 0x0312;
    private const int DockHotkeyId = 0x56D0;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyOem3 = 0xC0;
    private const int DragHoldDelayMilliseconds = 350;
    private readonly ISystemShortcutService _systemShortcutService;
    private readonly IAppLogger _logger;
    private readonly WindowsDockRuntimeService _dockRuntime = new();
    private readonly WindowsQuickSlotRuntimeService _quickSlotRuntime = new();
    private readonly WindowsIconService _iconService = new();
    private readonly WindowsSystemEventMonitor _systemEventMonitor;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _runtimeRefreshDebounceTimer;
    private AppSettings _settings;
    private bool _disposed;
    private double _tileSize = 58;
    private HwndSource? _windowSource;
    private System.Windows.Point _dragStartPoint;
    private DockItemViewModel? _dragCandidate;
    private long _dragPressStartedAtMilliseconds;
    private bool _dragOccurred;
    private bool _dockHotkeyRegistered;
    private bool _fullRuntimeRefreshPending;

    private static readonly HashSet<string> DevelopmentDockProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "devenv",
            "Code",
            "GitHubDesktop",
            "Rider64",
            "idea64",
            "pycharm64",
            "webstorm64"
        };
    private static readonly HashSet<string> ToolDockProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DroidCamApp",
            "whale",
            "firefox",
            "chrome",
            "msedge",
            "Discord",
            "Notion",
            "KakaoTalk",
            "ChatGPT",
            "Codex",
            "obs64"
        };
    private static readonly HashSet<string> GameDockProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "steam",
            "steamwebhelper",
            "EpicGamesLauncher"
        };

    public DockWindow(
        AppSettings settings,
        ISystemShortcutService systemShortcutService,
        IAppLogger logger,
        WindowsSystemEventMonitor systemEventMonitor)
    {
        InitializeComponent();
        DataContext = this;
        _settings = settings;
        _systemShortcutService = systemShortcutService;
        _logger = logger;
        _systemEventMonitor = systemEventMonitor;
        _refreshTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _runtimeRefreshDebounceTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _runtimeRefreshDebounceTimer.Tick +=
            RuntimeRefreshDebounceTimer_Tick;
        _dockRuntime.StateChanged += RuntimeStateChanged;
        _systemEventMonitor.ForegroundWindowChanged +=
            SystemEventMonitor_ForegroundWindowChanged;
        _systemEventMonitor.RecycleBinChanged +=
            SystemEventMonitor_RecycleBinChanged;
        SourceInitialized += Window_SourceInitialized;
        RebuildPinnedItems();
    }

    public ObservableCollection<DockItemViewModel> PinnedItems { get; } = [];
    public ObservableCollection<DockItemViewModel> RunningItems { get; } = [];

    public double TileSize
    {
        get => _tileSize;
        private set
        {
            if (Math.Abs(_tileSize - value) < 0.1)
            {
                return;
            }

            _tileSize = value;
            OnPropertyChanged();
        }
    }

    public Visibility RunningSectionVisibility =>
        RunningItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ShowMainWindowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? SettingsChanged;

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings;
        TileSize = settings.Dock.Copy().IconSize + 4;
        RebuildPinnedItems();
        RefreshRuntimeState();
        PositionDock();

        if (settings.Dock.IsEnabled)
        {
            if (!IsVisible)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _runtimeRefreshDebounceTimer.Stop();
        _runtimeRefreshDebounceTimer.Tick -=
            RuntimeRefreshDebounceTimer_Tick;
        _dockRuntime.StateChanged -= RuntimeStateChanged;
        _systemEventMonitor.ForegroundWindowChanged -=
            SystemEventMonitor_ForegroundWindowChanged;
        _systemEventMonitor.RecycleBinChanged -=
            SystemEventMonitor_RecycleBinChanged;
        _quickSlotRuntime.Dispose();
        _dockRuntime.Dispose();
        if (_dockHotkeyRegistered &&
            _windowSource is not null)
        {
            UnregisterHotKey(
                _windowSource.Handle,
                DockHotkeyId);
            _dockHotkeyRegistered = false;
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        TileSize = _settings.Dock.Copy().IconSize + 4;
        PositionDock();
        RefreshRuntimeState();
        if (_settings.Dock.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        EnableNonLayeredGlass(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _dockRuntime.Attach(handle);
        _dockHotkeyRegistered = RegisterHotKey(
            handle,
            DockHotkeyId,
            ModAlt | ModNoRepeat,
            VirtualKeyOem3);
        if (!_dockHotkeyRegistered)
        {
            _ = _logger.InfoAsync(
                "Dock 호출 단축키 Alt+`를 등록하지 못했습니다.");
        }
    }

    private void EnableNonLayeredGlass(nint handle)
    {
        if (_windowSource?.CompositionTarget is not null)
        {
            _windowSource.CompositionTarget.BackgroundColor =
                WpfColor.FromArgb(0, 0, 0, 0);
        }

        // Extends the DWM-managed frame through the small Dock window. This
        // preserves a translucent desktop backdrop without WS_EX_LAYERED,
        // which can conflict with Unreal Editor's floating Slate surfaces.
        var margins = new DwmMargins
        {
            LeftWidth = -1,
            RightWidth = -1,
            TopHeight = -1,
            BottomHeight = -1
        };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);

        // Windows 11 rounds the ordinary top-level window itself. Windows 10
        // safely ignores this hint and keeps the XAML DockSurface rounding.
        var cornerPreference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            Marshal.SizeOf<int>());
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmHotkey &&
            wParam.ToInt32() == DockHotkeyId)
        {
            BringDockForward();
            handled = true;
            return nint.Zero;
        }

        if (_dockRuntime.TryHandleShellMessage(
                message,
                wParam,
                lParam))
        {
            handled = false;
        }

        return nint.Zero;
    }

    private void BringDockForward()
    {
        if (!_settings.Dock.IsEnabled)
        {
            return;
        }

        RefreshRuntimeState();
        if (!IsVisible)
        {
            Show();
        }

        // Raise once, then immediately return to normal z-order so the
        // next activated application can cover the Dock.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    private void Window_SizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
        => PositionDock();

    private void RefreshTimer_Tick(object? sender, EventArgs eventArgs)
        => RefreshRuntimeState();

    private void RuntimeStateChanged(
        object? sender,
        EventArgs eventArgs)
        => QueueRuntimeRefresh(fullRefresh: true);

    private void SystemEventMonitor_ForegroundWindowChanged(
        object? sender,
        EventArgs eventArgs)
        => QueueRuntimeRefresh(fullRefresh: false);

    private void SystemEventMonitor_RecycleBinChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(RefreshRecycleBinState));
            return;
        }
        RefreshRecycleBinState();
    }

    private void QueueRuntimeRefresh(bool fullRefresh = true)
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => QueueRuntimeRefresh(fullRefresh)));
            return;
        }

        _fullRuntimeRefreshPending |= fullRefresh;
        _runtimeRefreshDebounceTimer.Stop();
        _runtimeRefreshDebounceTimer.Start();
    }

    private void RuntimeRefreshDebounceTimer_Tick(
        object? sender,
        EventArgs eventArgs)
    {
        _runtimeRefreshDebounceTimer.Stop();
        var fullRefresh = _fullRuntimeRefreshPending;
        _fullRuntimeRefreshPending = false;
        if (fullRefresh)
        {
            RefreshRuntimeState();
        }
        else
        {
            RefreshForegroundState();
        }
    }

    private void RefreshRuntimeState()
    {
        if (_disposed || !_settings.Dock.IsEnabled)
        {
            return;
        }

        var runningApplications = _dockRuntime.Capture();
        RefreshPinnedRuntimeState(runningApplications);
        RefreshRunningItems(runningApplications);
    }

    private void RefreshForegroundState()
    {
        if (_disposed || !_settings.Dock.IsEnabled)
        {
            return;
        }

        var runtimeItems = PinnedItems
            .Concat(RunningItems)
            .Where(item => item.RunningApplication is not null)
            .ToArray();
        var applications = runtimeItems
            .Select(item => item.RunningApplication!)
            .ToArray();
        var refreshedApplications =
            _dockRuntime.RefreshForegroundStates(applications);
        for (var index = 0; index < runtimeItems.Length; index++)
        {
            var item = runtimeItems[index];
            var refreshed = refreshedApplications[index];
            item.UpdateRunningApplication(refreshed);
            item.UpdateRuntime(
                isRunning: true,
                refreshed.HasAttention);
        }
    }

    private void RefreshRecycleBinState()
    {
        if (_disposed || !_settings.Dock.IsEnabled)
        {
            return;
        }

        var hasItems = _systemShortcutService.RecycleBinHasItems();
        foreach (var item in PinnedItems.Where(item =>
                     item.SystemKind == SystemShortcutKind.RecycleBin))
        {
            if (item.DynamicState == hasItems)
            {
                continue;
            }
            item.DynamicState = hasItems;
            item.Icon = _iconService.GetIcon(
                SystemShortcutSetting.GetDefaultTargetPath(
                    SystemShortcutKind.RecycleBin),
                item.SystemShortcut?.CustomIconPath ?? string.Empty,
                SystemShortcutKind.RecycleBin,
                hasItems);
        }
    }

    private void RebuildPinnedItems()
    {
        var sections = Enum.GetValues<DockSection>()
            .ToDictionary(
                section => section,
                _ => new List<DockItemViewModel>());
        sections[DockSection.System].Add(
            DockItemViewModel.CreateVellume(
                _iconService.GetIcon(
                    Environment.ProcessPath ?? string.Empty,
                    GetVellumeDockIconPath())));

        foreach (var system in _settings.Widgets.SystemShortcuts
                     .Where(item => item.ShowInDock))
        {
            var target =
                SystemShortcutSetting.GetDefaultTargetPath(system.Kind);
            var dynamicState =
                system.Kind == SystemShortcutKind.RecycleBin &&
                _systemShortcutService.RecycleBinHasItems();
            sections[DockSection.System].Add(
                DockItemViewModel.CreateSystem(
                    system,
                    _iconService.GetIcon(
                        target,
                        system.CustomIconPath,
                        system.Kind,
                        dynamicState),
                    dynamicState));
        }

        foreach (var slot in _settings.Widgets.QuickSlots
                     .Where(item => item.ShowInDock))
        {
            var resolvedTarget =
                ExecutablePathIdentity.ResolveCurrentExecutablePath(
                    slot.TargetPath);
            sections[ResolveDockSection(slot)].Add(
                DockItemViewModel.CreateQuickSlot(
                    slot,
                    _iconService.GetIcon(
                        resolvedTarget,
                        slot.IconPath)));
        }

        var candidates = new List<DockItemViewModel>();
        foreach (var section in Enum.GetValues<DockSection>())
        {
            var items = sections[section]
                .OrderBy(item => item.DockOrder <= 0
                    ? int.MaxValue
                    : item.DockOrder)
                .ThenBy(
                    item => item.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (items.Length == 0)
            {
                continue;
            }
            if (candidates.Count > 0)
            {
                candidates.Add(
                    DockItemViewModel.CreateSeparator(
                        section.ToString()));
            }
            candidates.AddRange(items);
        }

        ApplyDiff(PinnedItems, candidates);
    }

    private DockSection ResolveDockSection(QuickSlotItem slot)
    {
        var processName = Path.GetFileNameWithoutExtension(
            slot.TargetPath);
        if (processName is not null &&
            DevelopmentDockProcesses.Contains(processName))
        {
            return DockSection.Development;
        }
        if (processName is not null &&
            ToolDockProcesses.Contains(processName))
        {
            return DockSection.Tools;
        }
        if (processName is not null &&
            GameDockProcesses.Contains(processName))
        {
            return DockSection.Games;
        }

        var group = _settings.Widgets.QuickSlotGroups
            .FirstOrDefault(candidate => candidate.Id.Equals(
                slot.GroupId,
                StringComparison.OrdinalIgnoreCase));
        var classification = string.Join(
            ' ',
            slot.GroupId,
            slot.GroupName,
            group?.Name ?? string.Empty).ToLowerInvariant();
        if (classification.Contains("game") ||
            classification.Contains("게임"))
        {
            return DockSection.Games;
        }
        if (classification.Contains("development") ||
            classification.Contains("develop") ||
            classification.Contains("work") ||
            classification.Contains("개발") ||
            classification.Contains("작업"))
        {
            return DockSection.Development;
        }
        return DockSection.Tools;
    }

    private void RefreshPinnedRuntimeState(
        IReadOnlyList<DockApplicationSnapshot>? runningApplications = null)
    {
        runningApplications ??= _dockRuntime.Capture();
        var snapshot = _quickSlotRuntime.Capture(
            _settings.Widgets.QuickSlots,
            _settings.Widgets.SystemShortcuts);
        var recycleBinHasItems =
            _systemShortcutService.RecycleBinHasItems();
        foreach (var item in PinnedItems)
        {
            var runningApplication = FindRunningApplication(
                item,
                runningApplications);
            item.UpdateRunningApplication(runningApplication);
            if (item.QuickSlot is not null &&
                snapshot.QuickSlots.TryGetValue(
                    item.QuickSlot.Slot,
                    out var quickState))
            {
                item.UpdateRuntime(
                    runningApplication is not null ||
                    quickState.IsRunning,
                    runningApplication?.HasAttention == true ||
                    quickState.HasAttention ||
                    _dockRuntime.HasAttention(item.ExecutablePath));
            }
            else if (item.SystemKind is { } kind)
            {
                if (snapshot.SystemShortcuts.TryGetValue(
                        kind,
                        out var systemState))
                {
                    item.UpdateRuntime(
                        runningApplication is not null ||
                        systemState.IsRunning,
                        runningApplication?.HasAttention == true ||
                        systemState.HasAttention ||
                        _dockRuntime.HasAttention(item.ExecutablePath));
                }

                if (kind == SystemShortcutKind.RecycleBin &&
                    item.DynamicState != recycleBinHasItems)
                {
                    item.DynamicState = recycleBinHasItems;
                    item.Icon = _iconService.GetIcon(
                        SystemShortcutSetting.GetDefaultTargetPath(kind),
                        item.SystemShortcut?.CustomIconPath ?? string.Empty,
                        kind,
                        recycleBinHasItems);
                }
            }
        }
    }

    private void RefreshRunningItems(
        IReadOnlyList<DockApplicationSnapshot>? runningApplications = null)
    {
        if (!_settings.Dock.ShowRunningApplications)
        {
            if (RunningItems.Count > 0)
            {
                RunningItems.Clear();
                OnPropertyChanged(nameof(RunningSectionVisibility));
            }
            return;
        }

        var pinnedPaths = PinnedItems
            .Select(item =>
                item.RunningApplication?.ExecutablePath ??
                item.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var explorerIsPinned = PinnedItems.Any(item =>
            item.SystemKind is
                SystemShortcutKind.FileExplorer or
                SystemShortcutKind.ThisPc);
        runningApplications ??= _dockRuntime.Capture();
        var candidates = runningApplications
            .Where(item =>
                !explorerIsPinned ||
                !WindowsDockRuntimeService.IsExplorerExecutable(
                    item.ExecutablePath))
            .Where(item => !pinnedPaths.Any(path =>
                ExecutablePathIdentity.AreEquivalent(
                    path,
                    item.ExecutablePath)))
            .Select(item => DockItemViewModel.CreateRunning(
                item,
                _iconService.GetIcon(item.ExecutablePath)))
            .ToList();
        var hadRunningItems = RunningItems.Count > 0;
        ApplyDiff(RunningItems, candidates);
        if (hadRunningItems != (RunningItems.Count > 0))
        {
            OnPropertyChanged(nameof(RunningSectionVisibility));
        }
    }

    private static DockApplicationSnapshot? FindRunningApplication(
        DockItemViewModel item,
        IReadOnlyList<DockApplicationSnapshot> runningApplications)
    {
        if (item.IsVellume ||
            item.SystemKind == SystemShortcutKind.RecycleBin)
        {
            return null;
        }

        if (item.SystemKind is
            SystemShortcutKind.FileExplorer or
            SystemShortcutKind.ThisPc)
        {
            return runningApplications.FirstOrDefault(application =>
                Path.GetFileName(application.ExecutablePath).Equals(
                    "explorer.exe",
                    StringComparison.OrdinalIgnoreCase));
        }

        return string.IsNullOrWhiteSpace(item.ExecutablePath)
            ? null
            : runningApplications.FirstOrDefault(application =>
                ExecutablePathIdentity.AreEquivalent(
                    item.ExecutablePath,
                    application.ExecutablePath));
    }

    private static void ApplyDiff(
        ObservableCollection<DockItemViewModel> target,
        IReadOnlyList<DockItemViewModel> desired)
    {
        var desiredKeys = desired
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(target[index].Key))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var incoming = desired[index];
            var existing = target.FirstOrDefault(item =>
                item.Key.Equals(
                    incoming.Key,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                target.Insert(Math.Min(index, target.Count), incoming);
                continue;
            }

            existing.UpdateFrom(incoming);
            var currentIndex = target.IndexOf(existing);
            if (currentIndex != index)
            {
                target.Move(currentIndex, index);
            }
        }
    }

    private static string GetVellumeDockIconPath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "vellume-dock-icon-v3.png");

    private async void DockItem_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement
            {
                DataContext: DockItemViewModel item
            } element)
        {
            return;
        }

        eventArgs.Handled = true;
        _dragCandidate = null;
        _dragPressStartedAtMilliseconds = 0;
        if (_dragOccurred)
        {
            _dragOccurred = false;
            return;
        }

        try
        {
            if (item.IsVellume)
            {
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (item.RunningApplication is not null)
            {
                var windows = item.RunningApplication.Windows
                    .Where(window => window.Handle != nint.Zero)
                    .ToArray();
                if (windows.Length > 1)
                {
                    ShowRunningWindowSelection(
                        element,
                        item.RunningApplication,
                        windows);
                }
                else
                {
                    _dockRuntime.Activate(item.RunningApplication);
                }
            }
            else if (item.SystemKind is { } kind)
            {
                if (!_quickSlotRuntime.TryActivateSystemShortcut(kind))
                {
                    await _systemShortcutService.LaunchAsync(kind);
                }
            }
            else if (item.QuickSlot is not null)
            {
                await _quickSlotRuntime.ActivateOrLaunchAsync(
                    item.QuickSlot,
                    forceNewInstance: false);
            }
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                $"{item.Name} Dock 동작에 실패했습니다.",
                exception);
        }
    }

    private void ShowRunningWindowSelection(
        FrameworkElement placementTarget,
        DockApplicationSnapshot application,
        IReadOnlyList<DockWindowInfo> windows)
    {
        var menu = CreateDockContextMenu();
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var title = string.IsNullOrWhiteSpace(window.Title)
                ? $"{application.Name} 창 {index + 1}"
                : window.Title;
            menu.Items.Add(CreateWindowMenuItem(
                menu,
                title,
                window.Handle));
        }

        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.Placement =
            System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void DockItem_MouseRightButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement
            {
                DataContext: DockItemViewModel item
            } element)
        {
            return;
        }

        eventArgs.Handled = true;
        var menu = CreateDockContextMenu();
        if (item.IsVellume)
        {
            AddMenuItem(menu, "Vellume 열기", () =>
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(new System.Windows.Controls.Separator());
            AddMenuItem(menu, "Vellume 종료", () =>
                ExitRequested?.Invoke(this, EventArgs.Empty));
        }
        else if (item.RunningApplication is not null)
        {
            AddRunningWindowItems(menu, item.RunningApplication);
            menu.Items.Add(new System.Windows.Controls.Separator());
            if (item.SystemKind is { } runningSystemKind)
            {
                AddMenuItem(menu, "새로 열기", async () =>
                    await _systemShortcutService.LaunchAsync(
                        runningSystemKind));
                AddMenuItem(menu, "Dock에서 제거", () =>
                    UnpinItem(item));
            }
            else if (item.QuickSlot is not null)
            {
                var isSingleInstance =
                    ExecutablePathIdentity.IsKnownSingleInstanceExecutable(
                        item.QuickSlot.TargetPath);
                AddMenuItem(
                    menu,
                    isSingleInstance ? "열기" : "새로 실행",
                    async () =>
                    await _quickSlotRuntime.ActivateOrLaunchAsync(
                        item.QuickSlot,
                        forceNewInstance: !isSingleInstance));
                AddMenuItem(menu, "Dock에서 제거", () =>
                    UnpinItem(item));
            }
            else
            {
                var isSingleInstance =
                    ExecutablePathIdentity.IsKnownSingleInstanceExecutable(
                        item.RunningApplication.ExecutablePath);
                AddMenuItem(
                    menu,
                    isSingleInstance ? "열기" : "새로 실행",
                    () =>
                    {
                        if (isSingleInstance)
                        {
                            _dockRuntime.Activate(
                                item.RunningApplication,
                                minimizeWhenForeground: false);
                        }
                        else
                        {
                            _dockRuntime.LaunchNew(
                                item.RunningApplication.ExecutablePath);
                        }
                    });
                AddMenuItem(menu, "Dock에 고정", () =>
                    PinRunningApplication(item.RunningApplication));
            }
        }
        else if (item.SystemKind == SystemShortcutKind.RecycleBin)
        {
            AddMenuItem(menu, "휴지통 열기", async () =>
                await _systemShortcutService.LaunchAsync(
                    SystemShortcutKind.RecycleBin));
            AddMenuItem(menu, "휴지통 비우기", async () =>
                await _systemShortcutService.EmptyRecycleBinAsync(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle));
            menu.Items.Add(new System.Windows.Controls.Separator());
            AddMenuItem(menu, "Dock에서 제거", () =>
                UnpinItem(item));
        }
        else if (item.SystemKind is { } systemKind)
        {
            AddMenuItem(menu, "새로 열기", async () =>
                await _systemShortcutService.LaunchAsync(systemKind));
            menu.Items.Add(new System.Windows.Controls.Separator());
            AddMenuItem(menu, "Dock에서 제거", () =>
                UnpinItem(item));
        }
        else if (item.QuickSlot is not null)
        {
            var isSingleInstance =
                ExecutablePathIdentity.IsKnownSingleInstanceExecutable(
                    item.QuickSlot.TargetPath);
            AddMenuItem(
                menu,
                isSingleInstance ? "열기" : "새로 실행",
                async () =>
                await _quickSlotRuntime.ActivateOrLaunchAsync(
                    item.QuickSlot,
                    forceNewInstance: !isSingleInstance));
            menu.Items.Add(new System.Windows.Controls.Separator());
            AddMenuItem(menu, "Dock에서 제거", () =>
                UnpinItem(item));
        }

        if (menu.Items.Count > 0)
        {
            element.ContextMenu = menu;
            menu.IsOpen = true;
        }
    }

    private void AddRunningWindowItems(
        System.Windows.Controls.ContextMenu menu,
        DockApplicationSnapshot application)
    {
        var windows = application.Windows
            .Where(window => window.Handle != nint.Zero)
            .ToArray();
        if (windows.Length <= 1)
        {
            foreach (var window in windows)
            {
                menu.Items.Add(CreateWindowMenuItem(
                    menu,
                    $"열린 창 · {window.Title}",
                    window.Handle));
            }
            return;
        }

        var windowList = CreateDockMenuItem(
            $"열린 창 ({windows.Length})");
        foreach (var window in windows)
        {
            var child = CreateWindowMenuItem(
                menu,
                window.Title,
                window.Handle);
            windowList.Items.Add(child);
        }
        menu.Items.Add(windowList);
    }

    private System.Windows.Controls.MenuItem CreateWindowMenuItem(
        System.Windows.Controls.ContextMenu rootMenu,
        string title,
        nint windowHandle)
    {
        var item = CreateDockMenuItem(string.Empty);
        item.Width = 420;
        item.HorizontalContentAlignment =
            System.Windows.HorizontalAlignment.Stretch;
        item.Template = CreateWindowRowMenuItemTemplate();
        var header = new System.Windows.Controls.Grid
        {
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Stretch
        };
        header.ColumnDefinitions.Add(
            new System.Windows.Controls.ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(
            new System.Windows.Controls.ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var text = CreateDockMenuHeader(
            WindowsDockRuntimeService.TruncateWindowTitle(title));
        text.MaxWidth = 350;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.ToolTip = title;
        text.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(text);

        var close = new System.Windows.Controls.Button
        {
            Width = 28,
            Height = 24,
            Margin = new Thickness(14, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = WpfBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "창 닫기",
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Right,
            VerticalAlignment =
                System.Windows.VerticalAlignment.Center,
            Template = CreateDockCloseButtonTemplate()
        };
        close.Click += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            _dockRuntime.CloseWindow(windowHandle);
            rootMenu.IsOpen = false;
            QueueRuntimeRefresh();
        };
        System.Windows.Controls.Grid.SetColumn(close, 1);
        header.Children.Add(close);
        item.Header = header;
        item.Click += (_, _) =>
            _dockRuntime.ActivateWindow(windowHandle);
        return item;
    }

    private static System.Windows.Controls.ControlTemplate
        CreateWindowRowMenuItemTemplate()
    {
        var template = new System.Windows.Controls.ControlTemplate(
            typeof(System.Windows.Controls.MenuItem));
        var row = new FrameworkElementFactory(
            typeof(System.Windows.Controls.Border),
            "RowBorder");
        row.SetValue(
            System.Windows.Controls.Border.BackgroundProperty,
            WpfBrushes.Transparent);
        row.SetValue(
            System.Windows.Controls.Border.PaddingProperty,
            new Thickness(8, 2, 6, 2));
        row.SetValue(
            System.Windows.Controls.Border.SnapsToDevicePixelsProperty,
            true);

        var presenter = new FrameworkElementFactory(
            typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetValue(
            System.Windows.Controls.ContentPresenter.ContentSourceProperty,
            "Header");
        presenter.SetValue(
            System.Windows.Controls.ContentPresenter
                .HorizontalAlignmentProperty,
            System.Windows.HorizontalAlignment.Stretch);
        presenter.SetValue(
            System.Windows.Controls.ContentPresenter
                .VerticalAlignmentProperty,
            System.Windows.VerticalAlignment.Center);
        row.AppendChild(presenter);
        template.VisualTree = row;

        var highlighted = new Trigger
        {
            Property =
                System.Windows.Controls.MenuItem.IsHighlightedProperty,
            Value = true
        };
        highlighted.Setters.Add(new Setter(
            System.Windows.Controls.Border.BackgroundProperty,
            new SolidColorBrush(
                WpfColor.FromRgb(206, 232, 255)),
            "RowBorder"));
        template.Triggers.Add(highlighted);
        return template;
    }

    private static System.Windows.Controls.ControlTemplate
        CreateDockCloseButtonTemplate()
    {
        var template = new System.Windows.Controls.ControlTemplate(
            typeof(System.Windows.Controls.Button));
        var background = new FrameworkElementFactory(
            typeof(System.Windows.Controls.Border));
        background.SetValue(
            System.Windows.Controls.Border.BackgroundProperty,
            new SolidColorBrush(
                WpfColor.FromRgb(246, 176, 211)));
        background.SetValue(
            System.Windows.Controls.Border.BorderBrushProperty,
            new SolidColorBrush(
                WpfColor.FromRgb(225, 40, 133)));
        background.SetValue(
            System.Windows.Controls.Border.BorderThicknessProperty,
            new Thickness(1));
        background.SetValue(
            System.Windows.Controls.Border.CornerRadiusProperty,
            new CornerRadius(6));

        var glyph = new FrameworkElementFactory(
            typeof(System.Windows.Controls.TextBlock));
        glyph.SetValue(
            System.Windows.Controls.TextBlock.TextProperty,
            "×");
        glyph.SetValue(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            new SolidColorBrush(
                WpfColor.FromRgb(55, 59, 62)));
        glyph.SetValue(
            System.Windows.Controls.TextBlock.FontFamilyProperty,
            new WpfFontFamily("Segoe UI"));
        glyph.SetValue(
            System.Windows.Controls.TextBlock.FontSizeProperty,
            15d);
        glyph.SetValue(
            System.Windows.Controls.TextBlock.FontWeightProperty,
            FontWeights.Bold);
        glyph.SetValue(
            System.Windows.Controls.TextBlock.HorizontalAlignmentProperty,
            System.Windows.HorizontalAlignment.Center);
        glyph.SetValue(
            System.Windows.Controls.TextBlock.VerticalAlignmentProperty,
            System.Windows.VerticalAlignment.Center);
        background.AppendChild(glyph);
        template.VisualTree = background;
        return template;
    }

    private void DockItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        _dragStartPoint = eventArgs.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext
            as DockItemViewModel;
        _dragPressStartedAtMilliseconds =
            _dragCandidate is null
                ? 0
                : Environment.TickCount64;
        _dragOccurred = false;
    }

    private void DockItem_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed ||
            _dragCandidate is null ||
            _dragCandidate.IsVellume ||
            _dragCandidate.IsSeparator)
        {
            return;
        }

        if (_dragPressStartedAtMilliseconds <= 0 ||
            Environment.TickCount64 -
            _dragPressStartedAtMilliseconds <
            DragHoldDelayMilliseconds)
        {
            return;
        }

        var point = eventArgs.GetPosition(this);
        if (Math.Abs(point.X - _dragStartPoint.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStartPoint.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedItem = _dragCandidate;
        _dragCandidate = null;
        _dragPressStartedAtMilliseconds = 0;
        _dragOccurred = true;
        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            draggedItem,
            System.Windows.DragDropEffects.Move);
    }

    private void DockItem_Drop(
        object sender,
        System.Windows.DragEventArgs eventArgs)
    {
        if (sender is not FrameworkElement
            {
                DataContext: DockItemViewModel target
            } ||
            target.IsVellume ||
            target.IsSeparator ||
            eventArgs.Data.GetData(typeof(DockItemViewModel))
                is not DockItemViewModel dragged ||
            dragged.IsSeparator ||
            ReferenceEquals(dragged, target))
        {
            return;
        }

        eventArgs.Handled = true;
        if (dragged.RunningApplication is not null)
        {
            dragged = PinRunningApplication(
                          dragged.RunningApplication,
                          notify: false) ??
                      dragged;
        }
        if (!PinnedItems.Contains(dragged) ||
            !PinnedItems.Contains(target))
        {
            return;
        }

        var sourceIndex = PinnedItems.IndexOf(dragged);
        var targetIndex = PinnedItems.IndexOf(target);
        if (sourceIndex < 1 || targetIndex < 1)
        {
            return;
        }

        PinnedItems.Move(sourceIndex, targetIndex);
        PersistPinnedOrder();
        NotifySettingsChanged();
    }

    private DockItemViewModel? PinRunningApplication(
        DockApplicationSnapshot application,
        bool notify = true)
    {
        var existing = _settings.Widgets.QuickSlots
            .FirstOrDefault(item =>
                ExecutablePathIdentity.AreEquivalent(
                    item.TargetPath,
                    application.ExecutablePath));
        if (existing is null)
        {
            var occupiedSlots = _settings.Widgets.QuickSlots
                .Select(item => item.Slot)
                .Where(slot => slot is >= 1 and <= 99)
                .ToHashSet();
            var slot = Enumerable.Range(1, 99)
                .FirstOrDefault(candidate =>
                    !occupiedSlots.Contains(candidate));
            if (slot == 0)
            {
                return null;
            }

            var group =
                _settings.Widgets.QuickSlotGroups.FirstOrDefault(item =>
                    item.Id.Equals(
                        "apps",
                        StringComparison.OrdinalIgnoreCase)) ??
                _settings.Widgets.QuickSlotGroups.FirstOrDefault() ??
                new QuickSlotGroup
                {
                    Id = "apps",
                    Name = "앱"
                };
            if (!_settings.Widgets.QuickSlotGroups.Contains(group))
            {
                _settings.Widgets.QuickSlotGroups.Add(group);
            }

            existing = new QuickSlotItem
            {
                Slot = slot,
                Name = application.Name,
                TargetPath = application.ExecutablePath,
                GroupId = group.Id,
                GroupName = group.Name
            };
            _settings.Widgets.QuickSlots.Add(existing);
        }
        else
        {
            var resolvedTarget =
                ExecutablePathIdentity.ResolveCurrentExecutablePath(
                    existing.TargetPath);
            if (File.Exists(application.ExecutablePath) &&
                !string.Equals(
                    resolvedTarget,
                    application.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                existing.TargetPath = application.ExecutablePath;
            }
        }

        existing.ShowInDock = true;
        existing.DockOrder = GetNextDockOrder();
        RebuildPinnedItems();
        RefreshRuntimeState();
        var pinned = PinnedItems.FirstOrDefault(item =>
            item.QuickSlot is not null &&
            ReferenceEquals(item.QuickSlot, existing));
        if (notify)
        {
            NotifySettingsChanged();
        }

        return pinned;
    }

    private void UnpinItem(DockItemViewModel item)
    {
        if (item.SystemShortcut is not null)
        {
            item.SystemShortcut.ShowInDock = false;
        }
        else if (item.QuickSlot is not null)
        {
            item.QuickSlot.ShowInDock = false;
        }
        else
        {
            return;
        }

        RebuildPinnedItems();
        RefreshRuntimeState();
        PersistPinnedOrder();
        NotifySettingsChanged();
    }

    private int GetNextDockOrder()
        => PinnedItems
               .Where(item =>
                   !item.IsVellume &&
                   !item.IsSeparator)
               .Select(item => item.DockOrder)
               .DefaultIfEmpty(0)
               .Max() + 10;

    private void PersistPinnedOrder()
    {
        var order = 10;
        foreach (var item in PinnedItems.Where(item =>
                     !item.IsVellume &&
                     !item.IsSeparator))
        {
            item.DockOrder = order;
            if (item.SystemShortcut is not null)
            {
                item.SystemShortcut.DockOrder = order;
            }
            else if (item.QuickSlot is not null)
            {
                item.QuickSlot.DockOrder = order;
            }

            order += 10;
        }
    }

    private void NotifySettingsChanged()
    {
        _settings.SchemaVersion = 16;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void AddMenuItem(
        System.Windows.Controls.ContextMenu menu,
        string title,
        Action action)
    {
        var item = CreateDockMenuItem(title);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void AddMenuItem(
        System.Windows.Controls.ContextMenu menu,
        string title,
        Func<Task> action)
    {
        var item = CreateDockMenuItem(title);
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    private static System.Windows.Controls.MenuItem
        CreateDockMenuItem(string title)
        => new()
        {
            Header = CreateDockMenuHeader(title),
            Foreground = WpfBrushes.Black,
            Background = WpfBrushes.Transparent,
            FontWeight = FontWeights.SemiBold
        };

    private static System.Windows.Controls.ContextMenu
        CreateDockContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new SolidColorBrush(
                WpfColor.FromRgb(248, 248, 248)),
            Foreground = WpfBrushes.Black,
            BorderBrush = new SolidColorBrush(
                WpfColor.FromRgb(146, 146, 146)),
            BorderThickness = new Thickness(1),
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 12
        };
        menu.Resources[WpfSystemColors.MenuBrushKey] =
            menu.Background;
        menu.Resources[WpfSystemColors.MenuTextBrushKey] =
            WpfBrushes.Black;
        menu.Resources[WpfSystemColors.HighlightTextBrushKey] =
            WpfBrushes.Black;
        menu.Resources[WpfSystemColors.GrayTextBrushKey] =
            new SolidColorBrush(WpfColor.FromRgb(95, 95, 95));

        var textStyle = new Style(
            typeof(System.Windows.Controls.TextBlock));
        textStyle.Setters.Add(new Setter(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            WpfBrushes.Black));
        textStyle.Setters.Add(new Setter(
            System.Windows.Controls.TextBlock.FontWeightProperty,
            FontWeights.SemiBold));
        menu.Resources[
            typeof(System.Windows.Controls.TextBlock)] = textStyle;

        var itemStyle = new Style(
            typeof(System.Windows.Controls.MenuItem));
        itemStyle.Setters.Add(new Setter(
            System.Windows.Controls.MenuItem.ForegroundProperty,
            WpfBrushes.Black));
        itemStyle.Setters.Add(new Setter(
            System.Windows.Controls.MenuItem.BackgroundProperty,
            WpfBrushes.Transparent));
        itemStyle.Setters.Add(new Setter(
            System.Windows.Controls.MenuItem.FontWeightProperty,
            FontWeights.SemiBold));
        menu.Resources[
            typeof(System.Windows.Controls.MenuItem)] = itemStyle;
        return menu;
    }

    private static System.Windows.Controls.TextBlock
        CreateDockMenuHeader(string title)
        => new()
        {
            Text = title,
            Foreground = WpfBrushes.Black,
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };

    private void PositionDock()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Left = Math.Max(
            0,
            (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2);
        Top = Math.Max(
            0,
            SystemParameters.PrimaryScreenHeight -
            ActualHeight -
            _settings.Dock.Copy().BottomMargin);
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    // Lets DWM provide transparency without turning the WPF Dock into a
    // per-pixel layered window.
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        nint window,
        ref DwmMargins margins);

    // Applies supported Windows 11 visual hints to the ordinary Dock HWND.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    // Registers Alt+` as an explicit way to raise the non-topmost Dock.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint window,
        int identifier,
        uint modifiers,
        uint virtualKey);

    // Releases the Dock shortcut during shutdown without affecting other apps.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        nint window,
        int identifier);
}

public sealed class DockItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isRunning;
    private bool _hasAttention;
    private bool _dynamicState;
    private DockApplicationSnapshot? _runningApplication;

    private DockItemViewModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public string Key { get; }
    public string Name { get; private set; }
    public string ExecutablePath { get; private set; } = string.Empty;
    public bool IsVellume { get; private set; }
    public bool IsSeparator { get; private set; }
    public SystemShortcutKind? SystemKind { get; private set; }
    public SystemShortcutSetting? SystemShortcut { get; private set; }
    public QuickSlotItem? QuickSlot { get; private set; }
    public int DockOrder { get; set; }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public bool DynamicState
    {
        get => _dynamicState;
        set => SetField(ref _dynamicState, value);
    }

    public DockApplicationSnapshot? RunningApplication
    {
        get => _runningApplication;
        private set => SetField(ref _runningApplication, value);
    }

    public Visibility RunningVisibility =>
        _isRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AttentionVisibility =>
        _hasAttention ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static DockItemViewModel CreateVellume(ImageSource? icon)
        => new("vellume:control", "Vellume")
        {
            Icon = icon,
            IsVellume = true,
            DockOrder = 1
        };

    public static DockItemViewModel CreateSeparator(string section)
        => new($"separator:{section}", string.Empty)
        {
            IsSeparator = true
        };

    public static DockItemViewModel CreateSystem(
        SystemShortcutSetting system,
        ImageSource? icon,
        bool dynamicState)
        => new($"system:{system.Kind}", system.Name)
        {
            Icon = icon,
            SystemKind = system.Kind,
            SystemShortcut = system,
            DockOrder = system.DockOrder,
            ExecutablePath =
                SystemShortcutSetting.GetDefaultTargetPath(system.Kind),
            DynamicState = dynamicState
        };

    public static DockItemViewModel CreateQuickSlot(
        QuickSlotItem slot,
        ImageSource? icon)
        => new($"quick:{slot.Slot}:{slot.TargetPath}", slot.Name)
        {
            Icon = icon,
            QuickSlot = slot,
            DockOrder = slot.DockOrder,
            ExecutablePath =
                ExecutablePathIdentity.ResolveCurrentExecutablePath(
                    slot.TargetPath)
        };

    public static DockItemViewModel CreateRunning(
        DockApplicationSnapshot application,
        ImageSource? icon)
        => new($"running:{application.Key}", application.Name)
        {
            Icon = icon,
            ExecutablePath = application.ExecutablePath,
            RunningApplication = application,
            _isRunning = true,
            _hasAttention = application.HasAttention
        };

    public void UpdateRuntime(bool isRunning, bool hasAttention)
    {
        if (_isRunning != isRunning)
        {
            _isRunning = isRunning;
            OnPropertyChanged(nameof(RunningVisibility));
        }
        if (_hasAttention != hasAttention)
        {
            _hasAttention = hasAttention;
            OnPropertyChanged(nameof(AttentionVisibility));
        }
    }

    public void UpdateRunningApplication(
        DockApplicationSnapshot? application)
        => RunningApplication = application;

    public void UpdateFrom(DockItemViewModel incoming)
    {
        if (!string.Equals(Name, incoming.Name, StringComparison.Ordinal))
        {
            Name = incoming.Name;
            OnPropertyChanged(nameof(Name));
        }

        ExecutablePath = incoming.ExecutablePath;
        IsVellume = incoming.IsVellume;
        IsSeparator = incoming.IsSeparator;
        SystemKind = incoming.SystemKind;
        SystemShortcut = incoming.SystemShortcut;
        QuickSlot = incoming.QuickSlot;
        DockOrder = incoming.DockOrder;
        RunningApplication = incoming.RunningApplication;
        DynamicState = incoming.DynamicState;
        if (Icon is null && incoming.Icon is not null)
        {
            Icon = incoming.Icon;
        }
        UpdateRuntime(incoming._isRunning, incoming._hasAttention);
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
