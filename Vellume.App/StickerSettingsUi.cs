using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Vellume.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Vellume.App;

public partial class MainWindow
{
    private sealed record StickerChoice(string FileName, string Name, BitmapImage Preview);
    private sealed record StickerRow(string Id, string Name, bool CanMoveUp, bool CanMoveDown);
    private readonly List<StickerChoice> _stickerChoices = [];
    private bool _updatingStickerControls;

    private void InitializeStickerControls()
    {
        if (_stickerChoices.Count > 0) return;
        foreach (var asset in StickerCatalog.All)
        {
            if (!File.Exists(asset.ImagePath)) continue;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.UriSource = new Uri(asset.ImagePath);
            bitmap.EndInit();
            bitmap.Freeze();
            _stickerChoices.Add(new StickerChoice(asset.FileName, asset.Name, bitmap));
        }
        StickerGallery.ItemsSource = _stickerChoices;
    }

    private StickerItem? SelectedSticker => StickerInstanceList.SelectedItem is StickerRow row
        ? _settings.Widgets.Stickers.FirstOrDefault(item => item.Id == row.Id) : null;

    private void RefreshStickerControls()
    {
        if (_updatingStickerControls) return;
        _updatingStickerControls = true;
        try
        {
            var selectedId = SelectedSticker?.Id;
            var rows = _settings.Widgets.Stickers.Select((item, index) => new StickerRow(item.Id,
                $"{index + 1}. {StickerCatalog.Find(item.AssetName)?.Name}",
                index > 0, index < _settings.Widgets.Stickers.Count - 1)).ToList();
            StickerInstanceList.ItemsSource = rows;
            StickerInstanceList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId) ?? rows.LastOrDefault();
            StickerEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshSelectedSticker();
        }
        finally { _updatingStickerControls = false; }
    }

    private void RefreshSelectedSticker()
    {
        var sticker = SelectedSticker;
        StickerDetails.IsEnabled = sticker is not null;
        StickerTransformControls.IsEnabled = sticker is not null && !sticker.Placement.IsLocked;
        StickerLockCheckBox.IsChecked = sticker?.Placement.IsLocked ?? false;
        StickerVisibleCheckBox.IsChecked = sticker?.IsVisible ?? true;
        StickerSizeTextBox.Text = (sticker?.Size ?? 240).ToString("0.#", CultureInfo.CurrentCulture);
        StickerRotationTextBox.Text = (sticker?.Rotation ?? 0).ToString("0.#", CultureInfo.CurrentCulture);
        var displays = new List<SelectionOption<string?>> { new(null, "주 모니터") };
        displays.AddRange(Displays.Select(display => new SelectionOption<string?>(display.Id, display.Name)));
        if (sticker?.Placement.DisplayId is { } id && !Displays.Any(display => display.Id == id))
            displays.Add(new SelectionOption<string?>(id, "연결 해제된 화면"));
        StickerDisplayComboBox.ItemsSource = displays;
        StickerDisplayComboBox.SelectedItem = displays.FirstOrDefault(option =>
            string.Equals(option.Value, sticker?.Placement.DisplayId, StringComparison.OrdinalIgnoreCase)) ?? displays[0];
    }

    private void StickerInstanceList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingStickerControls) return;
        _updatingStickerControls = true;
        try { RefreshSelectedSticker(); }
        finally { _updatingStickerControls = false; }
    }

    private async void AddSticker_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string fileName } || StickerCatalog.Find(fileName) is null) return;
        var added = new StickerItem { AssetName = fileName };
        await CommitWidgetControlChangeAsync(() =>
        {
            _settings.Widgets.IsEnabled = true;
            WidgetsEnabledCheckBox.IsChecked = true;
            _settings.Widgets.Stickers.Add(added);
        });
        if (StickerInstanceList.ItemsSource is IEnumerable<StickerRow> rows)
            StickerInstanceList.SelectedItem = rows.FirstOrDefault(row => row.Id == added.Id);
    }

    private async void StickerFlags_Changed(object sender, RoutedEventArgs args)
    {
        if (_updatingStickerControls || SelectedSticker is not { } sticker || sender is not CheckBox check) return;
        var value = check.IsChecked == true;
        var isLock = ReferenceEquals(check, StickerLockCheckBox);
        var id = sticker.Id;
        await CommitWidgetControlChangeAsync(() =>
        {
            var current = _settings.Widgets.Stickers.FirstOrDefault(item => item.Id == id);
            if (current is null) return;
            if (isLock) current.Placement.IsLocked = value;
            else current.IsVisible = value;
        });
    }

    private async void StickerDisplay_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingStickerControls || SelectedSticker is not { } sticker ||
            sender is not ComboBox { SelectedItem: SelectionOption<string?> option }) return;
        var id = sticker.Id;
        await CommitWidgetControlChangeAsync(() =>
        {
            var current = _settings.Widgets.Stickers.FirstOrDefault(item => item.Id == id);
            if (current is not null) current.Placement.DisplayId = option.Value;
        });
    }

    private async void ApplyStickerTransform_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedSticker is not { } sticker || sticker.Placement.IsLocked) return;
        if (!double.TryParse(StickerSizeTextBox.Text, out var size) || !double.IsFinite(size) ||
            !double.TryParse(StickerRotationTextBox.Text, out var rotation) || !double.IsFinite(rotation))
        {
            StickerStatusText.Text = "스티커 크기와 회전 각도를 숫자로 입력하세요.";
            return;
        }
        var id = sticker.Id;
        StickerStatusText.Text = string.Empty;
        await CommitWidgetControlChangeAsync(() =>
        {
            var index = _settings.Widgets.Stickers.FindIndex(item => item.Id == id);
            if (index < 0 || _settings.Widgets.Stickers[index].Placement.IsLocked) return;
            var current = _settings.Widgets.Stickers[index].Copy();
            current.Size = size;
            current.Rotation = rotation;
            _settings.Widgets.Stickers[index] = current.Copy();
        });
    }

    private async void RemoveSticker_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedSticker is not { } sticker) return;
        var id = sticker.Id;
        await CommitWidgetControlChangeAsync(() => _settings.Widgets.Stickers.RemoveAll(item => item.Id == id));
    }

    private async void MoveStickerPriority_Click(object sender, RoutedEventArgs args)
    {
        if (_updatingStickerControls || sender is not FrameworkElement { DataContext: StickerRow row, Tag: string direction }) return;
        var offset = direction == "Up" ? -1 : 1;
        await CommitWidgetControlChangeAsync(() =>
        {
            var stickers = _settings.Widgets.Stickers;
            var index = stickers.FindIndex(item => item.Id == row.Id);
            var destination = index + offset;
            if (index < 0 || destination < 0 || destination >= stickers.Count) return;
            (stickers[index], stickers[destination]) = (stickers[destination], stickers[index]);
        });
        if (StickerInstanceList.ItemsSource is IEnumerable<StickerRow> rows &&
            rows.FirstOrDefault(item => item.Id == row.Id) is { } moved)
        {
            StickerInstanceList.SelectedItem = moved;
            StickerInstanceList.ScrollIntoView(moved);
        }
    }

    private void WallpaperController_StickerChanged(object? sender, StickerChangedEventArgs args)
    {
        if (_runtimeClosing) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => WallpaperController_StickerChanged(sender, args)));
            return;
        }
        _ = PersistStickerAsync(args.Sticker);
    }

    private async Task PersistStickerAsync(StickerItem edited)
    {
        var entered = false;
        try
        {
            await _widgetSettingsGate.WaitAsync(_libraryLifetime.Token);
            entered = true;
            var index = _settings.Widgets.Stickers.FindIndex(item => item.Id == edited.Id);
            // A queued gesture cannot resurrect a removed sticker or override a newer Lock.
            if (index < 0 || _settings.Widgets.Stickers[index].Placement.IsLocked) return;
            var current = _settings.Widgets.Stickers[index];
            var next = edited.Copy();
            next.IsVisible = current.IsVisible;
            _settings.Widgets.Stickers[index] = next;
            RefreshStickerControls();
            await SaveAndRefreshWidgetsCoreAsync();
        }
        catch (OperationCanceledException) when (_runtimeClosing) { }
        catch (Exception exception) { await ReportErrorAsync("스티커 배치를 저장하지 못했습니다.", exception); }
        finally { if (entered) _widgetSettingsGate.Release(); }
    }
}
