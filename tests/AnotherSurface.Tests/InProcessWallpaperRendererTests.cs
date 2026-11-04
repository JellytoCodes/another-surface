using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Threading;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class InProcessWallpaperRendererTests
{
    [Fact]
    public void Factory_UsesTheInProcessRenderer() => Assert.IsType<InProcessWallpaperRenderer>(WallpaperRendererFactory.Create("unused"));

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public Task Video_ImageAndColorChangesKeepPlaybackInTheSameProcess()
    {
        var path = Environment.GetEnvironmentVariable("ANOTHER_SURFACE_NATIVE_VIDEO_TEST_PATH");
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;
        return RunOnStaAsync(async () =>
        {
            var host = NativeMethods.CreateWindowExW(0, "Static", "Another Surface in-process video test",
                unchecked((uint)(NativeMethods.WsPopup | NativeMethods.WsVisible)), -32000, -32000, 320, 180,
                nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            var shell = NativeMethods.CreateWindowExW(0, "Static", "Test shell",
                unchecked((uint)(NativeMethods.WsChild | NativeMethods.WsVisible)), 0, 0, 320, 180,
                host, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            var background = NativeMethods.CreateWindowExW(0, "Static", "Test background",
                unchecked((uint)(NativeMethods.WsChild | NativeMethods.WsVisible)), 0, 0, 320, 180,
                host, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            var imagePath = Path.Combine(Path.GetTempPath(), $"AnotherSurfaceSwitch-{Guid.NewGuid():N}.png");
            using (var image = new Bitmap(32, 32)) image.Save(imagePath, ImageFormat.Png);
            var renderer = WallpaperRendererFactory.Create("unused");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                var display = new DisplayInfo { Id = "TEST", X = -32000, Y = -32000, Width = 320, Height = 180 };
                await renderer.AttachToAsync(host, display, true, shell, background, timeout.Token);
                await renderer.InitializeAndNavigateAsync(new WallpaperItem { SourcePath = path, Kind = WallpaperKind.Video }, new PlaybackOptions(), timeout.Token);
                Assert.True(renderer.IsReady);
                Assert.Equal("Media Foundation", renderer.BackendDisplayName);
                Assert.Equal(0, NativeMethods.GetWindowLongPtr(renderer.Handle, NativeMethods.GwlExtendedStyle).ToInt64() & NativeMethods.WsExLayered);
                await renderer.PauseAsync();
                await renderer.ResumeAsync();
                await renderer.UpdatePlaybackAsync(new PlaybackOptions { Brightness = 1.1 });
                Assert.True(renderer.IsReady);
                Assert.Contains("색상", renderer.BackendDisplayName);
                Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(renderer.Handle, NativeMethods.GwlExtendedStyle).ToInt64() & NativeMethods.WsExLayered);
                await renderer.UpdatePlaybackAsync(new PlaybackOptions());
                Assert.True(renderer.IsReady);
                Assert.Equal("Media Foundation", renderer.BackendDisplayName);
                await renderer.AttachToAsync(host, display, true, shell, background, timeout.Token);
                await renderer.InitializeAndNavigateAsync(new WallpaperItem { SourcePath = imagePath, Kind = WallpaperKind.Image }, new PlaybackOptions(), timeout.Token);
                Assert.True(renderer.IsReady);
                Assert.Equal("네이티브 이미지", renderer.BackendDisplayName);
                Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(renderer.Handle, NativeMethods.GwlExtendedStyle).ToInt64() & NativeMethods.WsExLayered);
                NativeMethods.GetWindowThreadProcessId(renderer.Handle, out var process);
                Assert.Equal((uint)Environment.ProcessId, process);
            }
            finally { await renderer.CloseSafelyAsync(); NativeMethods.DestroyWindow(host); File.Delete(imagePath); }
        });
    }

    [Fact]
    public Task MultipleDisplays_UseOneRenderThreadAndTheCallingProcess_AndCloseIndependently()
        => RunOnStaAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"AnotherSurfaceInProcess-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "image.png");
            using (var image = new Bitmap(32, 32))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.CornflowerBlue);
                image.Save(path, ImageFormat.Png);
            }
            var host = NativeMethods.CreateWindowExW(0, "Static", "Another Surface in-process test",
                unchecked((uint)(NativeMethods.WsPopup | NativeMethods.WsVisible)), -32000, -32000, 320, 180,
                nint.Zero, nint.Zero, NativeMethods.GetModuleHandleW(null), nint.Zero);
            Assert.NotEqual(nint.Zero, host);
            var first = WallpaperRendererFactory.Create("unused");
            var second = WallpaperRendererFactory.Create("unused");
            try
            {
                var display = new DisplayInfo { Id = "TEST", Name = "TEST", X = -32000, Y = -32000, Width = 320, Height = 180 };
                var item = new WallpaperItem { SourcePath = path, Kind = WallpaperKind.Image };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                foreach (var renderer in new[] { first, second })
                {
                    await renderer.AttachToAsync(host, display, false, nint.Zero, nint.Zero, timeout.Token);
                    await renderer.InitializeAndNavigateAsync(item, new PlaybackOptions(), timeout.Token);
                    Assert.True(renderer.IsReady);
                    Assert.True(renderer.IsAttachedTo(host));
                    NativeMethods.GetWindowThreadProcessId(renderer.Handle, out var process);
                    Assert.Equal((uint)Environment.ProcessId, process);
                }
                Assert.NotEqual(first.Handle, second.Handle);
                var thread = NativeMethods.GetWindowThreadProcessId(first.Handle, out _);
                Assert.Equal(thread, NativeMethods.GetWindowThreadProcessId(second.Handle, out _));
                await first.PauseAsync();
                await first.ResumeAsync();
                await first.UpdatePlaybackAsync(new PlaybackOptions { Fit = WallpaperFit.Stretch });
                await first.CloseSafelyAsync();
                Assert.False(first.IsReady);
                Assert.True(second.IsReady);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => first.PauseAsync());
            }
            finally
            {
                await first.CloseSafelyAsync();
                await second.CloseSafelyAsync();
                NativeMethods.DestroyWindow(host);
                Directory.Delete(directory, recursive: true);
            }
        });

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception exception) { done.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
