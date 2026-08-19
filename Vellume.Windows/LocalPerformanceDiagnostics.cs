using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vellume.Core;

namespace Vellume.Windows;

/// <summary>
/// Aggregates local timing and resource counters without sending telemetry.
/// Set VELLUME_PERFORMANCE_LOG=1 before launch to append one summary per minute
/// to Vellume's ordinary LocalAppData log.
/// </summary>
public static class LocalPerformanceDiagnostics
{
    private const long FlushIntervalMilliseconds = 60_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MetricBucket> Metrics =
        new(StringComparer.Ordinal);
    private static IAppLogger? _logger;
    private static long _nextFlushTick;
    private static int _enabled;
    private static int _writePending;

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Initialize(IAppLogger logger)
    {
        _logger = logger;
        Volatile.Write(
            ref _enabled,
            Environment.GetEnvironmentVariable(
                "VELLUME_PERFORMANCE_LOG") == "1"
                ? 1
                : 0);
        _nextFlushTick =
            Environment.TickCount64 + FlushIntervalMilliseconds;
    }

    public static void Record(string operation, TimeSpan elapsed)
    {
        if (!IsEnabled)
        {
            return;
        }

        string? summary = null;
        lock (Gate)
        {
            if (!Metrics.TryGetValue(operation, out var metric))
            {
                metric = new MetricBucket();
                Metrics[operation] = metric;
            }
            metric.Add(elapsed.TotalMilliseconds);

            var now = Environment.TickCount64;
            if (now >= _nextFlushTick)
            {
                summary = BuildSummary();
                Metrics.Clear();
                _nextFlushTick = now + FlushIntervalMilliseconds;
            }
        }

        if (summary is not null &&
            Interlocked.Exchange(ref _writePending, 1) == 0)
        {
            _ = WriteSummaryAsync(summary);
        }
    }

    public static async Task FlushAsync()
    {
        if (!IsEnabled || _logger is null)
        {
            return;
        }

        string? summary;
        lock (Gate)
        {
            summary = Metrics.Count == 0 ? null : BuildSummary();
            Metrics.Clear();
            _nextFlushTick =
                Environment.TickCount64 + FlushIntervalMilliseconds;
        }
        if (summary is not null)
        {
            try
            {
                await _logger.InfoAsync(summary);
            }
            catch
            {
                // Diagnostics must not delay or prevent a clean shutdown.
            }
        }
    }

    private static async Task WriteSummaryAsync(string summary)
    {
        try
        {
            var logger = _logger;
            if (logger is not null)
            {
                await logger.InfoAsync(summary);
            }
        }
        catch
        {
            // Logging is best-effort and remains isolated from app behavior.
        }
        finally
        {
            Interlocked.Exchange(ref _writePending, 0);
        }
    }

    private static string BuildSummary()
    {
        var builder = new StringBuilder("[PERF] local 60s summary");
        foreach (var pair in Metrics.OrderBy(pair => pair.Key))
        {
            builder.Append(" | ")
                .Append(pair.Key)
                .Append(": count=")
                .Append(pair.Value.Count)
                .Append(", avg=")
                .Append(pair.Value.AverageMilliseconds.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture))
                .Append("ms, max=")
                .Append(pair.Value.MaximumMilliseconds.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture))
                .Append("ms");
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            builder.Append(" | working=")
                .Append(ToMegabytes(process.WorkingSet64))
                .Append("MB, private=")
                .Append(ToMegabytes(process.PrivateMemorySize64))
                .Append("MB, gdi=")
                .Append(GetGuiResources(process.Handle, 0))
                .Append(", user=")
                .Append(GetGuiResources(process.Handle, 1));
        }
        catch
        {
            // Resource counters are diagnostic-only and must never affect UI.
        }
        return builder.ToString();
    }

    private static long ToMegabytes(long bytes)
        => Math.Max(0, bytes) / (1024 * 1024);

    private sealed class MetricBucket
    {
        internal long Count { get; private set; }
        internal double TotalMilliseconds { get; private set; }
        internal double MaximumMilliseconds { get; private set; }
        internal double AverageMilliseconds =>
            Count == 0 ? 0 : TotalMilliseconds / Count;

        internal void Add(double milliseconds)
        {
            Count++;
            TotalMilliseconds += milliseconds;
            MaximumMilliseconds = Math.Max(
                MaximumMilliseconds,
                milliseconds);
        }
    }

    // Reads per-process GDI and USER handle counts for local leak diagnosis.
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(
        nint process,
        uint flags);
}
