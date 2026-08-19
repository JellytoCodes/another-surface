using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace Vellume.Windows;

internal sealed record CodexUsageLimitSnapshot(
    string Id,
    string DisplayName,
    int RemainingPercent,
    DateTimeOffset? ResetsAtUtc,
    TimeSpan? WindowDuration,
    string PlanType);

internal sealed record CodexUsageSnapshot(
    bool IsAvailable,
    IReadOnlyList<CodexUsageLimitSnapshot> Limits)
{
    public static CodexUsageSnapshot Unavailable()
        => new(
            false,
            []);
}

/// <summary>
/// Reads the signed-in Codex account quota through the local Codex
/// app-server. Vellume never reads or stores Codex credentials.
/// </summary>
internal sealed class CodexAccountUsageReader
{
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(5);

    public async Task<CodexUsageSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        var executablePath = FindCodexExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return CodexUsageSnapshot.Unavailable();
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("app-server");

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            if (!process.Start())
            {
                return CodexUsageSnapshot.Unavailable();
            }

            var errorDrain = process.StandardError
                .ReadToEndAsync(timeout.Token);
            await WriteRequestAsync(
                process.StandardInput,
                new
                {
                    method = "initialize",
                    id = 1,
                    @params = new
                    {
                        clientInfo = new
                        {
                            name = "vellume",
                            title = "Vellume",
                            version = "0.9.0"
                        }
                    }
                },
                timeout.Token);
            await WriteRequestAsync(
                process.StandardInput,
                new
                {
                    method = "initialized",
                    @params = new { }
                },
                timeout.Token);
            await WriteRequestAsync(
                process.StandardInput,
                new
                {
                    method = "account/rateLimits/read",
                    id = 2
                },
                timeout.Token);

            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput
                    .ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                if (TryParseUsageResponse(
                        line,
                        out var snapshot))
                {
                    return snapshot;
                }
            }

            _ = errorDrain;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // A quota badge is optional. A slow or unavailable Codex
            // service must never delay the desktop widgets.
        }
        catch (IOException)
        {
            // Codex may close its transport during app shutdown.
        }
        catch (InvalidOperationException)
        {
            // The executable can disappear during an in-place update.
        }
        catch (Win32Exception)
        {
            // Packaged Codex binaries can be visible but not directly
            // launchable from another app container.
        }
        finally
        {
            StopProcess(process);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CodexUsageSnapshot.Unavailable();
    }

    internal static bool TryParseUsageResponse(
        string json,
        out CodexUsageSnapshot snapshot)
    {
        snapshot = CodexUsageSnapshot.Unavailable();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt32() != 2 ||
                !root.TryGetProperty(
                    "result",
                    out var result))
            {
                return false;
            }

            var limits = new List<CodexUsageLimitSnapshot>(2);
            if (result.TryGetProperty(
                    "rateLimitsByLimitId",
                    out var byLimitId) &&
                byLimitId.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in byLimitId
                             .EnumerateObject()
                             .OrderBy(property =>
                                 string.Equals(
                                     property.Name,
                                     "codex",
                                     StringComparison.OrdinalIgnoreCase)
                                     ? 0
                                     : 1))
                {
                    if (TryCreateLimit(
                            property.Name,
                            property.Value,
                            out var limit))
                    {
                        limits.Add(limit);
                        if (limits.Count == 2)
                        {
                            break;
                        }
                    }
                }
            }

            if (limits.Count == 0 &&
                result.TryGetProperty(
                    "rateLimits",
                    out var rateLimit) &&
                TryCreateLimit(
                    "codex",
                    rateLimit,
                    out var fallbackLimit))
            {
                limits.Add(fallbackLimit);
            }

            if (limits.Count == 0)
            {
                return false;
            }

            snapshot = new CodexUsageSnapshot(
                true,
                limits);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryCreateLimit(
        string id,
        JsonElement rateLimit,
        out CodexUsageLimitSnapshot limit)
    {
        limit = default!;
        if (rateLimit.ValueKind != JsonValueKind.Object ||
            !rateLimit.TryGetProperty(
                "primary",
                out var primary) ||
            primary.ValueKind != JsonValueKind.Object ||
            !primary.TryGetProperty(
                "usedPercent",
                out var usedElement) ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return false;
        }

        DateTimeOffset? resetsAt = null;
        if (primary.TryGetProperty(
                "resetsAt",
                out var resetsElement) &&
            resetsElement.TryGetInt64(
                out var resetSeconds))
        {
            try
            {
                resetsAt = DateTimeOffset
                    .FromUnixTimeSeconds(resetSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        TimeSpan? windowDuration = null;
        if (primary.TryGetProperty(
                "windowDurationMins",
                out var durationElement) &&
            durationElement.TryGetDouble(
                out var durationMinutes) &&
            durationMinutes >= 0)
        {
            windowDuration = TimeSpan.FromMinutes(
                durationMinutes);
        }

        var planType =
            rateLimit.TryGetProperty(
                "planType",
                out var planElement) &&
            planElement.ValueKind == JsonValueKind.String
                ? planElement.GetString() ?? string.Empty
                : string.Empty;
        var displayName =
            rateLimit.TryGetProperty(
                "limitName",
                out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = string.Equals(
                    id,
                    "codex",
                    StringComparison.OrdinalIgnoreCase)
                ? planType.StartsWith(
                    "pro",
                    StringComparison.OrdinalIgnoreCase)
                    ? "CODEX PRO"
                    : "CODEX"
                : id.Contains(
                    "bengalfox",
                    StringComparison.OrdinalIgnoreCase)
                    ? "GPT-5.3-Codex-Spark"
                    : id.Replace('_', ' ').ToUpperInvariant();
        }

        var remaining = (int)Math.Round(
            100 - usedPercent,
            MidpointRounding.AwayFromZero);
        limit = new CodexUsageLimitSnapshot(
            id,
            displayName,
            Math.Clamp(remaining, 0, 100),
            resetsAt,
            windowDuration,
            planType);
        return true;
    }

    private static async Task WriteRequestAsync(
        StreamWriter writer,
        object message,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(message)
                .AsMemory(),
            cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string? FindCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(
            "VELLUME_CODEX_EXECUTABLE");
        if (IsExecutable(configured))
        {
            return configured;
        }

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var npmExecutable = Path.Combine(
            appData,
            "npm",
            "node_modules",
            "@openai",
            "codex",
            "node_modules",
            "@openai",
            "codex-win32-x64",
            "vendor",
            "x86_64-pc-windows-msvc",
            "bin",
            "codex.exe");
        if (IsExecutable(npmExecutable))
        {
            return npmExecutable;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName(
                         "codex"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (IsExecutable(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                        // Packaged and elevated processes can hide their
                        // executable path. Continue with PATH lookup.
                    }
                }
            }
        }
        catch
        {
            // Process enumeration is only one discovery strategy.
        }

        var pathValue = Environment.GetEnvironmentVariable(
            "PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(
                        directory,
                        "codex.exe");
                    if (IsExecutable(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           string.Equals(
               Path.GetExtension(path),
               ".exe",
               StringComparison.OrdinalIgnoreCase) &&
           File.Exists(path);

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The short-lived probe may have already exited.
        }
    }
}
