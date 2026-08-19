using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vellume.Core;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Vellume.Windows;

/// <summary>
/// Plays local GIF frames in one lightweight WPF child surface. This avoids a
/// Chromium process while keeping every GIF frame out of the managed 4K
/// full-screen bitmap-copy path.
/// </summary>
internal sealed class NativeGifPlayer : IDisposable
{
    private readonly IReadOnlyList<BitmapSource> _frames;
    private readonly IReadOnlyList<TimeSpan> _frameDelays;
    private readonly DispatcherTimer _timer;
    private readonly Grid _root;
    private readonly Image _image;
    private HwndSource? _source;
    private int _frameIndex;
    private bool _playbackPaused;
    private bool _visible = true;
    private bool _disposed;

    internal NativeGifPlayer(
        nint parentHandle,
        string sourcePath,
        int width,
        int height,
        WallpaperFit fit)
    {
        if (parentHandle == nint.Zero ||
            !NativeMethods.IsWindow(parentHandle))
        {
            throw new InvalidOperationException(
                "네이티브 GIF 부모 창이 준비되지 않았습니다.");
        }

        (_frames, _frameDelays) = DecodeFrames(sourcePath);
        if (_frames.Count == 0)
        {
            throw new InvalidDataException(
                "GIF에서 표시 가능한 프레임을 찾지 못했습니다.");
        }

        _image = new Image
        {
            Source = _frames[0],
            Stretch = ToStretch(fit),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        RenderOptions.SetBitmapScalingMode(
            _image,
            BitmapScalingMode.HighQuality);
        _root = new Grid
        {
            Background = Brushes.Black
        };
        _root.Children.Add(_image);

        var parameters = new HwndSourceParameters(
            "Vellume Native GIF")
        {
            ParentWindow = parentHandle,
            PositionX = 0,
            PositionY = 0,
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            WindowStyle = unchecked((int)(
                NativeMethods.WsChild |
                NativeMethods.WsVisible |
                NativeMethods.WsClipChildren |
                NativeMethods.WsClipSiblings)),
            ExtendedWindowStyle = unchecked((int)(
                NativeMethods.WsExNoActivate |
                NativeMethods.WsExToolWindow))
        };
        _source = new HwndSource(parameters)
        {
            RootVisual = _root
        };
        if (_source.CompositionTarget is not null)
        {
            _source.CompositionTarget.BackgroundColor = Colors.Black;
        }

        _timer = new DispatcherTimer(
            DispatcherPriority.Background,
            _source.Dispatcher);
        _timer.Tick += TimerOnTick;
        UpdateLayout(width, height, fit);
        ScheduleNextFrame();
    }

    internal bool IsReady =>
        !_disposed &&
        _source is { Handle: not 0 } &&
        NativeMethods.IsWindow(_source.Handle);

    internal nint Handle => _source?.Handle ?? nint.Zero;

    internal void Pause()
    {
        _playbackPaused = true;
        _timer.Stop();
    }

    internal void Resume()
    {
        _playbackPaused = false;
        ScheduleNextFrame();
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (IsReady)
        {
            NativeMethods.ShowWindow(
                Handle,
                visible
                    ? NativeMethods.SwShowNoActivate
                    : NativeMethods.SwHide);
        }
        ScheduleNextFrame();
    }

    internal void UpdateLayout(
        int width,
        int height,
        WallpaperFit fit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _image.Stretch = ToStretch(fit);
        if (IsReady)
        {
            NativeMethods.SetWindowPos(
                Handle,
                nint.Zero,
                0,
                0,
                Math.Max(1, width),
                Math.Max(1, height),
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate);
        }
    }

    internal static TimeSpan NormalizeFrameDelay(int hundredths)
        => TimeSpan.FromMilliseconds(Math.Clamp(
            hundredths * 10,
            33,
            10_000));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= TimerOnTick;
        _source?.Dispose();
        _source = null;
    }

    private void TimerOnTick(object? sender, EventArgs eventArgs)
    {
        _timer.Stop();
        if (_disposed || _playbackPaused || !_visible ||
            _frames.Count <= 1)
        {
            return;
        }

        _frameIndex = (_frameIndex + 1) % _frames.Count;
        _image.Source = _frames[_frameIndex];
        ScheduleNextFrame();
    }

    private void ScheduleNextFrame()
    {
        _timer.Stop();
        if (_disposed || _playbackPaused || !_visible ||
            _frames.Count <= 1)
        {
            return;
        }
        _timer.Interval = _frameDelays[_frameIndex];
        _timer.Start();
    }

    private static Stretch ToStretch(WallpaperFit fit)
        => fit switch
        {
            WallpaperFit.Fill => Stretch.UniformToFill,
            WallpaperFit.Fit => Stretch.Uniform,
            _ => Stretch.Fill
        };

    private static (
        IReadOnlyList<BitmapSource> Frames,
        IReadOnlyList<TimeSpan> Delays) DecodeFrames(
        string sourcePath)
    {
        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        foreach (var sourceFrame in decoder.Frames)
        {
            var frame = sourceFrame.Clone();
            frame.Freeze();
            frames.Add(frame);
            delays.Add(ReadFrameDelay(sourceFrame.Metadata));
        }
        return (frames, delays);
    }

    private static TimeSpan ReadFrameDelay(ImageMetadata? metadata)
    {
        try
        {
            if (metadata is BitmapMetadata bitmapMetadata &&
                bitmapMetadata.GetQuery("/grctlext/Delay") is ushort delay)
            {
                return NormalizeFrameDelay(delay);
            }
        }
        catch
        {
            // Malformed GIF metadata falls back to a conservative 10 FPS.
        }
        return TimeSpan.FromMilliseconds(100);
    }
}
