using System.Windows;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Vellume.App;

internal sealed class QuickSlotGroupDialog : Window
{
    private readonly WpfTextBox _nameTextBox = new();

    public QuickSlotGroupDialog()
    {
        Title = "새 퀵슬롯 그룹";
        Width = 390;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(WpfColor.FromRgb(13, 17, 26));
        Foreground = WpfBrushes.White;

        var panel = new WpfStackPanel
        {
            Margin = new Thickness(24)
        };
        panel.Children.Add(new WpfTextBlock
        {
            Text = "그룹 이름",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new WpfTextBlock
        {
            Text = "예: 자주 사용, 게임, 개발",
            Margin = new Thickness(0, 5, 0, 10),
            Foreground = new SolidColorBrush(
                WpfColor.FromRgb(148, 158, 181))
        });
        _nameTextBox.Height = 32;
        _nameTextBox.MaxLength = 32;
        _nameTextBox.Padding = new Thickness(8, 4, 8, 4);
        panel.Children.Add(_nameTextBox);

        var buttons = new WpfStackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancelButton = new WpfButton
        {
            Content = "취소",
            IsCancel = true,
            MinWidth = 78,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var createButton = new WpfButton
        {
            Content = "만들기",
            IsDefault = true,
            MinWidth = 78
        };
        createButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
            {
                return;
            }

            DialogResult = true;
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(createButton);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _nameTextBox.Focus();
    }

    public string GroupName => _nameTextBox.Text;
}
