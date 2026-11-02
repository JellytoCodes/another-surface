using AnotherSurface.Core;

namespace AnotherSurface.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _localData = Path.Combine(
        Path.GetTempPath(), $"AnotherSurfacePaths-{Guid.NewGuid():N}");

    [Fact]
    public void NewInstallUsesAnotherSurfaceDirectory()
        => Assert.Equal(Path.Combine(_localData, "Another Surface"),
            AppPaths.ResolveDefaultRoot(_localData));

    [Fact]
    public void UpgradeReusesExistingLibraryAndSettings()
    {
        var legacy = Path.Combine(_localData, "Vellume");
        Directory.CreateDirectory(Path.Combine(legacy, "Library"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        var paths = new AppPaths(AppPaths.ResolveDefaultRoot(_localData));

        Assert.Equal(legacy, paths.RootDirectory);
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.Equal(Path.Combine(legacy, "Library"), paths.LibraryDirectory);
        Assert.False(Directory.Exists(Path.Combine(_localData, "Another Surface")));
    }

    [Fact]
    public void ExplicitRootIsPreserved()
        => Assert.Equal(_localData, new AppPaths(_localData).RootDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_localData))
            Directory.Delete(_localData, recursive: true);
    }
}
