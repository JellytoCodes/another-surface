using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vellume.Core;
using Vellume.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;

namespace Vellume.App;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsWallpaperApplyingProperty =
        DependencyProperty.Register(
            nameof(IsWallpaperApplying), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false));

    public bool IsWallpaperApplying
    {
        get => (bool)GetValue(IsWallpaperApplyingProperty);
        private set => SetValue(IsWallpaperApplyingProperty, value);
    }

    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly IWallpaperController _wallpaperController;
    private readonly IMonitorService _monitorService;
    private readonly ISystemShortcutService _systemShortcutService;
    private readonly WallpaperLibraryStore _libraryStore;
    private readonly IAppLogger _logger;
    private readonly QuickSlotHotkeyManager _hotkeyManager = new();
    private readonly WindowsQuickSlotRuntimeService
        _quickSlotRuntimeService = new();
    private readonly WindowsSystemEventMonitor _systemEventMonitor;
    private readonly WindowsStartupRegistrationService
        _startupRegistrationService;
    private readonly DispatcherTimer _quickSlotRuntimeTimer;
    private readonly SemaphoreSlim _wallpaperApplyGate = new(1, 1);
    private readonly SemaphoreSlim _libraryMutationGate = new(1, 1);
    private readonly CancellationTokenSource _libraryLifetime = new();
    private readonly WallpaperThumbnailService _thumbnails = new(
        Path.Combine(new AppPaths().RootDirectory, "Thumbnails"));
    private readonly VideoProxyCache _videoMetadata = new();
    private string _libraryFilter = "All";
    private CancellationTokenSource? _wallpaperApplyCancellation;
    private string? _applyingDisplayId;
    private HwndSource? _windowSource;
    private bool _initialized;
    private bool _runtimeRefreshInProgress;
    private bool _runtimeClosing;
    private bool _updatingPlaybackControls;
    private bool _refreshingDisplays;
    private readonly Dictionary<string, DisplayInfo> _displayStateCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Actual renderer sources can differ from profile originals (video proxies).
    // Keep disconnected/stopped displays until replacement or app exit: controllers
    // can reconnect, and Stop can overlap an in-flight Apply on the UI dispatcher.
    private readonly Dictionary<string, HashSet<string>> _activePlaybackSources =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheMaintenanceReady;
    private DateTimeOffset _lastCacheMaintenanceUtc;

    public MainWindow(
        AppSettings settings,
        ISettingsStore settingsStore,
        IWallpaperController wallpaperController,
        IMonitorService monitorService,
        ISystemShortcutService systemShortcutService,
        WallpaperLibraryStore libraryStore,
        IAppLogger logger,
        WindowsSystemEventMonitor systemEventMonitor,
        WindowsStartupRegistrationService startupRegistrationService)
    {
        InitializeComponent();
        _quickSlotRuntimeTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _quickSlotRuntimeTimer.Tick +=
            QuickSlotRuntimeTimerOnTick;
        _quickSlotRuntimeService.StateChanged +=
            QuickSlotRuntimeService_StateChanged;
        _settings = settings;
        _settingsStore = settingsStore;
        _wallpaperController = wallpaperController;
        _monitorService = monitorService;
        _systemShortcutService = systemShortcutService;
        _libraryStore = libraryStore;
        _logger = logger;
        _systemEventMonitor = systemEventMonitor;
        _startupRegistrationService = startupRegistrationService;
        _systemEventMonitor.RecycleBinChanged +=
            SystemEventMonitor_RuntimeStateChanged;
        _systemEventMonitor.DisplayConfigurationChanged +=
            SystemEventMonitor_DisplayConfigurationChanged;
        _wallpaperController.StateChanged += WallpaperController_StateChanged;
        _wallpaperController.QuickSlotInvoked +=
            WallpaperController_QuickSlotInvoked;
        _wallpaperController.QuickSlotSecondaryInvoked +=
            WallpaperController_QuickSlotSecondaryInvoked;
        _wallpaperController.QuickSlotLayoutChanged +=
            WallpaperController_QuickSlotLayoutChanged;
        _wallpaperController.WidgetPlacementChanged += WallpaperController_WidgetPlacementChanged;
        _wallpaperController.StickerChanged += WallpaperController_StickerChanged;
        _hotkeyManager.SlotInvoked += QuickSlotHotkeyManager_SlotInvoked;
        SourceInitialized += Window_SourceInitialized;
        IsVisibleChanged += Window_IsVisibleChanged;
        InitializeWidgetControls();
    }

    public ObservableCollection<WallpaperItem> Library { get; } = [];
    public ObservableCollection<DisplayInfo> Displays { get; } = [];
    public ObservableCollection<QuickSlotItem> QuickSlots { get; } = [];
    public ObservableCollection<SystemShortcutSetting> SystemShortcuts
        { get; } = [];
    public ObservableCollection<QuickSlotGroup> QuickSlotGroups { get; } = [];
    public bool AllowClose { get; set; }
    public event EventHandler? DockSettingsChanged;
    public event EventHandler? InitializationCompleted;

    public void SynchronizeDockItems()
    {
        QuickSlots.Clear();
        foreach (var item in _settings.Widgets.QuickSlots
                     .OrderBy(item => item.Slot))
        {
            QuickSlots.Add(item);
        }

        SystemShortcuts.Clear();
        foreach (var item in _settings.Widgets.SystemShortcuts)
        {
            SystemShortcuts.Add(item);
        }

        QuickSlotList.Items.Refresh();
        SystemShortcutList.Items.Refresh();
    }

    public void PrepareForExit()
    {
        if (_runtimeClosing)
        {
            return;
        }

        _runtimeClosing = true;
        _libraryLifetime.Cancel();
        Interlocked.Exchange(
                ref _wallpaperApplyCancellation,
                null)
            ?.Cancel();
        _quickSlotRuntimeTimer.Stop();
        _clockPreviewTimer?.Stop();
        _wallpaperController.WidgetPlacementChanged -= WallpaperController_WidgetPlacementChanged;
        _wallpaperController.StickerChanged -= WallpaperController_StickerChanged;
        _quickSlotRuntimeTimer.Tick -=
            QuickSlotRuntimeTimerOnTick;
        IsVisibleChanged -= Window_IsVisibleChanged;
        _quickSlotRuntimeService.StateChanged -=
            QuickSlotRuntimeService_StateChanged;
        _systemEventMonitor.RecycleBinChanged -=
            SystemEventMonitor_RuntimeStateChanged;
        _systemEventMonitor.DisplayConfigurationChanged -=
            SystemEventMonitor_DisplayConfigurationChanged;
        _quickSlotRuntimeService.Dispose();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            _settings.Library.RemoveAll(item => item.IsBuiltIn);
            foreach (var profile in _settings.DisplayWallpapers)
            {
                if (profile.Wallpaper?.IsBuiltIn == true)
                {
                    profile.Wallpaper = null;
                }
            }
            if (_settings.ActiveWallpaper?.IsBuiltIn == true)
            {
                _settings.ActiveWallpaper = null;
            }
            await MigrateLibraryToManagedStorageAsync(
                _libraryLifetime.Token);
            foreach (var wallpaper in _settings.Library)
            {
                Library.Add(wallpaper);
            }
            LibraryList.ItemsSource = Library;
            Library.CollectionChanged += Library_CollectionChanged;
            RefreshLibraryFilter();

            DisplayList.ItemsSource = Displays;
            RefreshDisplays();
            if (DisplayList.SelectedItem is DisplayInfo selectedDisplay)
            {
                _settings.Playback = _settings
                    .GetOrCreateDisplayProfile(selectedDisplay.Id)
                    .Playback
                    .Copy();
            }

            var fitOptions = new[]
            {
                new SelectionOption<WallpaperFit>(
                    WallpaperFit.Fill,
                    "채우기"),
                new SelectionOption<WallpaperFit>(
                    WallpaperFit.Fit,
                    "맞춤"),
                new SelectionOption<WallpaperFit>(
                    WallpaperFit.Stretch,
                    "늘이기")
            };
            FitComboBox.ItemsSource = fitOptions;
            FitComboBox.SelectedItem = fitOptions.First(option =>
                option.Value == _settings.Playback.Fit);
            MuteCheckBox.IsChecked = _settings.Playback.IsMuted;
            VolumeSlider.Value = _settings.Playback.Volume * 100;
            OptimizeVideoCheckBox.IsChecked =
                _settings.Playback.UseOptimizedVideo;
            NormalizeInternalPlaybackPolicy();
            RestoreCheckBox.IsChecked = _settings.RestoreOnStartup;
            LaunchAtLoginCheckBox.IsChecked =
                _settings.LaunchAtWindowsLogin;

            _settings.Widgets ??= new WidgetSettings();
            _settings.Dock ??= new DockSettings();
            RefreshWidgetPlacementControls();
            SnapQuickSlotsCheckBox.IsChecked =
                _settings.Widgets.SnapQuickSlotsToGrid;
            QuickSlotGridSizeComboBox.ItemsSource =
                new[] { 20, 40, 80 };
            QuickSlotGridSizeComboBox.SelectedItem =
                _settings.Widgets.QuickSlotGridSize;
            if (QuickSlotGridSizeComboBox.SelectedItem is null)
            {
                QuickSlotGridSizeComboBox.SelectedItem = 40;
            }
            foreach (var group in _settings.Widgets.QuickSlotGroups)
            {
                QuickSlotGroups.Add(group);
            }
            QuickSlotGroupComboBox.ItemsSource = QuickSlotGroups;
            QuickSlotGroupComboBox.SelectedItem =
                QuickSlotGroups.FirstOrDefault();
            foreach (var slot in _settings.Widgets.QuickSlots
                         .Where(item => item.Slot is >= 1 and <= 99)
                         .OrderBy(item => item.Slot)
                         .GroupBy(item => item.Slot)
                         .Select(group => group.First()))
            {
                QuickSlots.Add(slot);
            }
            QuickSlotList.ItemsSource = QuickSlots;
            foreach (var systemItem in
                     _settings.Widgets.SystemShortcuts)
            {
                SystemShortcuts.Add(systemItem);
            }
            SystemShortcutList.ItemsSource = SystemShortcuts;
            var discoveryResult = DiscoverAndAddKnownApps();
            UpdateKnownAppsStatus(discoveryResult);
            WidgetsEnabledCheckBox.IsChecked = _settings.Widgets.IsEnabled;
            ClockWidgetCheckBox.IsChecked = _settings.Widgets.ShowClock;
            SystemWidgetCheckBox.IsChecked = _settings.Widgets.ShowSystemInfo;
            QuickSlotsWidgetCheckBox.IsChecked =
                _settings.Widgets.ShowQuickSlots;
            CodexCompanionWidgetCheckBox.IsChecked =
                _settings.Widgets.ShowCodexCompanion;
            DockEnabledCheckBox.IsChecked = _settings.Dock.IsEnabled;
            DockRunningAppsCheckBox.IsChecked =
                _settings.Dock.ShowRunningApplications;
            WindowsTaskbarVisibleCheckBox.IsChecked =
                _settings.Dock.ShowWindowsTaskbar;
            DockIconSizeComboBox.ItemsSource =
                new[] { 44, 54, 64, 72 };
            DockIconSizeComboBox.SelectedItem =
                _settings.Dock.Copy().IconSize;
            if (DockIconSizeComboBox.SelectedItem is null)
            {
                DockIconSizeComboBox.SelectedItem = 54;
            }
            DockBottomMarginComboBox.ItemsSource =
                new[] { 0, 8, 12, 20, 32 };
            DockBottomMarginComboBox.SelectedItem =
                _settings.Dock.Copy().BottomMargin;
            if (DockBottomMarginComboBox.SelectedItem is null)
            {
                DockBottomMarginComboBox.SelectedItem = 12;
            }
            _initialized = true;
            await SaveAndRefreshWidgetsAsync();
            if (IsVisible)
            {
                _quickSlotRuntimeTimer.Start();
            }
            await RefreshQuickSlotRuntimeStateAsync();

            if (_settings.RestoreOnStartup)
            {
                await RestoreDisplayWallpapersAsync();
            }
            _cacheMaintenanceReady = true;
            await _wallpaperApplyGate.WaitAsync(_libraryLifetime.Token);
            try
            {
                await MaintainVideoCacheAsync();
            }
            finally
            {
                _wallpaperApplyGate.Release();
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
                await _wallpaperController
                    .CompleteDesktopWallpaperHandoffAsync();
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync(
                    "Windows 정지 배경 인계를 마무리하지 못했습니다.",
                    exception);
            }

            InitializationCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (AllowClose)
        {
            PrepareForExit();
            _wallpaperController.StateChanged -= WallpaperController_StateChanged;
            _wallpaperController.QuickSlotInvoked -=
                WallpaperController_QuickSlotInvoked;
            _wallpaperController.QuickSlotSecondaryInvoked -=
                WallpaperController_QuickSlotSecondaryInvoked;
            _wallpaperController.QuickSlotLayoutChanged -=
                WallpaperController_QuickSlotLayoutChanged;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            _hotkeyManager.Dispose();
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private async void ExitButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (System.Windows.Application.Current is App app)
        {
            await app.RequestExitAsync();
        }
    }

    private async Task ApplyWallpaperAsync(
        WallpaperItem wallpaper,
        bool showDialogOnError = true,
        DisplayInfo? targetDisplay = null)
    {
        if (!await _wallpaperApplyGate.WaitAsync(0))
        {
            return;
        }

        if (_runtimeClosing)
        {
            _wallpaperApplyGate.Release();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _wallpaperApplyCancellation = cancellation;
        IsWallpaperApplying = true;
        try
        {
        var display = targetDisplay ??
            DisplayList.SelectedItem as DisplayInfo ??
            Displays.FirstOrDefault(item => item.IsPrimary);
        if (display is null)
        {
            if (showDialogOnError)
            {
                System.Windows.MessageBox.Show(
                    "연결된 디스플레이를 찾지 못했습니다.",
                    "Vellume",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }

        try
        {
            display = FindCurrentDisplay(display);
            _applyingDisplayId = display.Id;
            HomeStatusText.Text = $"'{wallpaper.Name}' → {display.Name} 적용 중…";
            SidebarStatusText.Text = "적용 중…";
            var profile = _settings.GetOrCreateDisplayProfile(display.Id);
            var playback = profile.Playback.Copy();
            var playbackWallpaper = wallpaper;
            var generatedProxy = false;
            if (wallpaper.Kind == WallpaperKind.Video &&
                (playback.UseOptimizedVideo || wallpaper.HasVideoRange))
            {
                HomeStatusText.Text =
                    wallpaper.HasVideoRange ? "재생 구간 준비 중…"
                        : "영상 준비 중…";
                var preparation = await _libraryStore.PrepareForPlaybackAsync(
                    wallpaper,
                    display.Width,
                    display.Height,
                    cancellation.Token,
                    optimize: playback.UseOptimizedVideo);
                playbackWallpaper = preparation.Wallpaper;
                generatedProxy = preparation.GeneratedProxy;
                await _logger.InfoAsync(
                    $"Video playback preparation for {display.Id}: " +
                    $"proxy={preparation.UsedProxy}, " +
                    $"generated={preparation.GeneratedProxy}, " +
                    preparation.Detail);
            }
            if (!_activePlaybackSources.TryGetValue(display.Id, out var protectedSources))
            {
                protectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _activePlaybackSources[display.Id] = protectedSources;
            }
            // A controller can retain the new session even if visibility/logging
            // fails late in Apply. Keep both old and candidate sources on failure.
            protectedSources.Add(playbackWallpaper.SourcePath);
            cancellation.Token.ThrowIfCancellationRequested();
            await _wallpaperController.ApplyAsync(
                playbackWallpaper,
                display,
                playback,
                _settings.Widgets.Copy(),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            protectedSources.Clear();
            protectedSources.Add(playbackWallpaper.SourcePath);
            // Display topology can change while video preparation or renderer
            // initialization is awaited. Update the current card, not its old snapshot.
            display = FindCurrentDisplay(display);
            // Saving another display selection can replace profile objects while
            // preparation is awaited. Commit to the current settings instance.
            profile = _settings.GetOrCreateDisplayProfile(display.Id);
            profile.Wallpaper = wallpaper.Copy();
            display.HasAssignedWallpaper = true;
            display.IsWallpaperPaused = false;
            if (DisplayList.SelectedItem is DisplayInfo selected &&
                selected.Id.Equals(
                    display.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings.ActiveWallpaper = wallpaper;
                _settings.SelectedMonitorId = display.Id;
                _settings.Playback = profile.Playback.Copy();
                NowPlayingText.Text = wallpaper.Name;
            }
            display.AssignedWallpaperName = wallpaper.Name;
            DisplayList.Items.Refresh();
            HomeStatusText.Text = $"'{wallpaper.Name}'을(를) {display.Name}에 적용했습니다.";
            await SaveSettingsAsync();
            await MaintainVideoCacheAsync(force: generatedProxy);
        }
        catch (OperationCanceledException)
            when (_runtimeClosing ||
                  cancellation.IsCancellationRequested)
        {
            // Exit cancels an in-flight RenderHost request so shutdown never
            // waits behind the wallpaper operation lock.
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("UI에서 월페이퍼 적용에 실패했습니다.", exception);
            HomeStatusText.Text = exception.Message;
            if (showDialogOnError)
            {
                System.Windows.MessageBox.Show(
                    exception.Message,
                    "월페이퍼를 적용할 수 없음",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _wallpaperApplyCancellation,
                null,
                cancellation);
            _wallpaperApplyGate.Release();
            _applyingDisplayId = null;
            IsWallpaperApplying = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        _settings.Library = Library.ToList();
        await _settingsStore.SaveAsync(_settings);
    }

    // Called while _wallpaperApplyGate is held, so cleanup cannot race another
    // cache preparation or a renderer starting to use an unprotected source.
    private async Task MaintainVideoCacheAsync(bool force = false)
    {
        if (!_cacheMaintenanceReady || _runtimeClosing ||
            (!force && DateTimeOffset.UtcNow - _lastCacheMaintenanceUtc < TimeSpan.FromMinutes(10)))
        {
            return;
        }
        _lastCacheMaintenanceUtc = DateTimeOffset.UtcNow;
        try
        {
            var result = await _libraryStore.CleanupVideoCacheAsync(
                _activePlaybackSources.Values.SelectMany(paths => paths).ToArray(),
                _libraryLifetime.Token);
            if (result.RemovedFiles > 0)
            {
                await _logger.InfoAsync(
                    $"미사용 영상 캐시 {result.RemovedFiles}개 정리 " +
                    $"({result.RemovedBytes / (1024 * 1024)} MB). 원본과 재생 중인 캐시는 보존했습니다.");
            }
        }
        catch (OperationCanceledException) when (_runtimeClosing)
        {
            // App exit takes priority over optional cache maintenance.
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("영상 캐시 정리를 건너뜁니다. 재생은 계속합니다.", exception);
        }
    }

    private async Task MigrateLibraryToManagedStorageAsync(
        CancellationToken cancellationToken)
    {
        var retainedIds = EnumeratePersistedWallpaperReferences()
            .Where(item =>
                item.IsManaged &&
                _libraryStore.OwnsItemPath(item))
            .Select(item => item.Id);
        try
        {
            var removedOrphans = await _libraryStore.CleanupOrphansAsync(
                retainedIds,
                cancellationToken);
            if (removedOrphans > 0)
            {
                await _logger.InfoAsync(
                    $"사용되지 않는 관리형 라이브러리 폴더 {removedOrphans}개를 정리했습니다.");
            }
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                "관리형 라이브러리의 고아 폴더를 정리하지 못했습니다.",
                exception);
        }

        var migratedCount = 0;
        var settingsChanged = false;
        foreach (var legacyItem in _settings.Library
                     .Where(item => item is not null && !item.IsBuiltIn)
                     .ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_libraryStore.OwnsItemPath(legacyItem))
            {
                if (!legacyItem.IsManaged)
                {
                    legacyItem.IsManaged = true;
                    settingsChanged = true;
                }
                continue;
            }

            if (!File.Exists(legacyItem.SourcePath) &&
                !Directory.Exists(legacyItem.SourcePath))
            {
                continue;
            }

            try
            {
                HomeStatusText.Text =
                    $"기존 월페이퍼 '{legacyItem.Name}'을(를) 라이브러리로 이전하는 중...";
                var previousId = legacyItem.Id;
                var managedItem = await _libraryStore.ImportAsync(
                    legacyItem.SourcePath,
                    itemName: legacyItem.Name,
                    cancellationToken: cancellationToken);
                ReplaceWallpaperReferences(
                    previousId,
                    legacyItem,
                    managedItem);
                migratedCount++;
                settingsChanged = true;
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync(
                    $"'{legacyItem.Name}'을(를) 관리형 라이브러리로 이전하지 못했습니다.",
                    exception);
            }
        }

        if (settingsChanged)
        {
            await _settingsStore.SaveAsync(_settings, cancellationToken);
        }
        if (migratedCount > 0)
        {
            HomeStatusText.Text =
                $"기존 월페이퍼 {migratedCount}개를 Vellume 라이브러리로 이전했습니다.";
            await _logger.InfoAsync(
                $"기존 외부 월페이퍼 {migratedCount}개를 관리형 라이브러리로 이전했습니다.");
        }
    }

    private IEnumerable<WallpaperItem> EnumeratePersistedWallpaperReferences()
    {
        foreach (var item in _settings.Library.OfType<WallpaperItem>())
        {
            yield return item;
        }

        if (_settings.ActiveWallpaper is not null)
        {
            yield return _settings.ActiveWallpaper;
        }

        foreach (var item in _settings.DisplayWallpapers
                     .Select(profile => profile.Wallpaper)
                     .OfType<WallpaperItem>())
        {
            yield return item;
        }
    }

    private void ReplaceWallpaperReferences(
        Guid previousId,
        WallpaperItem sourceItem,
        WallpaperItem managedItem)
    {
        foreach (var item in EnumeratePersistedWallpaperReferences()
                     .Where(item =>
                         ReferenceEquals(item, sourceItem) ||
                         item.Id == previousId))
        {
            item.Id = managedItem.Id;
            item.Name = managedItem.Name;
            item.SourcePath = managedItem.SourcePath;
            item.OriginalSourcePath = managedItem.OriginalSourcePath;
            item.Kind = managedItem.Kind;
            item.IsBuiltIn = false;
            item.IsManaged = true;
        }
    }

    private static bool IsSameImportSource(
        WallpaperItem item,
        string sourcePath)
    {
        if (PathsEqual(item.SourcePath, sourcePath))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.OriginalSourcePath) &&
               PathsEqual(item.OriginalSourcePath, sourcePath);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(left))
                .Equals(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(right)),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ReportErrorAsync(string message, Exception exception)
    {
        await _logger.ErrorAsync(message, exception);
        System.Windows.MessageBox.Show(
            $"{message}\n\n{exception.Message}",
            "Vellume",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void WallpaperController_StateChanged(
        object? sender,
        WallpaperStateChangedEventArgs eventArgs)
    {
        Dispatcher.Invoke(() =>
        {
            SidebarStatusText.Text =
                LocalizePlaybackState(eventArgs.State);
            HomeStatusText.Text = eventArgs.Message;
            if (eventArgs.State == WallpaperPlaybackState.Stopped)
            {
                NowPlayingText.Text = "활성 월페이퍼 없음";
            }
        });
    }

    private void ShowPage(WpfGrid page)
    {
        WallpaperPage.Visibility = Visibility.Collapsed;
        WidgetsPage.Visibility = Visibility.Collapsed;
        DockPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        UpdateClockPreviewTimer();
    }

    private void WallpaperNavigation_Click(object sender, RoutedEventArgs eventArgs)
        => ShowPage(WallpaperPage);

    private void WidgetsNavigation_Click(object sender, RoutedEventArgs eventArgs)
        => ShowPage(WidgetsPage);

    private void DockNavigation_Click(object sender, RoutedEventArgs eventArgs)
        => ShowPage(DockPage);

    private void SettingsNavigation_Click(object sender, RoutedEventArgs eventArgs)
        => ShowPage(SettingsPage);

    private async void ApplyCardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is WpfButton { Tag: WallpaperItem wallpaper })
        {
            LibraryList.SelectedItem = wallpaper;
            await ApplyWallpaperAsync(wallpaper);
        }
    }

    private async void ApplySelectedButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (LibraryList.SelectedItem is WallpaperItem wallpaper &&
            DisplayList.SelectedItem is DisplayInfo display)
        {
            await ApplyWallpaperAsync(wallpaper, targetDisplay: display);
        }
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // Keep content selection independent of the display's current wallpaper.
        // Users can choose either the content or the destination first.
        if (_initialized && !IsWallpaperApplying)
        {
            UpdateSelectedDisplayStatus();
        }
    }

    private void LibraryFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not WpfButton { Tag: string filter }) return;
        SetLibraryFilter(filter);
    }

    private void SetLibraryFilter(string filter)
    {
        _libraryFilter = filter;
        foreach (var button in LibraryFilterBar.Children.OfType<WpfButton>())
        {
            button.Background = (System.Windows.Media.Brush)FindResource(
                Equals(button.Tag, filter) ? "AccentBrush" : "SurfaceBrush");
        }
        RefreshLibraryFilter();
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_initialized) RefreshLibraryFilter();
    }

    private void Library_CollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => UpdateLibrarySummary();

    private void RefreshLibraryFilter()
    {
        var search = LibrarySearchBox.Text.Trim();
        System.Windows.Data.CollectionViewSource.GetDefaultView(Library).Filter = item =>
            item is WallpaperItem wallpaper &&
            (_libraryFilter == "All" || wallpaper.Kind.ToString() == _libraryFilter) &&
            (search.Length == 0 || wallpaper.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        UpdateLibrarySummary();
    }

    private void UpdateLibrarySummary()
    {
        LibraryFilterLabel.Text = $"{LibraryList.Items.Count}개";
        LibraryEmptyText.Text = Library.Count == 0
            ? "등록된 콘텐츠 없음"
            : "검색 결과 없음";
    }

    private void SelectImportedWallpaper(WallpaperItem wallpaper)
    {
        // A different kind filter or search must not hide the newly imported item.
        LibrarySearchBox.Clear();
        SetLibraryFilter("All");
        LibraryList.SelectedItem = wallpaper;
        LibraryList.ScrollIntoView(wallpaper);
    }

    private void WallpaperThumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is System.Windows.Controls.Image image)
        {
            // Recycling can rebind a visible image without raising Loaded again.
            image.Source = null;
            image.ToolTip = null;
            if (image.IsLoaded) WallpaperThumbnail_Loaded(image, new RoutedEventArgs());
        }
    }

    private async void WallpaperThumbnail_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not System.Windows.Controls.Image { DataContext: WallpaperItem item } image) return;
        try
        {
            var thumbnail = await _thumbnails.LoadAsync(item.Copy(), _libraryLifetime.Token);
            if (!_runtimeClosing && ReferenceEquals(image.DataContext, item))
            {
                image.Source = thumbnail;
                image.ToolTip = thumbnail is null ? "웹 월페이퍼는 정지 미리보기를 제공하지 않습니다." : item.Name;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_runtimeClosing)
            {
                image.ToolTip = $"미리보기를 표시하지 못했습니다: {exception.Message}";
                await _logger.ErrorAsync($"월페이퍼 미리보기 실패: {item.Name}", exception);
            }
        }
    }

    private async void VideoRangeButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not WpfButton { Tag: WallpaperItem { Kind: WallpaperKind.Video } item } button) return;
        var entered = false;
        button.IsEnabled = false;
        try
        {
            await _libraryMutationGate.WaitAsync(_libraryLifetime.Token);
            entered = true;
            var duration = await _videoMetadata.GetDurationAsync(item.SourcePath, _libraryLifetime.Token);
            if (_runtimeClosing) return;
            var dialog = new VideoRangeWindow(item, duration) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var oldStart = item.VideoStartSeconds;
            var oldEnd = item.VideoEndSeconds;
            item.VideoStartSeconds = dialog.Start;
            item.VideoEndSeconds = dialog.End;
            try { await SaveSettingsAsync(); }
            catch
            {
                item.VideoStartSeconds = oldStart;
                item.VideoEndSeconds = oldEnd;
                throw;
            }
            LibraryList.Items.Refresh();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        catch (Exception exception) { await ReportErrorAsync("영상 재생 구간을 설정하지 못했습니다.", exception); }
        finally
        {
            if (entered) _libraryMutationGate.Release();
            button.IsEnabled = true;
        }
    }

    private async void LibraryList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        ListBoxItem? clickedItem = null;
        for (var current = eventArgs.OriginalSource as DependencyObject;
             current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is WpfButton)
            {
                return;
            }
            if (current is ListBoxItem item)
            {
                clickedItem = item;
                break;
            }
        }

        if (clickedItem?.DataContext is WallpaperItem wallpaper)
        {
            await ApplyWallpaperAsync(wallpaper);
        }
    }

    private async void RemoveCardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not WpfButton { Tag: WallpaperItem wallpaper })
        {
            return;
        }

        if (wallpaper.IsBuiltIn)
        {
            System.Windows.MessageBox.Show(
                "포함된 샘플은 라이브러리에서 삭제할 수 없습니다.",
                "Vellume",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var gateEntered = false;
        var applyGateEntered = false;
        try
        {
            await _libraryMutationGate.WaitAsync(_libraryLifetime.Token);
            gateEntered = true;
            await _wallpaperApplyGate.WaitAsync(_libraryLifetime.Token);
            applyGateEntered = true;

            var assignedProfiles = _settings.DisplayWallpapers
                .Where(profile => profile.Wallpaper?.Id == wallpaper.Id)
                .ToArray();
            foreach (var profile in assignedProfiles)
            {
                await _wallpaperController.StopDisplayAsync(profile.DisplayId);
                _settings.GetOrCreateDisplayProfile(profile.DisplayId).Wallpaper = null;
                if (_displayStateCache.TryGetValue(profile.DisplayId, out var display))
                {
                    display.AssignedWallpaperName = "미적용";
                    display.HasAssignedWallpaper = false;
                    display.IsWallpaperPaused = false;
                }
            }
            if (_settings.ActiveWallpaper?.Id == wallpaper.Id)
            {
                _settings.ActiveWallpaper = null;
            }

            Library.Remove(wallpaper);
            DisplayList.Items.Refresh();
            UpdateSelectedDisplayStatus();
            await SaveSettingsAsync();
            try
            {
                await _libraryStore.DeleteAsync(
                    wallpaper,
                    _libraryLifetime.Token);
                await _thumbnails.RemoveAsync(wallpaper.Id);
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync(
                    "관리형 월페이퍼 파일을 정리하지 못했습니다. 다음 시작 때 다시 정리합니다.",
                    exception);
            }
        }
        catch (OperationCanceledException) when (_runtimeClosing)
        {
        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "라이브러리에서 월페이퍼를 제거하지 못했습니다.",
                exception);
        }
        finally
        {
            if (applyGateEntered) _wallpaperApplyGate.Release();
            if (gateEntered)
            {
                _libraryMutationGate.Release();
            }
        }
    }

    private async void AddFileButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "월페이퍼 추가",
            Filter =
                "지원 월페이퍼|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.mp4;*.webm;*.html;*.htm|" +
                "이미지|*.jpg;*.jpeg;*.png;*.bmp;*.gif|" +
                "영상|*.mp4;*.webm|" +
                "로컬 HTML|*.html;*.htm",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddWallpaperPathAsync(dialog.FileName);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
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
        var gateEntered = false;
        try
        {
            await _libraryMutationGate.WaitAsync(_libraryLifetime.Token);
            gateEntered = true;
            var fullPath = Path.GetFullPath(path);
            var existing = Library.FirstOrDefault(item =>
                IsSameImportSource(item, fullPath));
            if (existing is not null)
            {
                SelectImportedWallpaper(existing);
                return;
            }

            HomeStatusText.Text =
                "월페이퍼를 Vellume 라이브러리에 복사하는 중...";
            var wallpaper = await _libraryStore.ImportAsync(
                fullPath,
                cancellationToken: _libraryLifetime.Token);
            Library.Add(wallpaper);
            SelectImportedWallpaper(wallpaper);
            try
            {
                await SaveSettingsAsync();
            }
            catch
            {
                Library.Remove(wallpaper);
                await _libraryStore.DeleteAsync(wallpaper);
                throw;
            }

            HomeStatusText.Text =
                $"'{wallpaper.Name}'을(를) 라이브러리에 저장했습니다.";
        }
        catch (OperationCanceledException) when (_runtimeClosing)
        {
        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "해당 월페이퍼를 라이브러리에 복사하지 못했습니다.",
                exception);
        }
        finally
        {
            if (gateEntered)
            {
                _libraryMutationGate.Release();
            }
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var display = DisplayList.SelectedItem as DisplayInfo ??
                Displays.FirstOrDefault(item => item.IsPrimary);
            if (display is null)
            {
                return;
            }

            await StopDisplayCoreAsync(display);
        }
        catch (Exception exception)
        {
            await ReportErrorAsync("월페이퍼를 안전하게 중지하지 못했습니다.", exception);
        }
    }

    private async void StopAllButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var applyGateEntered = false;
        try
        {
            _wallpaperApplyCancellation?.Cancel();
            await _wallpaperApplyGate.WaitAsync(_libraryLifetime.Token);
            applyGateEntered = true;
            IsWallpaperApplying = true;
            await _wallpaperController.StopAsync();
            foreach (var profile in _settings.DisplayWallpapers)
            {
                profile.Wallpaper = null;
            }
            foreach (var display in Displays)
            {
                display.AssignedWallpaperName = "미적용";
                display.HasAssignedWallpaper = false;
                display.IsWallpaperPaused = false;
            }
            _settings.ActiveWallpaper = null;
            NowPlayingText.Text = "적용된 월페이퍼 없음";
            DisplayList.Items.Refresh();
            HomeStatusText.Text = "모든 디스플레이의 월페이퍼를 중지했습니다.";
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "모든 월페이퍼를 안전하게 중지하지 못했습니다.",
                exception);
        }
        finally
        {
            if (applyGateEntered)
            {
                IsWallpaperApplying = false;
                _wallpaperApplyGate.Release();
            }
        }
    }

    private async void DisplayPauseButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not WpfButton { Tag: DisplayInfo display } ||
            !display.HasAssignedWallpaper)
        {
            return;
        }

        try
        {
            if (display.IsWallpaperPaused)
            {
                await _wallpaperController.ResumeDisplayAsync(display.Id);
                display = FindCurrentDisplay(display);
                display.IsWallpaperPaused = false;
            }
            else
            {
                await _wallpaperController.PauseDisplayAsync(display.Id);
                display = FindCurrentDisplay(display);
                display.IsWallpaperPaused = true;
            }
            DisplayList.Items.Refresh();
        }
        catch (Exception exception)
        {
            await ReportErrorAsync("재생 상태를 변경하지 못했습니다.", exception);
        }
    }

    private async void DisplayStopButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not WpfButton { Tag: DisplayInfo display })
        {
            return;
        }

        try
        {
            DisplayList.SelectedItem = display;
            await StopDisplayCoreAsync(display);
        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "월페이퍼를 안전하게 중지하지 못했습니다.",
                exception);
        }
    }

    private async Task StopDisplayCoreAsync(DisplayInfo display)
    {
        if (display.Id.Equals(_applyingDisplayId, StringComparison.OrdinalIgnoreCase))
        {
            _wallpaperApplyCancellation?.Cancel();
        }
        await _wallpaperApplyGate.WaitAsync(_libraryLifetime.Token);
        try
        {
            IsWallpaperApplying = true;
            await _wallpaperController.StopDisplayAsync(display.Id);
            display = FindCurrentDisplay(display);
            var profile = _settings.GetOrCreateDisplayProfile(display.Id);
            profile.Wallpaper = null;
            display.AssignedWallpaperName = "미적용";
            display.HasAssignedWallpaper = false;
            display.IsWallpaperPaused = false;
            if (DisplayList.SelectedItem is DisplayInfo selected &&
                selected.Id.Equals(display.Id, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ActiveWallpaper = null;
            }
            UpdateSelectedDisplayStatus();
            DisplayList.Items.Refresh();
            HomeStatusText.Text = $"{display.Name}의 월페이퍼를 중지했습니다.";
            await SaveSettingsAsync();
        }
        finally
        {
            IsWallpaperApplying = false;
            _wallpaperApplyGate.Release();
        }
    }

    private async void DisplayList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (!_initialized || _refreshingDisplays)
        {
            return;
        }
        if (DisplayList.SelectedItem is not DisplayInfo display)
        {
            UpdateSelectedDisplayStatus();
            return;
        }

        try
        {
            _settings.SelectedMonitorId = display.Id;
            var profile = _settings.GetOrCreateDisplayProfile(display.Id);
            _settings.ActiveWallpaper = profile.Wallpaper?.Copy();
            _settings.Playback = profile.Playback.Copy();
            ApplyPlaybackProfileToControls(profile.Playback);
            UpdateSelectedDisplayStatus();
            DisplayList.ScrollIntoView(display);
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            await ReportErrorAsync("디스플레이 선택을 저장하지 못했습니다.", exception);
        }
    }

    private void UpdateSelectedDisplayStatus()
    {
        if (DisplayList.SelectedItem is not DisplayInfo display)
        {
            NowPlayingText.Text = "연결된 디스플레이 없음";
            if (!IsWallpaperApplying) HomeStatusText.Text = string.Empty;
            return;
        }

        var wallpaper = _settings.FindDisplayProfile(display.Id)?.Wallpaper;
        NowPlayingText.Text = wallpaper?.Name ?? "선택 화면에 적용된 월페이퍼 없음";
        if (!IsWallpaperApplying)
        {
            HomeStatusText.Text = string.Empty;
        }
    }

    private void DisplayList_PreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        var scroll = FindVisualChild<ScrollViewer>(DisplayList);
        if (scroll is null || scroll.ScrollableWidth <= 0) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - args.Delta);
        args.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async void FitComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (!_initialized ||
            FitComboBox.SelectedItem is not
                SelectionOption<WallpaperFit> option)
        {
            return;
        }

        if (_updatingPlaybackControls ||
            DisplayList.SelectedItem is not DisplayInfo display)
        {
            return;
        }

        var profile = _settings.GetOrCreateDisplayProfile(display.Id);
        profile.Playback.Fit = option.Value;
        _settings.Playback = profile.Playback.Copy();
        await _wallpaperController.UpdateDisplayPlaybackAsync(
            display.Id,
            profile.Playback.Copy());
        await SaveSettingsAsync();
    }

    private async void MuteCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (!_initialized ||
            _updatingPlaybackControls ||
            DisplayList.SelectedItem is not DisplayInfo display)
        {
            return;
        }

        var profile = _settings.GetOrCreateDisplayProfile(display.Id);
        profile.Playback.IsMuted = MuteCheckBox.IsChecked == true;
        _settings.Playback = profile.Playback.Copy();
        await _wallpaperController.UpdateDisplayPlaybackAsync(
            display.Id,
            profile.Playback.Copy());
        await SaveSettingsAsync();
    }

    private async void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        VolumeValueText.Text = $"{Math.Round(VolumeSlider.Value):0}%";
        if (!_initialized ||
            _updatingPlaybackControls ||
            DisplayList.SelectedItem is not DisplayInfo display)
        {
            return;
        }

        var profile = _settings.GetOrCreateDisplayProfile(display.Id);
        profile.Playback.Volume = VolumeSlider.Value / 100;
        _settings.Playback = profile.Playback.Copy();
        await _wallpaperController.UpdateDisplayPlaybackAsync(
            display.Id,
            profile.Playback.Copy());
        await SaveSettingsAsync();
    }

    private async void OptimizeVideoCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized ||
            _updatingPlaybackControls ||
            DisplayList.SelectedItem is not DisplayInfo display)
        {
            return;
        }

        var profile = _settings.GetOrCreateDisplayProfile(display.Id);
        profile.Playback.UseOptimizedVideo =
            OptimizeVideoCheckBox.IsChecked == true;
        _settings.Playback = profile.Playback.Copy();
        await SaveSettingsAsync();
        if (profile.Wallpaper?.Kind == WallpaperKind.Video)
        {
            await ApplyWallpaperAsync(
                profile.Wallpaper,
                targetDisplay: display);
        }
    }

    private async void RestoreCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.RestoreOnStartup = RestoreCheckBox.IsChecked == true;
        await SaveSettingsAsync();
    }

    private async void LaunchAtLoginCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        var enabled = LaunchAtLoginCheckBox.IsChecked == true;
        try
        {
            _startupRegistrationService.SetEnabled(enabled);
            _settings.LaunchAtWindowsLogin = enabled;
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            LaunchAtLoginCheckBox.IsChecked =
                _settings.LaunchAtWindowsLogin;
            await ReportErrorAsync(
                "Windows 자동 실행 설정을 변경하지 못했습니다.",
                exception);
        }
    }

    private void NormalizeInternalPlaybackPolicy()
    {
        // These controls were removed from the personal UI. Keep one
        // deterministic runtime path and discard values saved by older builds.
        foreach (var playback in _settings.DisplayWallpapers
                     .Select(profile => profile.Playback)
                     .Append(_settings.Playback))
        {
            playback.Brightness = 1;
            playback.Contrast = 1;
            playback.Saturation = 1;
            // Keep playback continuous while foreground applications use the
            // GPU. The optimized video proxy and Windows GPU scheduling lower
            // background cost without a visible pause/resume transition.
            playback.PauseWhenForegroundAppIsMaximized = false;
            playback.ApplicationRules = [];
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _quickSlotRuntimeService.Attach(handle);
        var registered = _hotkeyManager.Register(handle);
        _ = _logger.InfoAsync(
            $"Registered {registered}/9 quick-slot shortcuts.");
        if (registered < 9)
        {
            HotkeyStatusText.Text =
                $"{registered}/9개 단축키를 등록했습니다. 나머지 키는 다른 앱이 사용 중입니다.";
        }
    }

    private void Window_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_runtimeClosing)
        {
            return;
        }
        UpdateClockPreviewTimer();

        if (IsVisible)
        {
            if (_windowSource is not null)
            {
                _quickSlotRuntimeService.Attach(_windowSource.Handle);
            }
            if (_initialized)
            {
                _quickSlotRuntimeTimer.Start();
                _ = RefreshQuickSlotRuntimeStateAsync();
            }
        }
        else
        {
            _quickSlotRuntimeTimer.Stop();
            _quickSlotRuntimeService.Detach();
        }
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        handled = _quickSlotRuntimeService.TryHandleShellMessage(
                      message,
                      wParam,
                      lParam) ||
                  _hotkeyManager.TryHandleMessage(message, wParam);
        return nint.Zero;
    }

    private async void QuickSlotRuntimeTimerOnTick(
        object? sender,
        EventArgs eventArgs)
        => await RefreshQuickSlotRuntimeStateAsync();

    private void QuickSlotRuntimeService_StateChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (!IsVisible)
        {
            return;
        }
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
                _ = RefreshQuickSlotRuntimeStateAsync()));
    }

    private void SystemEventMonitor_RuntimeStateChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (!IsVisible)
        {
            return;
        }
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
                _ = RefreshQuickSlotRuntimeStateAsync()));
    }

    private async Task RefreshQuickSlotRuntimeStateAsync()
    {
        if (!_initialized ||
            _runtimeClosing ||
            _runtimeRefreshInProgress)
        {
            return;
        }

        _runtimeRefreshInProgress = true;
        try
        {
            var quickSlotSnapshot = QuickSlots
                .Select(item => item.Copy())
                .ToArray();
            var systemSnapshot = SystemShortcuts
                .Select(item => item.Copy())
                .ToArray();
            var captureStarted = Stopwatch.GetTimestamp();
            var runtime = await Task.Run(() =>
                _quickSlotRuntimeService.Capture(
                    quickSlotSnapshot,
                    systemSnapshot));
            LocalPerformanceDiagnostics.Record(
                "settings.runtime-scan",
                Stopwatch.GetElapsedTime(captureStarted));
            if (_runtimeClosing)
            {
                return;
            }

            var changed = false;
            foreach (var item in QuickSlots)
            {
                if (!runtime.QuickSlots.TryGetValue(
                        item.Slot,
                        out var state))
                {
                    state = default;
                }

                if (item.IsRunning != state.IsRunning ||
                    item.HasAttention != state.HasAttention)
                {
                    item.IsRunning = state.IsRunning;
                    item.HasAttention = state.HasAttention;
                    changed = true;
                }
            }

            foreach (var item in SystemShortcuts)
            {
                if (!runtime.SystemShortcuts.TryGetValue(
                        item.Kind,
                        out var state))
                {
                    state = default;
                }

                if (item.IsRunning != state.IsRunning ||
                    item.HasAttention != state.HasAttention)
                {
                    item.IsRunning = state.IsRunning;
                    item.HasAttention = state.HasAttention;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            QuickSlotList.Items.Refresh();
            SystemShortcutList.Items.Refresh();
            _settings.Widgets.QuickSlots = QuickSlots
                .OrderBy(item => item.Slot)
                .Select(item => item.Copy())
                .ToList();
            _settings.Widgets.SystemShortcuts = SystemShortcuts
                .Select(item => item.Copy())
                .ToList();
            await _wallpaperController.UpdateQuickSlotRuntimeAsync(
                _settings.Widgets.Copy());
        }
        catch (ObjectDisposedException) when (_runtimeClosing)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                "Quick-slot runtime state refresh failed.",
                exception);
        }
        finally
        {
            _runtimeRefreshInProgress = false;
        }
    }

    private void QuickSlotHotkeyManager_SlotInvoked(
        object? sender,
        int slot)
        => _ = LaunchQuickSlotAsync(slot);

    private void WallpaperController_QuickSlotInvoked(
        object? sender,
        int slot)
        => Dispatcher.Invoke(() => _ = LaunchQuickSlotAsync(slot));

    private void WallpaperController_QuickSlotSecondaryInvoked(
        object? sender,
        int slot)
        => Dispatcher.Invoke(() =>
            _ = HandleQuickSlotSecondaryActionAsync(slot));

    private void WallpaperController_QuickSlotLayoutChanged(
        object? sender,
        QuickSlotLayoutChangedEventArgs eventArgs)
        => Dispatcher.Invoke(() =>
        {
            var group = QuickSlotGroups.FirstOrDefault(candidate =>
                candidate.Id.Equals(
                    eventArgs.GroupId,
                    StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                return;
            }

            group.X = eventArgs.X;
            group.Y = eventArgs.Y;
            var persistedGroup =
                _settings.Widgets.QuickSlotGroups.FirstOrDefault(
                    candidate => candidate.Id.Equals(
                        eventArgs.GroupId,
                        StringComparison.OrdinalIgnoreCase));
            if (persistedGroup is not null)
            {
                persistedGroup.X = eventArgs.X;
                persistedGroup.Y = eventArgs.Y;
            }
            LayoutStatusText.Text =
                $"{group.Name} 위치 저장됨: {group.X}, {group.Y}";
            _ = SaveSettingsAsync();
        });

    private async Task LaunchQuickSlotAsync(int slot)
    {
        var item = QuickSlots.FirstOrDefault(candidate =>
            candidate.Slot == slot);
        var systemItem = SystemShortcuts.FirstOrDefault(candidate =>
            candidate.IsVisible &&
            candidate.Slot == slot);
        if (!_settings.Widgets.IsEnabled ||
            !_settings.Widgets.ShowQuickSlots ||
            item is null && systemItem is null)
        {
            return;
        }

        try
        {
            if (systemItem is not null)
            {
                var activated =
                    _quickSlotRuntimeService
                        .TryActivateSystemShortcut(
                            systemItem.Kind);
                if (!activated)
                {
                    await _systemShortcutService.LaunchAsync(
                        systemItem.Kind);
                }
                await _logger.InfoAsync(
                    $"System quick slot {slot} " +
                    $"{(activated ? "activated" : "launched")} " +
                    $"'{systemItem.Kind}'.");
            }
            else
            {
                System.Diagnostics.Debug.Assert(item is not null);
                var result = await _quickSlotRuntimeService
                    .ActivateOrLaunchAsync(
                        item,
                        forceNewInstance: false);
                await _logger.InfoAsync(
                    $"Quick slot {slot} " +
                    $"{(result == QuickSlotActivationResult.Activated ? "activated" : "launched")} " +
                    $"'{item.TargetPath}'.");
            }

        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync(
                $"Quick slot {slot} could not be launched.",
                exception);
            Dispatcher.Invoke(() =>
            {
                HotkeyStatusText.Text = exception.Message;
            });
        }
    }

    private async Task HandleQuickSlotSecondaryActionAsync(int slot)
    {
        var item = QuickSlots.FirstOrDefault(candidate =>
            candidate.Slot == slot);
        var systemItem = SystemShortcuts.FirstOrDefault(candidate =>
            candidate.IsVisible &&
            candidate.Slot == slot);
        if (item is null && systemItem is null)
        {
            return;
        }

        try
        {
            if (systemItem?.Kind == SystemShortcutKind.RecycleBin)
            {
                await _systemShortcutService.EmptyRecycleBinAsync(
                    new WindowInteropHelper(this).Handle);
                await _logger.InfoAsync(
                    "Recycle Bin empty action completed or was cancelled.");
                await SaveAndRefreshWidgetsAsync();
                return;
            }

            if (systemItem is not null)
            {
                await _systemShortcutService.LaunchAsync(
                    systemItem.Kind);
                await _logger.InfoAsync(
                    $"System quick slot {slot} launched a new " +
                    $"'{systemItem.Kind}' window.");
            }
            else if (item is not null)
            {
                await _quickSlotRuntimeService
                    .ActivateOrLaunchAsync(
                        item,
                        forceNewInstance: true);
                await _logger.InfoAsync(
                    $"Quick slot {slot} requested a new instance of " +
                    $"'{item.TargetPath}'.");
            }

        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "퀵슬롯 보조 동작에 실패했습니다.",
                exception);
        }
    }

    private async void AddQuickSlotButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (QuickSlots.Count >= 99)
        {
            System.Windows.MessageBox.Show(
                "퀵슬롯 최대 개수 99개에 도달했습니다.",
                "Vellume",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "퀵슬롯에 넣을 앱 또는 파일 선택",
            Filter = "앱과 파일|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await AddQuickSlotItemAsync(
            Path.GetFileNameWithoutExtension(dialog.FileName),
            Path.GetFullPath(dialog.FileName));
    }

    private async Task AddQuickSlotItemAsync(
        string name,
        string targetPath)
    {
        var occupiedSlots = QuickSlots
            .Select(item => item.Slot)
            .Concat(SystemShortcuts
                .Where(item => item.IsVisible)
                .Select(item => item.Slot))
            .Where(slot => slot is >= 1 and <= 99)
            .ToHashSet();
        if (occupiedSlots.Count >= 99)
        {
            System.Windows.MessageBox.Show(
                "퀵슬롯 최대 개수 99개에 도달했습니다.",
                "Vellume",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var group =
            QuickSlotGroupComboBox.SelectedItem as QuickSlotGroup ??
            QuickSlotGroups.First();
        var slot = Enumerable.Range(1, 99)
            .First(candidate => !occupiedSlots.Contains(candidate));
        QuickSlots.Add(new QuickSlotItem
        {
            Slot = slot,
            Name = name,
            TargetPath = targetPath,
            GroupId = group.Id,
            GroupName = group.Name,
            ShowInDock = true,
            DockOrder = GetNextDockOrder()
        });
        SortQuickSlots();
        await SaveAndRefreshWidgetsAsync();
    }

    private async void DiscoverKnownAppsButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            var result = DiscoverAndAddKnownApps();
            UpdateKnownAppsStatus(result);
            await SaveAndRefreshWidgetsAsync();
        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "설치된 앱을 찾지 못했습니다.",
                exception);
        }
    }

    private (int Found, IReadOnlyList<string> Added)
        DiscoverAndAddKnownApps()
    {
        EnsurePresetQuickSlotGroups();
        var matches = KnownAppDiscoveryService.Discover();
        var added = new List<string>();
        var occupiedSlots = QuickSlots
            .Select(item => item.Slot)
            .Concat(SystemShortcuts
                .Where(item => item.IsVisible)
                .Select(item => item.Slot))
            .Where(slot => slot is >= 1 and <= 99)
            .ToHashSet();

        foreach (var match in matches)
        {
            var existing = QuickSlots.FirstOrDefault(item =>
                IsKnownAppMatch(item, match));
            if (existing is not null)
            {
                var shouldRefreshVersionedPath =
                    File.Exists(match.TargetPath) &&
                    ExecutablePathIdentity.AreEquivalent(
                        existing.TargetPath,
                        match.TargetPath) &&
                    !existing.TargetPath.Equals(
                        match.TargetPath,
                        StringComparison.OrdinalIgnoreCase);
                if ((!File.Exists(existing.TargetPath) &&
                     File.Exists(match.TargetPath)) ||
                    shouldRefreshVersionedPath)
                {
                    existing.TargetPath = match.TargetPath;
                }

                continue;
            }

            var slot = Enumerable.Range(1, 99)
                .FirstOrDefault(candidate =>
                    !occupiedSlots.Contains(candidate));
            if (slot == 0)
            {
                break;
            }

            var group = FindPresetGroup(match.GroupName);
            if (group is null)
            {
                continue;
            }

            QuickSlots.Add(new QuickSlotItem
            {
                Slot = slot,
                Name = match.Name,
                TargetPath = match.TargetPath,
                GroupId = group.Id,
                GroupName = group.Name,
                ShowInDock = true,
                DockOrder = GetNextDockOrder()
            });
            occupiedSlots.Add(slot);
            added.Add(match.Name);
        }

        if (added.Count > 0)
        {
            SortQuickSlots();
        }

        return (matches.Count, added);
    }

    private int GetNextDockOrder()
        => SystemShortcuts
               .Where(item => item.ShowInDock)
               .Select(item => item.DockOrder)
               .Concat(QuickSlots
                   .Where(item => item.ShowInDock)
                   .Select(item => item.DockOrder))
               .DefaultIfEmpty(0)
               .Max() + 10;

    private void EnsurePresetQuickSlotGroups()
    {
        foreach (var preset in QuickSlotGroup.CreatePresetGroups())
        {
            if (FindPresetGroup(preset.Name) is null)
            {
                QuickSlotGroups.Add(preset);
            }
        }
    }

    private QuickSlotGroup? FindPresetGroup(string name)
    {
        var preset = QuickSlotGroup.CreatePresetGroups()
            .FirstOrDefault(candidate =>
                candidate.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
        return QuickSlotGroups.FirstOrDefault(group =>
            group.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase) ||
            preset is not null &&
            group.Id.Equals(
                preset.Id,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownAppMatch(
        QuickSlotItem item,
        KnownAppMatch match)
    {
        if (item.Name.Equals(
                match.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            if (Path.GetFullPath(item.TargetPath).Equals(
                    match.TargetPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            // Custom shell targets are compared by name and executable.
        }

        return Path.GetFileName(item.TargetPath).Equals(
            Path.GetFileName(match.TargetPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateKnownAppsStatus(
        (int Found, IReadOnlyList<string> Added) result)
    {
        KnownAppsStatusText.Text = result.Added.Count switch
        {
            > 0 => $"설치된 앱 {result.Added.Count}개 추가: " +
                   string.Join(", ", result.Added),
            _ when result.Found > 0 =>
                $"확인된 앱 {result.Found}개가 이미 정리되어 있습니다.",
            _ => "기본 설치 경로에서 대상 앱을 찾지 못했습니다."
        };
    }

    private async void SystemShortcutVisibility_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized ||
            sender is not WpfCheckBox
            {
                DataContext: SystemShortcutSetting item
            } checkBox)
        {
            return;
        }

        item.IsVisible = checkBox.IsChecked == true;
        if (item.IsVisible)
        {
            var occupiedSlots = QuickSlots
                .Select(candidate => candidate.Slot)
                .Concat(SystemShortcuts
                    .Where(candidate =>
                        candidate.IsVisible &&
                        !ReferenceEquals(candidate, item))
                    .Select(candidate => candidate.Slot))
                .Where(slot => slot is >= 1 and <= 99)
                .ToHashSet();
            if (item.Slot is not (>= 1 and <= 99) ||
                occupiedSlots.Contains(item.Slot))
            {
                item.Slot = Enumerable.Range(1, 99)
                    .FirstOrDefault(slot =>
                        !occupiedSlots.Contains(slot));
            }

            if (item.Slot == 0)
            {
                item.IsVisible = false;
                checkBox.IsChecked = false;
                System.Windows.MessageBox.Show(
                    "사용 가능한 퀵슬롯 번호가 없습니다.",
                    "Vellume",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        SystemShortcutList.Items.Refresh();
        await SaveAndRefreshWidgetsAsync();
    }

    private async void ChooseSystemShortcutIconButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not WpfButton
            {
                Tag: SystemShortcutSetting item
            })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"{item.Name} 아이콘 선택",
            Filter =
                "아이콘 이미지|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.exe;*.dll|" +
                "모든 파일|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        item.CustomIconPath = Path.GetFullPath(dialog.FileName);
        SystemShortcutList.Items.Refresh();
        await SaveAndRefreshWidgetsAsync();
    }

    private async void ResetSystemShortcutIconButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not WpfButton
            {
                Tag: SystemShortcutSetting item
            })
        {
            return;
        }

        item.CustomIconPath = string.Empty;
        SystemShortcutList.Items.Refresh();
        await SaveAndRefreshWidgetsAsync();
    }

    private async void RemoveQuickSlotButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is WpfButton { Tag: QuickSlotItem item })
        {
            QuickSlots.Remove(item);
            await SaveAndRefreshWidgetsAsync();
        }
    }

    private async void WidgetCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await CommitWidgetControlChangeAsync(() =>
        {
            _settings.Widgets.IsEnabled =
                WidgetsEnabledCheckBox.IsChecked == true;
            _settings.Widgets.ShowClock =
                ClockWidgetCheckBox.IsChecked == true;
            _settings.Widgets.ShowSystemInfo =
                SystemWidgetCheckBox.IsChecked == true;
            _settings.Widgets.ShowQuickSlots = false;
            _settings.Widgets.ShowCodexCompanion =
                CodexCompanionWidgetCheckBox.IsChecked == true;
        });
    }

    private async void GridSettings_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        await SaveAndRefreshWidgetsAsync();
        LayoutStatusText.Text =
            SnapQuickSlotsCheckBox.IsChecked == true &&
            QuickSlotGridSizeComboBox.SelectedItem is int gridSize
                ? $"그리드 맞춤 사용: {gridSize}px"
                : "그리드 맞춤을 사용하지 않습니다.";
    }

    private async Task SaveAndRefreshWidgetsAsync()
    {
        if (_runtimeClosing) return;
        var entered = false;
        try
        {
            await _widgetSettingsGate.WaitAsync(_libraryLifetime.Token);
            entered = true;
            await SaveAndRefreshWidgetsCoreAsync();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        finally { if (entered) _widgetSettingsGate.Release(); }
    }

    private async Task SaveAndRefreshWidgetsCoreAsync()
    {
        _settings.Widgets.SnapQuickSlotsToGrid =
            SnapQuickSlotsCheckBox.IsChecked == true;
        if (QuickSlotGridSizeComboBox.SelectedItem is int gridSize)
        {
            _settings.Widgets.QuickSlotGridSize = gridSize;
        }
        if (_settings.Widgets.SnapQuickSlotsToGrid)
        {
            foreach (var group in QuickSlotGroups)
            {
                group.X = SnapCoordinate(
                    group.X,
                    _settings.Widgets.QuickSlotGridSize);
                group.Y = SnapCoordinate(
                    group.Y,
                    _settings.Widgets.QuickSlotGridSize);
            }
        }
        _settings.Widgets.QuickSlotGroups = QuickSlotGroups
            .Select(group => group.Copy())
            .ToList();
        _settings.Widgets.QuickSlots = QuickSlots
            .OrderBy(item => item.Slot)
            .Select(item => item.Copy())
            .ToList();
        _settings.Widgets.SystemShortcuts = SystemShortcuts
            .Select(item => item.Copy())
            .ToList();
        await _wallpaperController.UpdateWidgetsAsync(
            _settings.Widgets.Copy(),
            Displays.FirstOrDefault(display => display.IsPrimary) ??
            Displays.FirstOrDefault());
        await SaveSettingsAsync();
        DockSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void DockSettings_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        _settings.Dock.IsEnabled =
            DockEnabledCheckBox.IsChecked == true;
        _settings.Dock.ShowRunningApplications =
            DockRunningAppsCheckBox.IsChecked == true;
        _settings.Dock.ShowWindowsTaskbar =
            WindowsTaskbarVisibleCheckBox.IsChecked == true;
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
        DockSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void DockItemVisibility_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_initialized)
        {
            return;
        }

        await SaveAndRefreshWidgetsAsync();
    }

    private async void InstallCodexBridgeButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var hooksPath =
                await _wallpaperController
                    .InstallCodexCompanionBridgeAsync();
            CodexBridgeStatusText.Text =
                $"자동 감지는 이미 활성화되어 있습니다. 상세 상태 연결을 " +
                $"{hooksPath}에 설치했습니다. Codex를 다시 시작한 뒤 " +
                "/hooks에서 Vellume 훅을 한 번 신뢰하세요.";
        }
        catch (Exception exception)
        {
            await ReportErrorAsync(
                "Codex 컴패니언 연결을 설치하지 못했습니다.",
                exception);
        }
    }

    private static int SnapCoordinate(int value, int gridSize)
        => Math.Max(
            0,
            (int)Math.Round(
                value / (double)Math.Clamp(gridSize, 10, 200),
                MidpointRounding.AwayFromZero) *
            Math.Clamp(gridSize, 10, 200));

    private void SortQuickSlots()
    {
        var ordered = QuickSlots
            .OrderBy(item => item.Slot)
            .ToArray();
        QuickSlots.Clear();
        foreach (var item in ordered)
        {
            QuickSlots.Add(item);
        }
    }

    private async void AddQuickSlotGroupButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new QuickSlotGroupDialog
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var name = dialog.GroupName.Trim();
        if (QuickSlotGroups.Any(group =>
                group.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(
                "같은 이름의 퀵슬롯 그룹이 이미 있습니다.",
                "Vellume",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var index = QuickSlotGroups.Count;
        var group = new QuickSlotGroup
        {
            Name = name,
            X = 340 + index % 3 * 320,
            Y = 60 + index / 3 * 250
        };
        QuickSlotGroups.Add(group);
        QuickSlotGroupComboBox.SelectedItem = group;
        await SaveAndRefreshWidgetsAsync();
    }

    private async void MoveQuickSlotToGroupButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (QuickSlotList.SelectedItem is not QuickSlotItem item ||
            QuickSlotGroupComboBox.SelectedItem is not QuickSlotGroup group)
        {
            LayoutStatusText.Text =
                "퀵슬롯과 이동할 그룹을 먼저 선택하세요.";
            return;
        }

        item.GroupId = group.Id;
        item.GroupName = group.Name;
        QuickSlotList.Items.Refresh();
        LayoutStatusText.Text =
            $"{item.Name}을(를) {group.Name} 그룹으로 이동했습니다.";
        await SaveAndRefreshWidgetsAsync();
    }

    private void EnsureDisplayProfiles()
    {
        _settings.DisplayWallpapers ??= [];
        foreach (var display in Displays)
        {
            _settings.GetOrCreateDisplayProfile(display.Id);
        }
    }

    private async void SystemEventMonitor_DisplayConfigurationChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (!_initialized || _runtimeClosing)
        {
            return;
        }

        try
        {
            RefreshDisplays();
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("변경된 디스플레이 목록을 갱신하지 못했습니다.", exception);
        }
    }

    private void RefreshDisplays()
    {
        var latestDisplays = _monitorService.GetDisplays();
        var selectedId = (DisplayList.SelectedItem as DisplayInfo)?.Id ??
            _settings.SelectedMonitorId;
        _refreshingDisplays = true;
        try
        {
            Displays.Clear();
            foreach (var display in latestDisplays)
            {
                var wallpaper = _settings.FindDisplayProfile(display.Id)?.Wallpaper;
                display.AssignedWallpaperName = wallpaper?.Name ?? "미적용";
                display.HasAssignedWallpaper = wallpaper is not null;
                // Keep paused cards across disconnect/reconnect, while honoring
                // assignments cleared by Stop All or library deletion while absent.
                display.IsWallpaperPaused = display.HasAssignedWallpaper &&
                    _displayStateCache.TryGetValue(display.Id, out var previous) &&
                    previous.IsWallpaperPaused;
                _displayStateCache[display.Id] = display;
                Displays.Add(display);
            }

            EnsureDisplayProfiles();
            DisplayList.SelectedItem = Displays.FirstOrDefault(display =>
                display.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ??
                Displays.FirstOrDefault(display => display.IsPrimary) ??
                Displays.FirstOrDefault();
            if (_initialized && DisplayList.SelectedItem is DisplayInfo selected)
            {
                _settings.SelectedMonitorId = selected.Id;
                var profile = _settings.GetOrCreateDisplayProfile(selected.Id);
                _settings.ActiveWallpaper = profile.Wallpaper?.Copy();
                _settings.Playback = profile.Playback.Copy();
                ApplyPlaybackProfileToControls(profile.Playback);
                NowPlayingText.Text = profile.Wallpaper?.Name ??
                    "선택 화면에 적용된 월페이퍼 없음";
            }
            if (_initialized) UpdateSelectedDisplayStatus();
            if (_initialized) RefreshWidgetPlacementControls();
        }
        finally
        {
            _refreshingDisplays = false;
        }
    }

    private DisplayInfo FindCurrentDisplay(DisplayInfo display)
        => _displayStateCache.GetValueOrDefault(display.Id) ?? display;

    private void ApplyPlaybackProfileToControls(
        PlaybackOptions playback)
    {
        _updatingPlaybackControls = true;
        try
        {
            if (FitComboBox.ItemsSource is
                IEnumerable<SelectionOption<WallpaperFit>> options)
            {
                FitComboBox.SelectedItem = options.FirstOrDefault(
                    option => option.Value == playback.Fit);
            }
            MuteCheckBox.IsChecked = playback.IsMuted;
            VolumeSlider.Value = playback.Volume * 100;
            OptimizeVideoCheckBox.IsChecked = playback.UseOptimizedVideo;
        }
        finally
        {
            _updatingPlaybackControls = false;
        }
    }

    private async Task RestoreDisplayWallpapersAsync()
    {
        var originallySelected =
            DisplayList.SelectedItem as DisplayInfo ??
            Displays.FirstOrDefault(display => display.IsPrimary);
        foreach (var display in Displays
                     .OrderByDescending(candidate => candidate.IsPrimary)
                     .ToArray())
        {
            var profile = _settings.FindDisplayProfile(display.Id);
            if (profile?.Wallpaper is null)
            {
                continue;
            }

            if (profile.Wallpaper.IsBuiltIn)
            {
                profile.Wallpaper = null;
                display.AssignedWallpaperName = "미적용";
                display.HasAssignedWallpaper = false;
                continue;
            }
            var wallpaper = Library.FirstOrDefault(candidate =>
                                candidate.Id == profile.Wallpaper.Id) ??
                            profile.Wallpaper;

            await ApplyWallpaperAsync(
                wallpaper,
                showDialogOnError: false,
                targetDisplay: display);
        }

        if (originallySelected is not null)
        {
            var currentSelection = Displays.FirstOrDefault(display =>
                display.Id.Equals(originallySelected.Id, StringComparison.OrdinalIgnoreCase)) ??
                DisplayList.SelectedItem as DisplayInfo;
            if (currentSelection is null)
            {
                return;
            }
            DisplayList.SelectedItem = currentSelection;
            _settings.SelectedMonitorId = currentSelection.Id;
            var profile = _settings.GetOrCreateDisplayProfile(
                currentSelection.Id);
            _settings.Playback = profile.Playback.Copy();
            ApplyPlaybackProfileToControls(profile.Playback);
            NowPlayingText.Text =
                profile.Wallpaper?.Name ??
                "선택 화면에 적용된 월페이퍼 없음";
        }
    }

    private static string LocalizePlaybackState(
        WallpaperPlaybackState state)
        => state switch
        {
            WallpaperPlaybackState.Stopped => "중지됨",
            WallpaperPlaybackState.Playing => "재생 중",
            WallpaperPlaybackState.Paused => "일시정지됨",
            WallpaperPlaybackState.Error => "오류",
            _ => state.ToString()
        };

    private sealed record SelectionOption<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
