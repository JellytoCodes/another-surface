using System.Globalization;
using System.Windows;
using AnotherSurface.Core;

namespace AnotherSurface.App;

public partial class VideoRangeWindow : Window
{
    private readonly WallpaperItem _candidate;
    private readonly double _duration;
    public double? Start { get; private set; }
    public double? End { get; private set; }

    public VideoRangeWindow(WallpaperItem item, double duration)
    {
        InitializeComponent();
        _candidate = item.Copy();
        _duration = duration;
        VideoName.Text = item.Name;
        DurationLabel.Text = $"전체 길이: {duration:0.###}초";
        RangeEnabled.IsChecked = item.HasVideoRange;
        StartSeconds.Text = (item.VideoStartSeconds ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
        EndSeconds.Text = (item.VideoEndSeconds ?? Math.Floor(duration * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (RangeEnabled.IsChecked == true)
        {
            if (!double.TryParse(StartSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                !double.TryParse(EndSeconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            {
                ValidationError.Text = "시간을 초 단위 숫자로 입력하세요. 예: 10.5";
                return;
            }
            _candidate.VideoStartSeconds = start;
            _candidate.VideoEndSeconds = end;
            try { _candidate.ValidateVideoRange(_duration); }
            catch (ArgumentException exception) { ValidationError.Text = exception.Message; return; }
            Start = start;
            End = end;
        }
        DialogResult = true;
    }
}
