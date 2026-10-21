using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Vellume.Core;
using Vellume.Windows;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Vellume.App;

public partial class MainWindow
{
    private readonly List<ClockStyleChoice> _clockStyleChoices = [];
    private readonly SemaphoreSlim _widgetSettingsGate = new(1, 1);
    private readonly long[] _widgetPlacementRevisions = new long[Enum.GetValues<WidgetKind>().Length];
    private DispatcherTimer? _clockPreviewTimer;
    private bool _updatingWidgetControls;
    private SystemInfoAppearance? _systemPreviewAppearance;

    private sealed record ClockStyleChoice(ClockStyle Value, string Label, FrameworkElement Preview);

    private void InitializeWidgetControls()
    {
        _updatingWidgetControls = true;
        try
        {
            if (_clockStyleChoices.Count == 0)
            {
                foreach (var (style, label) in new[]
                {
                    (ClockStyle.AnalogMinimal, "아날로그 · 미니멀"),
                    (ClockStyle.AnalogNumerals, "아날로그 · 숫자"),
                    (ClockStyle.DigitalClean, "디지털 · 라이트"),
                    (ClockStyle.DigitalStacked, "디지털 · 스택"),
                    (ClockStyle.DigitalMono, "디지털 · 모노")
                })
                {
                    _clockStyleChoices.Add(new ClockStyleChoice(style, label,
                        SimpleClockVisual.CreatePreview(style, 190, 118)));
                }
                ClockStyleList.ItemsSource = _clockStyleChoices;
            }
            var anchors = new[]
            {
                new SelectionOption<WidgetAnchor>(WidgetAnchor.TopLeft, "왼쪽 위"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.TopCenter, "위 중앙"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.TopRight, "오른쪽 위"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.CenterLeft, "왼쪽 중앙"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.Center, "중앙"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.CenterRight, "오른쪽 중앙"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.BottomLeft, "왼쪽 아래"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.BottomCenter, "아래 중앙"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.BottomRight, "오른쪽 아래"),
                new SelectionOption<WidgetAnchor>(WidgetAnchor.Custom, "사용자 지정")
            };
            foreach (var kind in Enum.GetValues<WidgetKind>())
                GetWidgetControls(kind).Anchor.ItemsSource = anchors;
            InitializeStickerControls();
            RefreshWidgetPlacementControls();
        }
        finally { _updatingWidgetControls = false; }

        if (_clockPreviewTimer is null)
        {
            _clockPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockPreviewTimer.Tick += ClockPreviewTimer_Tick;
        }
        UpdateClockPreviewTimer();
    }

    private (ComboBox Anchor, ComboBox Display, CheckBox Lock) GetWidgetControls(WidgetKind kind)
        => kind switch
        {
            WidgetKind.Clock => (ClockAnchorComboBox, ClockDisplayComboBox, ClockLockCheckBox),
            WidgetKind.SystemInfo => (SystemAnchorComboBox, SystemDisplayComboBox, SystemLockCheckBox),
            _ => (CodexAnchorComboBox, CodexDisplayComboBox, CodexLockCheckBox)
        };

    private void RefreshWidgetPlacementControls()
    {
        var wasUpdating = _updatingWidgetControls;
        _updatingWidgetControls = true;
        try
        {
            var clockColor = WidgetSettings.NormalizeClockColor(_settings.Widgets.ClockColor);
            ClockColorTextBox.Text = clockColor;
            ClockColorSwatch.Background = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(clockColor));
            foreach (var choice in _clockStyleChoices)
                if (choice.Preview is Viewbox { Child: SimpleClockVisual clock }) clock.SetColor(clockColor);
            ClockStyleList.SelectedItem = _clockStyleChoices.FirstOrDefault(
                choice => choice.Value == _settings.Widgets.ClockStyle);
            RefreshSystemAppearanceControls();
            foreach (var kind in Enum.GetValues<WidgetKind>())
            {
                var placement = _settings.Widgets.GetPlacement(kind);
                var controls = GetWidgetControls(kind);
                if (controls.Anchor.ItemsSource is IEnumerable<SelectionOption<WidgetAnchor>> anchors)
                    controls.Anchor.SelectedItem = anchors.FirstOrDefault(option => option.Value == placement.Anchor);
                var monitors = new List<SelectionOption<string?>>
                {
                    new(null, "주 모니터")
                };
                monitors.AddRange(Displays.Select(display => new SelectionOption<string?>(display.Id, display.Name)));
                if (placement.DisplayId is { Length: > 0 } id &&
                    !Displays.Any(display => display.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    monitors.Add(new SelectionOption<string?>(id, "연결 해제된 화면"));
                controls.Display.ItemsSource = monitors;
                controls.Display.SelectedItem = monitors.FirstOrDefault(option =>
                    string.Equals(option.Value, placement.DisplayId, StringComparison.OrdinalIgnoreCase)) ?? monitors[0];
                controls.Lock.IsChecked = placement.IsLocked;
            }
        }
        finally { _updatingWidgetControls = wasUpdating; }
        RefreshStickerControls();
    }

    private void ClockPreviewTimer_Tick(object? sender, EventArgs args)
    {
        if (_runtimeClosing || !IsVisible || WidgetsPage.Visibility != Visibility.Visible)
        {
            _clockPreviewTimer?.Stop();
            return;
        }
        var now = DateTime.Now;
        foreach (var choice in _clockStyleChoices)
            if (choice.Preview is Viewbox { Child: SimpleClockVisual clock }) clock.Update(now);
    }

    private void UpdateClockPreviewTimer()
    {
        if (!_runtimeClosing && IsVisible && WidgetsPage.Visibility == Visibility.Visible)
        {
            ClockPreviewTimer_Tick(this, EventArgs.Empty);
            _clockPreviewTimer?.Start();
        }
        else _clockPreviewTimer?.Stop();
    }

    private async Task CommitWidgetControlChangeAsync(Action change, WidgetKind? positionKind = null)
    {
        if (!_initialized || _updatingWidgetControls || _runtimeClosing) return;
        if (positionKind is { } kind)
            Interlocked.Increment(ref _widgetPlacementRevisions[(int)kind]);
        var entered = false;
        try
        {
            await _widgetSettingsGate.WaitAsync(_libraryLifetime.Token);
            entered = true;
            change();
            RefreshWidgetPlacementControls();
            await SaveAndRefreshWidgetsCoreAsync();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        catch (Exception exception)
        {
            await ReportErrorAsync("위젯 설정을 적용하지 못했습니다.", exception);
        }
        finally { if (entered) _widgetSettingsGate.Release(); }
    }

    private async void ClockStyleList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ClockStyleList.SelectedItem is ClockStyleChoice choice)
            await CommitWidgetControlChangeAsync(() => _settings.Widgets.ClockStyle = choice.Value);
    }

    private async void ApplyClockColor_Click(object sender, RoutedEventArgs args)
    {
        var hex = ClockColorTextBox.Text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            ClockColorStatusText.Text = "#RRGGBB 형식으로 입력하세요. 예: #89E0D5";
            return;
        }
        await ApplyClockColorAsync("#" + hex.ToUpperInvariant());
    }

    private async void ResetClockColor_Click(object sender, RoutedEventArgs args)
        => await ApplyClockColorAsync("#FFFFFF");

    private async void ChooseClockColor_Click(object sender, RoutedEventArgs args)
    {
        if (!_initialized || _runtimeClosing) return;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            WidgetSettings.NormalizeClockColor(_settings.Widgets.ClockColor));
        using var picker = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B)
        };
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        }
        finally { owner.ReleaseHandle(); }
        await ApplyClockColorAsync($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
    }

    private async Task ApplyClockColorAsync(string color)
    {
        ClockColorStatusText.Text = string.Empty;
        await CommitWidgetControlChangeAsync(() => _settings.Widgets.ClockColor = color);
    }

    private static bool TryGetWidgetKind(FrameworkElement element, out WidgetKind kind)
        => Enum.TryParse(element.Tag?.ToString(), out kind);

    private async void WidgetAnchor_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: SelectionOption<WidgetAnchor> option } combo ||
            !TryGetWidgetKind(combo, out var kind)) return;
        await CommitWidgetControlChangeAsync(() =>
        {
            var placement = _settings.Widgets.GetPlacement(kind).Copy();
            if (option.Value == WidgetAnchor.Custom && placement.Anchor != WidgetAnchor.Custom)
            {
                // Preserve the current preset position when switching to free placement.
                placement.X = placement.Anchor switch
                {
                    WidgetAnchor.TopLeft or WidgetAnchor.CenterLeft or WidgetAnchor.BottomLeft => 0,
                    WidgetAnchor.TopRight or WidgetAnchor.CenterRight or WidgetAnchor.BottomRight => 1,
                    _ => 0.5
                };
                placement.Y = placement.Anchor switch
                {
                    WidgetAnchor.TopLeft or WidgetAnchor.TopCenter or WidgetAnchor.TopRight => 0,
                    WidgetAnchor.BottomLeft or WidgetAnchor.BottomCenter or WidgetAnchor.BottomRight => 1,
                    _ => 0.5
                };
            }
            placement.Anchor = option.Value;
            _settings.Widgets.SetPlacement(kind, placement);
        }, kind);
    }

    private async void WidgetDisplay_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { SelectedItem: SelectionOption<string?> option } combo ||
            !TryGetWidgetKind(combo, out var kind)) return;
        await CommitWidgetControlChangeAsync(() =>
        {
            var placement = _settings.Widgets.GetPlacement(kind).Copy();
            placement.DisplayId = option.Value;
            _settings.Widgets.SetPlacement(kind, placement);
        }, kind);
    }

    private async void WidgetLock_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox check || !TryGetWidgetKind(check, out var kind)) return;
        var locked = check.IsChecked == true;
        await CommitWidgetControlChangeAsync(() =>
        {
            var placement = _settings.Widgets.GetPlacement(kind).Copy();
            placement.IsLocked = locked;
            _settings.Widgets.SetPlacement(kind, placement);
        }, kind);
    }

    private async void LockAllWidgets_Click(object sender, RoutedEventArgs args)
        => await CommitWidgetControlChangeAsync(() =>
        {
            foreach (var kind in Enum.GetValues<WidgetKind>())
            {
                Interlocked.Increment(ref _widgetPlacementRevisions[(int)kind]);
                var placement = _settings.Widgets.GetPlacement(kind).Copy();
                placement.IsLocked = true;
                _settings.Widgets.SetPlacement(kind, placement);
            }
            foreach (var sticker in _settings.Widgets.Stickers)
            {
                AdvanceStickerRevision(sticker.Id);
                sticker.Placement.IsLocked = true;
            }
        });

    private void WallpaperController_WidgetPlacementChanged(object? sender, WidgetPlacementChangedEventArgs args)
    {
        if (_runtimeClosing) return;
        var revision = Interlocked.Increment(ref _widgetPlacementRevisions[(int)args.Kind]);
        if (Dispatcher.CheckAccess()) _ = PersistDraggedWidgetAsync(args, revision);
        else Dispatcher.BeginInvoke(new Action(() => _ = PersistDraggedWidgetAsync(args, revision)));
    }

    private async Task PersistDraggedWidgetAsync(WidgetPlacementChangedEventArgs args, long revision)
    {
        if (_runtimeClosing) return;
        var entered = false;
        try
        {
            await _widgetSettingsGate.WaitAsync(_libraryLifetime.Token);
            entered = true;
            if (revision != Volatile.Read(ref _widgetPlacementRevisions[(int)args.Kind])) return;
            var placement = args.Placement.Copy();
            if (!args.IsLockChange) placement.IsLocked = _settings.Widgets.GetPlacement(args.Kind).IsLocked;
            _settings.Widgets.SetPlacement(args.Kind, placement);
            if (args.Scale is { } scale) _settings.Widgets.SetScale(args.Kind, scale);
            RefreshWidgetPlacementControls();
            await SaveAndRefreshWidgetsCoreAsync();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        catch (Exception exception)
        {
            await ReportErrorAsync("위젯 위치를 저장하지 못했습니다.", exception);
        }
        finally { if (entered) _widgetSettingsGate.Release(); }
    }
}
