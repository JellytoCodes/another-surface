using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class SessionRecoveryGuardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "LocalIntegration")]
    public void Guard_RecordsOwnerExitCodeAndDistinguishesCleanMarker(bool clean)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AnotherSurfaceGuardExit-{Guid.NewGuid():N}");
        var paths = new AppPaths(directory);
        paths.EnsureCreated();
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 600; exit 37" })
                start.ArgumentList.Add(argument);
            using var owner = System.Diagnostics.Process.Start(start)!;
            SessionRecoveryGuard.MarkActive(paths.SessionMarkerFile, owner.Id);
            if (clean) Assert.True(SessionRecoveryGuard.MarkCleanExit(paths.SessionMarkerFile, owner.Id));
            Assert.False(SessionRecoveryGuard.Run(owner.Id, paths.SessionMarkerFile, 1));
            var log = File.ReadAllText(Path.Combine(paths.LogDirectory, "session-guard.log"));
            Assert.True(log.Contains("code=0x00000025"), log);
            Assert.Contains($"abnormalMarker={!clean}", log);
            Assert.False(File.Exists(paths.SessionMarkerFile));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MarkActiveAndCleanExit_UsesOwnerSpecificMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceRecoveryTests-{Guid.NewGuid():N}");
        var paths = new AppPaths(directory);

        try
        {
            paths.EnsureCreated();
            Assert.True(Directory.Exists(paths.LibraryDirectory));
            SessionRecoveryGuard.MarkActive(
                paths.SessionMarkerFile,
                1234);

            Assert.True(File.Exists(paths.SessionMarkerFile));
            Assert.False(SessionRecoveryGuard.MarkCleanExit(
                paths.SessionMarkerFile,
                4321));
            Assert.True(File.Exists(paths.SessionMarkerFile));
            Assert.True(SessionRecoveryGuard.MarkCleanExit(
                paths.SessionMarkerFile,
                1234));
            Assert.False(File.Exists(paths.SessionMarkerFile));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
