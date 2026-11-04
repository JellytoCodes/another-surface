using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class SessionMarkerTests
{
    [Fact]
    public void CleanExit_RemovesOnlyItsOwnMarker()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AnotherSurfaceMarker-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "session.active");
        try
        {
            SessionMarker.MarkActive(path, 1234);
            Assert.Equal("1234", File.ReadAllText(path));
            Assert.False(SessionMarker.MarkCleanExit(path, 4321));
            Assert.Equal("1234", File.ReadAllText(path));
            Assert.True(SessionMarker.MarkCleanExit(path, 1234));
            Assert.False(File.Exists(path));
            Assert.False(SessionMarker.MarkCleanExit(path, 1234));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
