using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Vellume.Core;

namespace Vellume.Windows;

/// <summary>
/// Controls the isolated C++ Direct3D renderer over a small versioned binary
/// protocol. Closing stdin is also a graceful shutdown signal, so Explorer is
/// never terminated when the renderer becomes unavailable.
/// </summary>
internal sealed class NativeWallpaperRendererClient : IWallpaperRenderer
{
    private const uint ProtocolMagic = 0x454D4C56;
    private const ushort ProtocolVersion = 1;
    private const int HeaderSize = 12;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    private readonly string _executablePath;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private Task<string>? _stderrTask;
    private NativeRendererState _state = NativeRendererState.Empty;
    private bool _visible = true;
    private bool _closed;

    internal NativeWallpaperRendererClient(string executablePath)
    {
        _executablePath = executablePath;
    }

    public nint Handle => new(unchecked((long)_state.Handle));

    public string BackendDisplayName =>
        string.IsNullOrWhiteSpace(_state.Backend)
            ? "C++ Direct3D 11"
            : _state.Backend;

    public bool IsReady =>
        IsProcessAlive() && _state.IsReady && NativeMethods.IsWindow(Handle);

    public string GetAttachmentDiagnostics() =>
        string.IsNullOrWhiteSpace(_state.Diagnostics)
            ? "backend=native-cpp, state=starting"
            : _state.Diagnostics;

    public Task AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken)
        => SendAsync(
            NativeCommand.Attach,
            writer =>
            {
                writer.Write(hostHandle.ToInt64());
                writer.Write(display.X);
                writer.Write(display.Y);
                writer.Write(display.Width);
                writer.Write(display.Height);
                writer.Write(raisedDesktop);
                writer.Write(_visible);
                writer.Write((ushort)0);
                writer.Write(shellViewHandle.ToInt64());
                writer.Write(backgroundWorkerHandle.ToInt64());
            },
            cancellationToken);

    public Task InitializeAndNavigateAsync(
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken)
        => SendAsync(
            NativeCommand.Initialize,
            writer =>
            {
                writer.Write((int)wallpaper.Kind);
                WritePlayback(writer, options);
                WriteString(writer, wallpaper.SourcePath);
            },
            cancellationToken);

    public Task CapturePreviewAsync(
        string outputPath,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "C++ Direct3D 렌더러의 진단 캡처는 아직 지원되지 않습니다.");

    public bool IsAttachedTo(nint hostHandle)
        => NativeMethods.IsWindow(Handle) &&
           NativeMethods.GetParent(Handle) == hostHandle;

    public Task SetWindowVisibleAsync(
        bool visible,
        CancellationToken cancellationToken)
    {
        _visible = visible;
        return SendAsync(
            NativeCommand.SetVisible,
            writer => writer.Write(visible),
            cancellationToken);
    }

    public Task PauseAsync()
        => SendAsync(
            NativeCommand.Pause,
            writePayload: null,
            CancellationToken.None);

    public Task ResumeAsync()
        => SendAsync(
            NativeCommand.Resume,
            writePayload: null,
            CancellationToken.None);

    public Task UpdatePlaybackAsync(PlaybackOptions options)
        => SendAsync(
            NativeCommand.UpdatePlayback,
            writer => WritePlayback(writer, options),
            CancellationToken.None);

    public async Task CloseSafelyAsync()
    {
        if (_closed)
        {
            return;
        }

        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return;
            }

            if (IsProcessAlive() && _input is not null && _output is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(3));
                    await SendFrameUnlockedAsync(
                            NativeCommand.Close,
                            [],
                            timeout.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Closing stdin below asks the native process to use the
                    // same deterministic window and graphics cleanup path.
                }
            }

            _input?.Dispose();
            _input = null;
            _output?.Dispose();
            _output = null;
            if (_process is { HasExited: false })
            {
                try
                {
                    await _process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(3))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Never force-kill the renderer. It owns no user data and
                    // observes the closed command stream asynchronously.
                }
            }
            _process?.Dispose();
            _process = null;
            _stderrTask = null;
            _state = NativeRendererState.Empty;
            _closed = true;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    internal static string? ResolveExecutablePath()
    {
        if (Environment.GetEnvironmentVariable(
                "VELLUME_DISABLE_NATIVE_RENDERER") == "1")
        {
            return null;
        }

        var overridePath = Environment.GetEnvironmentVariable(
            "VELLUME_NATIVE_RENDERER_EXECUTABLE");
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                AppContext.BaseDirectory,
                "Vellume.Renderer.Native.exe")
            : Path.GetFullPath(overridePath);
        return OperatingSystem.IsWindows() && File.Exists(path)
            ? path
            : null;
    }

    private async Task SendAsync(
        NativeCommand command,
        Action<BinaryWriter>? writePayload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GetTimeout(command));
        var lockTaken = false;
        try
        {
            await _requestLock.WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            lockTaken = true;
            EnsureProcess();

            using var payloadStream = new MemoryStream();
            using (var writer = new BinaryWriter(
                       payloadStream,
                       Encoding.UTF8,
                       leaveOpen: true))
            {
                writePayload?.Invoke(writer);
                writer.Flush();
            }

            await SendFrameUnlockedAsync(
                    command,
                    payloadStream.ToArray(),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            DisposeConnection();
            throw new TimeoutException(
                $"C++ 렌더러 명령 '{command}'의 응답 시간이 초과되었습니다.");
        }
        catch
        {
            if (!IsProcessAlive())
            {
                DisposeConnection();
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

    private async Task SendFrameUnlockedAsync(
        NativeCommand command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var input = _input ?? throw new EndOfStreamException(
            "C++ 렌더러 입력 스트림이 닫혔습니다.");
        var output = _output ?? throw new EndOfStreamException(
            "C++ 렌더러 출력 스트림이 닫혔습니다.");

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0, 4),
            ProtocolMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4, 2),
            ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(6, 2),
            (ushort)command);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8, 4),
            (uint)payload.Length);
        await input.WriteAsync(header, cancellationToken)
            .ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await input.WriteAsync(payload, cancellationToken)
                .ConfigureAwait(false);
        }
        await input.FlushAsync(cancellationToken)
            .ConfigureAwait(false);

        await output.ReadExactlyAsync(header, cancellationToken)
            .ConfigureAwait(false);
        var responseMagic = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(0, 4));
        var responseVersion = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(4, 2));
        var responseCommand = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(6, 2));
        var responseLength = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(8, 4));
        if (responseMagic != ProtocolMagic ||
            responseVersion != ProtocolVersion ||
            responseCommand != (ushort)command ||
            responseLength > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "C++ 렌더러가 잘못된 프로토콜 응답을 반환했습니다.");
        }

        var responsePayload = new byte[responseLength];
        await output.ReadExactlyAsync(
                responsePayload,
                cancellationToken)
            .ConfigureAwait(false);
        using var responseStream = new MemoryStream(
            responsePayload,
            writable: false);
        using var reader = new BinaryReader(
            responseStream,
            Encoding.UTF8,
            leaveOpen: false);
        var result = reader.ReadInt32();
        var state = new NativeRendererState(
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadByte() != 0,
            ReadReservedResponseBytes(reader),
            ReadString(reader),
            ReadString(reader));
        var error = ReadString(reader);
        _state = state;
        if (result != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "C++ 렌더러 명령을 수행하지 못했습니다."
                    : error);
        }
    }

    private void EnsureProcess()
    {
        if (IsProcessAlive() && _input is not null && _output is not null)
        {
            return;
        }

        DisposeConnection();
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "C++ 렌더러 실행 파일을 찾을 수 없습니다.",
                _executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "C++ 렌더러 프로세스를 시작하지 못했습니다.");
        _input = _process.StandardInput.BaseStream;
        _output = _process.StandardOutput.BaseStream;
        _stderrTask = _process.StandardError.ReadToEndAsync();
    }

    private bool IsProcessAlive()
    {
        try
        {
            return _process is { HasExited: false };
        }
        catch
        {
            return false;
        }
    }

    private void DisposeConnection()
    {
        _input?.Dispose();
        _input = null;
        _output?.Dispose();
        _output = null;
        _process?.Dispose();
        _process = null;
        _stderrTask = null;
        _state = NativeRendererState.Empty;
    }

    private static void WritePlayback(
        BinaryWriter writer,
        PlaybackOptions options)
    {
        writer.Write((int)options.Fit);
        writer.Write(Math.Clamp(options.Brightness, 0.25, 1.25));
        writer.Write(Math.Clamp(options.Contrast, 0.5, 1.5));
        writer.Write(Math.Clamp(options.Saturation, 0, 1.5));
        writer.Write(Math.Clamp(options.Volume, 0, 1));
        writer.Write(options.IsMuted);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt32();
        if (length > MaximumPayloadBytes ||
            length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException(
                "C++ 렌더러 문자열 응답이 손상되었습니다.");
        }
        return Encoding.UTF8.GetString(
            reader.ReadBytes(checked((int)length)));
    }

    private static ushort ReadReservedResponseBytes(BinaryReader reader)
    {
        reader.ReadByte();
        return reader.ReadUInt16();
    }

    private static TimeSpan GetTimeout(NativeCommand command)
        => command switch
        {
            NativeCommand.Attach => TimeSpan.FromSeconds(5),
            NativeCommand.Initialize => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(10)
        };

    private enum NativeCommand : ushort
    {
        Attach = 1,
        Initialize = 2,
        SetVisible = 3,
        Pause = 4,
        Resume = 5,
        UpdatePlayback = 6,
        Close = 7
    }

    private sealed record NativeRendererState(
        ulong Handle,
        ulong ParentHandle,
        bool IsReady,
        ushort Reserved,
        string Backend,
        string Diagnostics)
    {
        internal static NativeRendererState Empty { get; } = new(
            0,
            0,
            false,
            0,
            string.Empty,
            string.Empty);
    }
}
