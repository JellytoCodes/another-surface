using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vellume.Core;

namespace Vellume.Windows;

public enum QuickSlotActivationResult
{
    Started,
    Activated
}

public readonly record struct QuickSlotRuntimeState(
    bool IsRunning,
    bool HasAttention);

public sealed record QuickSlotRuntimeSnapshot(
    IReadOnlyDictionary<int, QuickSlotRuntimeState> QuickSlots,
    IReadOnlyDictionary<SystemShortcutKind, QuickSlotRuntimeState>
        SystemShortcuts);

public sealed class WindowsQuickSlotRuntimeService : IDisposable
{
    private const int ShellWindowActivated = 4;
    private const int ShellRudeAppActivated = 0x8004;
    private const int ShellFlash = 0x8006;
    private const int ShowRestore = 9;
    private const int GetWindowOwner = 4;
    private const int WindowLongExtendedStyle = -20;
    private const long WindowStyleToolWindow = 0x00000080L;
    private readonly object _attentionLock = new();
    private readonly HashSet<string> _attentionProcessNames =
        new(StringComparer.OrdinalIgnoreCase);
    private nint _messageWindow;
    private bool _disposed;

    public WindowsQuickSlotRuntimeService()
    {
        ShellHookMessage = RegisterWindowMessageW("SHELLHOOK");
    }

    public int ShellHookMessage { get; }

    public event EventHandler? StateChanged;

    public void Attach(nint messageWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_messageWindow == messageWindow)
        {
            return;
        }

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
        if (_disposed ||
            ShellHookMessage == 0 ||
            message != ShellHookMessage)
        {
            return false;
        }

        var eventCode = unchecked((int)wParam.ToInt64());
        StateChanged?.Invoke(this, EventArgs.Empty);
        var target = TryGetExecutablePathForWindow(lParam);
        var processName = TryGetProcessName(target);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

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

        return true;
    }

    public QuickSlotRuntimeSnapshot Capture(
        IReadOnlyList<QuickSlotItem> quickSlots,
        IReadOnlyList<SystemShortcutSetting> systemShortcuts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ClearForegroundAttention();
        var targetStates = new Dictionary<
            string,
            QuickSlotRuntimeState>(
            StringComparer.OrdinalIgnoreCase);
        var quickSlotStates = new Dictionary<
            int,
            QuickSlotRuntimeState>();

        foreach (var item in quickSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetTargetState(
                item.TargetPath,
                targetStates);
            quickSlotStates[item.Slot] = state;
        }

        var systemStates = new Dictionary<
            SystemShortcutKind,
            QuickSlotRuntimeState>();
        foreach (var item in systemShortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            systemStates[item.Kind] = item.Kind switch
            {
                SystemShortcutKind.PowerShell => GetTargetState(
                    SystemShortcutSetting.GetDefaultTargetPath(
                        SystemShortcutKind.PowerShell),
                    targetStates),
                SystemShortcutKind.FileExplorer or
                SystemShortcutKind.ThisPc =>
                    GetExplorerState(),
                _ => default
            };
        }

        return new QuickSlotRuntimeSnapshot(
            quickSlotStates,
            systemStates);
    }

    public async Task<QuickSlotActivationResult> ActivateOrLaunchAsync(
        QuickSlotItem item,
        bool forceNewInstance,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceNewInstance &&
            TryActivateExecutable(item.TargetPath))
        {
            ClearAttention(item.TargetPath);
            return QuickSlotActivationResult.Activated;
        }

        await LaunchTargetAsync(item.TargetPath);
        ClearAttention(item.TargetPath);
        return QuickSlotActivationResult.Started;
    }

    public bool TryActivateSystemShortcut(SystemShortcutKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kind == SystemShortcutKind.PowerShell)
        {
            var target =
                SystemShortcutSetting.GetDefaultTargetPath(kind);
            var activated = TryActivateExecutable(target);
            if (activated)
            {
                ClearAttention(target);
            }

            return activated;
        }

        if (kind is
            SystemShortcutKind.FileExplorer or
            SystemShortcutKind.ThisPc)
        {
            var window = FindTopLevelWindow(
                _ => true,
                "CabinetWClass");
            if (window != nint.Zero)
            {
                ActivateWindow(window);
                var target = TryGetExecutablePathForWindow(window);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    ClearAttention(target);
                }

                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
        lock (_attentionLock)
        {
            _attentionProcessNames.Clear();
        }
    }

    private QuickSlotRuntimeState GetTargetState(
        string targetPath,
        Dictionary<string, QuickSlotRuntimeState> cache)
    {
        if (!TryNormalizeExecutablePath(
                targetPath,
                out var executablePath))
        {
            return default;
        }

        if (cache.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        var processName = TryGetProcessName(executablePath);
        var processIds = GetProcessIds(executablePath);
        bool attention;
        lock (_attentionLock)
        {
            attention =
                processName is not null &&
                _attentionProcessNames.Contains(processName);
            if (processIds.Count == 0)
            {
                if (processName is not null)
                {
                    _attentionProcessNames.Remove(processName);
                }
                attention = false;
            }
        }

        var state = new QuickSlotRuntimeState(
            processIds.Count > 0,
            attention);
        cache[executablePath] = state;
        return state;
    }

    private QuickSlotRuntimeState GetExplorerState()
    {
        var window = FindTopLevelWindow(
            _ => true,
            "CabinetWClass");
        if (window == nint.Zero)
        {
            return default;
        }

        var target = TryGetExecutablePathForWindow(window);
        var attention = false;
        if (!string.IsNullOrWhiteSpace(target))
        {
            var processName = TryGetProcessName(target);
            lock (_attentionLock)
            {
                attention =
                    processName is not null &&
                    _attentionProcessNames.Contains(processName);
            }
        }

        return new QuickSlotRuntimeState(true, attention);
    }

    private bool TryActivateExecutable(string targetPath)
    {
        if (!TryNormalizeExecutablePath(
                targetPath,
                out var executablePath))
        {
            return false;
        }

        var processIds = GetProcessIds(executablePath);
        if (processIds.Count == 0)
        {
            return false;
        }

        var window = FindTopLevelWindow(
            processIds.Contains,
            className: null);
        if (window == nint.Zero)
        {
            return false;
        }

        ActivateWindow(window);
        return true;
    }

    private static void ActivateWindow(nint window)
    {
        ShowWindowAsync(window, ShowRestore);
        SetForegroundWindow(window);
    }

    private static async Task LaunchTargetAsync(string targetPath)
    {
        var isShellTarget = targetPath.StartsWith(
            "shell:",
            StringComparison.OrdinalIgnoreCase);
        if (!isShellTarget &&
            await PackagedAppLauncher.TryLaunchAsync(targetPath))
        {
            return;
        }

        var resolvedTarget = isShellTarget
            ? targetPath
            : ExecutablePathIdentity.ResolveCurrentExecutablePath(
                targetPath);
        if (!isShellTarget &&
            !File.Exists(resolvedTarget) &&
            !Directory.Exists(resolvedTarget))
        {
            throw new FileNotFoundException(
                "The quick-slot target no longer exists.",
                targetPath);
        }

        var startInfo = isShellTarget
            ? new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.Windows),
                    "explorer.exe"),
                Arguments = targetPath,
                UseShellExecute = true
            }
            : new ProcessStartInfo
            {
                FileName = resolvedTarget,
                UseShellExecute = true
            };
        if (!isShellTarget &&
            ExecutablePathIdentity.TryGetSquirrelLaunchCommand(
                resolvedTarget,
                out var launcherPath,
                out var arguments,
                out var workingDirectory))
        {
            startInfo.FileName = launcherPath;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = workingDirectory;
        }
        Process.Start(startInfo);
    }

    private static HashSet<uint> GetProcessIds(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(
            executablePath);
        var result = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return result;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var candidatePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(candidatePath) ||
                        !ExecutablePathIdentity.AreEquivalent(
                            executablePath,
                            candidatePath))
                    {
                        continue;
                    }

                    result.Add(unchecked((uint)process.Id));
                }
                catch
                {
                    // An exiting process may disappear during the snapshot.
                }
            }
        }

        return result;
    }

    private static nint FindTopLevelWindow(
        Func<uint, bool> processPredicate,
        string? className,
        bool includeHidden = false)
    {
        var result = nint.Zero;
        EnumWindows(
            (window, _) =>
            {
                if (!IsTaskWindow(window, includeHidden))
                {
                    return true;
                }

                GetWindowThreadProcessId(window, out var processId);
                if (!processPredicate(processId) ||
                    className is not null &&
                    !GetWindowClass(window).Equals(
                        className,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                result = window;
                return false;
            },
            nint.Zero);
        return result;
    }

    private static bool IsTaskWindow(
        nint window,
        bool includeHidden = false)
    {
        if ((!includeHidden && !IsWindowVisible(window)) ||
            GetWindow(window, GetWindowOwner) != nint.Zero)
        {
            return false;
        }

        var extendedStyle = GetWindowLongPtrW(
            window,
            WindowLongExtendedStyle).ToInt64();
        if ((extendedStyle & WindowStyleToolWindow) != 0)
        {
            return false;
        }

        var className = GetWindowClass(window);
        return className is not (
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd" or
            "Progman" or
            "WorkerW" or
            "Windows.UI.Core.CoreWindow");
    }

    private static string GetWindowClass(nint window)
    {
        var builder = new StringBuilder(128);
        return GetClassNameW(
                   window,
                   builder,
                   builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string? TryGetExecutablePathForWindow(nint window)
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
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizeExecutablePath(
        string targetPath,
        out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            var resolved =
                ExecutablePathIdentity.ResolveCurrentExecutablePath(
                    targetPath);
            if (!File.Exists(resolved) ||
                !Path.GetExtension(resolved).Equals(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath = Path.GetFullPath(resolved);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ClearForegroundAttention()
    {
        var foregroundPath = TryGetExecutablePathForWindow(
            GetForegroundWindow());
        if (!string.IsNullOrWhiteSpace(foregroundPath))
        {
            ClearAttention(foregroundPath);
        }
    }

    private void ClearAttention(string targetPath)
    {
        var processName = TryGetProcessName(targetPath);
        if (processName is null)
        {
            return;
        }

        lock (_attentionLock)
        {
            _attentionProcessNames.Remove(processName);
        }
    }

    private static string? TryGetProcessName(string? targetPath)
    {
        try
        {
            var processName = Path.GetFileNameWithoutExtension(
                targetPath);
            return string.IsNullOrWhiteSpace(processName)
                ? null
                : processName;
        }
        catch
        {
            return null;
        }
    }

    public void Detach()
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

    // Registers Vellume for shell activation and attention messages.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterShellHookWindow(nint window);

    // Stops shell messages without affecting Explorer or other applications.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterShellHookWindow(nint window);

    // Resolves the session-local message identifier used by shell hooks.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string messageName);

    // Enumerates visible top-level application windows for activation.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    // Filters background and helper windows from task-style activation.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    // Reads the owner so helper/tool windows are not selected as app windows.
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, int command);

    // Reads extended styles used to exclude non-task tool windows.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(
        nint window,
        int index);

    // Maps shell notification windows back to their owning process.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    // Distinguishes Explorer folder windows from the desktop shell process.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint window,
        StringBuilder className,
        int maximumCount);

    // Restores a minimized target without blocking the Vellume UI thread.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(
        nint window,
        int command);

    // Brings the selected running application's window to the foreground.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    // Clears attention badges as soon as the user activates that app.
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
