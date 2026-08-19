using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Vellume.Windows;

public sealed record DockWindowInfo(
    nint Handle,
    string Title);

public sealed record DockApplicationSnapshot(
    string Key,
    string Name,
    string ExecutablePath,
    IReadOnlyList<DockWindowInfo> Windows,
    bool IsForeground,
    bool HasAttention);

public sealed class WindowsDockRuntimeService : IDisposable
{
    private static readonly HashSet<string> ShellHostProcessNames =
        new(
            new[]
            {
                "ApplicationFrameHost",
                "SystemSettings",
                "ShellExperienceHost",
                "StartMenuExperienceHost",
                "SearchHost",
                "TextInputHost"
            },
            StringComparer.OrdinalIgnoreCase);
    private const int ShellWindowActivated = 4;
    private const int ShellRudeAppActivated = 0x8004;
    private const int ShellFlash = 0x8006;
    private const int WmClose = 0x0010;
    private const int ShowRestore = 9;
    private const int ShowMinimized = 2;
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private readonly int _vellumeProcessId = Environment.ProcessId;
    private readonly Dictionary<string, int> _activationIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _attentionProcessNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ProcessIdentity> _processIdentities =
        new();
    private readonly object _attentionLock = new();
    private nint _messageWindow;

    public WindowsDockRuntimeService()
    {
        ShellHookMessage = RegisterWindowMessageW("SHELLHOOK");
    }

    public int ShellHookMessage { get; }

    public event EventHandler? StateChanged;

    public void Attach(nint messageWindow)
    {
        Detach();
        if (messageWindow != nint.Zero &&
            RegisterShellHookWindow(messageWindow))
        {
            _messageWindow = messageWindow;
        }
    }

    public bool TryHandleShellMessage(
        int message,
        nint wParam,
        nint lParam)
    {
        if (message != ShellHookMessage || ShellHookMessage == 0)
        {
            return false;
        }

        var eventCode = unchecked((int)wParam.ToInt64());
        var processName = TryGetProcessNameForWindow(lParam);
        if (!string.IsNullOrWhiteSpace(processName))
        {
            lock (_attentionLock)
            {
                if (eventCode == ShellFlash)
                {
                    _attentionProcessNames.Add(processName);
                }
                else if (eventCode is
                         ShellWindowActivated or
                         ShellRudeAppActivated)
                {
                    _attentionProcessNames.Remove(processName);
                }
            }
        }

        // EVENT_SYSTEM_FOREGROUND already supplies activation changes. Avoid
        // a complete EnumWindows scan for the duplicate shell activation.
        if (eventCode is not
            (ShellWindowActivated or ShellRudeAppActivated))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    public bool HasAttention(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        lock (_attentionLock)
        {
            return !string.IsNullOrWhiteSpace(processName) &&
                   _attentionProcessNames.Contains(processName);
        }
    }

    public IReadOnlyList<DockApplicationSnapshot> Capture()
    {
        var captureStarted = Stopwatch.GetTimestamp();
        var foreground = GetForegroundWindow();
        var windows = new List<(nint Handle, uint ProcessId, string Title)>();
        EnumWindows(
            (window, _) =>
            {
                if (!IsTaskWindow(window))
                {
                    return true;
                }

                GetWindowThreadProcessId(window, out var processId);
                if (processId == 0 || processId == _vellumeProcessId)
                {
                    return true;
                }

                var title = GetWindowTitle(window);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                windows.Add((window, processId, title));
                return true;
            },
            nint.Zero);

        var result = new List<DockApplicationSnapshot>();
        foreach (var processWindows in windows.GroupBy(item => item.ProcessId))
        {
            if (!TryGetProcessIdentity(
                    processWindows.Key,
                    out var identity))
            {
                continue;
            }

            if (IsShellHostProcessName(identity.ProcessName))
            {
                continue;
            }

            var entries = processWindows
                .Select(item => new DockWindowInfo(
                    item.Handle,
                    item.Title))
                .ToArray();
            result.Add(new DockApplicationSnapshot(
                GetApplicationKey(identity.ExecutablePath),
                identity.FriendlyName,
                identity.ExecutablePath,
                entries,
                entries.Any(item => item.Handle == foreground),
                HasAttention(identity.ExecutablePath)));
        }

        var activeProcessIds = windows
            .Select(item => item.ProcessId)
            .ToHashSet();
        foreach (var processId in _processIdentities.Keys
                     .Where(processId =>
                         !activeProcessIds.Contains(processId))
                     .ToArray())
        {
            _processIdentities.Remove(processId);
        }

        var snapshot = result
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var combinedWindows = group
                    .SelectMany(item => item.Windows)
                    .DistinctBy(item => item.Handle)
                    .ToArray();
                return first with
                {
                    Windows = combinedWindows,
                    IsForeground = group.Any(item => item.IsForeground),
                    HasAttention = group.Any(item => item.HasAttention)
                };
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        LocalPerformanceDiagnostics.Record(
            "dock.window-scan",
            Stopwatch.GetElapsedTime(captureStarted));
        return snapshot;
    }

    public IReadOnlyList<DockApplicationSnapshot> RefreshForegroundStates(
        IReadOnlyList<DockApplicationSnapshot> applications)
    {
        if (applications.Count == 0)
        {
            return Array.Empty<DockApplicationSnapshot>();
        }

        var foregroundWindow = GetForegroundWindow();
        HashSet<string> attentionProcessNames;
        lock (_attentionLock)
        {
            attentionProcessNames = new HashSet<string>(
                _attentionProcessNames,
                StringComparer.OrdinalIgnoreCase);
        }

        var refreshed = new DockApplicationSnapshot[applications.Count];
        for (var index = 0; index < applications.Count; index++)
        {
            var application = applications[index];
            var processName = Path.GetFileNameWithoutExtension(
                application.ExecutablePath);
            refreshed[index] = ApplyForegroundHandle(
                application,
                foregroundWindow) with
            {
                HasAttention =
                    !string.IsNullOrWhiteSpace(processName) &&
                    attentionProcessNames.Contains(processName)
            };
        }

        return refreshed;
    }

    internal static DockApplicationSnapshot ApplyForegroundHandle(
        DockApplicationSnapshot application,
        nint foregroundWindow)
        => application with
        {
            IsForeground = application.Windows.Any(window =>
                window.Handle == foregroundWindow)
        };

    public bool Activate(
        DockApplicationSnapshot application,
        bool minimizeWhenForeground = true)
    {
        var available = application.Windows
            .Where(item => IsWindow(item.Handle))
            .ToArray();
        if (available.Length == 0)
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        if (minimizeWhenForeground &&
            available.Length == 1 &&
            available[0].Handle == foreground)
        {
            ShowWindowAsync(foreground, ShowMinimized);
            return true;
        }

        _activationIndexes.TryGetValue(
            application.Key,
            out var previousIndex);
        var index = application.IsForeground && available.Length > 1
            ? (previousIndex + 1) % available.Length
            : Math.Clamp(previousIndex, 0, available.Length - 1);
        _activationIndexes[application.Key] = index;
        var activated = ActivateWindow(available[index].Handle);
        if (activated)
        {
            ClearAttention(application.ExecutablePath);
        }
        return activated;
    }

    public bool ActivateWindow(nint window)
    {
        if (!IsWindow(window))
        {
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindowAsync(window, ShowRestore);
        }

        return SetForegroundWindow(window);
    }

    public bool CloseWindow(nint window)
        => IsWindow(window) &&
           PostMessageW(
               window,
               WmClose,
               nint.Zero,
               nint.Zero);

    public static string TruncateWindowTitle(
        string? value,
        int maximumCharacters = 36)
    {
        var text = value?.Trim() ?? string.Empty;
        if (maximumCharacters <= 1 ||
            text.Length <= maximumCharacters)
        {
            return text;
        }

        return text[..(maximumCharacters - 1)] + "…";
    }

    public void LaunchNew(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Dock 실행 파일을 찾을 수 없습니다.",
                executablePath);
        }
        if (IsShellHostProcessName(
                Path.GetFileNameWithoutExtension(executablePath)))
        {
            throw new InvalidOperationException(
                "Windows 셸 호스트는 Dock에서 새로 실행할 수 없습니다.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true
        });
    }

    internal static bool IsShellHostProcessName(string? processName)
        => string.IsNullOrWhiteSpace(processName) ||
           ShellHostProcessNames.Contains(processName);

    internal static string GetApplicationKey(string executablePath)
        => ExecutablePathIdentity.GetCanonicalExecutablePath(executablePath);

    public static bool IsExplorerExecutable(string? executablePath)
    {
        try
        {
            return string.Equals(
                Path.GetFileName(executablePath),
                "explorer.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Detach();
        _processIdentities.Clear();
        lock (_attentionLock)
        {
            _attentionProcessNames.Clear();
        }
    }

    private bool TryGetProcessIdentity(
        uint processId,
        out ProcessIdentity identity)
    {
        identity = default!;
        try
        {
            using var process = Process.GetProcessById(
                unchecked((int)processId));
            var processName = process.ProcessName;
            long startTimeTicks;
            try
            {
                startTimeTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                startTimeTicks = 0;
            }

            if (_processIdentities.TryGetValue(
                    processId,
                    out var cached) &&
                cached.ProcessName.Equals(
                    processName,
                    StringComparison.OrdinalIgnoreCase) &&
                (startTimeTicks == 0 ||
                 cached.StartTimeUtcTicks == startTimeTicks))
            {
                identity = cached;
                return true;
            }

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(executablePath);
            identity = new ProcessIdentity(
                fullPath,
                processName,
                GetFriendlyName(fullPath, processName),
                startTimeTicks);
            _processIdentities[processId] = identity;
            return true;
        }
        catch
        {
            _processIdentities.Remove(processId);
            return false;
        }
    }

    private static bool IsTaskWindow(nint window)
    {
        if (!IsWindowVisible(window) ||
            GetWindow(window, GwOwner) != nint.Zero)
        {
            return false;
        }

        var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        var className = GetWindowClass(window);
        return className is not
            ("Shell_TrayWnd" or
             "Shell_SecondaryTrayWnd" or
             "Progman" or
             "WorkerW" or
             "Windows.UI.Core.CoreWindow");
    }

    private static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowTextW(window, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetWindowClass(nint window)
    {
        var builder = new StringBuilder(128);
        return GetClassNameW(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string GetFriendlyName(
        string executablePath,
        string fallback)
    {
        try
        {
            var description = FileVersionInfo
                .GetVersionInfo(executablePath)
                .FileDescription;
            return string.IsNullOrWhiteSpace(description)
                ? fallback
                : description.Trim();
        }
        catch
        {
            return fallback;
        }
    }

    private static string? TryGetProcessNameForWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(
                unchecked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private void ClearAttention(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        lock (_attentionLock)
        {
            if (!string.IsNullOrWhiteSpace(processName))
            {
                _attentionProcessNames.Remove(processName);
            }
        }
    }

    private void Detach()
    {
        if (_messageWindow == nint.Zero)
        {
            return;
        }

        DeregisterShellHookWindow(_messageWindow);
        _messageWindow = nint.Zero;
    }

    private delegate bool EnumWindowsCallback(
        nint window,
        nint parameter);

    private sealed record ProcessIdentity(
        string ExecutablePath,
        string ProcessName,
        string FriendlyName,
        long StartTimeUtcTicks);

    // Registers the Dock for shell activation and attention events.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterShellHookWindow(nint window);

    // Stops shell notifications when the Dock closes.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterShellHookWindow(nint window);

    // Resolves the session-local shell-hook message identifier.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string messageName);

    // Enumerates only existing top-level windows; no process is modified.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    // Filters hidden background helpers from the task-style Dock list.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    // Checks that a window still exists between a snapshot and a click.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    // Maps a Dock window to its owning process for executable grouping.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    // Reads the task-window title shown in Dock window menus.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint window);

    // Copies a task-window title without changing that application.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(
        nint window,
        StringBuilder text,
        int maximumCount);

    // Reads native window classes to exclude Explorer desktop hosts.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint window,
        StringBuilder className,
        int maximumCount);

    // Excludes owned popups that should not appear as separate Dock apps.
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, int command);

    // Reads extended styles so tool windows are not treated as applications.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    // Detects minimized windows before restoring them.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    // Restores or minimizes a window without blocking Vellume's UI thread.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    // Activates the task window selected from the Dock.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    // Requests a normal close so the target application can save or prompt.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(
        nint window,
        int message,
        nint wParam,
        nint lParam);

    // Detects which application is currently active for Dock indicators.
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
