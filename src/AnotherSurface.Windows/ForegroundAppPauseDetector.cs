using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AnotherSurface.Core;

namespace AnotherSurface.Windows;

internal readonly record struct ScreenBounds(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

internal readonly record struct ForegroundWindowSnapshot(
    bool IsValid,
    bool IsVisible,
    bool IsOwnProcess,
    uint ProcessId,
    string ProcessName,
    string WindowClass,
    bool IsMaximized,
    ScreenBounds WindowBounds,
    ScreenBounds MonitorBounds,
    ScreenBounds WorkAreaBounds)
{
    public bool IsSystemDirect3DFullScreen { get; init; }
}

internal static class ForegroundAppPausePolicy
{
    private const double FullWorkspaceCoverageThreshold = 0.97;
    private static readonly HashSet<string> DesktopWindowClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "SHELLDLL_DefView",
            "SysListView32",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd"
        };
    private static readonly HashSet<string> ShellProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer",
            "ApplicationFrameHost",
            "SystemSettings",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "TextInputHost",
            "LockApp"
        };
    internal static bool ShouldPause(ForegroundWindowSnapshot snapshot)
        => IsEligibleForegroundApp(snapshot) &&
           !IsShellOrNavigationWindow(snapshot) &&
           (snapshot.IsSystemDirect3DFullScreen ||
            IsBorderlessFullScreen(snapshot) ||
            IsMaximizedWorkspaceWindow(snapshot));

    internal static ApplicationPlaybackAction ResolveAction(
        ForegroundWindowSnapshot snapshot,
        PlaybackOptions options)
    {
        if (!options.PauseWhenForegroundAppIsMaximized ||
            !IsEligibleForegroundApp(snapshot) ||
            IsShellOrNavigationWindow(snapshot))
        {
            return ApplicationPlaybackAction.KeepRunning;
        }

        var processName = NormalizeProcessName(snapshot.ProcessName);
        var explicitRule = (options.ApplicationRules ?? [])
            .FirstOrDefault(rule =>
                rule is not null &&
                !string.IsNullOrWhiteSpace(rule.ProcessName) &&
                NormalizeProcessName(rule.ProcessName)
                    .Equals(
                        processName,
                        StringComparison.OrdinalIgnoreCase) &&
                (rule.Condition ==
                     ApplicationRuleCondition.IsForeground ||
                 rule.Condition ==
                     ApplicationRuleCondition.IsRunning));
        if (explicitRule is not null)
        {
            return explicitRule.Action;
        }

        return snapshot.IsSystemDirect3DFullScreen ||
               IsBorderlessFullScreen(snapshot) ||
               IsMaximizedWorkspaceWindow(snapshot)
            ? ApplicationPlaybackAction.Pause
            : ApplicationPlaybackAction.KeepRunning;
    }

    internal static string NormalizeProcessName(string? processName)
    {
        var normalized = processName?.Trim() ?? string.Empty;
        return normalized.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    internal static bool IsEligibleForegroundApp(
        ForegroundWindowSnapshot snapshot)
        => snapshot.IsValid &&
           snapshot.IsVisible &&
           !snapshot.IsOwnProcess &&
           snapshot.ProcessId != 0 &&
           !DesktopWindowClasses.Contains(snapshot.WindowClass);

    internal static bool IsShellOrNavigationWindow(
        ForegroundWindowSnapshot snapshot)
        => ShellProcessNames.Contains(snapshot.ProcessName) ||
           snapshot.WindowClass is
               "CabinetWClass" or
               "ExploreWClass";

    internal static bool CoversWorkspace(
        ForegroundWindowSnapshot snapshot)
    {
        var target = snapshot.WorkAreaBounds.Width > 0 &&
                     snapshot.WorkAreaBounds.Height > 0
            ? snapshot.WorkAreaBounds
            : snapshot.MonitorBounds;
        if (target.Width <= 0 ||
            target.Height <= 0 ||
            snapshot.WindowBounds.Width <= 0 ||
            snapshot.WindowBounds.Height <= 0)
        {
            return false;
        }

        var intersectionWidth = Math.Max(
            0,
            Math.Min(
                snapshot.WindowBounds.Right,
                target.Right) -
            Math.Max(
                snapshot.WindowBounds.Left,
                target.Left));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(
                snapshot.WindowBounds.Bottom,
                target.Bottom) -
            Math.Max(
                snapshot.WindowBounds.Top,
                target.Top));
        var targetArea = (double)target.Width * target.Height;
        var coveredArea =
            (double)intersectionWidth * intersectionHeight;
        return coveredArea / targetArea >=
               FullWorkspaceCoverageThreshold;
    }

    internal static bool IsBorderlessFullScreen(
        ForegroundWindowSnapshot snapshot)
    {
        if (snapshot.IsMaximized ||
            snapshot.MonitorBounds.Width <= 0 ||
            snapshot.MonitorBounds.Height <= 0)
        {
            return false;
        }

        const int tolerance = 2;
        return Math.Abs(
                   snapshot.WindowBounds.Left -
                   snapshot.MonitorBounds.Left) <= tolerance &&
               Math.Abs(
                   snapshot.WindowBounds.Top -
                   snapshot.MonitorBounds.Top) <= tolerance &&
               Math.Abs(
                   snapshot.WindowBounds.Right -
                   snapshot.MonitorBounds.Right) <= tolerance &&
               Math.Abs(
                   snapshot.WindowBounds.Bottom -
                   snapshot.MonitorBounds.Bottom) <= tolerance;
    }

    internal static bool IsMaximizedWorkspaceWindow(
        ForegroundWindowSnapshot snapshot)
        => snapshot.IsMaximized && CoversWorkspace(snapshot);
}

internal sealed class ForegroundAppPauseDetector
{
    internal ForegroundWindowSnapshot Capture()
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == nint.Zero ||
            !NativeMethods.IsWindow(windowHandle))
        {
            return default;
        }

        var classNameBuffer = new StringBuilder(128);
        NativeMethods.GetClassNameW(
            windowHandle,
            classNameBuffer,
            classNameBuffer.Capacity);
        NativeMethods.GetWindowThreadProcessId(
            windowHandle,
            out var processId);
        var processName = TryGetProcessName(processId);

        var hasWindowBounds = NativeMethods.GetWindowRect(
            windowHandle,
            out var windowRectangle);
        var monitorHandle = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfoEx
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<
                NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        var hasMonitorBounds =
            monitorHandle != nint.Zero &&
            NativeMethods.GetMonitorInfoW(
                monitorHandle,
                ref monitorInfo);

        return new ForegroundWindowSnapshot(
            true,
            NativeMethods.IsWindowVisible(windowHandle),
            processId == (uint)Environment.ProcessId,
            processId,
            processName,
            classNameBuffer.ToString(),
            NativeMethods.IsZoomed(windowHandle),
            hasWindowBounds
                ? ToBounds(windowRectangle)
                : default,
            hasMonitorBounds
                ? ToBounds(monitorInfo.Monitor)
                : default,
            hasMonitorBounds
                ? ToBounds(monitorInfo.WorkArea)
                : default)
        {
            IsSystemDirect3DFullScreen =
                IsDirect3DFullScreenActive()
        };
    }

    private static bool IsDirect3DFullScreenActive()
        => SHQueryUserNotificationState(out var state) >= 0 &&
           state == QueryUserNotificationState
               .RunningDirect3DFullScreen;

    private static ScreenBounds ToBounds(NativeMethods.Rect rectangle)
        => new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);

    private static string TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(
                checked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    // Reads the public Windows shell signal for an exclusive Direct3D
    // full-screen application without inspecting or modifying other apps.
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(
        out QueryUserNotificationState state);
}
