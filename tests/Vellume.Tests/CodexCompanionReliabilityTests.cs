using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class CodexCompanionReliabilityTests
{
    [Fact]
    public async Task StatusPublication_RetriesTheSameMessageAfterSharingFailure()
    {
        var root = CreateTestDirectory();
        try
        {
            var statusPath = Path.Combine(root, "status.json");
            var logger = new RecordingLogger();
            await using var server = new CodexCompanionPipeServer(statusPath, logger);
            var attempts = 0;
            const string message = """{"state":"working","eventName":"PreToolUse"}""";

            await server.PersistAsync(message, CancellationToken.None, (source, target) =>
            {
                Assert.Equal(statusPath + ".pending", source);
                Assert.Equal(statusPath, target);
                Assert.Equal(message, File.ReadAllText(source));
                if (++attempts < 3)
                {
                    throw SharingViolation();
                }
                File.Move(source, target, overwrite: true);
            });

            Assert.Equal(3, attempts);
            Assert.Equal(message, await File.ReadAllTextAsync(statusPath));
            Assert.False(File.Exists(statusPath + ".pending"));
            Assert.Empty(logger.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusPublication_ExhaustedAccessFailureKeepsOnlyLatestRecoverySnapshot()
    {
        var root = CreateTestDirectory();
        try
        {
            var statusPath = Path.Combine(root, "status.json");
            var logger = new RecordingLogger();
            await using var server = new CodexCompanionPipeServer(statusPath, logger);
            var attempts = 0;
            const string previous = """{"state":"ready"}""";
            const string first = """{"state":"working"}""";
            const string latest = """{"state":"complete"}""";
            await File.WriteAllTextAsync(statusPath, previous);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                server.PersistAsync(first, CancellationToken.None, (_, _) =>
                {
                    attempts++;
                    throw new UnauthorizedAccessException("Access denied.");
                }));

            Assert.Equal(4, attempts);
            Assert.Equal(previous, await File.ReadAllTextAsync(statusPath));
            Assert.Equal(first, await File.ReadAllTextAsync(statusPath + ".pending"));
            Assert.Contains("4회", Assert.Single(logger.Errors));

            // The recovery slot is replaced, never replayed over newer status.
            await server.PersistAsync(latest, CancellationToken.None);
            Assert.Equal(latest, await File.ReadAllTextAsync(statusPath));
            Assert.Equal(new[] { statusPath }, Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusPublication_PermanentFailureDoesNotRetryAndPreservesMessage()
    {
        var root = CreateTestDirectory();
        try
        {
            var statusPath = Path.Combine(root, "status.json");
            var logger = new RecordingLogger();
            await using var server = new CodexCompanionPipeServer(statusPath, logger);
            var attempts = 0;
            const string message = """{"state":"working"}""";

            await Assert.ThrowsAsync<IOException>(() => server.PersistAsync(
                message, CancellationToken.None, (_, _) =>
                {
                    attempts++;
                    throw new IOException("Disk full.", unchecked((int)0x80070070));
                }));

            Assert.Equal(1, attempts);
            Assert.Equal(message, await File.ReadAllTextAsync(statusPath + ".pending"));
            Assert.Contains("재시도 대상이 아닌", Assert.Single(logger.Errors));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusPublication_CancellationStopsRetriesAndKeepsCompletedMessage()
    {
        var root = CreateTestDirectory();
        try
        {
            var statusPath = Path.Combine(root, "status.json");
            var logger = new RecordingLogger();
            await using var server = new CodexCompanionPipeServer(statusPath, logger);
            using var cancellation = new CancellationTokenSource();
            var attempts = 0;
            const string message = """{"state":"complete"}""";

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.PersistAsync(
                message, cancellation.Token, (_, _) =>
                {
                    attempts++;
                    cancellation.Cancel();
                    throw SharingViolation();
                }));

            Assert.Equal(1, attempts);
            Assert.Equal(message, await File.ReadAllTextAsync(statusPath + ".pending"));
            Assert.Empty(logger.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StatusReplacement_PreCancelledTokenNeverAttemptsPublication()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexCompanionPipeServer.ReplaceStatusFileAsync(
                "unused.pending", "unused.json", cancellation.Token,
                (_, _) => attempts++));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task StatusReader_CoalescesSessionAndReadBurstsWithinProcessTtl()
    {
        var root = CreateTestDirectory();
        try
        {
            long tick = 0;
            var enumerations = 0;
            using var reader = new CodexCompanionStatusReader(
                Path.Combine(root, "status.json"),
                codexHome: root,
                getCodexProcesses: () =>
                {
                    Interlocked.Increment(ref enumerations);
                    return [];
                },
                getTickCount: () => Interlocked.Read(ref tick));

            Parallel.For(0, 64, _ => reader.RecordLocalActivity());
            var snapshots = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => reader.ReadAsync(CancellationToken.None)));
            Assert.All(snapshots, snapshot => Assert.Equal("disconnected", snapshot.State));
            Assert.Equal(1, enumerations);

            Interlocked.Exchange(ref tick, 1_000);
            Parallel.For(0, 64, _ => reader.RecordLocalActivity());
            await reader.ReadAsync(CancellationToken.None);
            Assert.Equal(2, enumerations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IOException SharingViolation()
        => new("Sharing violation.", unchecked((int)0x80070020));

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"VellumeCodexReliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = [];

        public Task InfoAsync(string message) => Task.CompletedTask;

        public Task ErrorAsync(string message, Exception? exception = null)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }
    }
}
