using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using AnotherSurface.Core;

namespace AnotherSurface.Windows;

internal sealed class WallpaperRenderHostClient : IWallpaperRenderer
{
    private readonly string _renderDataDirectory;
    private readonly string _gpuSafetyDirectory;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private RenderHostAttachment? _attachment;
    private WallpaperItem? _wallpaper;
    private PlaybackOptions? _playbackOptions;
    private RenderHostState _state = RenderHostState.Empty;
    private bool _visible = true;
    private bool _hostAttached;
    private bool _closed;

    internal WallpaperRenderHostClient(string renderDataDirectory)
    {
        _renderDataDirectory = renderDataDirectory;
        var applicationDataDirectory =
            Path.GetDirectoryName(renderDataDirectory) ??
            renderDataDirectory;
        _gpuSafetyDirectory = Path.Combine(
            applicationDataDirectory,
            "GpuSafety");
    }

    public nint Handle => new(_state.Handle);
    public string BackendDisplayName =>
        string.IsNullOrWhiteSpace(_state.BackendDisplayName)
            ? "렌더 호스트 준비 중"
            : _state.BackendDisplayName;
    public bool IsReady =>
        _process is { HasExited: false } &&
        _state.IsReady && NativeMethods.IsWindow(Handle);

    public string GetAttachmentDiagnostics()
        => string.IsNullOrWhiteSpace(
            _state.AttachmentDiagnostics)
            ? "render-host=starting"
            : _state.AttachmentDiagnostics;

    public async Task AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _attachment = new RenderHostAttachment
        {
            HostHandle = hostHandle.ToInt64(),
            Display = display,
            RaisedDesktop = raisedDesktop,
            ShellViewHandle = shellViewHandle.ToInt64(),
            BackgroundWorkerHandle =
                backgroundWorkerHandle.ToInt64()
        };
        _hostAttached = false;

        if (_pipe?.IsConnected != true ||
            _process is not { HasExited: false })
        {
            return;
        }

        await SendAttachAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Attach,
                Attachment = _attachment,
                Visible = _visible
            },
            cancellationToken).ConfigureAwait(false);
        _hostAttached = true;
    }

    public async Task InitializeAndNavigateAsync(
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _wallpaper = wallpaper.Copy();
        _playbackOptions = options.Copy();

        var revealAfterInitialization =
            ShouldAttachHiddenForInitialization(
                _hostAttached,
                _visible);
        await EnsureAttachedAsync(
            cancellationToken,
            revealAfterInitialization
                ? false
                : null);
        await SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Initialize,
                Wallpaper = _wallpaper,
                Playback = _playbackOptions
            },
            cancellationToken);
        if (revealAfterInitialization)
        {
            await SetRenderHostVisibilityAsync(
                visible: true,
                cancellationToken: cancellationToken);
        }
    }

    public async Task CapturePreviewAsync(
        string outputPath,
        CancellationToken cancellationToken)
        => await SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Capture,
                OutputPath = outputPath
            },
            cancellationToken);

    public bool IsAttachedTo(nint hostHandle)
        => NativeMethods.IsWindow(Handle) &&
           NativeMethods.GetParent(Handle) == hostHandle;

    public async Task SetWindowVisibleAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _visible = visible;
        if (_closed ||
            !_hostAttached ||
            _pipe?.IsConnected != true ||
            _process is not { HasExited: false })
        {
            return;
        }

        await SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.SetVisible,
                Visible = visible
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task PauseAsync()
        => SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Pause
            },
            CancellationToken.None);

    public Task ResumeAsync()
        => SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Resume
            },
            CancellationToken.None);

    public async Task UpdatePlaybackAsync(PlaybackOptions options)
    {
        _playbackOptions = options.Copy();
        await SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.UpdatePlayback,
                Playback = _playbackOptions
            },
            CancellationToken.None);
    }

    public async Task CloseSafelyAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await _requestLock.WaitAsync();
        try
        {
            await CloseHostConnectionAsync(
                requestCleanClose: true);
            WindowsDesktopWallpaperLease.TryRecover(
                _gpuSafetyDirectory);
        }
        finally
        {
            _state = RenderHostState.Empty;
            _requestLock.Release();
            _requestLock.Dispose();
        }
    }

    internal static bool ShouldAttachHiddenForInitialization(
        bool hostAttached,
        bool desiredVisibility)
        => !hostAttached && desiredVisibility;

    private async Task EnsureAttachedAsync(
        CancellationToken cancellationToken,
        bool? initialVisibility = null)
    {
        if (_hostAttached)
        {
            return;
        }

        var attachment = _attachment ??
            throw new InvalidOperationException(
                "월페이퍼 RenderHost 연결 정보가 없습니다.");
        await SendAttachAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.Attach,
                Attachment = attachment,
                Visible = initialVisibility ?? _visible
            },
            cancellationToken);
        _hostAttached = true;
    }

    private async Task SetRenderHostVisibilityAsync(
        bool visible,
        CancellationToken cancellationToken)
        => await SendAsync(
            new RenderHostRequest
            {
                Command = RenderHostCommands.SetVisible,
                Visible = visible
            },
            cancellationToken).ConfigureAwait(false);

    private async Task<RenderHostResponse> SendAttachAsync(
        RenderHostRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // A timed-out response leaves a framed request stream unsafe to
            // reuse. Closing the pipe asks the child to clean itself up.
            DisposePipeAndProcess();
            throw new TimeoutException(
                "The wallpaper render host did not attach within 5 seconds.");
        }
    }

    private async Task<RenderHostResponse> SendAsync(
        RenderHostRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(GetCommandTimeout(request.Command));
        var commandToken = timeout.Token;
        var lockTaken = false;
        try
        {
            await _requestLock.WaitAsync(commandToken)
                .ConfigureAwait(false);
            lockTaken = true;
            await EnsureHostAsync(commandToken)
                .ConfigureAwait(false);
            var pipe = _pipe ??
                throw new InvalidOperationException(
                    "월페이퍼 렌더 호스트 파이프가 연결되지 않았습니다.");
            await RenderHostWire.WriteAsync(
                    pipe,
                    request,
                    commandToken)
                .ConfigureAwait(false);
            var response =
                await RenderHostWire.ReadAsync<RenderHostResponse>(
                        pipe,
                        commandToken)
                    .ConfigureAwait(false) ??
                throw new EndOfStreamException(
                    "월페이퍼 렌더 호스트가 응답 전에 종료되었습니다.");
            if (!response.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "월페이퍼 렌더 호스트 명령이 실패했습니다."
                        : response.Error);
            }

            if (response.State is not null)
            {
                _state = response.State;
            }
            return response;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            if (lockTaken)
            {
                // Cancellation after a frame was started makes the stream
                // unsafe to reuse because a late response could be mistaken
                // for the next command.
                DisposePipeAndProcess();
            }
            throw new TimeoutException(
                $"The wallpaper render host command '{request.Command}' timed out.");
        }
        catch (OperationCanceledException)
        {
            if (lockTaken)
            {
                DisposePipeAndProcess();
            }
            throw;
        }
        catch
        {
            if (_process?.HasExited != false)
            {
                DisposePipeAndProcess();
            }
            throw;
        }
        finally
        {
            if (lockTaken)
            {
                _requestLock.Release();
            }
        }
    }

    private static TimeSpan GetCommandTimeout(string command)
        => command switch
        {
            RenderHostCommands.Attach =>
                TimeSpan.FromSeconds(5),
            RenderHostCommands.Initialize =>
                TimeSpan.FromSeconds(30),
            RenderHostCommands.Capture =>
                TimeSpan.FromSeconds(20),
            _ => TimeSpan.FromSeconds(10)
        };

    private async Task EnsureHostAsync(
        CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true &&
            _process is { HasExited: false })
        {
            return;
        }

        DisposePipeAndProcess();
        var executablePath =
            Environment.GetEnvironmentVariable(
                "ANOTHER_SURFACE_RENDER_HOST_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Environment.ProcessPath;
        }
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Another Surface 렌더 호스트 실행 파일을 찾을 수 없습니다.",
                executablePath);
        }

        var pipeName =
            $"another-surface-render-{Environment.ProcessId}-" +
            Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(
            "--wallpaper-render-host");
        startInfo.ArgumentList.Add("--render-pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--render-owner");
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--render-data");
        startInfo.ArgumentList.Add(_renderDataDirectory);
        _process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "월페이퍼 렌더 호스트 프로세스를 시작하지 못했습니다.");

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await _pipe.WaitForConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            if (_process is null ||
                !RenderHostPipeSecurity.IsExpectedClient(
                    _pipe,
                    _process.Id))
            {
                throw new UnauthorizedAccessException(
                    "예상한 RenderHost 프로세스가 파이프에 연결하지 않았습니다.");
            }
        }
        catch
        {
            DisposePipeAndProcess();
            throw new TimeoutException(
                "월페이퍼 렌더 호스트가 8초 안에 연결되지 않았습니다.");
        }
    }

    private async Task CloseHostConnectionAsync(
        bool requestCleanClose)
    {
        var pipe = _pipe;
        if (requestCleanClose &&
            pipe?.IsConnected == true &&
            _process is { HasExited: false })
        {
            try
            {
                await RenderHostWire.WriteAsync(
                        pipe,
                        new RenderHostRequest
                        {
                            Command = RenderHostCommands.Close
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await RenderHostWire
                    .ReadAsync<RenderHostResponse>(
                        pipe,
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Closing the pipe below lets the child observe EOF and
                // perform the same renderer cleanup path.
            }
        }

        await WaitForRenderHostExitAsync()
            .ConfigureAwait(false);
        DisposePipeAndProcess();
    }

    private async Task WaitForRenderHostExitAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                await _process
                    .WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // The process owns no user data. Pipe disposal below requests
            // graceful shutdown without force-terminating it.
        }
    }

    private void DisposePipeAndProcess()
    {
        _pipe?.Dispose();
        _pipe = null;
        _process?.Dispose();
        _process = null;
        _hostAttached = false;
    }

}

/// <summary>
/// Runs inside the short-lived wallpaper process. Its process boundary is the
/// reliable way to release media decoding and GPU resources
/// for Stop (free memory).
/// </summary>
public static class WallpaperRenderHostServer
{
    public static async Task RunAsync(
        string pipeName,
        string renderDataDirectory,
        int ownerProcessId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException(
                "렌더 호스트 파이프 이름이 비어 있습니다.",
                nameof(pipeName));
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var connectTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        await pipe.ConnectAsync(connectTimeout.Token);
        if (!RenderHostPipeSecurity.IsExpectedServer(
                pipe,
                ownerProcessId))
        {
            throw new UnauthorizedAccessException(
                "예상한 Another Surface 제어 프로세스의 파이프가 아닙니다.");
        }

        WallpaperRendererWindow? renderer = null;
        RenderHostAttachment? attachment = null;
        var shutdownRequested = false;
        try
        {
            while (!shutdownRequested &&
                   !cancellationToken.IsCancellationRequested)
            {
                var request =
                    await RenderHostWire.ReadAsync<RenderHostRequest>(
                        pipe,
                        cancellationToken);
                if (request is null)
                {
                    break;
                }

                RenderHostResponse response;
                try
                {
                    renderer ??=
                        new WallpaperRendererWindow();
                    var booleanResult = true;
                    switch (request.Command)
                    {
                        case RenderHostCommands.Attach:
                            attachment = request.Attachment ??
                                throw new InvalidOperationException(
                                    "렌더 호스트 연결 정보가 없습니다.");
                            // Store the requested visibility before creating
                            // or reparenting the HWND so a hidden handoff does
                            // not briefly force the renderer on-screen.
                            renderer.SetWindowVisible(
                                request.Visible);
                            await AttachAsync(renderer, attachment, cancellationToken);
                            renderer.SetWindowVisible(
                                request.Visible);
                            break;

                        case RenderHostCommands.Initialize:
                            var wallpaper = request.Wallpaper ??
                                throw new InvalidOperationException(
                                    "월페이퍼 정보가 없습니다.");
                            var playback = request.Playback ??
                                new PlaybackOptions();
                            await renderer.InitializeAndNavigateAsync(
                                wallpaper,
                                playback,
                                cancellationToken);
                            break;

                        case RenderHostCommands.Capture:
                            await renderer.CapturePreviewAsync(
                                request.OutputPath ??
                                throw new InvalidOperationException(
                                    "캡처 출력 경로가 없습니다."),
                                cancellationToken);
                            break;

                        case RenderHostCommands.SetVisible:
                            renderer.SetWindowVisible(
                                request.Visible);
                            break;

                        case RenderHostCommands.Pause:
                            await renderer.PauseAsync();
                            break;

                        case RenderHostCommands.Resume:
                            await renderer.ResumeAsync();
                            break;

                        case RenderHostCommands.UpdatePlayback:
                            await renderer.UpdatePlaybackAsync(
                                request.Playback ??
                                new PlaybackOptions());
                            break;

                        case RenderHostCommands.Close:
                            await renderer.CloseSafelyAsync();
                            renderer = null;
                            shutdownRequested = true;
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"알 수 없는 렌더 호스트 명령: " +
                                request.Command);
                    }

                    response = RenderHostResponse.Ok(
                        CaptureState(renderer, attachment),
                        booleanResult);
                }
                catch (Exception exception)
                {
                    response = RenderHostResponse.Failed(
                        exception.Message,
                        CaptureState(renderer, attachment));
                }

                await RenderHostWire.WriteAsync(
                    pipe,
                    response,
                    cancellationToken);
            }
        }
        finally
        {
            if (renderer is not null)
            {
                await renderer.CloseSafelyAsync();
            }
        }
    }

    private static Task AttachAsync(
        WallpaperRendererWindow renderer,
        RenderHostAttachment attachment,
        CancellationToken cancellationToken)
    {
        var display = attachment.Display ??
            throw new InvalidOperationException(
                "렌더 호스트 모니터 정보가 없습니다.");
        return ((IWallpaperRenderer)renderer).AttachToAsync(
            new nint(attachment.HostHandle),
            display,
            attachment.RaisedDesktop,
            new nint(attachment.ShellViewHandle),
            new nint(attachment.BackgroundWorkerHandle),
            cancellationToken);
    }

    private static RenderHostState CaptureState(
        WallpaperRendererWindow? renderer,
        RenderHostAttachment? attachment)
        => renderer is null
            ? RenderHostState.Empty
            : new RenderHostState
            {
                Handle = renderer.Handle.ToInt64(),
                ParentHandle =
                    attachment?.HostHandle ?? 0,
                BackendDisplayName =
                    renderer.BackendDisplayName,
                IsReady = renderer.IsReady,
                AttachmentDiagnostics =
                    renderer.GetAttachmentDiagnostics()
            };
}

internal static class RenderHostCommands
{
    internal const string Attach = "attach";
    internal const string Initialize = "initialize";
    internal const string Capture = "capture";
    internal const string SetVisible = "set-visible";
    internal const string Pause = "pause";
    internal const string Resume = "resume";
    internal const string UpdatePlayback = "update-playback";
    internal const string Close = "close";
}

internal static class RenderHostPipeSecurity
{
    internal static bool IsExpectedClient(
        NamedPipeServerStream pipe,
        int expectedProcessId)
        => expectedProcessId > 0 &&
           GetNamedPipeClientProcessId(
               pipe.SafePipeHandle,
               out var processId) &&
           processId == unchecked((uint)expectedProcessId);

    internal static bool IsExpectedServer(
        NamedPipeClientStream pipe,
        int expectedProcessId)
        => expectedProcessId > 0 &&
           GetNamedPipeServerProcessId(
               pipe.SafePipeHandle,
               out var processId) &&
           processId == unchecked((uint)expectedProcessId);

    // Authenticates the process at the other end of the private render pipe.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    // Prevents a render child from accepting a pipe owned by another process.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}

internal sealed class RenderHostRequest
{
    public string Command { get; set; } = string.Empty;
    public RenderHostAttachment? Attachment { get; set; }
    public WallpaperItem? Wallpaper { get; set; }
    public PlaybackOptions? Playback { get; set; }
    public string? OutputPath { get; set; }
    public bool Visible { get; set; }
}

internal sealed class RenderHostAttachment
{
    public long HostHandle { get; set; }
    public DisplayInfo? Display { get; set; }
    public bool RaisedDesktop { get; set; }
    public long ShellViewHandle { get; set; }
    public long BackgroundWorkerHandle { get; set; }
}

internal sealed class RenderHostState
{
    internal static RenderHostState Empty => new();

    public long Handle { get; set; }
    public long ParentHandle { get; set; }
    public string BackendDisplayName { get; set; } =
        string.Empty;
    public bool IsReady { get; set; }
    public string AttachmentDiagnostics { get; set; } =
        string.Empty;
}

internal sealed class RenderHostResponse
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public bool BooleanResult { get; set; }
    public RenderHostState? State { get; set; }

    internal static RenderHostResponse Ok(
        RenderHostState state,
        bool booleanResult)
        => new()
        {
            Success = true,
            BooleanResult = booleanResult,
            State = state
        };

    internal static RenderHostResponse Failed(
        string error,
        RenderHostState state)
        => new()
        {
            Success = false,
            Error = error,
            State = state
        };
}

internal static class RenderHostWire
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            Options);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                "렌더 호스트 메시지가 허용 크기를 초과했습니다.");
        }

        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(
            header,
            cancellationToken);
        await stream.WriteAsync(
            payload,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<T?> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var headerRead = await ReadExactlyOrEofAsync(
            stream,
            header,
            cancellationToken);
        if (!headerRead)
        {
            return default;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                "렌더 호스트 메시지 길이가 잘못되었습니다.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEofAsync(
                stream,
                payload,
                cancellationToken))
        {
            throw new EndOfStreamException(
                "렌더 호스트 메시지가 중간에 끊겼습니다.");
        }

        return JsonSerializer.Deserialize<T>(
            payload,
            Options);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                return offset == 0
                    ? false
                    : throw new EndOfStreamException();
            }

            offset += read;
        }

        return true;
    }
}
