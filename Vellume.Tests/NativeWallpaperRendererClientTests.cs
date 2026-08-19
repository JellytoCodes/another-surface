using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class NativeWallpaperRendererClientTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task ImageLifecycle_UsesDwmRedirectedCompositionWindow()
    {
        var root = FindRepositoryRoot();
        var executablePath = Path.Combine(
            root,
            "Vellume.Renderer.Native",
            "bin",
            "x64",
            "Release",
            "Vellume.Renderer.Native.exe");
        var imagePath = Path.Combine(
            root,
            "Vellume.App",
            "Assets",
            "Brand",
            "vellume-dock-icon.png");
        Assert.True(File.Exists(executablePath), executablePath);
        Assert.True(File.Exists(imagePath), imagePath);

        using var host = new MessageWindowHost();
        var parent = host.Handle;
        Assert.NotEqual(nint.Zero, parent);

        var renderer = new NativeWallpaperRendererClient(
            executablePath);
        nint rendererHandle = nint.Zero;
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(12));
            var display = new DisplayInfo
            {
                Id = "native-test",
                Name = "Native test display",
                X = -32000,
                Y = -32000,
                Width = 320,
                Height = 180,
                IsPrimary = true
            };
            await renderer.AttachToAsync(
                parent,
                display,
                raisedDesktop: false,
                shellViewHandle: nint.Zero,
                backgroundWorkerHandle: nint.Zero,
                timeout.Token);
            await renderer.InitializeAndNavigateAsync(
                new WallpaperItem
                {
                    Name = "Native renderer test",
                    SourcePath = imagePath,
                    Kind = WallpaperKind.Image
                },
                new PlaybackOptions(),
                timeout.Token);

            rendererHandle = renderer.Handle;
            Assert.True(renderer.IsReady);
            Assert.True(renderer.IsAttachedTo(parent));
            Assert.True(NativeMethods.IsWindow(rendererHandle));
            Assert.Equal(parent, NativeMethods.GetParent(rendererHandle));
            var extendedStyle = NativeMethods.GetWindowLongPtr(
                    rendererHandle,
                    NativeMethods.GwlExtendedStyle)
                .ToInt64();
            Assert.Equal(0, extendedStyle & NativeMethods.WsExLayered);
            Assert.Equal(
                0,
                extendedStyle &
                NativeMethods.WsExNoRedirectionBitmap);
            Assert.Contains(
                "native-cpp-d3d11-composition",
                renderer.GetAttachmentDiagnostics());
            Assert.Contains(
                "redirectionBitmap=true",
                renderer.GetAttachmentDiagnostics());

            await renderer.SetWindowVisibleAsync(false, timeout.Token);
            Assert.False(NativeMethods.IsWindowVisible(rendererHandle));
            await renderer.SetWindowVisibleAsync(true, timeout.Token);
            Assert.True(NativeMethods.IsWindowVisible(rendererHandle));
            await renderer.UpdatePlaybackAsync(
                new PlaybackOptions
                {
                    Fit = WallpaperFit.Fit,
                    Brightness = 0.8,
                    Contrast = 1.1,
                    Saturation = 0.7
                });
            await renderer.PauseAsync();
            await renderer.ResumeAsync();
        }
        finally
        {
            await renderer.CloseSafelyAsync();
        }

        Assert.False(renderer.IsReady);
        Assert.False(NativeMethods.IsWindow(rendererHandle));
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task VideoLifecycle_DecodesFirstFrameAndSupportsPauseResume()
    {
        var videoPath = Environment.GetEnvironmentVariable(
            "VELLUME_NATIVE_VIDEO_TEST_PATH");
        if (string.IsNullOrWhiteSpace(videoPath) ||
            !File.Exists(videoPath))
        {
            return;
        }

        var executablePath = Path.Combine(
            FindRepositoryRoot(),
            "Vellume.Renderer.Native",
            "bin",
            "x64",
            "Release",
            "Vellume.Renderer.Native.exe");
        Assert.True(File.Exists(executablePath), executablePath);

        using var host = new MessageWindowHost();
        var renderer = new NativeWallpaperRendererClient(
            executablePath);
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(25));
            var display = new DisplayInfo
            {
                Id = "native-video-test",
                Name = "Native video test display",
                X = -32000,
                Y = -32000,
                Width = 320,
                Height = 180,
                IsPrimary = true
            };
            await renderer.AttachToAsync(
                host.Handle,
                display,
                raisedDesktop: false,
                shellViewHandle: nint.Zero,
                backgroundWorkerHandle: nint.Zero,
                timeout.Token);
            await renderer.InitializeAndNavigateAsync(
                new WallpaperItem
                {
                    Name = "Native video renderer test",
                    SourcePath = videoPath,
                    Kind = WallpaperKind.Video
                },
                new PlaybackOptions
                {
                    IsMuted = true,
                    Fit = WallpaperFit.Fill
                },
                timeout.Token);

            Assert.True(renderer.IsReady);
            Assert.Contains(
                "Media Foundation Media Engine",
                renderer.BackendDisplayName);
            await renderer.PauseAsync();
            await Task.Delay(80, timeout.Token);
            await renderer.ResumeAsync();
            Assert.True(renderer.IsReady);
        }
        finally
        {
            await renderer.CloseSafelyAsync();
        }

        Assert.False(renderer.IsReady);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(
                    directory.FullName,
                    "Vellume.Renderer.Native")) &&
                Directory.Exists(Path.Combine(
                    directory.FullName,
                    "Vellume.App")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Vellume 프로젝트 루트를 찾을 수 없습니다.");
    }

    private sealed class MessageWindowHost : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private System.Windows.Threading.Dispatcher? _dispatcher;
        private Exception? _failure;

        internal MessageWindowHost()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Vellume native renderer test host"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)));
            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    "테스트 HWND를 만들지 못했습니다.",
                    _failure);
            }
        }

        internal nint Handle { get; private set; }

        public void Dispose()
        {
            var dispatcher = _dispatcher;
            if (dispatcher is not null)
            {
                dispatcher.Invoke(() =>
                {
                    if (Handle != nint.Zero)
                    {
                        NativeMethods.DestroyWindow(Handle);
                        Handle = nint.Zero;
                    }
                    dispatcher.BeginInvokeShutdown(
                        System.Windows.Threading.DispatcherPriority.Send);
                });
            }
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _ready.Dispose();
        }

        private void Run()
        {
            try
            {
                _dispatcher =
                    System.Windows.Threading.Dispatcher.CurrentDispatcher;
                Handle = NativeMethods.CreateWindowExW(
                    0,
                    "Static",
                    "Vellume Native Renderer Integration Parent",
                    unchecked((uint)(
                        NativeMethods.WsPopup |
                        NativeMethods.WsVisible)),
                    -32000,
                    -32000,
                    320,
                    180,
                    nint.Zero,
                    nint.Zero,
                    NativeMethods.GetModuleHandleW(null),
                    nint.Zero);
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            finally
            {
                _ready.Set();
            }

            if (_failure is null && Handle != nint.Zero)
            {
                System.Windows.Threading.Dispatcher.Run();
            }
        }
    }
}
