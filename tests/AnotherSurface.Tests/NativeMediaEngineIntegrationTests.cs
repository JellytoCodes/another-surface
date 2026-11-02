using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class NativeMediaEngineIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task LocalMp4_InitializesWithoutWebView()
    {
        var sourcePath = Environment.GetEnvironmentVariable(
            "ANOTHER_SURFACE_NATIVE_MEDIA_TEST_FILE");
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        Assert.True(File.Exists(sourcePath));
        var window = NativeMethods.CreateWindowExW(
            0,
            "Static",
            "Another Surface Native Media Test",
            unchecked((uint)(
                NativeMethods.WsPopup |
                NativeMethods.WsVisible |
                NativeMethods.SsBlackRect)),
            -32000,
            -32000,
            64,
            64,
            nint.Zero,
            nint.Zero,
            NativeMethods.GetModuleHandleW(null),
            nint.Zero);
        Assert.NotEqual(nint.Zero, window);

        try
        {
            using var player = new NativeMediaEnginePlayer();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(12));
            await player.InitializeAsync(
                window,
                sourcePath,
                isMuted: true,
                volume: 0,
                timeout.Token);
            Assert.True(player.IsReady);
            Assert.True(player.NativeWidth > 0);
            Assert.True(player.NativeHeight > 0);
            await player.PauseAsync();
            await player.ResumeAsync();
        }
        finally
        {
            NativeMethods.DestroyWindow(window);
        }
    }
}
