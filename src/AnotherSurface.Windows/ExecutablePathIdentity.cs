using System.IO;

namespace AnotherSurface.Windows;

public static class ExecutablePathIdentity
{
    private static readonly HashSet<string> KnownSingleInstanceExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Discord.exe"
        };

    public static bool AreEquivalent(
        string firstPath,
        string secondPath)
        => GetCanonicalExecutablePath(firstPath).Equals(
            GetCanonicalExecutablePath(secondPath),
            StringComparison.OrdinalIgnoreCase);

    public static string ResolveCurrentExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            var directory = Directory.GetParent(fullPath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                directory is null)
            {
                return fullPath;
            }

            if (File.Exists(fullPath) &&
                !IsVersionDirectory(directory.Name))
            {
                return fullPath;
            }

            var squirrelRoot =
                IsVersionDirectory(directory.Name) &&
                directory.Parent is not null
                    ? directory.Parent
                    : directory;
            if (!squirrelRoot.Exists)
            {
                return fullPath;
            }

            return squirrelRoot
                       .EnumerateDirectories()
                       .Where(candidate =>
                           IsVersionDirectory(candidate.Name))
                       .Select(candidate => new
                       {
                           Candidate = Path.Combine(
                               candidate.FullName,
                               fileName),
                           Version = ParseVersion(candidate.Name),
                           candidate.LastWriteTimeUtc
                       })
                       .Where(candidate =>
                           File.Exists(candidate.Candidate))
                       .OrderByDescending(candidate =>
                           candidate.Version)
                       .ThenByDescending(candidate =>
                           candidate.LastWriteTimeUtc)
                       .Select(candidate => candidate.Candidate)
                       .FirstOrDefault() ??
                   fullPath;
        }
        catch
        {
            return path.Trim();
        }
    }

    public static string GetCanonicalExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            var directory = Directory.GetParent(fullPath);
            if (directory?.Parent is not null &&
                IsVersionDirectory(directory.Name))
            {
                return Path.Combine(
                    directory.Parent.FullName,
                    fileName);
            }

            return fullPath;
        }
        catch
        {
            return path.Trim();
        }
    }

    public static bool IsKnownSingleInstanceExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return KnownSingleInstanceExecutables.Contains(
                Path.GetFileName(path));
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetSquirrelLaunchCommand(
        string executablePath,
        out string launcherPath,
        out string arguments,
        out string workingDirectory)
    {
        launcherPath = string.Empty;
        arguments = string.Empty;
        workingDirectory = string.Empty;
        try
        {
            var resolved = ResolveCurrentExecutablePath(executablePath);
            if (!IsKnownSingleInstanceExecutable(resolved))
            {
                return false;
            }

            var executableDirectory = Directory.GetParent(resolved);
            if (executableDirectory?.Parent is null ||
                !IsVersionDirectory(executableDirectory.Name))
            {
                return false;
            }

            var updater = Path.Combine(
                executableDirectory.Parent.FullName,
                "Update.exe");
            if (!File.Exists(updater))
            {
                return false;
            }

            launcherPath = updater;
            arguments =
                $"--processStart {Path.GetFileName(resolved)}";
            workingDirectory = executableDirectory.FullName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVersionDirectory(string directoryName)
    {
        var candidate = directoryName.StartsWith(
            "app-",
            StringComparison.OrdinalIgnoreCase)
            ? directoryName[4..]
            : directoryName;
        var parts = candidate.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               parts.All(part => int.TryParse(part, out _));
    }

    private static Version ParseVersion(string directoryName)
    {
        var candidate = directoryName.StartsWith(
            "app-",
            StringComparison.OrdinalIgnoreCase)
            ? directoryName[4..]
            : directoryName;
        return Version.TryParse(candidate, out var version)
            ? version
            : new Version(0, 0);
    }
}
