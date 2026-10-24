using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

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
        catch (Exception exception)
        {
            WriteDiagnostic(markerPath, $"guard start failed: {exception}");
            return null;
        }
    }

    public static bool Run(
        int ownerProcessId,
        string markerPath,
        int recoveryAttempt)
    {
        WriteDiagnostic(markerPath, $"watching owner={ownerProcessId} guard={Environment.ProcessId} attempt={recoveryAttempt} executable={Environment.ProcessPath}");
        int? exitCode = null;
        try
        {
            using var owner = Process.GetProcessById(ownerProcessId);
            var ownerHandle = owner.Handle;
            owner.WaitForExit();
            // A process attached by ID does not reliably expose Process.ExitCode.
            // Keep its OS handle alive through the wait and query that handle.
            if (GetExitCodeProcess(ownerHandle, out var nativeExitCode)) exitCode = unchecked((int)nativeExitCode);
        }
        catch (Exception exception)
        {
            WriteDiagnostic(markerPath, $"owner wait failed: {exception}");
            // If the owner already exited, recovery should run immediately.
        }

        TaskbarVisibilityController.RestoreAllTaskbars();
        var consumed = ConsumeMarker(markerPath, ownerProcessId);
        WriteDiagnostic(markerPath, $"owner={ownerProcessId} exited code={(exitCode is { } code ? $"0x{unchecked((uint)code):X8}" : "unavailable")} abnormalMarker={consumed} attempt={recoveryAttempt}");
        if (!consumed || recoveryAttempt > 0)
        {
            WriteDiagnostic(markerPath, "recovery skipped: clean exit or recovery already attempted");
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            WriteDiagnostic(markerPath, "recovery skipped: executable unavailable");
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
            using var recovered = Process.Start(startInfo);
            WriteDiagnostic(markerPath, $"recovery started pid={recovered?.Id}");
            return recovered is not null;
        }
        catch (Exception exception)
        {
            WriteDiagnostic(markerPath, $"recovery failed: {exception}");
            return false;
        }
    }

    private static void WriteDiagnostic(string markerPath, string message)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(markerPath)!, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "session-guard.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { } // Diagnostics must not disable session recovery.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

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
