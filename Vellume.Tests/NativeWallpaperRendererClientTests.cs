using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

[Collection("Native renderer environment")]
public sealed class NativeWallpaperRendererClientTests
{
    [Theory]
    [InlineData(320, 180)]
    [InlineData(3840, 2160)]
    [Trait("Category", "LocalIntegration")]
    public async Task IdleRenderer_DispatchesDisplayMessagesWithoutWaitingForAnotherCommand(int width, int height)
    {
        var root = FindRepositoryRoot();
        var executable = Path.Combine(root, "Vellume.Renderer.Native", "bin", "x64", "Release", "Vellume.Renderer.Native.exe");
        using var host = new MessageWindowHost();
        var renderer = new NativeWallpaperRendererClient(executable);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var display = new DisplayInfo { Id = "idle-message-test", Name = "Offscreen display", X = -32000, Y = -32000,
                Width = width, Height = height, IsPrimary = true };
            await renderer.AttachToAsync(host.Handle, display, false, nint.Zero, nint.Zero, timeout.Token);
            await renderer.InitializeAndNavigateAsync(new WallpaperItem
            {
                Kind = WallpaperKind.Image,
                SourcePath = Path.Combine(root, "Vellume.App", "Assets", "Brand", "vellume-dock-icon.png")
            }, new PlaybackOptions(), timeout.Token);
            await Task.Delay(100, timeout.Token);

            // No pipe traffic is sent while Windows synchronously delivers these
            // notifications, as happens on exclusive-fullscreen display changes.
            foreach (var message in new uint[] { 0, 0x007E, 0x031E }) // WM_NULL, WM_DISPLAYCHANGE, WM_DWMCOMPOSITIONCHANGED
            {
                var acknowledged = NativeMethods.SendMessageTimeoutW(renderer.Handle, message, new nint(32),
                    new nint((height << 16) | width), 3, 1000, out _);
                Assert.True(acknowledged != nint.Zero,
                    $"Idle native renderer did not dispatch message 0x{message:X} within one second at {width}x{height}.");
            }
            await renderer.PauseAsync();
            await renderer.ResumeAsync();
            Assert.True(renderer.IsReady);
        }
        finally { await renderer.CloseSafelyAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "LocalIntegration")]
    public async Task ImageLifecycle_UsesDwmRedirectedCompositionWindow(bool useAdaptiveWrapper)
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

        var previousExecutable = Environment.GetEnvironmentVariable(
            "VELLUME_NATIVE_RENDERER_EXECUTABLE");
        var previousDisable = Environment.GetEnvironmentVariable(
            "VELLUME_DISABLE_NATIVE_RENDERER");
        if (useAdaptiveWrapper)
        {
            Environment.SetEnvironmentVariable("VELLUME_NATIVE_RENDERER_EXECUTABLE", executablePath);
            Environment.SetEnvironmentVariable("VELLUME_DISABLE_NATIVE_RENDERER", null);
        }
        IWallpaperRenderer renderer = useAdaptiveWrapper
            ? new AdaptiveWallpaperRenderer(Path.Combine(
                Path.GetTempPath(), "Vellume.NativeRendererTests", Guid.NewGuid().ToString("N")))
            : new NativeWallpaperRendererClient(executablePath);
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
            AssertPhysicalBounds(rendererHandle, display);
            Assert.Contains("image=320x320", renderer.GetAttachmentDiagnostics());
            Assert.Contains("imageUploads=1", renderer.GetAttachmentDiagnostics());
            await renderer.UpdatePlaybackAsync(new PlaybackOptions { Brightness = 0.8 });
            Assert.Contains("imageUploads=1", renderer.GetAttachmentDiagnostics());

            // A no-op attach must not discard the decoded image or its HWND.
            await renderer.AttachToAsync(
                parent, display, false, nint.Zero, nint.Zero, timeout.Token);
            Assert.Equal(rendererHandle, renderer.Handle);
            Assert.True(renderer.IsReady);
            AssertPhysicalBounds(rendererHandle, display);

            var resizedDisplay = new DisplayInfo
            {
                Id = display.Id,
                Name = display.Name,
                X = -31980,
                Y = -31970,
                Width = 480,
                Height = 270,
                IsPrimary = true
            };
            await renderer.AttachToAsync(
                parent, resizedDisplay, false, nint.Zero, nint.Zero, timeout.Token);
            Assert.Equal(rendererHandle, renderer.Handle);
            Assert.True(renderer.IsReady);
            Assert.Contains("WIC", renderer.BackendDisplayName);
            AssertPhysicalBounds(rendererHandle, resizedDisplay);
            Assert.Contains("image=480x480", renderer.GetAttachmentDiagnostics());
            Assert.Contains("imageUploads=2", renderer.GetAttachmentDiagnostics());

            // Exercise both growth and shrinkage of the existing swap chain.
            await renderer.AttachToAsync(
                parent, display, false, nint.Zero, nint.Zero, timeout.Token);
            Assert.Equal(rendererHandle, renderer.Handle);
            Assert.True(renderer.IsReady);
            AssertPhysicalBounds(rendererHandle, display);

            await renderer.SetWindowVisibleAsync(false, timeout.Token);
            Assert.False(NativeMethods.IsWindowVisible(rendererHandle));
            await renderer.AttachToAsync(
                parent, display, false, nint.Zero, nint.Zero, timeout.Token);
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
            Assert.Contains("image=180x180", renderer.GetAttachmentDiagnostics());
            Assert.Contains("imageUploads=4", renderer.GetAttachmentDiagnostics());
            await renderer.PauseAsync();
            await renderer.ResumeAsync();
        }
        finally
        {
            try
            {
                await renderer.CloseSafelyAsync();
            }
            finally
            {
                if (useAdaptiveWrapper)
                {
                    Environment.SetEnvironmentVariable("VELLUME_NATIVE_RENDERER_EXECUTABLE", previousExecutable);
                    Environment.SetEnvironmentVariable("VELLUME_DISABLE_NATIVE_RENDERER", previousDisable);
                }
            }
        }

        Assert.False(renderer.IsReady);
        Assert.False(NativeMethods.IsWindow(rendererHandle));
    }

    [Theory]
    [InlineData(210, 90, WallpaperFit.Fill, 210, 119)]
    [InlineData(210, 90, WallpaperFit.Fit, 160, 90)]
    [InlineData(90, 160, WallpaperFit.Fill, 285, 160)]
    [InlineData(90, 160, WallpaperFit.Fit, 90, 51)]
    [Trait("Category", "LocalIntegration")]
    public async Task ImageDecode_UsesDisplayResolutionAndReloadsOriginalWhenGrowing(
        int width, int height, WallpaperFit fit, int decodedWidth, int decodedHeight)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"VellumeDecodeTest-{Guid.NewGuid():N}.png");
        var executablePath = Path.Combine(FindRepositoryRoot(), "Vellume.Renderer.Native",
            "bin", "x64", "Release", "Vellume.Renderer.Native.exe");
        Assert.True(File.Exists(executablePath));
        using var host = new MessageWindowHost();
        var renderer = new NativeWallpaperRendererClient(executablePath);
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(1920, 1080))
            {
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.MidnightBlue);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var display = new DisplayInfo
            {
                Id = "decode-test", Name = "Decode test", X = -32000, Y = -32000,
                Width = width, Height = height
            };
            await renderer.AttachToAsync(host.Handle, display, false, nint.Zero, nint.Zero, timeout.Token);
            await renderer.InitializeAndNavigateAsync(new WallpaperItem
            {
                Name = "Decode test", SourcePath = imagePath, Kind = WallpaperKind.Image
            }, new PlaybackOptions { Fit = fit }, timeout.Token);
            Assert.Contains($"image={decodedWidth}x{decodedHeight}", renderer.GetAttachmentDiagnostics());
            Assert.Contains("imageUploads=1", renderer.GetAttachmentDiagnostics());

            await renderer.UpdatePlaybackAsync(new PlaybackOptions { Fit = fit, Brightness = 0.9 });
            Assert.Contains("imageUploads=1", renderer.GetAttachmentDiagnostics());
            var grownDisplay = new DisplayInfo
            {
                Id = display.Id, Name = display.Name, X = display.X, Y = display.Y,
                Width = 1920, Height = 1080
            };
            await renderer.AttachToAsync(host.Handle, grownDisplay, false, nint.Zero, nint.Zero, timeout.Token);
            Assert.Contains("image=1920x1080", renderer.GetAttachmentDiagnostics());
            Assert.Contains("imageUploads=2", renderer.GetAttachmentDiagnostics());
        }
        finally
        {
            await renderer.CloseSafelyAsync();
            File.Delete(imagePath);
        }
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
            var rendererHandle = renderer.Handle;
            AssertPhysicalBounds(rendererHandle, display);
            await renderer.PauseAsync();
            var resizedDisplay = new DisplayInfo
            {
                Id = display.Id,
                Name = display.Name,
                X = -31990,
                Y = -31980,
                Width = 480,
                Height = 270,
                IsPrimary = true
            };
            await renderer.AttachToAsync(
                host.Handle, resizedDisplay, false, nint.Zero, nint.Zero, timeout.Token);
            Assert.Equal(rendererHandle, renderer.Handle);
            Assert.True(renderer.IsReady);
            Assert.Contains("Media Foundation Media Engine", renderer.BackendDisplayName);
            AssertPhysicalBounds(rendererHandle, resizedDisplay);
            await renderer.ResumeAsync();
            Assert.True(renderer.IsReady);
            foreach (var (width, height) in new[] { (3840, 2160), (320, 180) })
            {
                var transitionDisplay = new DisplayInfo { Id = display.Id, Name = display.Name,
                    X = display.X, Y = display.Y, Width = width, Height = height, IsPrimary = true };
                await renderer.AttachToAsync(host.Handle, transitionDisplay, false, nint.Zero, nint.Zero, timeout.Token);
                Assert.Equal(rendererHandle, renderer.Handle);
                AssertPhysicalBounds(rendererHandle, transitionDisplay);
                await Task.Delay(100, timeout.Token);
                Assert.NotEqual(nint.Zero, NativeMethods.SendMessageTimeoutW(rendererHandle, 0x007E,
                    new nint(32), new nint((height << 16) | width), 3, 1000, out _));
            }
        }
        finally
        {
            await renderer.CloseSafelyAsync();
        }

        Assert.False(renderer.IsReady);
    }

    private static void AssertPhysicalBounds(nint handle, DisplayInfo display)
    {
        // The test runner itself need not be DPI aware; scope only this raw-pixel query.
        var previousContext = NativeMethods.SetThreadDpiAwarenessContext(new nint(-4));
        Assert.NotEqual(nint.Zero, previousContext);
        try
        {
            Assert.True(NativeMethods.GetWindowRect(handle, out var bounds));
            Assert.Equal(display.X, bounds.Left);
            Assert.Equal(display.Y, bounds.Top);
            Assert.Equal(display.X + display.Width, bounds.Right);
            Assert.Equal(display.Y + display.Height, bounds.Bottom);
            Assert.True(NativeMethods.GetClientRect(handle, out var client));
            Assert.Equal(display.Width, client.Right - client.Left);
            Assert.Equal(display.Height, client.Bottom - client.Top);
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previousContext);
        }
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
            // Match the native child before creating its cross-process parent.
            var previousContext = NativeMethods.SetThreadDpiAwarenessContext(new nint(-4));
            try
            {
                Assert.NotEqual(nint.Zero, previousContext);
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

            try
            {
                if (_failure is null && Handle != nint.Zero)
                {
                    System.Windows.Threading.Dispatcher.Run();
                }
            }
            finally
            {
                if (previousContext != nint.Zero)
                {
                    NativeMethods.SetThreadDpiAwarenessContext(previousContext);
                }
            }
        }
    }
}

// The adaptive variant temporarily selects its native binary through process environment.
[CollectionDefinition("Native renderer environment", DisableParallelization = true)]
public sealed class NativeRendererEnvironmentCollection
{
}
