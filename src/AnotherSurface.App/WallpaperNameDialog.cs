using System.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AnotherSurface.App;

internal sealed class WallpaperNameDialog : Window
{
    private readonly WpfTextBox _nameTextBox;

    public WallpaperNameDialog(string name)
    {
        Title = "월페이퍼 이름 변경";
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/AnotherSurface;component/Assets/Brand/another-surface-app.ico"));
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (WpfBrush)FindResource("WindowBrush");
        Foreground = (WpfBrush)FindResource("TextBrush");

        var panel = new WpfStackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new WpfTextBlock
        {
            Text = "라이브러리에 표시할 이름",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _nameTextBox = new WpfTextBox { Text = name, Padding = new Thickness(9, 7, 9, 7) };
        System.Windows.Automation.AutomationProperties.SetName(_nameTextBox, "월페이퍼 이름");
        panel.Children.Add(_nameTextBox);

        var buttons = new WpfStackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(new WpfButton
        {
            Content = "취소", IsCancel = true, MinWidth = 78, Margin = new Thickness(0, 0, 8, 0)
        });
        var saveButton = new WpfButton
        {
            Content = "저장", IsDefault = true, MinWidth = 78,
            Background = (WpfBrush)FindResource("AccentBrush"),
            IsEnabled = !string.IsNullOrWhiteSpace(name)
        };
        _nameTextBox.TextChanged += (_, _) => saveButton.IsEnabled =
            !string.IsNullOrWhiteSpace(_nameTextBox.Text);
        saveButton.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _nameTextBox.Focus(); _nameTextBox.SelectAll(); };
    }

    public string WallpaperName => _nameTextBox.Text.Trim();
}
