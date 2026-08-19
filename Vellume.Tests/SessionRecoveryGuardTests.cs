using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class SessionRecoveryGuardTests
{
    [Fact]
    public void MarkActiveAndCleanExit_UsesOwnerSpecificMarker()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeRecoveryTests-{Guid.NewGuid():N}");
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
