using System.Runtime.InteropServices;
using System.Text;

namespace Vellume.Windows;

public sealed class TaskbarVisibilityController : IDisposable
{
    private const int HideWindow = 0;
    private const int ShowWithoutActivation = 8;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private readonly WinEventCallback _winEventCallback;
    private readonly nint _showEventHook;
    private volatile bool _hidden;
    private volatile bool _disposed;

    public TaskbarVisibilityController()
    {
        _winEventCallback = WinEventReceived;
        _showEventHook = SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            nint.Zero,
            _winEventCallback,
            0,
            0,
            WinEventOutOfContext |
            WinEventSkipOwnProcess);
    }

    public bool IsHidden => _hidden;

    public void SetHidden(bool hidden)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hidden)
        {
            Hide();
        }
        else
        {
            Restore();
        }
    }

    public void Maintain()
    {
        if (!_disposed && _hidden)
        {
            Apply(visible: false);
        }
    }

    public void Restore()
    {
        if (_disposed)
        {
            return;
        }

        var ownedHiddenState = _hidden;
        _hidden = false;
        if (ownedHiddenState)
        {
            Apply(visible: true);
        }
    }

    public static void RestoreAllTaskbars()
        => Apply(visible: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Restore();
        if (_showEventHook != nint.Zero)
        {
            UnhookWinEvent(_showEventHook);
        }
        _disposed = true;
    }

    private void Hide()
    {
        _hidden = true;
        Apply(visible: false);
    }

    private static void Apply(bool visible)
    {
        var command = visible ? ShowWithoutActivation : HideWindow;
        EnumWindows(
            (window, _) =>
            {
                if (IsTaskbarClassName(GetWindowClass(window)))
                {
                    ShowWindowAsync(window, command);
                }

                return true;
            },
            nint.Zero);
    }

    internal static bool IsTaskbarClassName(string className)
        => className is
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd";

    private void WinEventReceived(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed ||
            !_hidden ||
            eventType != EventObjectShow ||
            window == nint.Zero ||
            objectId != ObjectIdWindow ||
            childId != ChildIdSelf ||
            !IsTaskbarClassName(GetWindowClass(window)))
        {
            return;
        }

        ShowWindowAsync(window, HideWindow);
    }

    private static string GetWindowClass(nint window)
    {
        var builder = new StringBuilder(64);
        return GetClassNameW(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private delegate bool EnumWindowsCallback(
        nint window,
        nint parameter);

    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    // Locates primary and secondary Explorer taskbars without restarting Explorer.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    // Identifies Explorer taskbar windows by their stable native class names.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint window,
        StringBuilder className,
        int maximumCount);

    // Hides or restores only the taskbar window and never terminates Explorer.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    // Reacts immediately when Explorer or another process shows a taskbar.
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    // Releases the accessibility event hook during Vellume shutdown.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint eventHook);
}
