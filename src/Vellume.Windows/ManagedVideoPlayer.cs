using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Vellume.Core;

namespace Vellume.Windows;

// Used only when Media Foundation's direct HWND output cannot apply the
// requested color correction or decode a file. No browser runtime is involved.
internal sealed class ManagedVideoPlayer : IDisposable
{
    private readonly HwndSource _source;
    private readonly Grid _root;
    private readonly MediaElement _media;
    private readonly VideoColorEffect _effect = new();
    private bool _paused;
    private bool _disposed;

    public ManagedVideoPlayer(nint parent, int width, int height)
    {
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            IsHitTestVisible = false,
            Effect = _effect
        };
        _media.MediaEnded += OnMediaEnded;
        _root = new Grid { Background = System.Windows.Media.Brushes.Black, ClipToBounds = true };
        _root.Children.Add(_media);
        _source = new HwndSource(new HwndSourceParameters("Vellume.Video")
        {
            ParentWindow = parent,
            WindowStyle = unchecked((int)0x56000000), // child, visible, clip children/siblings
            Width = Math.Max(1, width),
            Height = Math.Max(1, height)
        });
        _source.CompositionTarget.BackgroundColor = Colors.Black;
        _source.RootVisual = _root;
        Resize(width, height);
    }

    public bool IsReady { get; private set; }

    public async Task OpenAsync(string path, PlaybackOptions options, CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, RoutedEventArgs args) => opened.TrySetResult();
        void OnFailed(object? sender, ExceptionRoutedEventArgs args)
        {
            IsReady = false;
            opened.TrySetException(args.ErrorException);
        }
        _media.MediaOpened += OnOpened;
        _media.MediaFailed += OnFailed;
        try
        {
            UpdatePlayback(options);
            _media.Source = new Uri(Path.GetFullPath(path));
            _media.Play();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            IsReady = true;
            _media.MediaFailed += OnPlaybackFailed;
        }
        finally
        {
            _media.MediaOpened -= OnOpened;
            _media.MediaFailed -= OnFailed;
        }
    }

    public void Resize(int width, int height)
    {
        NativeMethods.SetWindowPos(_source.Handle, nint.Zero, 0, 0,
            Math.Max(1, width), Math.Max(1, height),
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        var scale = _source.CompositionTarget.TransformFromDevice;
        _root.Width = Math.Max(1, width * scale.M11);
        _root.Height = Math.Max(1, height * scale.M22);
    }

    public void UpdatePlayback(PlaybackOptions options)
    {
        _media.IsMuted = options.IsMuted;
        _media.Volume = Math.Clamp(options.Volume, 0, 1);
        _media.Stretch = options.Fit switch
        {
            WallpaperFit.Fit => Stretch.Uniform,
            WallpaperFit.Stretch => Stretch.Fill,
            _ => Stretch.UniformToFill
        };
        _effect.SetOptions(options);
    }

    public void Pause() { _paused = true; _media.Pause(); }
    public void Resume() { _paused = false; _media.Play(); }
    private void OnMediaEnded(object? sender, RoutedEventArgs args)
    {
        _media.Position = TimeSpan.Zero;
        if (!_paused) _media.Play();
    }
    private void OnPlaybackFailed(object? sender, ExceptionRoutedEventArgs args) => IsReady = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsReady = false;
        _media.MediaEnded -= OnMediaEnded;
        _media.MediaFailed -= OnPlaybackFailed;
        _media.Close();
        _source.RootVisual = null;
        _source.Dispose();
    }

    private sealed class VideoColorEffect : ShaderEffect
    {
        private static readonly PixelShader Shader = LoadShader();
        private static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(VideoColorEffect), 0);
        private static readonly DependencyProperty BrightnessProperty = RegisterConstant("Brightness", 0);
        private static readonly DependencyProperty ContrastProperty = RegisterConstant("Contrast", 1);
        private static readonly DependencyProperty SaturationProperty = RegisterConstant("Saturation", 2);

        public VideoColorEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(BrightnessProperty);
            UpdateShaderValue(ContrastProperty);
            UpdateShaderValue(SaturationProperty);
        }
        public void SetOptions(PlaybackOptions options)
        {
            SetValue(BrightnessProperty, Math.Clamp(options.Brightness, .25, 1.25));
            SetValue(ContrastProperty, Math.Clamp(options.Contrast, .5, 1.5));
            SetValue(SaturationProperty, Math.Clamp(options.Saturation, 0, 1.5));
        }
        private static DependencyProperty RegisterConstant(string name, int register) =>
            DependencyProperty.Register(name, typeof(double), typeof(VideoColorEffect),
                new UIPropertyMetadata(1d, PixelShaderConstantCallback(register)));
        private static PixelShader LoadShader()
        {
            using var stream = typeof(ManagedVideoPlayer).Assembly.GetManifestResourceStream(
                "Vellume.Windows.Shaders.VideoColor.ps")!;
            var shader = new PixelShader();
            shader.SetStreamSource(stream);
            shader.Freeze();
            return shader;
        }
    }
}
