using System.Runtime.ExceptionServices;
using System.Drawing;
using System.Drawing.Imaging;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class NativeDesktopOverlayIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void NativeVideo_RendersIntoCallerOwnedWindow()
    {
        var sourcePath = Environment.GetEnvironmentVariable(
            "VELLUME_NATIVE_VIDEO_TEST_PATH");
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath))
        {
            return;
        }

        RunOnStaThreadAsync(async () =>
        {
            var host = NativeMethods.CreateWindowExW(
                0,
                "Static",
                "Vellume Native Video Direct Host Test",
                unchecked((uint)(
                    NativeMethods.WsPopup |
                    NativeMethods.WsVisible |
                    NativeMethods.SsBlackRect)),
                -32000,
                -32000,
                320,
                180,
                nint.Zero,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, host);

            try
            {
                using var player = new NativeMediaEnginePlayer();
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(12));
                await player.InitializeAsync(
                        host,
                        sourcePath,
                        isMuted: true,
                        volume: 0,
                        timeout.Token);
                player.UpdateVideoLayout(
                    0,
                    0,
                    320,
                    180);
                Assert.True(player.IsReady);
                Assert.True(NativeMethods.IsWindow(host));
                await player.PauseAsync();
                await player.ResumeAsync();
            }
            finally
            {
                NativeMethods.DestroyWindow(host);
            }
        });
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void BoundedPanel_StaysHiddenUntilAlphaFrameIsReady()
    {
        RunOnStaThread(() =>
        {
            var parent = NativeMethods.CreateWindowExW(
                0,
                "Static",
                "Vellume Alpha Visibility Test Parent",
                unchecked((uint)(
                    NativeMethods.WsPopup |
                    NativeMethods.WsVisible)),
                -32000,
                -32000,
                400,
                300,
                nint.Zero,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, parent);

            try
            {
                using var host = new BoundedDesktopPanelHost(
                    "Vellume Alpha Visibility Test",
                    parent,
                    0,
                    0,
                    180,
                    120);
                Assert.False(
                    NativeMethods.IsWindowVisible(host.Handle));
                Assert.False(
                    NativeMethods.IsWindowVisible(host.SurfaceHandle));
                AssertSingleAlphaSurface(host.Handle);

                host.SetWindowVisible(true);
                Assert.False(
                    NativeMethods.IsWindowVisible(host.Handle));
                Assert.False(
                    NativeMethods.IsWindowVisible(host.SurfaceHandle));

                var visual = new System.Windows.Controls.Canvas
                {
                    Width = 180,
                    Height = 120,
                    Background =
                        System.Windows.Media.Brushes.Transparent
                };
                var marker = new System.Windows.Controls.Border
                {
                    Width = 48,
                    Height = 36,
                    Background =
                        System.Windows.Media.Brushes.White
                };
                System.Windows.Controls.Canvas.SetLeft(marker, 12);
                System.Windows.Controls.Canvas.SetTop(marker, 16);
                visual.Children.Add(marker);
                host.Render(visual);
                Assert.InRange(
                    host.LastNonTransparentPixelCount,
                    1,
                    (180 * 120) - 1);
                Assert.False(
                    NativeMethods.IsWindowVisible(host.Handle));
                Assert.False(
                    NativeMethods.IsWindowVisible(host.SurfaceHandle));

                host.SetWindowVisible(true);
                Assert.True(
                    NativeMethods.IsWindowVisible(host.Handle));
                Assert.True(
                    NativeMethods.IsWindowVisible(host.SurfaceHandle));
                Assert.True(host.LastPresentSucceeded);
            }
            finally
            {
                NativeMethods.DestroyWindow(parent);
            }
        });
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void TransparentChildHosts_CreateAttachUpdateAndClose()
    {
        RunOnStaThread(() =>
        {
            var parent = NativeMethods.CreateWindowExW(
                unchecked((uint)
                    NativeMethods.WsExNoRedirectionBitmap),
                "Static",
                "Vellume Native Overlay Test Parent",
                unchecked((uint)(
                    NativeMethods.WsPopup |
                    NativeMethods.WsVisible)),
                -32000,
                -32000,
                800,
                600,
                nint.Zero,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, parent);
            var shellView = NativeMethods.CreateWindowExW(
                0,
                "Static",
                "Vellume Raised Desktop Shell View",
                unchecked((uint)(
                    NativeMethods.WsChild |
                    NativeMethods.WsVisible)),
                0,
                0,
                800,
                600,
                parent,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, shellView);
            var backgroundWorker = NativeMethods.CreateWindowExW(
                0,
                "Static",
                "Vellume Raised Desktop Background Worker",
                unchecked((uint)(
                    NativeMethods.WsChild |
                    NativeMethods.WsVisible)),
                0,
                0,
                800,
                600,
                parent,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, backgroundWorker);
            var wallpaperRenderer = NativeMethods.CreateWindowExW(
                unchecked((uint)NativeMethods.WsExLayered),
                "Static",
                "Vellume Raised Desktop Wallpaper Renderer",
                unchecked((uint)(
                    NativeMethods.WsChild |
                    NativeMethods.WsVisible)),
                0,
                0,
                800,
                600,
                parent,
                nint.Zero,
                NativeMethods.GetModuleHandleW(null),
                nint.Zero);
            Assert.NotEqual(nint.Zero, wallpaperRenderer);
            Assert.True(
                NativeMethods.SetLayeredWindowAttributes(
                    wallpaperRenderer,
                    0,
                    byte.MaxValue,
                    NativeMethods.LwaAlpha));

            var display = new DisplayInfo
            {
                Id = "TEST",
                Name = "Test",
                X = -32000,
                Y = -32000,
                Width = 800,
                Height = 600,
                IsPrimary = true
            };
            try
            {
                TestInformationWidgets(
                    parent,
                    display);
            }
            finally
            {
                NativeMethods.DestroyWindow(parent);
            }
        });
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void ReferenceClock_RendersVisibleTransparentComposition()
    {
        RunOnStaThread(() =>
        {
            const int width = 320;
            const int height = 200;
            var renderer = new NativeWidgetRendererWindow();
            var clock = renderer.CreateClock(WidgetSide.Right);
            renderer.UpdateClock();
            clock.Measure(new System.Windows.Size(width, height));
            clock.Arrange(new System.Windows.Rect(
                0,
                0,
                width,
                height));
            clock.UpdateLayout();

            var bitmap =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width,
                    height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(clock);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(
                pixels,
                width * 4,
                0);
            var visiblePixels = 0;
            for (var index = 3;
                 index < pixels.Length;
                 index += 4)
            {
                if (pixels[index] > 8)
                {
                    visiblePixels++;
                }
            }

            Assert.InRange(
                visiblePixels,
                1_000,
                width * height / 2);

            var previewPath = Environment.GetEnvironmentVariable(
                "VELLUME_WIDGET_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                var encoder =
                    new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(
                        bitmap));
                using var stream = File.Create(previewPath);
                encoder.Save(stream);
            }
        });
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.75d)]
    [Trait("Category", "LocalIntegration")]
    public void SysDashSystemPanel_PreservesConfiguredBackgroundAndTransparentCorners(double opacity)
    {
        RunOnStaThread(() =>
        {
            const int width = (int)SystemInfoVisual.NaturalWidth;
            const int height = (int)SystemInfoVisual.DefaultHeight;
            var renderer = new NativeWidgetRendererWindow();
            Assert.True(renderer.ApplySettingsAsync(new WidgetSettings
            {
                SystemInfoAppearance = new SystemInfoAppearance { BackgroundOpacity = opacity }
            }).IsCompletedSuccessfully);
            var panel = renderer.CreateSystemPanel(WidgetSide.Right);
            renderer.UpdateSystemMetricsAsync(
                new SystemMetricsSnapshot(
                    12,
                    5,
                    54,
                    27UL * 1024 * 1024 * 1024,
                    48UL * 1024 * 1024 * 1024,
                    new DriveMetricsSnapshot(
                        "C",
                        true,
                        74,
                        329UL * 1024 * 1024 * 1024,
                        441UL * 1024 * 1024 * 1024),
                    new DriveMetricsSnapshot(
                        "D",
                        true,
                        75,
                        692UL * 1024 * 1024 * 1024,
                        931UL * 1024 * 1024 * 1024),
                    TimeSpan.FromHours(12)));
            panel.Measure(new System.Windows.Size(width, height));
            panel.Arrange(new System.Windows.Rect(
                0,
                0,
                width,
                height));
            panel.UpdateLayout();

            var bitmap =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width,
                    height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(
                pixels,
                width * 4,
                0);
            var visiblePixels = 0;
            for (var index = 3;
                 index < pixels.Length;
                 index += 4)
            {
                if (pixels[index] > 8)
                {
                    visiblePixels++;
                }
            }

            if (opacity == 0)
                Assert.InRange(visiblePixels, 2_000, width * height / 3);
            else
                Assert.InRange(visiblePixels, width * height - 250, width * height - 1);
            Assert.Equal(0, pixels[3]);
            Assert.Equal((byte)Math.Round(opacity * 255), pixels[(10 * width + 10) * 4 + 3]);

            var previewPath = Environment.GetEnvironmentVariable(
                "VELLUME_SYSTEM_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                var encoder =
                    new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(
                        bitmap));
                using var stream = File.Create(previewPath);
                encoder.Save(stream);
            }
        });
    }

    private static void TestInformationWidgets(
        nint parent,
        DisplayInfo display)
    {
        var window = new NativeWidgetRendererWindow();
        window.AttachTo(
            parent,
            display);
        window.InitializeAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var settings = new WidgetSettings
        {
            IsEnabled = true,
            ShowClock = true,
            ShowSystemInfo = true,
            ShowCodexCompanion = true
        };
        window.ApplySettingsAsync(
                settings,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        window.UpdateSystemMetricsAsync(
                new SystemMetricsSnapshot(
                    25,
                    40,
                    50,
                    8UL * 1024 * 1024 * 1024,
                    16UL * 1024 * 1024 * 1024,
                    new DriveMetricsSnapshot(
                        "C",
                        true,
                        60,
                        60,
                        100),
                    DriveMetricsSnapshot.Unavailable("D"),
                    TimeSpan.FromHours(1)))
            .GetAwaiter()
            .GetResult();
        window.UpdateCodexAsync(
                new CodexCompanionSnapshot(
                    "working",
                    "빌드하는 중",
                    "네이티브 위젯 테스트",
                    "Vellume",
                    "Codex",
                    DateTimeOffset.UtcNow,
                    [
                        new CodexProjectSnapshot(
                            "project:vellume",
                            "working",
                            "Vellume",
                            "네이티브 위젯 테스트 중",
                            "Vellume",
                            DateTimeOffset.UtcNow)
                    ],
                    new CodexUsageSnapshot(
                        true,
                        [
                            new CodexUsageLimitSnapshot(
                                "codex",
                                "CODEX PRO",
                                42,
                                DateTimeOffset.UtcNow.AddDays(3),
                                TimeSpan.FromDays(7),
                                "pro"),
                            new CodexUsageLimitSnapshot(
                                "codex_bengalfox",
                                "GPT-5.3-Codex-Spark",
                                91,
                                DateTimeOffset.UtcNow.AddDays(4),
                                TimeSpan.FromDays(7),
                                "pro")
                        ])))
            .GetAwaiter()
            .GetResult();
        var handlesBeforePause =
            window.PanelHandles.ToArray();
        var generationsBeforePause =
            window.PanelPresentationGenerations.ToArray();

        window.PauseAsync().GetAwaiter().GetResult();
        Assert.True(window.IsPaused);
        window.UpdateSystemMetricsAsync(
                new SystemMetricsSnapshot(
                    80,
                    75,
                    70,
                    12UL * 1024 * 1024 * 1024,
                    16UL * 1024 * 1024 * 1024,
                    new DriveMetricsSnapshot(
                        "C",
                        true,
                        75,
                        75,
                        100),
                    DriveMetricsSnapshot.Unavailable("D"),
                    TimeSpan.FromHours(2)))
            .GetAwaiter()
            .GetResult();
        window.UpdateCodexAsync(
                new CodexCompanionSnapshot(
                    "complete",
                    "동결 중 캐시",
                    "Resume 때 한 번 반영",
                    "Vellume",
                    "Codex",
                    DateTimeOffset.UtcNow,
                    [
                        new CodexProjectSnapshot(
                            "project:vellume",
                            "complete",
                            "Vellume",
                            "작업 완료",
                            "Vellume",
                            DateTimeOffset.UtcNow)
                    ],
                    new CodexUsageSnapshot(
                        true,
                        [
                            new CodexUsageLimitSnapshot(
                                "codex",
                                "CODEX PRO",
                                41,
                                DateTimeOffset.UtcNow.AddDays(3),
                                TimeSpan.FromDays(7),
                                "pro"),
                            new CodexUsageLimitSnapshot(
                                "codex_bengalfox",
                                "GPT-5.3-Codex-Spark",
                                90,
                                DateTimeOffset.UtcNow.AddDays(4),
                                TimeSpan.FromDays(7),
                                "pro")
                        ])))
            .GetAwaiter()
            .GetResult();
        Assert.Equal(
            handlesBeforePause,
            window.PanelHandles.ToArray());
        var generationsWhilePaused =
            window.PanelPresentationGenerations.ToArray();
        Assert.Equal(
            generationsBeforePause,
            generationsWhilePaused);

        window.ResumeAsync().GetAwaiter().GetResult();
        Assert.False(window.IsPaused);
        Assert.All(
            window.PanelPresentationGenerations.Skip(1)
                .Zip(
                    generationsWhilePaused.Skip(1),
                    (current, previous) => current > previous),
            Assert.True);

        Assert.True(window.IsReady);
        Assert.True(window.IsAttachedTo(parent));
        var panelHandles = window.PanelHandles.ToArray();
        // Independent surfaces remain small at arbitrary widget positions.
        Assert.Equal(3, panelHandles.Length);
        Assert.Equal(
            panelHandles.Length,
            panelHandles.Distinct().Count());
        var surfaceHandles =
            window.PanelSurfaceHandles.ToArray();
        Assert.Equal(panelHandles, surfaceHandles);
        Assert.True(window.AllPanelsPresented);
        Assert.All(
            window.PanelPresentationGenerations,
            generation => Assert.True(generation > 0));
        Assert.All(
            window.PanelNonTransparentPixelCounts,
            count => Assert.True(count > 100));

        for (var index = 0;
             index < panelHandles.Length;
             index++)
        {
            var panelHandle = panelHandles[index];
            var surfaceHandle = surfaceHandles[index];
            Assert.NotEqual(nint.Zero, panelHandle);
            Assert.True(NativeMethods.IsWindow(panelHandle));
            Assert.Equal(
                nint.Zero,
                NativeMethods.GetParent(panelHandle));
            Assert.NotEqual(nint.Zero, surfaceHandle);
            Assert.True(NativeMethods.IsWindow(surfaceHandle));
            Assert.Equal(
                nint.Zero,
                NativeMethods.GetParent(surfaceHandle));

            var extendedStyle = NativeMethods.GetWindowLongPtr(
                    panelHandle,
                    NativeMethods.GwlExtendedStyle)
                .ToInt64();
            Assert.NotEqual(
                0,
                extendedStyle & NativeMethods.WsExLayered);
            Assert.NotEqual(
                0,
                extendedStyle & NativeMethods.WsExTransparent);
            var surfaceExtendedStyle =
                NativeMethods.GetWindowLongPtr(
                        surfaceHandle,
                        NativeMethods.GwlExtendedStyle)
                    .ToInt64();
            Assert.NotEqual(
                0,
                surfaceExtendedStyle &
                NativeMethods.WsExLayered);
            Assert.NotEqual(
                0,
                surfaceExtendedStyle &
                NativeMethods.WsExTransparent);

            Assert.True(
                NativeMethods.GetWindowRect(
                    panelHandle,
                    out var panelRectangle));
            var panelWidth =
                panelRectangle.Right - panelRectangle.Left;
            var panelHeight =
                panelRectangle.Bottom - panelRectangle.Top;
            Assert.InRange(
                panelWidth,
                1,
                display.Width - 1);
            Assert.InRange(
                panelHeight,
                1,
                display.Height - 1);

            Assert.True(
                NativeMethods.GetWindowRect(
                    surfaceHandle,
                    out var surfaceRectangle));
            Assert.Equal(
                panelRectangle.Right - panelRectangle.Left,
                surfaceRectangle.Right - surfaceRectangle.Left);
            Assert.Equal(
                panelRectangle.Bottom - panelRectangle.Top,
                surfaceRectangle.Bottom - surfaceRectangle.Top);
        }

        window.ApplySettingsAsync(
                settings,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert.Equal(panelHandles, window.PanelHandles);
        Assert.Equal(
            surfaceHandles,
            window.PanelSurfaceHandles);

        window.SetWindowVisible(false);
        Assert.All(
            panelHandles,
            panelHandle => Assert.False(
                NativeMethods.IsWindowVisible(panelHandle)));
        Assert.All(
            surfaceHandles,
            surfaceHandle => Assert.False(
                NativeMethods.IsWindowVisible(surfaceHandle)));
        window.SetWindowVisible(true);
        Assert.All(
            panelHandles,
            panelHandle => Assert.True(
                NativeMethods.IsWindowVisible(panelHandle)));
        Assert.All(
            surfaceHandles,
            surfaceHandle => Assert.True(
                NativeMethods.IsWindowVisible(surfaceHandle)));

        var generationsBeforeSameAttachment =
            window.PanelPresentationGenerations.ToArray();
        window.AttachTo(
            parent,
            display);
        Assert.Equal(
            generationsBeforeSameAttachment,
            window.PanelPresentationGenerations);

        var replacementParent = NativeMethods.CreateWindowExW(
            0,
            "Static",
            "Vellume Widget Reattach Test Parent",
            unchecked((uint)(
                NativeMethods.WsPopup |
                NativeMethods.WsVisible)),
            display.X,
            display.Y,
            display.Width,
            display.Height,
            nint.Zero,
            nint.Zero,
            NativeMethods.GetModuleHandleW(null),
            nint.Zero);
        Assert.NotEqual(nint.Zero, replacementParent);
        try
        {
            var generationsBeforeReattach =
                window.PanelPresentationGenerations.ToArray();
            window.AttachTo(
                replacementParent,
                display);
            Assert.True(window.IsAttachedTo(replacementParent));
            Assert.All(
                panelHandles,
                panelHandle => Assert.Equal(
                    nint.Zero,
                    NativeMethods.GetParent(panelHandle)));
            for (var index = 0;
                 index < panelHandles.Length;
                 index++)
            {
                Assert.Equal(
                    nint.Zero,
                    NativeMethods.GetParent(
                        surfaceHandles[index]));
                AssertSingleAlphaSurface(panelHandles[index]);
            }
            Assert.All(
                window.PanelPresentationGenerations
                    .Zip(
                        generationsBeforeReattach,
                        (current, previous) =>
                            current > previous),
                Assert.True);

            var generationsBeforeReturn =
                window.PanelPresentationGenerations.ToArray();
            window.AttachTo(
                parent,
                display);
            Assert.True(window.IsAttachedTo(parent));
            Assert.All(
                panelHandles,
                AssertSingleAlphaSurface);
            Assert.All(
                window.PanelPresentationGenerations
                    .Zip(
                        generationsBeforeReturn,
                        (current, previous) =>
                            current > previous),
                Assert.True);
        }
        finally
        {
            NativeMethods.DestroyWindow(replacementParent);
        }

        Assert.Contains(
            "backend=native-top-level-alpha-skins",
            window.GetAttachmentDiagnostics());
        Assert.Contains(
            "panelCount=3",
            window.GetAttachmentDiagnostics());
        Assert.Contains(
            "topLevelPanels=true",
            window.GetAttachmentDiagnostics());
        window.CloseSafelyAsync().GetAwaiter().GetResult();
        Assert.False(window.IsReady);
        Assert.All(
            panelHandles,
            panelHandle => Assert.False(
                NativeMethods.IsWindow(panelHandle)));
        Assert.All(
            surfaceHandles,
            surfaceHandle => Assert.False(
                NativeMethods.IsWindow(surfaceHandle)));
    }

    private static void AssertSingleAlphaSurface(nint surfaceHandle)
    {
        var className = new System.Text.StringBuilder(128);
        Assert.True(
            NativeMethods.GetClassNameW(
                surfaceHandle,
                className,
                className.Capacity) > 0);
        Assert.Equal(
            NativeAlphaPanelWindowClass.ClassName,
            className.ToString());
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            surfaceHandle,
            NativeMethods.GwlExtendedStyle).ToInt64();
        Assert.NotEqual(
            0,
            extendedStyle & NativeMethods.WsExLayered);
        Assert.NotEqual(
            0,
            extendedStyle & NativeMethods.WsExTransparent);
        var style = NativeMethods.GetWindowLongPtr(
            surfaceHandle,
            NativeMethods.GwlStyle).ToInt64();
        Assert.NotEqual(0, style & NativeMethods.WsPopup);
        Assert.Equal(0, style & NativeMethods.WsChild);
        Assert.Equal(nint.Zero, NativeMethods.GetParent(surfaceHandle));
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunOnStaThreadAsync(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher =
                System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading
                    .DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            System.Windows.Threading.Dispatcher.Run();

            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(
                        System.Windows.Threading
                            .DispatcherPriority.Send);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
