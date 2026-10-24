using System.Diagnostics;
using System.IO.Pipes;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WallpaperRenderHostProcessIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task PublishedApp_RenderHostAcceptsCloseAndExitsCleanly()
    {
        var executablePath = Environment.GetEnvironmentVariable(
            "VELLUME_RENDER_HOST_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        Assert.True(File.Exists(executablePath));
        var pipeName =
            $"vellume-render-test-{Environment.ProcessId}-" +
            Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeRenderHostTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "--wallpaper-render-host",
                    "--render-pipe",
                    pipeName,
                    "--render-owner",
                    Environment.ProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    "--render-data",
                    dataDirectory
                }
            }) ?? throw new InvalidOperationException(
                "RenderHost 테스트 프로세스를 시작하지 못했습니다.");

        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token);
            Assert.True(
                RenderHostPipeSecurity.IsExpectedClient(
                    pipe,
                    process.Id));
            await RenderHostWire.WriteAsync(
                pipe,
                new RenderHostRequest
                {
                    Command = RenderHostCommands.Close
                },
                timeout.Token);
            var response =
                await RenderHostWire.ReadAsync<RenderHostResponse>(
                    pipe,
                    timeout.Token);

            Assert.NotNull(response);
            Assert.True(response.Success, response.Error);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(3));
            }

            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
