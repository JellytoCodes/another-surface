using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class SimpleClockVisualTests
{
    [Theory]
    [InlineData(ClockStyle.AnalogMinimal, true)]
    [InlineData(ClockStyle.AnalogNumerals, true)]
    [InlineData(ClockStyle.DigitalClean, true)]
    [InlineData(ClockStyle.DigitalStacked, false)]
    [InlineData(ClockStyle.DigitalMono, false)]
    public void Update_RefreshesOnlyWhenDisplayedTimeChanges(ClockStyle style, bool seconds)
    {
        RunOnStaThread(() =>
        {
            var clock = new SimpleClockVisual(style);
            var time = new DateTime(2024, 10, 13, 23, 59, 58);
            Assert.Equal(seconds, clock.NeedsSeconds);
            Assert.True(clock.Update(time));
            Assert.False(clock.Update(time.AddMilliseconds(200)));
            Assert.Equal(seconds, clock.Update(time.AddSeconds(1)));
            Assert.True(clock.Update(time.AddSeconds(2)));
            Assert.False(clock.Update(time.AddSeconds(2)));
        });
    }

    [Theory]
    [InlineData(ClockStyle.DigitalClean, "00:00", ":00")]
    [InlineData(ClockStyle.DigitalStacked, "00", "00")]
    [InlineData(ClockStyle.DigitalMono, "00:00", null)]
    public void DigitalClock_RollsTimeAndDateOverAtMidnight(
        ClockStyle style,
        string expectedTime,
        string? expectedSecondLine)
    {
        RunOnStaThread(() =>
        {
            var clock = new SimpleClockVisual(style);
            var beforeMidnight = new DateTime(2024, 12, 31, 23, 59, 59);
            clock.Update(beforeMidnight);
            var nextDay = beforeMidnight.AddSeconds(1);
            Assert.True(clock.Update(nextDay));
            var text = clock.Children.OfType<TextBlock>().Select(item => item.Text).ToArray();
            Assert.Equal(expectedTime, text[0]);
            if (expectedSecondLine is not null)
            {
                Assert.Equal(expectedSecondLine, text[1]);
            }
            Assert.Equal(nextDay.ToString("yyyy.MM.dd  ddd"), text[^1]);
        });
    }

    [Fact]
    public void GalleryPreview_UsesDesktopVisualAndPreservesItsTime()
    {
        RunOnStaThread(() =>
        {
            var time = new DateTime(2024, 10, 13, 9, 41, 26);
            var preview = SimpleClockVisual.CreatePreview(ClockStyle.DigitalClean, 160, 100, time);
            var clock = Assert.IsType<SimpleClockVisual>(preview.Child);
            Assert.Equal(160, preview.Width);
            Assert.Equal(100, preview.Height);
            Assert.Equal(Stretch.Uniform, preview.Stretch);
            Assert.Equal(ClockStyle.DigitalClean, clock.SelectedStyle);
            Assert.False(clock.Update(time));
            Assert.Equal(["09:41", ":26", time.ToString("yyyy.MM.dd  ddd")],
                clock.Children.OfType<TextBlock>().Select(item => item.Text));
        });
    }

    [Fact]
    public void AllStyles_RenderDistinctTransparentClocksInsideTheSameFootprint()
    {
        RunOnStaThread(() =>
        {
            var frames = new List<byte[]>();
            foreach (var style in Enum.GetValues<ClockStyle>())
            {
                var clock = new SimpleClockVisual(style);
                clock.Update(new DateTime(2024, 10, 13, 9, 41, 26));
                clock.Measure(new System.Windows.Size(SimpleClockVisual.NaturalWidth, SimpleClockVisual.NaturalHeight));
                clock.Arrange(new Rect(0, 0, SimpleClockVisual.NaturalWidth, SimpleClockVisual.NaturalHeight));
                var bitmap = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(clock);
                var pixels = new byte[320 * 200 * 4];
                bitmap.CopyPixels(pixels, 320 * 4, 0);

                // A clock must actually draw, leave the desktop visible, and fit its allocation.
                var drawnPixels = Enumerable.Range(0, 320 * 200)
                    .Count(index => pixels[index * 4 + 3] > 20);
                Assert.InRange(drawnPixels, 300, 14000);
                Assert.All(new[] { 0, 319, 199 * 320, 200 * 320 - 1 },
                    index => Assert.Equal(0, pixels[index * 4 + 3]));
                Assert.All(frames, frame => Assert.False(frame.SequenceEqual(pixels)));
                frames.Add(pixels);
            }
        });
    }

    [Theory]
    [InlineData(ClockStyle.AnalogMinimal)]
    [InlineData(ClockStyle.AnalogNumerals)]
    [InlineData(ClockStyle.DigitalClean)]
    [InlineData(ClockStyle.DigitalStacked)]
    [InlineData(ClockStyle.DigitalMono)]
    public void CustomColor_ColorsEveryElement_AndUpdatesExistingPreview(ClockStyle style)
    {
        RunOnStaThread(() =>
        {
            var preview = SimpleClockVisual.CreatePreview(style, color: "#E26578");
            var clock = Assert.IsType<SimpleClockVisual>(preview.Child);
            var brushes = clock.Children.OfType<TextBlock>().Select(text => text.Foreground)
                .Concat(clock.Children.OfType<System.Windows.Shapes.Shape>()
                    .SelectMany(shape => new[] { shape.Fill, shape.Stroke }).OfType<Brush>())
                .Cast<SolidColorBrush>().ToArray();
            Assert.NotEmpty(brushes);
            var alphas = brushes.Select(brush => brush.Color.A).ToArray();
            Assert.All(brushes, brush =>
                Assert.Equal(Color.FromArgb(brush.Color.A, 0xE2, 0x65, 0x78), brush.Color));
            clock.SetColor("#2389ED");
            Assert.Equal(alphas, brushes.Select(brush => brush.Color.A));
            Assert.All(brushes, brush =>
                Assert.Equal(Color.FromArgb(brush.Color.A, 0x23, 0x89, 0xED), brush.Color));

            clock.Measure(new System.Windows.Size(320, 200));
            clock.Arrange(new Rect(0, 0, 320, 200));
            var bitmap = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(clock);
            var pixels = new byte[320 * 200 * 4];
            bitmap.CopyPixels(pixels, 320 * 4, 0);
            Assert.True(Enumerable.Range(0, 320 * 200)
                .Count(index => pixels[index * 4 + 3] > 64 && pixels[index * 4] > pixels[index * 4 + 2] * 2) > 250);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
