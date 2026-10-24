using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vellume.Core;
using Vellume.Windows;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Vellume.App;

public partial class MainWindow
{
    private void RefreshSystemAppearanceControls()
    {
        var appearance = _settings.Widgets.SystemInfoAppearance.Copy();
        SystemCpuCheckBox.IsChecked = appearance.ShowCpu;
        SystemGpuCheckBox.IsChecked = appearance.ShowGpu;
        SystemMemoryCheckBox.IsChecked = appearance.ShowMemory;
        SystemDriveCCheckBox.IsChecked = appearance.ShowDriveC;
        SystemDriveDCheckBox.IsChecked = appearance.ShowDriveD;
        SystemAccentColorTextBox.Text = appearance.AccentColor;
        SystemTextColorTextBox.Text = appearance.TextColor;
        SystemAccentSwatch.Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(appearance.AccentColor));
        SystemTextSwatch.Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(appearance.TextColor));
        SystemScaleComboBox.ItemsSource = new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 }
            .Append(appearance.Scale).Distinct().Order().Select(value => new SelectionOption<double>(value, $"{value:P0}")).ToList();
        SystemScaleComboBox.SelectedItem = ((IEnumerable<SelectionOption<double>>)SystemScaleComboBox.ItemsSource)
            .First(option => option.Value == appearance.Scale);
        SystemOpacityComboBox.ItemsSource = new[] { 0, 0.25, 0.5, 0.75, 1 }
            .Append(appearance.BackgroundOpacity).Distinct().Order().Select(value =>
                new SelectionOption<double>(value, value == 0 ? "투명 (0%)" : $"{value:P0}")).ToList();
        SystemOpacityComboBox.SelectedItem = ((IEnumerable<SelectionOption<double>>)SystemOpacityComboBox.ItemsSource)
            .First(option => option.Value == appearance.BackgroundOpacity);
        if (_systemPreviewAppearance != appearance)
        {
            SystemInfoPreview.Content = appearance.HasVisibleItems ? SystemInfoVisual.CreatePreview(appearance) : null;
            SystemInfoEmptyPreview.Visibility = appearance.HasVisibleItems ? Visibility.Collapsed : Visibility.Visible;
            _systemPreviewAppearance = appearance;
        }
    }

    private async void SystemMetric_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox check) return;
        var enabled = check.IsChecked == true;
        var metric = check.Tag?.ToString();
        await CommitWidgetControlChangeAsync(() =>
        {
            var appearance = _settings.Widgets.SystemInfoAppearance;
            switch (metric)
            {
                case "Cpu": appearance.ShowCpu = enabled; break;
                case "Gpu": appearance.ShowGpu = enabled; break;
                case "Memory": appearance.ShowMemory = enabled; break;
                case "DriveC": appearance.ShowDriveC = enabled; break;
                case "DriveD": appearance.ShowDriveD = enabled; break;
            }
        });
    }

    private async void SystemScale_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: SelectionOption<double> option })
            await CommitWidgetControlChangeAsync(() => _settings.Widgets.SystemInfoAppearance.Scale = option.Value);
    }

    private async void SystemOpacity_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: SelectionOption<double> option })
            await CommitWidgetControlChangeAsync(() => _settings.Widgets.SystemInfoAppearance.BackgroundOpacity = option.Value);
    }

    private async void ApplySystemColors_Click(object sender, RoutedEventArgs args)
    {
        var accent = SystemAccentColorTextBox.Text.Trim().TrimStart('#');
        var text = SystemTextColorTextBox.Text.Trim().TrimStart('#');
        if (accent.Length != 6 || text.Length != 6 || !accent.All(Uri.IsHexDigit) || !text.All(Uri.IsHexDigit))
        {
            SystemColorStatusText.Text = "#RRGGBB 형식으로 입력하세요. 예: #1696F3";
            return;
        }
        SystemColorStatusText.Text = string.Empty;
        await CommitWidgetControlChangeAsync(() =>
        {
            _settings.Widgets.SystemInfoAppearance.AccentColor = "#" + accent.ToUpperInvariant();
            _settings.Widgets.SystemInfoAppearance.TextColor = "#" + text.ToUpperInvariant();
        });
    }

    private async void ChooseSystemColor_Click(object sender, RoutedEventArgs args)
    {
        if (!_initialized || _runtimeClosing || sender is not FrameworkElement element) return;
        var accent = element.Tag?.ToString() == "Accent";
        var appearance = _settings.Widgets.SystemInfoAppearance.Copy();
        var color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(accent ? appearance.AccentColor : appearance.TextColor);
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
        var selected = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        SystemColorStatusText.Text = string.Empty;
        await CommitWidgetControlChangeAsync(() =>
        {
            if (accent) _settings.Widgets.SystemInfoAppearance.AccentColor = selected;
            else _settings.Widgets.SystemInfoAppearance.TextColor = selected;
        });
    }

    private async void ResetSystemAppearance_Click(object sender, RoutedEventArgs args)
    {
        SystemColorStatusText.Text = string.Empty;
        await CommitWidgetControlChangeAsync(() => _settings.Widgets.SystemInfoAppearance = new());
    }
}
