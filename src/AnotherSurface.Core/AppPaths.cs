namespace AnotherSurface.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? ResolveDefaultRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    public static string ResolveDefaultRoot(string localApplicationData)
    {
        // Keep absolute library paths and recovery markers valid on upgrades.
        var legacyRoot = Path.Combine(localApplicationData, "Vellume");
        return Directory.Exists(legacyRoot)
            ? legacyRoot
            : Path.Combine(localApplicationData, "Another Surface");
    }

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string LibraryDirectory => Path.Combine(RootDirectory, "Library");
    public string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public string LogFile => Path.Combine(LogDirectory, "another-surface.log");
    public string RenderDataDirectory => Path.Combine(RootDirectory, "Renderer");
    public string SessionMarkerFile => Path.Combine(
        RootDirectory,
        "session.active");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RenderDataDirectory);
    }
}
