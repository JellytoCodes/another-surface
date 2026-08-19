namespace Vellume.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vellume");
    }

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string LibraryDirectory => Path.Combine(RootDirectory, "Library");
    public string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public string LogFile => Path.Combine(LogDirectory, "vellume.log");
    public string WebViewDataDirectory => Path.Combine(RootDirectory, "WebView2");
    public string SessionMarkerFile => Path.Combine(
        RootDirectory,
        "session.active");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(WebViewDataDirectory);
    }
}
