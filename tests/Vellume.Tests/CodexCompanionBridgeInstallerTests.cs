using System.Diagnostics;
using System.Text.Json;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class CodexCompanionBridgeInstallerTests
{
    [Fact]
    public async Task Hook_DeliversStatusThroughLocalPipe()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexPipeTests-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var bridgeDirectory = Path.Combine(root, "bridge");
        var fallbackStatusPath = Path.Combine(
            root,
            "fallback",
            "status.json");
        var receivedStatusPath = Path.Combine(
            root,
            "received",
            "status.json");
        var pipeName = $"Vellume.Codex.Tests.{Guid.NewGuid():N}";
        var previousCodexHome = Environment.GetEnvironmentVariable(
            "CODEX_HOME");
        Directory.CreateDirectory(codexHome);
        using var logger = new FileAppLogger(
            Path.Combine(root, "test.log"));

        try
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                codexHome);
            await using var server = new CodexCompanionPipeServer(
                receivedStatusPath,
                logger,
                pipeName);
            server.Start();
            var installer = new CodexCompanionBridgeInstaller(
                bridgeDirectory,
                fallbackStatusPath,
                pipeName);
            await installer.InstallAsync(CancellationToken.None);

            var hookOutput = await RunHookAsync(
                Path.Combine(
                    bridgeDirectory,
                    "vellume-codex-hook.ps1"),
                """
                {
                  "session_id": "pipe-test",
                  "cwd": "D:\\Dev\\Vellume",
                  "hook_event_name": "PreToolUse",
                  "model": "gpt-test",
                  "tool_name": "apply_patch"
                }
                """);

            Assert.Equal(string.Empty, hookOutput);
            var waitDeadline =
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (!File.Exists(receivedStatusPath) &&
                   DateTimeOffset.UtcNow < waitDeadline)
            {
                await Task.Delay(25);
            }

            Assert.True(
                File.Exists(receivedStatusPath),
                $"Pipe status was not received. " +
                $"Fallback exists: {File.Exists(fallbackStatusPath)}. " +
                $"Log: {(File.Exists(Path.Combine(root, "test.log")) ? await File.ReadAllTextAsync(Path.Combine(root, "test.log")) : "<none>")}. " +
                $"Script: {await File.ReadAllTextAsync(Path.Combine(bridgeDirectory, "vellume-codex-hook.ps1"))}");
            Assert.False(File.Exists(fallbackStatusPath));
            using var statusDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(receivedStatusPath));
            Assert.Equal(
                "PreToolUse",
                statusDocument.RootElement
                    .GetProperty("eventName")
                    .GetString());
            Assert.Equal(
                "working",
                statusDocument.RootElement
                    .GetProperty("state")
                    .GetString());
            var project = Assert.Single(
                statusDocument.RootElement
                    .GetProperty("projects")
                    .EnumerateArray()
                    .ToArray());
            Assert.Equal(
                "프로젝트 파일 수정 중",
                project.GetProperty("phase").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                previousCodexHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Install_PreservesExistingHooksAndIsIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexBridgeTests-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var bridgeDirectory = Path.Combine(root, "bridge");
        var statusPath = Path.Combine(bridgeDirectory, "status.json");
        Directory.CreateDirectory(codexHome);
        var hooksPath = Path.Combine(codexHome, "hooks.json");
        await File.WriteAllTextAsync(
            hooksPath,
            """
            {
              "description": "Existing hooks",
              "hooks": {
                "SessionStart": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "existing-command"
                      }
                    ]
                  }
                ]
              }
            }
            """);
        var previousCodexHome = Environment.GetEnvironmentVariable(
            "CODEX_HOME");

        try
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                codexHome);
            var installer = new CodexCompanionBridgeInstaller(
                bridgeDirectory,
                statusPath);

            await installer.InstallAsync(CancellationToken.None);
            var scriptPath = Path.Combine(
                bridgeDirectory,
                "vellume-codex-hook.ps1");
            var unchangedTimestamp =
                new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(
                scriptPath,
                unchangedTimestamp);
            File.SetLastWriteTimeUtc(
                hooksPath,
                unchangedTimestamp);
            await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(
                unchangedTimestamp,
                File.GetLastWriteTimeUtc(scriptPath));
            Assert.Equal(
                unchangedTimestamp,
                File.GetLastWriteTimeUtc(hooksPath));

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(hooksPath));
            var hooks = document.RootElement.GetProperty("hooks");
            var sessionStart = hooks.GetProperty("SessionStart");
            Assert.Equal(2, sessionStart.GetArrayLength());
            Assert.Contains(
                sessionStart.EnumerateArray(),
                group => group.GetProperty("hooks")
                    .EnumerateArray()
                    .Any(handler =>
                        handler.TryGetProperty(
                            "command",
                            out var command) &&
                        command.GetString() == "existing-command"));
            foreach (var eventName in new[]
                     {
                          "SessionStart",
                          "UserPromptSubmit",
                          "PreToolUse",
                          "Stop"
                      })
            {
                var matchingHandlers = hooks.GetProperty(eventName)
                    .EnumerateArray()
                    .SelectMany(group =>
                        group.GetProperty("hooks").EnumerateArray())
                    .Count(handler =>
                        handler.GetProperty("command")
                            .GetString()!
                            .Contains(
                                "vellume-codex-hook.ps1",
                                StringComparison.OrdinalIgnoreCase));
                Assert.Equal(1, matchingHandlers);
            }
            Assert.False(
                hooks.TryGetProperty(
                    "SessionEnd",
                    out _));

            var script = await File.ReadAllTextAsync(
                scriptPath);
            Assert.Contains("hook_event_name", script);
            Assert.Contains("tool_name", script);
            var installedCommand = hooks
                .GetProperty("SessionStart")
                .EnumerateArray()
                .SelectMany(group =>
                    group.GetProperty("hooks").EnumerateArray())
                .Single(handler =>
                    handler.GetProperty("command")
                        .GetString()!
                        .Contains(
                            "vellume-codex-hook.ps1",
                            StringComparison.OrdinalIgnoreCase))
                .GetProperty("command")
                .GetString()!;
            Assert.Contains(
                "-ExecutionPolicy Bypass",
                installedCommand,
                StringComparison.Ordinal);
            Assert.False(
                installedCommand.StartsWith(
                    '"'));
            Assert.DoesNotContain(
                "last_assistant_message",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "$payload.prompt",
                script,
                StringComparison.OrdinalIgnoreCase);

            var hookOutput = await RunHookAsync(
                Path.Combine(
                    bridgeDirectory,
                    "vellume-codex-hook.ps1"),
                """
                {
                  "session_id": "test",
                  "cwd": "D:\\Dev\\Sample",
                  "hook_event_name": "UserPromptSubmit",
                  "model": "gpt-test",
                  "prompt": "Codex 컴패니언 상태 표시 구현\nTHIS PRIVATE TEXT MUST NOT BE STORED"
                }
                """);
            Assert.Equal(string.Empty, hookOutput);
            var status = await File.ReadAllTextAsync(statusPath);
            Assert.DoesNotContain(
                "THIS PRIVATE TEXT MUST NOT BE STORED",
                status);
            Assert.Contains(
                "\"title\":\"Sample\"",
                status);
            using var statusDocument = JsonDocument.Parse(status);
            Assert.Equal(
                "working",
                statusDocument.RootElement
                    .GetProperty("state")
                    .GetString());
            Assert.Equal(
                "Sample",
                statusDocument.RootElement
                    .GetProperty("workspace")
                    .GetString());
            Assert.Equal(
                1,
                statusDocument.RootElement
                    .GetProperty("projects")
                    .GetArrayLength());

            hookOutput = await RunHookAsync(
                Path.Combine(
                    bridgeDirectory,
                    "vellume-codex-hook.ps1"),
                """
                {
                  "session_id": "test",
                  "cwd": "D:\\Dev\\Sample",
                  "hook_event_name": "Stop",
                  "model": "gpt-test",
                  "last_assistant_message": "PRIVATE RESPONSE"
                }
                """);
            Assert.Equal("{}", hookOutput);
            status = await File.ReadAllTextAsync(statusPath);
            Assert.DoesNotContain("PRIVATE RESPONSE", status);
            Assert.Contains("작업 완료", status);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                previousCodexHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Hook_KeepsThreeRecentProjectsAndTracksToolPhase()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexTasks-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var bridgeDirectory = Path.Combine(root, "bridge");
        var statusPath = Path.Combine(root, "status.json");
        var previousCodexHome = Environment.GetEnvironmentVariable(
            "CODEX_HOME");
        Directory.CreateDirectory(codexHome);

        try
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                codexHome);
            var installer = new CodexCompanionBridgeInstaller(
                bridgeDirectory,
                statusPath);
            await installer.InstallAsync(CancellationToken.None);
            var scriptPath = Path.Combine(
                bridgeDirectory,
                "vellume-codex-hook.ps1");

            for (var index = 1; index <= 4; index++)
            {
                await RunHookAsync(
                    scriptPath,
                    $$"""
                    {
                      "session_id": "session-{{index}}",
                      "turn_id": "turn-{{index}}",
                      "cwd": "D:\\Dev\\Project{{index}}",
                      "hook_event_name": "UserPromptSubmit",
                      "model": "gpt-test",
                      "prompt": "TASK {{index}}\nprivate second line"
                    }
                    """);
                var intermediateText =
                    await File.ReadAllTextAsync(statusPath);
                using var intermediate =
                    JsonDocument.Parse(intermediateText);
                Assert.True(
                    intermediate.RootElement
                        .GetProperty("projects")
                        .GetArrayLength() ==
                    Math.Min(index, 3),
                    $"Project {index} 상태: {intermediateText}");
            }

            await RunHookAsync(
                scriptPath,
                """
                {
                  "session_id": "session-4",
                  "turn_id": "turn-4",
                  "cwd": "D:\\Dev\\Project4",
                  "hook_event_name": "PreToolUse",
                  "model": "gpt-test",
                  "tool_name": "Bash",
                  "tool_input": {
                    "command": "dotnet test Vellume.sln"
                  }
                }
                """);

            using var status = JsonDocument.Parse(
                await File.ReadAllTextAsync(statusPath));
            var projects = status.RootElement
                .GetProperty("projects")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(3, projects.Length);
            Assert.Equal(
                new[]
                {
                    "project:d:\\dev\\project4",
                    "project:d:\\dev\\project3",
                    "project:d:\\dev\\project2"
                },
                projects.Select(project =>
                    project.GetProperty("id").GetString()));
            Assert.Equal(
                new[] { "Project4", "Project3", "Project2" },
                projects.Select(project =>
                    project.GetProperty("title").GetString()));
            Assert.Equal(
                "테스트 검증 중",
                projects[0].GetProperty("phase").GetString());
            Assert.DoesNotContain(
                "private second line",
                await File.ReadAllTextAsync(statusPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                previousCodexHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void UsageParser_ReadsRemainingPercentAndResetDate()
    {
        const long resetTimestamp = 1785903299;
        var parsed = CodexAccountUsageReader.TryParseUsageResponse(
            $$"""
            {
              "id": 2,
              "result": {
                "rateLimitsByLimitId": {
                  "codex": {
                    "planType": "pro",
                    "primary": {
                      "usedPercent": 91,
                      "windowDurationMins": 10080,
                      "resetsAt": {{resetTimestamp}}
                    }
                  },
                  "codex_bengalfox": {
                    "limitName": "GPT-5.3-Codex-Spark",
                    "planType": "pro",
                    "primary": {
                      "usedPercent": 1,
                      "windowDurationMins": 10080,
                      "resetsAt": {{resetTimestamp + 86400}}
                    }
                  }
                }
              }
            }
            """,
            out var usage);

        Assert.True(parsed);
        Assert.True(usage.IsAvailable);
        Assert.Equal(2, usage.Limits.Count);
        var pro = usage.Limits[0];
        Assert.Equal("CODEX PRO", pro.DisplayName);
        Assert.Equal(9, pro.RemainingPercent);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(resetTimestamp),
            pro.ResetsAtUtc);
        Assert.Equal(TimeSpan.FromDays(7), pro.WindowDuration);
        Assert.Equal("pro", pro.PlanType);
        var spark = usage.Limits[1];
        Assert.Equal(
            "GPT-5.3-Codex-Spark",
            spark.DisplayName);
        Assert.Equal(99, spark.RemainingPercent);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(
                resetTimestamp + 86400),
            spark.ResetsAtUtc);
    }

    [Fact]
    public async Task Install_RepairsMissingPowerShellUtf8Bom()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexBomTests-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var bridgeDirectory = Path.Combine(root, "bridge");
        var previousCodexHome = Environment.GetEnvironmentVariable(
            "CODEX_HOME");
        Directory.CreateDirectory(codexHome);

        try
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                codexHome);
            var installer = new CodexCompanionBridgeInstaller(
                bridgeDirectory,
                Path.Combine(root, "status.json"));
            await installer.InstallAsync(CancellationToken.None);

            var scriptPath = Path.Combine(
                bridgeDirectory,
                "vellume-codex-hook.ps1");
            var script = await File.ReadAllTextAsync(scriptPath);
            await File.WriteAllTextAsync(
                scriptPath,
                script,
                new System.Text.UTF8Encoding(false));

            await installer.InstallAsync(CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(scriptPath);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "CODEX_HOME",
                previousCodexHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StatusReader_DoesNotKeepAStaleCompletedTaskVisible()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexStale-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                $$"""
                {
                  "state": "complete",
                  "headline": "작업 완료",
                  "detail": "다음 요청을 기다리는 중",
                  "workspace": "Vellume",
                  "model": "gpt-test",
                  "updatedAtUtc": "{{DateTimeOffset.UtcNow.AddHours(-1):O}}"
                }
                """);

            using var reader =
                new CodexCompanionStatusReader(
                    path,
                    () => false,
                    Path.Combine(
                        Path.GetTempPath(),
                        $"VellumeCodexHome-{Guid.NewGuid():N}"));
            var snapshot = await reader.ReadAsync(
                CancellationToken.None);

            Assert.Equal("disconnected", snapshot.State);
            Assert.Equal("CODEX 연결 안 됨", snapshot.Headline);
            Assert.Contains("자동", snapshot.Detail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StatusReader_RaisesChangeEventForAtomicFileUpdate()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexEvents-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "status.json");
        Directory.CreateDirectory(directory);
        try
        {
            using var reader =
                new CodexCompanionStatusReader(
                    path,
                    () => false,
                    directory);
            var changed =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            reader.Changed += (_, _) =>
                changed.TrySetResult(true);

            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                """{"state":"ready"}""");
            File.Move(
                temporaryPath,
                path,
                overwrite: true);

            Assert.True(
                await changed.Task.WaitAsync(
                    TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StatusReader_AutomaticallyDetectsRunningCodex()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexAuto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var reader = new CodexCompanionStatusReader(
                Path.Combine(root, "status.json"),
                () => true,
                Path.Combine(root, ".codex"));

            var snapshot = await reader.ReadAsync(
                CancellationToken.None);

            Assert.Equal("ready", snapshot.State);
            Assert.Equal("CODEX 연결됨", snapshot.Headline);
            Assert.Contains("자동 감지", snapshot.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusReader_UsesRecentSessionActivityWithoutHook()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"VellumeCodexActivity-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(root, ".codex");
        var sessionDirectory = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "30");
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "rollout-test.jsonl"),
            """{"type":"test"}""");
        try
        {
            using var reader = new CodexCompanionStatusReader(
                Path.Combine(root, "status.json"),
                () => true,
                codexHome);

            var snapshot = await reader.ReadAsync(
                CancellationToken.None);

            Assert.Equal("working", snapshot.State);
            Assert.Equal("CODEX 작업 중", snapshot.Headline);
            Assert.Contains("세션 활동", snapshot.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> RunHookAsync(
        string scriptPath,
        string input)
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments =
                    $"-NoProfile -NonInteractive -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment[
            "VELLUME_CODEX_HOOK_DEBUG"] = "1";
        Assert.True(process.Start());
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Hook process exited {process.ExitCode}: {error}");
        Assert.True(
            string.IsNullOrWhiteSpace(error),
            $"Hook process wrote an error: {error}");
        return output.Trim();
    }
}
