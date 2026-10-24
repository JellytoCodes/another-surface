using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellume.Windows;

internal sealed record CodexProjectSnapshot(
    string Id,
    string State,
    string Title,
    string Phase,
    string Workspace,
    DateTimeOffset UpdatedAtUtc);

internal sealed record CodexCompanionSnapshot(
    string State,
    string Headline,
    string Detail,
    string Workspace,
    string Model,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CodexProjectSnapshot> Projects,
    CodexUsageSnapshot Usage)
{
    public static CodexCompanionSnapshot Disconnected()
        => new(
            "disconnected",
            "CODEX 연결 안 됨",
            "Codex Desktop이 실행되면 자동으로 연결됩니다",
            string.Empty,
            string.Empty,
            DateTimeOffset.MinValue,
            [],
            CodexUsageSnapshot.Unavailable());
}

internal sealed class CodexCompanionStatusReader : IDisposable
{
    private static readonly TimeSpan HookFreshness =
        TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LocalActivityWindow =
        TimeSpan.FromSeconds(6);
    private const long ProcessRefreshIntervalMilliseconds = 1_000;
    private readonly string _statusPath;
    private readonly FileSystemWatcher? _statusWatcher;
    private readonly FileSystemWatcher? _sessionWatcher;
    private readonly System.Threading.Timer _activitySettledTimer;
    private readonly CodexAccountUsageReader _usageReader = new();
    private readonly SemaphoreSlim _usageLock = new(1, 1);
    private readonly Func<bool> _isCodexRunning;
    private readonly bool _trackCodexProcesses;
    private readonly Func<Process[]> _getCodexProcesses;
    private readonly Func<long> _getTickCount;
    private readonly object _processGate = new();
    private readonly Dictionary<int, Process> _trackedProcesses = [];
    private bool _hasUntrackedRunningProcess;
    private long? _lastProcessRefreshTick;
    private CodexUsageSnapshot _lastUsage =
        CodexUsageSnapshot.Unavailable();
    private DateTimeOffset _lastUsageAttemptUtc =
        DateTimeOffset.MinValue;
    private DateTimeOffset _lastUsageStopEventUtc =
        DateTimeOffset.MinValue;
    private long _lastLocalActivityUtcTicks;
    private bool _disposed;

    public CodexCompanionStatusReader(
        string statusPath,
        Func<bool>? isCodexRunning = null,
        string? codexHome = null,
        Func<Process[]>? getCodexProcesses = null,
        Func<long>? getTickCount = null)
    {
        _statusPath = statusPath;
        _isCodexRunning =
            isCodexRunning ?? IsTrackedCodexRunning;
        _trackCodexProcesses = isCodexRunning is null;
        _getCodexProcesses = getCodexProcesses ??
            (() => Process.GetProcessesByName("codex"));
        _getTickCount = getTickCount ?? (() => Environment.TickCount64);
        _activitySettledTimer = new System.Threading.Timer(
            ActivitySettled,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        var directory = Path.GetDirectoryName(statusPath);
        var fileName = Path.GetFileName(statusPath);
        if (!string.IsNullOrWhiteSpace(directory) &&
            !string.IsNullOrWhiteSpace(fileName))
        {
            Directory.CreateDirectory(directory);
            _statusWatcher = new FileSystemWatcher(
                directory,
                fileName)
            {
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _statusWatcher.Changed += WatcherOnStatusChanged;
            _statusWatcher.Created += WatcherOnStatusChanged;
            _statusWatcher.Deleted += WatcherOnStatusChanged;
            _statusWatcher.Renamed += WatcherOnStatusRenamed;
        }

        var resolvedCodexHome =
            codexHome ??
            Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                ".codex");
        var sessionsDirectory = Path.Combine(
            resolvedCodexHome,
            "sessions");
        if (Directory.Exists(sessionsDirectory))
        {
            var latestWrite = GetLatestSessionWriteUtc(
                sessionsDirectory);
            Interlocked.Exchange(
                ref _lastLocalActivityUtcTicks,
                latestWrite.UtcTicks);
            _sessionWatcher = new FileSystemWatcher(
                sessionsDirectory,
                "*.jsonl")
            {
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _sessionWatcher.Changed += SessionWatcherOnChanged;
            _sessionWatcher.Created += SessionWatcherOnChanged;
            _sessionWatcher.Renamed += SessionWatcherOnRenamed;
        }

        RefreshTrackedCodexProcesses();
    }

    public event EventHandler? Changed;

    public async Task<CodexCompanionSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        var codexRunning = _isCodexRunning();
        var lastLocalActivity = GetLastLocalActivityUtc();

        if (File.Exists(_statusPath))
        {
            try
            {
                await using var stream = new FileStream(
                    _statusPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    useAsync: true);
                var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);
                using (document)
                {
                    var root = document.RootElement;
                    DateTimeOffset updatedAt =
                        File.GetLastWriteTimeUtc(_statusPath);
                    if (root.TryGetProperty(
                            "updatedAtUtc",
                            out var updatedElement) &&
                        DateTimeOffset.TryParse(
                            updatedElement.GetString(),
                            out var parsedUpdatedAt))
                    {
                        updatedAt = parsedUpdatedAt;
                    }

                    var hookIsFresh =
                        DateTimeOffset.UtcNow - updatedAt <=
                        HookFreshness;
                    var localActivitySupersedesHook =
                        lastLocalActivity >
                        updatedAt + LocalActivityWindow;
                    if (hookIsFresh &&
                        !localActivitySupersedesHook)
                    {
                        var eventName = GetString(
                            root,
                            "eventName",
                            string.Empty);
                        var usage = await GetUsageAsync(
                            string.Equals(
                                eventName,
                                "Stop",
                                StringComparison.OrdinalIgnoreCase) &&
                            updatedAt > _lastUsageStopEventUtc,
                            updatedAt,
                            cancellationToken);
                        return new CodexCompanionSnapshot(
                            GetString(root, "state", "ready"),
                            LocalizeHeadline(
                                GetString(
                                    root,
                                    "headline",
                                    "CODEX 대기 중")),
                            LocalizeDetail(
                                GetString(
                                    root,
                                    "detail",
                                    "새 작업을 기다리는 중")),
                            GetString(
                                root,
                                "workspace",
                                string.Empty),
                            GetString(
                                root,
                                "model",
                                string.Empty),
                            updatedAt,
                            ReadProjects(root),
                            usage);
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A partial or obsolete Hook file must not disable the
                // automatic Codex Desktop presence fallback.
            }
        }

        if (codexRunning)
        {
            var activityAge =
                DateTimeOffset.UtcNow - lastLocalActivity;
            if (lastLocalActivity != DateTimeOffset.MinValue &&
                activityAge <= LocalActivityWindow)
            {
                return new CodexCompanionSnapshot(
                    "working",
                    "CODEX 작업 중",
                    "로컬 세션 활동을 감지했습니다",
                    string.Empty,
                    string.Empty,
                    lastLocalActivity,
                    [],
                    _lastUsage);
            }

            return new CodexCompanionSnapshot(
                "ready",
                "CODEX 연결됨",
                "Codex Desktop 자동 감지 · 세부 단계는 선택 연결",
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow,
                [],
                await GetUsageAsync(
                    false,
                    DateTimeOffset.MinValue,
                    cancellationToken));
        }

        return CodexCompanionSnapshot.Disconnected();
    }

    private async Task<CodexUsageSnapshot> GetUsageAsync(
        bool forceRefresh,
        DateTimeOffset stopEventUtc,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var minimumRetry = _lastUsage.IsAvailable
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(2);
        if (!forceRefresh &&
            now - _lastUsageAttemptUtc < minimumRetry)
        {
            return _lastUsage;
        }

        await _usageLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh &&
                now - _lastUsageAttemptUtc < minimumRetry)
            {
                return _lastUsage;
            }

            _lastUsageAttemptUtc = now;
            var refreshed = await _usageReader.ReadAsync(
                cancellationToken);
            if (refreshed.IsAvailable)
            {
                _lastUsage = refreshed;
            }
            if (forceRefresh)
            {
                _lastUsageStopEventUtc = stopEventUtc;
            }

            return _lastUsage;
        }
        finally
        {
            _usageLock.Release();
        }
    }

    private static IReadOnlyList<CodexProjectSnapshot> ReadProjects(
        JsonElement root)
    {
        if (!root.TryGetProperty(
                "projects",
                out var projects) ||
            projects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var snapshots = new List<CodexProjectSnapshot>(3);
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(project, "id", string.Empty);
            var title = GetString(
                project,
                "title",
                "이름 없는 프로젝트");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = title;
            }

            DateTimeOffset updatedAt = DateTimeOffset.MinValue;
            if (project.TryGetProperty(
                    "updatedAtUtc",
                    out var updatedElement))
            {
                DateTimeOffset.TryParse(
                    updatedElement.GetString(),
                    out updatedAt);
            }

            snapshots.Add(new CodexProjectSnapshot(
                id,
                GetString(project, "state", "working"),
                title,
                GetString(project, "phase", "작업 중"),
                GetString(project, "workspace", string.Empty),
                updatedAt));
            if (snapshots.Count == 3)
            {
                break;
            }
        }

        return snapshots;
    }

    private static DateTimeOffset GetLatestSessionWriteUtc(
        string sessionsDirectory)
    {
        try
        {
            var latest = Directory
                .EnumerateFiles(
                    sessionsDirectory,
                    "*.jsonl",
                    SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return latest == DateTime.MinValue
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(latest, TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private DateTimeOffset GetLastLocalActivityUtc()
    {
        var ticks = Interlocked.Read(
            ref _lastLocalActivityUtcTicks);
        return ticks <= 0
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private bool IsTrackedCodexRunning()
    {
        RefreshTrackedCodexProcesses();
        lock (_processGate)
        {
            return _trackedProcesses.Count > 0 || _hasUntrackedRunningProcess;
        }
    }

    private void RefreshTrackedCodexProcesses()
    {
        if (!_trackCodexProcesses || _disposed)
        {
            return;
        }

        lock (_processGate)
        {
            var now = _getTickCount();
            if (_disposed ||
                _lastProcessRefreshTick is { } previous &&
                now - previous < ProcessRefreshIntervalMilliseconds)
            {
                return;
            }
            // Gate the enumeration itself, not just its Changed event.
            // Session writes and status reads share the same short cache.
            _lastProcessRefreshTick = now;
            Process[] processes;
            try
            {
                processes = _getCodexProcesses();
            }
            catch
            {
                return;
            }

            _hasUntrackedRunningProcess = false;
            foreach (var process in processes)
            {
                if (_trackedProcesses.ContainsKey(process.Id))
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += CodexProcessOnExited;
                    if (process.HasExited)
                    {
                        process.Exited -= CodexProcessOnExited;
                        process.Dispose();
                        continue;
                    }

                    _trackedProcesses[process.Id] = process;
                }
                catch
                {
                    // Presence must still work when Windows permits the
                    // query but denies an exit-event subscription.
                    try
                    {
                        _hasUntrackedRunningProcess |= !process.HasExited;
                    }
                    catch
                    {
                    }
                    process.Dispose();
                }
            }
        }
    }

    private void CodexProcessOnExited(
        object? sender,
        EventArgs eventArgs)
    {
        if (sender is Process process)
        {
            lock (_processGate)
            {
                if (_disposed)
                {
                    return;
                }
                _trackedProcesses.Remove(process.Id);
                process.Exited -= CodexProcessOnExited;
                process.Dispose();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RecordLocalActivity()
    {
        var now = DateTimeOffset.UtcNow;
        var previousTicks = Interlocked.Exchange(
            ref _lastLocalActivityUtcTicks,
            now.UtcTicks);
        var previous = previousTicks <= 0
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(previousTicks, TimeSpan.Zero);
        RefreshTrackedCodexProcesses();
        _activitySettledTimer.Change(
            LocalActivityWindow,
            Timeout.InfiniteTimeSpan);
        if (now - previous > LocalActivityWindow)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ActivitySettled(object? state)
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SessionWatcherOnChanged(
        object sender,
        FileSystemEventArgs eventArgs)
        => RecordLocalActivity();

    private void SessionWatcherOnRenamed(
        object sender,
        RenamedEventArgs eventArgs)
        => RecordLocalActivity();

    private static string GetString(
        JsonElement root,
        string name,
        string fallback)
        => root.TryGetProperty(name, out var element) &&
           element.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : fallback;

    private static string LocalizeHeadline(string value)
        => value.ToUpperInvariant() switch
        {
            "CODEX BRIDGE OFFLINE" => "CODEX 연결 안 됨",
            "CODEX READY" => "CODEX 대기 중",
            "CODEX ONLINE" => "CODEX 연결됨",
            "THINKING" => "생각하는 중",
            "BUILDING" => "빌드하는 중",
            "EDITING" => "편집하는 중",
            "USING A TOOL" => "도구 사용 중",
            "TASK COMPLETE" => "작업 완료",
            "CODEX OFFLINE" => "CODEX 오프라인",
            _ => value
        };

    private static string LocalizeDetail(string value)
        => value switch
        {
            "Connect once from Vellume settings" =>
                "Codex Desktop이 실행되면 자동으로 연결됩니다",
            "Waiting for a task" => "새 작업을 기다리는 중",
            "Session connected" => "세션이 연결되었습니다",
            "Planning the next move" => "다음 작업을 계획하는 중",
            "Running a local command" => "로컬 명령을 실행하는 중",
            "Updating project files" => "프로젝트 파일을 수정하는 중",
            "Ready for the next request" => "다음 요청을 기다리는 중",
            "Session ended" => "세션이 종료되었습니다",
            _ => value
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activitySettledTimer.Dispose();
        if (_statusWatcher is not null)
        {
            _statusWatcher.EnableRaisingEvents = false;
            _statusWatcher.Changed -= WatcherOnStatusChanged;
            _statusWatcher.Created -= WatcherOnStatusChanged;
            _statusWatcher.Deleted -= WatcherOnStatusChanged;
            _statusWatcher.Renamed -= WatcherOnStatusRenamed;
            _statusWatcher.Dispose();
        }

        if (_sessionWatcher is not null)
        {
            _sessionWatcher.EnableRaisingEvents = false;
            _sessionWatcher.Changed -= SessionWatcherOnChanged;
            _sessionWatcher.Created -= SessionWatcherOnChanged;
            _sessionWatcher.Renamed -= SessionWatcherOnRenamed;
            _sessionWatcher.Dispose();
        }

        lock (_processGate)
        {
            foreach (var process in _trackedProcesses.Values)
            {
                process.Exited -= CodexProcessOnExited;
                process.Dispose();
            }
            _trackedProcesses.Clear();
            _hasUntrackedRunningProcess = false;
        }
    }

    private void WatcherOnStatusChanged(
        object sender,
        FileSystemEventArgs eventArgs)
        => Changed?.Invoke(this, EventArgs.Empty);

    private void WatcherOnStatusRenamed(
        object sender,
        RenamedEventArgs eventArgs)
        => Changed?.Invoke(this, EventArgs.Empty);
}

public static class CodexCompanionBridgeMaintenance
{
    public static Task<string> InstallForCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var bridgeDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Vellume",
            "Codex");
        var statusPath = Path.Combine(
            bridgeDirectory,
            "status.json");
        var installer = new CodexCompanionBridgeInstaller(
            bridgeDirectory,
            statusPath,
            CodexCompanionPipeServer.PipeName);
        return installer.InstallAsync(cancellationToken);
    }
}

internal sealed class CodexCompanionBridgeInstaller
{
    private const string HookDescription =
        "Vellume Codex 컴패니언 상태 연결";
    private static readonly string[] HookEvents =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "Stop"
    ];
    private static readonly string[] RetiredHookEvents =
    [
        // Process exit is detected locally. Keeping this fifth command hook
        // caused a permanent "one hook needs review" state on installations
        // where the desktop hook browser exposed only the four turn events.
        "SessionEnd"
    ];
    private readonly string _bridgeDirectory;
    private readonly string _statusPath;
    private readonly string? _pipeName;

    public CodexCompanionBridgeInstaller(
        string bridgeDirectory,
        string statusPath,
        string? pipeName = null)
    {
        _bridgeDirectory = bridgeDirectory;
        _statusPath = statusPath;
        _pipeName = pipeName;
    }

    public async Task<string> InstallAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_bridgeDirectory);
        var scriptPath = Path.Combine(
            _bridgeDirectory,
            "vellume-codex-hook.ps1");
        await WriteTextIfChangedAsync(
            scriptPath,
            BuildHookScript(_statusPath, _pipeName),
            // Windows PowerShell 5.1은 BOM이 있어야 한글 UTF-8을 확실히 읽습니다.
            new UTF8Encoding(true),
            cancellationToken);

        var codexHome = Environment.GetEnvironmentVariable(
                            "CODEX_HOME") ??
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.UserProfile),
                            ".codex");
        Directory.CreateDirectory(codexHome);
        var hooksPath = Path.Combine(codexHome, "hooks.json");
        var root = await ReadHooksAsync(
            hooksPath,
            cancellationToken);
        root["description"] ??= HookDescription;
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var shellPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        // Codex command hooks parse the executable token themselves on
        // Windows. A leading quoted executable path is treated as part of
        // the file name and every hook reports "Failed". This system path
        // contains no spaces, so leave only the script argument quoted.
        var command =
            $"{shellPath} -NoProfile -NonInteractive " +
            "-ExecutionPolicy Bypass " +
            $"-File \"{scriptPath}\"";
        foreach (var eventName in RetiredHookEvents)
        {
            if (hooks[eventName] is not JsonArray retiredGroups)
            {
                continue;
            }

            RemoveExistingVellumeHandlers(
                retiredGroups,
                scriptPath);
            if (retiredGroups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        foreach (var eventName in HookEvents)
        {
            var groups = hooks[eventName] as JsonArray;
            if (groups is null)
            {
                groups = [];
                hooks[eventName] = groups;
            }

            RemoveExistingVellumeHandlers(groups, scriptPath);
            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["commandWindows"] = command,
                ["timeout"] = 3
            };
            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(handler)
            });
        }

        await WriteTextIfChangedAsync(
            hooksPath,
            root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            new UTF8Encoding(false),
            cancellationToken);
        return hooksPath;
    }

    private static async Task WriteTextIfChangedAsync(
        string path,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var current = await File.ReadAllTextAsync(
                path,
                cancellationToken);
            if (string.Equals(
                    current,
                    content,
                    StringComparison.Ordinal))
            {
                var preamble = encoding.GetPreamble();
                if (preamble.Length == 0 ||
                    await StartsWithPreambleAsync(
                        path,
                        preamble,
                        cancellationToken))
                {
                    return;
                }
            }
        }

        var temporaryPath =
            $"{path}.{Guid.NewGuid():N}.vellume.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                encoding,
                cancellationToken);
            File.Move(
                temporaryPath,
                path,
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

    private static async Task<bool> StartsWithPreambleAsync(
        string path,
        byte[] preamble,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: preamble.Length,
            useAsync: true);
        var buffer = new byte[preamble.Length];
        var bytesRead = await stream.ReadAsync(
            buffer,
            cancellationToken);
        return bytesRead == preamble.Length &&
               buffer.AsSpan().SequenceEqual(preamble);
    }

    private static async Task<JsonObject> ReadHooksAsync(
        string hooksPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(hooksPath))
        {
            return new JsonObject();
        }

        var text = await File.ReadAllTextAsync(
            hooksPath,
            cancellationToken);
        try
        {
            return JsonNode.Parse(text) as JsonObject ??
                   throw new InvalidDataException(
                        "Codex hooks.json의 최상위 값은 JSON 객체여야 합니다.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Codex hooks.json이 올바른 JSON이 아닙니다. " +
                "Vellume은 파일을 변경하지 않았습니다.",
                exception);
        }
    }

    private static void RemoveExistingVellumeHandlers(
        JsonArray groups,
        string scriptPath)
    {
        foreach (var group in groups.OfType<JsonObject>().ToArray())
        {
            if (group["hooks"] is not JsonArray handlers)
            {
                continue;
            }

            foreach (var handler in handlers
                         .OfType<JsonObject>()
                         .Where(handler =>
                             ContainsScriptPath(
                                 handler["command"],
                                 scriptPath) ||
                             ContainsScriptPath(
                                 handler["commandWindows"],
                                 scriptPath))
                         .ToArray())
            {
                handlers.Remove(handler);
            }

            if (handlers.Count == 0)
            {
                groups.Remove(group);
            }
        }
    }

    private static bool ContainsScriptPath(
        JsonNode? command,
        string scriptPath)
        => command is JsonValue &&
           command.GetValue<string>().Contains(
               scriptPath,
               StringComparison.OrdinalIgnoreCase);

    private static string BuildHookScript(
        string statusPath,
        string? pipeName)
    {
        var escapedStatusPath = statusPath.Replace("'", "''");
        var escapedPipeName = pipeName?.Replace("'", "''");
        var pipeDelivery = string.IsNullOrWhiteSpace(escapedPipeName)
            ? string.Empty
            : $$"""
                $pipe = $null
                $writer = $null
                try {
                    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
                        '.',
                        '{{escapedPipeName}}',
                        [IO.Pipes.PipeDirection]::Out,
                        [IO.Pipes.PipeOptions]::Asynchronous)
                    $pipe.Connect(250)
                    $writer = [IO.StreamWriter]::new(
                        $pipe,
                        [Text.UTF8Encoding]::new($false),
                        1024,
                        $true)
                    $writer.WriteLine($statusJson)
                    $writer.Flush()
                    $delivered = $true
                } catch {
                    if ($env:VELLUME_CODEX_HOOK_DEBUG -eq '1') {
                        [Console]::Error.WriteLine($_.Exception.ToString())
                    }
                    $delivered = $false
                } finally {
                    if ($writer) {
                        $writer.Dispose()
                    }
                    if ($pipe) {
                        $pipe.Dispose()
                    }
                }
                """;
        return $$"""
            $ErrorActionPreference = 'Stop'
            $eventName = ''
            try {
                $statusPath = '{{escapedStatusPath}}'
                $raw = [Console]::In.ReadToEnd()
                $payload = $raw | ConvertFrom-Json
                $eventName = [string]$payload.hook_event_name
                $sessionId = [string]$payload.session_id
                $projectPath = [string]$payload.cwd
                $workspace = ''
                if (
                    -not [string]::IsNullOrWhiteSpace($projectPath)
                ) {
                    $projectPath = [IO.Path]::GetFullPath(
                        $projectPath).TrimEnd('\', '/')
                    $workspace = Split-Path -Leaf $projectPath
                    if ([string]::IsNullOrWhiteSpace($workspace)) {
                        $workspace = $projectPath
                    }
                }
                $projectId = if (
                    -not [string]::IsNullOrWhiteSpace($projectPath)
                ) {
                    'project:' + $projectPath.ToLowerInvariant()
                } elseif (
                    -not [string]::IsNullOrWhiteSpace($sessionId)
                ) {
                    'session:' + $sessionId
                } else {
                    'codex-local'
                }
                $projectTitle = if (
                    [string]::IsNullOrWhiteSpace($workspace)
                ) {
                    'Codex'
                } else {
                    $workspace
                }
                $model = [string]$payload.model
                $now = [DateTimeOffset]::UtcNow.ToString('O')
                $projects = @()
                try {
                    if (Test-Path -LiteralPath $statusPath) {
                        $previous = Get-Content `
                            -LiteralPath $statusPath `
                            -Raw `
                            -Encoding UTF8 | ConvertFrom-Json
                        if (
                            $previous.schemaVersion -eq 3 -and
                            $previous.projects
                        ) {
                            $projects = @($previous.projects)
                        }
                    }
                } catch {
                    if ($env:VELLUME_CODEX_HOOK_DEBUG -eq '1') {
                        [Console]::Error.WriteLine(
                            '기존 프로젝트 상태 읽기 실패: ' +
                            $_.Exception.ToString())
                    }
                    $projects = @()
                }

                function New-Project() {
                    return [pscustomobject][ordered]@{
                        id = $projectId
                        sessionId = $sessionId
                        state = 'working'
                        title = $projectTitle
                        phase = '요청 분석 중'
                        workspace = $workspace
                        model = $model
                        updatedAtUtc = $now
                    }
                }

                function Get-ToolPhase(
                    [string]$tool
                ) {
                    if ($tool -eq 'Bash') {
                        $command = ''
                        if (
                            $payload.tool_input -and
                            $payload.tool_input.command
                        ) {
                            $command = [string]$payload.tool_input.command
                        }
                        if ($command -match 'dotnet\s+(test|vstest)') {
                            return '테스트 검증 중'
                        }
                        if ($command -match 'dotnet\s+(build|publish)') {
                            return 'Vellume 빌드 중'
                        }
                        if ($command -match '(^|\s)rg(\.exe)?(\s|$)') {
                            return '프로젝트 검색 중'
                        }
                        if ($command -match '(^|\s)git(\.exe)?(\s|$)') {
                            return 'Git 상태 확인 중'
                        }
                        return '로컬 명령 실행 중'
                    }
                    if ($tool -eq 'apply_patch') {
                        return '프로젝트 파일 수정 중'
                    }
                    if ($tool -eq 'update_plan') {
                        return '작업 계획 갱신 중'
                    }
                    if ($tool -eq 'tool_search') {
                        return '연결 도구 확인 중'
                    }
                    if ($tool -match 'image_gen|imagegen') {
                        return '이미지 생성 중'
                    }
                    if ($tool -match '^mcp__') {
                        return '연결된 앱 사용 중'
                    }
                    if ([string]::IsNullOrWhiteSpace($tool)) {
                        return '도구 사용 중'
                    }
                    if ($tool.Length -gt 28) {
                        $tool = $tool.Substring(0, 25) + '...'
                    }
                    return "$tool 사용 중"
                }

                $state = 'ready'
                $headline = 'CODEX 대기 중'
                $detail = '새 작업을 기다리는 중'
                $currentProject = $projects |
                    Where-Object {
                        [string]$_.id -eq $projectId
                    } |
                    Select-Object -First 1
                switch ($eventName) {
                    'SessionStart' {
                        $headline = 'CODEX 연결됨'
                        $detail = '세션이 연결되었습니다'
                    }
                    'UserPromptSubmit' {
                        $state = 'working'
                        $headline = '생각하는 중'
                        $detail = '요청을 분석하고 계획하는 중'
                        if (-not $currentProject) {
                            $currentProject = New-Project
                        }
                        $currentProject.state = 'working'
                        $currentProject.phase = '요청 분석 중'
                        $currentProject.sessionId = $sessionId
                        $currentProject.model = $model
                        $currentProject.updatedAtUtc = $now
                    }
                    'PreToolUse' {
                        $state = 'working'
                        $tool = [string]$payload.tool_name
                        $phase = Get-ToolPhase $tool
                        $headline = $phase
                        $detail = $phase
                        if (-not $currentProject) {
                            $currentProject = New-Project
                        }
                        $currentProject.state = 'working'
                        $currentProject.phase = $phase
                        $currentProject.workspace = $workspace
                        $currentProject.model = $model
                        $currentProject.updatedAtUtc = $now
                    }
                    'Stop' {
                        $state = 'complete'
                        $headline = '작업 완료'
                        $detail = '다음 요청을 기다리는 중'
                        if (-not $currentProject) {
                            $currentProject = New-Project
                        }
                        $currentProject.state = 'complete'
                        $currentProject.phase = '작업 완료'
                        $currentProject.workspace = $workspace
                        $currentProject.model = $model
                        $currentProject.updatedAtUtc = $now
                    }
                    'SessionEnd' {
                        $state = 'offline'
                        $headline = 'CODEX 오프라인'
                        $detail = '세션이 종료되었습니다'
                    }
                }
                if ($currentProject) {
                    $existingProjects = @($projects)
                    $recentProjects = [Collections.Generic.List[object]]::new()
                    $recentProjects.Add($currentProject)
                    foreach ($existingProject in $existingProjects) {
                        if (
                            [string]$existingProject.id -ne $projectId
                        ) {
                            $recentProjects.Add($existingProject)
                        }
                    }
                    $projects = @($recentProjects)
                }
                $projects = @($projects | Select-Object -First 3)
                $status = [ordered]@{
                    schemaVersion = 3
                    state = $state
                    headline = $headline
                    detail = $detail
                    workspace = $workspace
                    model = $model
                    eventName = $eventName
                    updatedAtUtc = $now
                    projects = $projects
                }
                $statusJson = $status | ConvertTo-Json -Compress
                $delivered = $false
                {{pipeDelivery}}
                if (-not $delivered) {
                    $directory = Split-Path -Parent $statusPath
                    New-Item -ItemType Directory -Force -Path $directory | Out-Null
                    $temporaryPath = "$statusPath.$([Guid]::NewGuid().ToString('N')).tmp"
                    [IO.File]::WriteAllText(
                        $temporaryPath,
                        $statusJson,
                        [Text.UTF8Encoding]::new($false))
                    Move-Item -LiteralPath $temporaryPath -Destination $statusPath -Force
                }
            } catch {
                # The status bridge is advisory and must never interrupt Codex.
            }
            if ($eventName -eq 'Stop') {
                [Console]::Out.Write('{}')
            }
            exit 0
            """;
    }
}
