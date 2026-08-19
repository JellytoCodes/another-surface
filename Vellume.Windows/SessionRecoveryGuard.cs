using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Vellume.Windows;

public static class SessionRecoveryGuard
{
    public static void MarkActive(
        string markerPath,
        int ownerProcessId)
    {
        var directory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            markerPath,
            ownerProcessId.ToString(CultureInfo.InvariantCulture));
    }

    public static bool MarkCleanExit(
        string markerPath,
        int ownerProcessId)
    {
        if (!MarkerBelongsTo(markerPath, ownerProcessId))
        {
            return false;
        }

        try
        {
            File.Delete(markerPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Process? Start(
        int ownerProcessId,
        string markerPath,
        int recoveryAttempt)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--session-guard");
        startInfo.ArgumentList.Add(
            ownerProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--session-marker");
        startInfo.ArgumentList.Add(markerPath);
        startInfo.ArgumentList.Add("--recovery-attempt");
        startInfo.ArgumentList.Add(
            recoveryAttempt.ToString(CultureInfo.InvariantCulture));
        try
        {
            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    public static bool Run(
        int ownerProcessId,
        string markerPath,
        int recoveryAttempt)
    {
        try
        {
            using var owner = Process.GetProcessById(ownerProcessId);
            owner.WaitForExit();
        }
        catch
        {
            // If the owner already exited, recovery should run immediately.
        }

        TaskbarVisibilityController.RestoreAllTaskbars();
        if (!ConsumeMarker(markerPath, ownerProcessId) ||
            recoveryAttempt > 0)
        {
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("--recovered-session");
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ConsumeMarker(
        string markerPath,
        int ownerProcessId)
    {
        if (!MarkerBelongsTo(markerPath, ownerProcessId))
        {
            return false;
        }

        try
        {
            File.Delete(markerPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool MarkerBelongsTo(
        string markerPath,
        int ownerProcessId)
    {
        try
        {
            return File.Exists(markerPath) &&
                   int.TryParse(
                       File.ReadAllText(markerPath).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var markerOwner) &&
                   markerOwner == ownerProcessId;
        }
        catch
        {
            return false;
        }
    }
}
