using System.Windows.Threading;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class ManagedVideoPlayerIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task FilteredMp4_OpensResizesPausesAndReleasesWithoutBrowser()
    {
        var path = Environment.GetEnvironmentVariable("ANOTHER_SURFACE_NATIVE_MEDIA_TEST_FILE");
        if (string.IsNullOrEmpty(path)) return;
        Assert.True(File.Exists(path));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                nint parent = nint.Zero;
                try
                {
                    parent = NativeMethods.CreateWindowExW(0, "Static", "Another Surface managed video test",
                        unchecked((uint)(NativeMethods.WsPopup | NativeMethods.WsVisible)),
                        -32000, -32000, 320, 180, nint.Zero, nint.Zero,
                        NativeMethods.GetModuleHandleW(null), nint.Zero);
                    Assert.NotEqual(nint.Zero, parent);
                    using var player = new ManagedVideoPlayer(parent, 320, 180);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await player.OpenAsync(path, new PlaybackOptions
                    {
                        IsMuted = true, Brightness = 1.2, Contrast = 1.3, Saturation = .5
                    }, timeout.Token);
                    Assert.True(player.IsReady);
                    player.Resize(640, 360);
                    player.Pause();
                    player.UpdatePlayback(new PlaybackOptions { IsMuted = true, Fit = WallpaperFit.Fit });
                    player.Resume();
                    await Task.Delay(200, timeout.Token);
                    Assert.True(player.IsReady);
                    player.Dispose();
                    Assert.False(player.IsReady);
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally
                {
                    if (parent != nint.Zero) NativeMethods.DestroyWindow(parent);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
