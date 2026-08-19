using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellume.Core;

namespace Vellume.Windows;

/// <summary>
/// Receives local Codex hook status messages without requiring the hook
/// process to write outside its sandbox.
/// </summary>
internal sealed class CodexCompanionPipeServer : IAsyncDisposable
{
    public const string PipeName = "Vellume.Codex.Status.v1";

    private const int MaximumMessageLength = 64 * 1024;
    private readonly string _statusPath;
    private readonly IAppLogger _logger;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _startGate = new();
    private Task? _listenTask;
    private bool _disposed;

    public CodexCompanionPipeServer(
        string statusPath,
        IAppLogger logger,
        string pipeName = PipeName)
    {
        _statusPath = statusPath;
        _logger = logger;
        _pipeName = pipeName;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_startGate)
        {
            _listenTask ??= ListenAsync(_cancellation.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync();
        try
        {
            if (_listenTask is not null)
            {
                await _listenTask;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown while waiting for the next hook connection.
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                var message = await ReadMessageAsync(
                    reader,
                    cancellationToken);
                if (message is not null)
                {
                    await PersistAsync(message, cancellationToken);
                }
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 ||
                    consecutiveFailures % 6 == 0)
                {
                    await _logger.ErrorAsync(
                        "Codex 컴패니언 로컬 연결 처리에 실패했습니다. " +
                        "백오프 후 다시 시도합니다.",
                        exception);
                }
                try
                {
                    var delaySeconds = Math.Min(
                        30,
                        Math.Pow(
                            2,
                            Math.Min(5, consecutiveFailures - 1)));
                    await Task.Delay(
                        TimeSpan.FromSeconds(delaySeconds),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        try
        {
            var security = new PipeSecurity();
            var readWrite = PipeAccessRights.ReadWrite |
                            PipeAccessRights.CreateNewInstance;
            security.AddAccessRule(
                new PipeAccessRule(
                    new SecurityIdentifier(
                        WellKnownSidType.WorldSid,
                        null),
                    readWrite,
                    AccessControlType.Allow));
            security.AddAccessRule(
                new PipeAccessRule(
                    new SecurityIdentifier("S-1-15-2-1"),
                    readWrite,
                    AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                security);
        }
        catch (UnauthorizedAccessException)
        {
            // Some Windows policies reject a broad pipe ACL. The
            // current-user pipe still supports normal local hook clients.
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096);
        }
    }

    private static async Task<string?> ReadMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var message = new StringBuilder();
        while (message.Length <= MaximumMessageLength)
        {
            var count = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (count == 0)
            {
                break;
            }

            var newline = Array.IndexOf(buffer, '\n', 0, count);
            if (newline >= 0)
            {
                message.Append(buffer, 0, newline);
                break;
            }

            message.Append(buffer, 0, count);
        }

        if (message.Length == 0 ||
            message.Length > MaximumMessageLength)
        {
            return null;
        }

        var text = message.ToString().TrimEnd('\r');
        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? text
            : null;
    }

    private async Task PersistAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statusPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath =
            $"{_statusPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var mergedMessage = await MergeRecentProjectsAsync(
                message,
                cancellationToken);
            await File.WriteAllTextAsync(
                temporaryPath,
                mergedMessage,
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(
                temporaryPath,
                _statusPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<string> MergeRecentProjectsAsync(
        string message,
        CancellationToken cancellationToken)
    {
        JsonObject? incoming;
        try
        {
            incoming = JsonNode.Parse(message) as JsonObject;
        }
        catch (JsonException)
        {
            return message;
        }

        if (incoming?["projects"] is not JsonArray incomingProjects ||
            !File.Exists(_statusPath))
        {
            return message;
        }

        try
        {
            var previousText = await File.ReadAllTextAsync(
                _statusPath,
                cancellationToken);
            var previous = JsonNode.Parse(previousText) as JsonObject;
            if (previous?["projects"] is not JsonArray previousProjects)
            {
                return message;
            }

            var merged = new JsonArray();
            var projectIds = new HashSet<string>(
                StringComparer.Ordinal);
            AddDistinctProjects(
                incomingProjects,
                merged,
                projectIds);
            AddDistinctProjects(
                previousProjects,
                merged,
                projectIds);
            while (merged.Count > 3)
            {
                merged.RemoveAt(merged.Count - 1);
            }

            incoming["projects"] = merged;
            return incoming.ToJsonString();
        }
        catch (JsonException)
        {
            return message;
        }
        catch (IOException)
        {
            return message;
        }
    }

    private static void AddDistinctProjects(
        JsonArray source,
        JsonArray destination,
        ISet<string> projectIds)
    {
        foreach (var node in source)
        {
            if (node is not JsonObject project)
            {
                continue;
            }

            var id = project["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) ||
                !projectIds.Add(id))
            {
                continue;
            }

            destination.Add(project.DeepClone());
        }
    }
}
