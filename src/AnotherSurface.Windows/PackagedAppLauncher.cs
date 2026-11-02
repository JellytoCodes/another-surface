using System.IO;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AnotherSurface.Windows;

internal static class PackagedAppLauncher
{
    public static async Task<bool> TryLaunchAsync(string targetPath)
    {
        var package = FindPackage(targetPath);
        if (package is null)
        {
            return false;
        }

        try
        {
            var entries = await package.GetAppListEntriesAsync();
            foreach (var entry in entries)
            {
                if (await entry.LaunchAsync())
                {
                    return true;
                }
            }
        }
        catch
        {
            // The caller can still fall back to a normal executable launch.
        }

        return false;
    }

    private static Package? FindPackage(string targetPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetPath);
        }
        catch
        {
            return null;
        }

        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(
                         string.Empty))
            {
                string? installPath = null;
                try
                {
                    installPath = package.InstalledLocation.Path;
                }
                catch
                {
                    // Framework/resource packages may not expose a location.
                }

                if (!string.IsNullOrWhiteSpace(installPath) &&
                    fullPath.StartsWith(
                        installPath + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return package;
                }

                var windowsAppsSegment = fullPath
                    .Split(
                        Path.DirectorySeparatorChar,
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(segment =>
                        segment.StartsWith(
                            package.Id.Name + "_",
                            StringComparison.OrdinalIgnoreCase));
                if (windowsAppsSegment is not null)
                {
                    return package;
                }
            }
        }
        catch
        {
            // Package enumeration is optional; direct launch remains available.
        }

        return null;
    }
}
