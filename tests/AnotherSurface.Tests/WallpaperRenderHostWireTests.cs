using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WallpaperRenderHostWireTests
{
    [Fact]
    public async Task RequestAndResponse_RoundTripAsConsecutiveFrames()
    {
        var wallpaperId = Guid.NewGuid();
        var request = new RenderHostRequest
        {
            Command = RenderHostCommands.Initialize,
            Attachment = new RenderHostAttachment
            {
                HostHandle = 0x1234,
                Display = new DisplayInfo
                {
                    Id = "DISPLAY1",
                    Name = "Primary",
                    X = -1920,
                    Y = 0,
                    Width = 1920,
                    Height = 1080,
                    IsPrimary = true
                },
                RaisedDesktop = true,
                ShellViewHandle = 0x5678,
                BackgroundWorkerHandle = 0x9ABC
            },
            Wallpaper = new WallpaperItem
            {
                Id = wallpaperId,
                Name = "Test wallpaper",
                SourcePath = @"D:\Wallpapers\test.mp4",
                Kind = WallpaperKind.Video,
                IsBuiltIn = false
            },
            Playback = new PlaybackOptions
            {
                IsMuted = false,
                Volume = 0.42,
                Fit = WallpaperFit.Fit,
                Brightness = 0.8,
                Contrast = 1.1,
                Saturation = 0.7,
                PauseWhenForegroundAppIsMaximized = true
            },
            OutputPath = @"D:\Wallpapers\preview.png",
            Visible = true
        };
        var response = RenderHostResponse.Ok(
            new RenderHostState
            {
                Handle = 0xCDEF,
                ParentHandle = 0x1234,
                BackendDisplayName = "Windows 영상",
                IsReady = true,
                AttachmentDiagnostics = "attached"
            },
            booleanResult: true);

        await using var stream = new MemoryStream();
        await RenderHostWire.WriteAsync(
            stream,
            request,
            CancellationToken.None);
        await RenderHostWire.WriteAsync(
            stream,
            response,
            CancellationToken.None);

        stream.Position = 0;
        var restoredRequest =
            await RenderHostWire.ReadAsync<RenderHostRequest>(
                stream,
                CancellationToken.None);
        var restoredResponse =
            await RenderHostWire.ReadAsync<RenderHostResponse>(
                stream,
                CancellationToken.None);

        Assert.NotNull(restoredRequest);
        Assert.Equal(RenderHostCommands.Initialize, restoredRequest.Command);
        Assert.Equal(0x1234, restoredRequest.Attachment?.HostHandle);
        Assert.Equal("DISPLAY1", restoredRequest.Attachment?.Display?.Id);
        Assert.Equal(-1920, restoredRequest.Attachment?.Display?.X);
        Assert.True(restoredRequest.Attachment?.RaisedDesktop);
        Assert.Equal(wallpaperId, restoredRequest.Wallpaper?.Id);
        Assert.Equal(WallpaperKind.Video, restoredRequest.Wallpaper?.Kind);
        Assert.Equal(WallpaperFit.Fit, restoredRequest.Playback?.Fit);
        Assert.Equal(0.42, restoredRequest.Playback?.Volume);
        Assert.True(restoredRequest.Visible);

        Assert.NotNull(restoredResponse);
        Assert.True(restoredResponse.Success);
        Assert.True(restoredResponse.BooleanResult);
        Assert.Equal(0xCDEF, restoredResponse.State?.Handle);
        Assert.Equal("Windows 영상", restoredResponse.State?.BackendDisplayName);
        Assert.True(restoredResponse.State?.IsReady);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void WallpaperRendererContract_UsesAsyncLifecycleOperations()
    {
        var contract = typeof(IWallpaperRenderer);

        Assert.Equal(
            typeof(Task),
            contract.GetMethod(nameof(IWallpaperRenderer.AttachToAsync))?
                .ReturnType);
        Assert.Equal(
            typeof(Task),
            contract.GetMethod(
                    nameof(IWallpaperRenderer.SetWindowVisibleAsync))?
                .ReturnType);
        Assert.Null(contract.GetMethod("AttachTo"));
        Assert.Null(contract.GetMethod("SetWindowVisible"));
        Assert.Null(contract.GetMethod("EnterStaticPerformanceModeAsync"));
        Assert.Null(contract.GetMethod("ExitStaticPerformanceModeAsync"));
        Assert.Null(contract.GetMethod(
            "DestroyCompositionWindowForIsolationAsync"));
    }

    [Fact]
    public async Task DisconnectedClient_CachesAttachmentAndVisibilityWithoutStartingHost()
    {
        var client = new WallpaperRenderHostClient(
            Path.Combine(
                Path.GetTempPath(),
                "AnotherSurface.Tests",
                Guid.NewGuid().ToString("N")));
        var display = new DisplayInfo
        {
            Id = "DISPLAY1",
            Name = "Primary",
            Width = 1920,
            Height = 1080,
            IsPrimary = true
        };

        await client.AttachToAsync(
            new nint(0x1234),
            display,
            raisedDesktop: false,
            shellViewHandle: nint.Zero,
            backgroundWorkerHandle: nint.Zero,
            CancellationToken.None);
        await client.SetWindowVisibleAsync(
            false,
            CancellationToken.None);

        Assert.False(client.IsReady);

        await client.CloseSafelyAsync();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void FreshVisibleHost_AttachesHiddenUntilInitializationCompletes(
        bool hostAttached,
        bool desiredVisibility,
        bool expected)
    {
        Assert.Equal(
            expected,
            WallpaperRenderHostClient
                .ShouldAttachHiddenForInitialization(
                    hostAttached,
                    desiredVisibility));
    }

    [Fact]
    public async Task ReadAsync_CleanEofReturnsNull()
    {
        await using var stream = new MemoryStream();

        var message =
            await RenderHostWire.ReadAsync<RenderHostRequest>(
                stream,
                CancellationToken.None);

        Assert.Null(message);
    }

    [Theory]
    [InlineData(new byte[] { 8, 0 })]
    [InlineData(new byte[] { 8, 0, 0, 0, 123, 34, 97 })]
    public async Task ReadAsync_TruncatedFrameThrowsEndOfStream(
        byte[] frame)
    {
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => RenderHostWire.ReadAsync<RenderHostRequest>(
                stream,
                CancellationToken.None));
    }
}
