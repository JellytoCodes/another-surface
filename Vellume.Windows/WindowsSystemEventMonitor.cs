using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Vellume.Windows;

/// <summary>
/// Converts Windows shell and accessibility notifications into coalesced
/// application events. Callers still keep a slow watchdog for events lost
/// while Explorer is restarting.
/// </summary>
public sealed class WindowsSystemEventMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const int CsidlBitBucket = 0x000A;
    private const int RecycleBinMessage = 0x8000 + 0x56;
    private const int ShellChangeSources = 0x0001 | 0x0002;
    private const int ShellChangeAllEvents = 0x7FFFFFFF;
    private const int WindowStylePopup = unchecked((int)0x80000000);
    private const int WindowStyleExToolWindow = 0x00000080;
    private const int WindowStyleExNoActivate = 0x08000000;

    private readonly Dispatcher _dispatcher;
    private readonly WinEventCallback _winEventCallback;
    private readonly List<nint> _winEventHooks = [];
    private nint _foregroundLocationHook;
    private nint _foregroundLocationWindow;
    private HwndSource? _messageSource;
    private uint _recycleBinRegistration;
    private int _taskbarCreatedMessage;
    private int _foregroundEventPending;
    private int _foregroundHookRefreshPending;
    private int _windowInventoryEventPending;
    private int _desktopShellEventPending;
    private int _recycleBinEventPending;
    private bool _disposed;

    public WindowsSystemEventMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _winEventCallback = WinEventReceived;
        CreateMessageWindow();
        RegisterWinEvents();
        RefreshForegroundLocationHook();
        RegisterRecycleBinEvents();
    }

    public bool RecycleBinEventsAvailable =>
        _recycleBinRegistration != 0;

    public event EventHandler? ForegroundWindowChanged;
    public event EventHandler? WindowInventoryChanged;
    public event EventHandler? DesktopShellChanged;
    public event EventHandler? RecycleBinChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_recycleBinRegistration != 0)
        {
            SHChangeNotifyDeregister(_recycleBinRegistration);
            _recycleBinRegistration = 0;
        }

        foreach (var hook in _winEventHooks)
        {
            if (hook != nint.Zero)
            {
                UnhookWinEvent(hook);
            }
        }
        _winEventHooks.Clear();
        ReleaseForegroundLocationHook();

        if (_messageSource is not null)
        {
            _messageSource.RemoveHook(MessageWindowHook);
            _messageSource.Dispose();
            _messageSource = null;
        }
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters(
            "Vellume Windows Event Monitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStylePopup,
            ExtendedWindowStyle =
                WindowStyleExToolWindow |
                WindowStyleExNoActivate
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(MessageWindowHook);
        _taskbarCreatedMessage =
            RegisterWindowMessageW("TaskbarCreated");
    }

    private void RegisterWinEvents()
    {
        AddWinEventHook(
            EventSystemForeground,
            EventSystemForeground);
        AddWinEventHook(
            EventSystemMinimizeStart,
            EventSystemMinimizeEnd);
    }

    private void AddWinEventHook(uint minimum, uint maximum)
    {
        var hook = SetWinEventHook(
            minimum,
            maximum,
            nint.Zero,
            _winEventCallback,
            0,
            0,
            WinEventOutOfContext |
            WinEventSkipOwnProcess);
        if (hook != nint.Zero)
        {
            _winEventHooks.Add(hook);
        }
    }

    private void RegisterRecycleBinEvents()
    {
        if (_messageSource is null ||
            SHGetSpecialFolderLocation(
                nint.Zero,
                CsidlBitBucket,
                out var recycleBinPidl) < 0 ||
            recycleBinPidl == nint.Zero)
        {
            return;
        }

        try
        {
            var entry = new ShellChangeNotifyEntry
            {
                Pidl = recycleBinPidl,
                Recursive = true
            };
            _recycleBinRegistration =
                SHChangeNotifyRegister(
                    _messageSource.Handle,
                    ShellChangeSources,
                    ShellChangeAllEvents,
                    RecycleBinMessage,
                    1,
                    ref entry);
        }
        finally
        {
            Marshal.FreeCoTaskMem(recycleBinPidl);
        }
    }

    private nint MessageWindowHook(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (_disposed)
        {
            return nint.Zero;
        }

        if (message == RecycleBinMessage)
        {
            QueueEvent(
                PendingEvent.RecycleBin,
                RecycleBinChanged);
        }
        else if (_taskbarCreatedMessage != 0 &&
                 message == _taskbarCreatedMessage)
        {
            QueueEvent(
                PendingEvent.DesktopShell,
                DesktopShellChanged);
        }

        return nint.Zero;
    }

    private void WinEventReceived(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (eventType is
            EventSystemForeground or
            EventSystemMinimizeStart or
            EventSystemMinimizeEnd)
        {
            QueueEvent(
                PendingEvent.Foreground,
                ForegroundWindowChanged);
            QueueEvent(
                PendingEvent.WindowInventory,
                WindowInventoryChanged);
            QueueForegroundLocationHookRefresh();
        }
        else if (eventType == EventObjectLocationChange &&
                 objectId == ObjectIdWindow &&
                 childId == ChildIdSelf &&
                 window != nint.Zero &&
                 window == _foregroundLocationWindow &&
                 window == GetForegroundWindow())
        {
            QueueEvent(
                PendingEvent.Foreground,
                ForegroundWindowChanged);
        }
    }

    private void QueueForegroundLocationHookRefresh()
    {
        if (_disposed ||
            Interlocked.Exchange(
                ref _foregroundHookRefreshPending,
                1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                Interlocked.Exchange(
                    ref _foregroundHookRefreshPending,
                    0);
                if (!_disposed)
                {
                    RefreshForegroundLocationHook();
                }
            }));
    }

    private void RefreshForegroundLocationHook()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == _foregroundLocationWindow &&
            _foregroundLocationHook != nint.Zero)
        {
            return;
        }

        ReleaseForegroundLocationHook();
        if (foregroundWindow == nint.Zero)
        {
            return;
        }

        var threadId = GetWindowThreadProcessId(
            foregroundWindow,
            out var processId);
        if (threadId == 0 ||
            processId == 0 ||
            processId == (uint)Environment.ProcessId)
        {
            return;
        }

        _foregroundLocationHook = SetWinEventHook(
            EventObjectLocationChange,
            EventObjectLocationChange,
            nint.Zero,
            _winEventCallback,
            processId,
            threadId,
            WinEventOutOfContext |
            WinEventSkipOwnProcess);
        if (_foregroundLocationHook != nint.Zero)
        {
            _foregroundLocationWindow = foregroundWindow;
        }
    }

    private void ReleaseForegroundLocationHook()
    {
        if (_foregroundLocationHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundLocationHook);
            _foregroundLocationHook = nint.Zero;
        }
        _foregroundLocationWindow = nint.Zero;
    }

    private void QueueEvent(
        PendingEvent pendingEvent,
        EventHandler? handler)
    {
        ref var pendingFlag = ref GetPendingFlag(pendingEvent);
        if (handler is null ||
            Interlocked.Exchange(ref pendingFlag, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                Interlocked.Exchange(
                    ref GetPendingFlag(pendingEvent),
                    0);
                if (!_disposed)
                {
                    handler.Invoke(this, EventArgs.Empty);
                }
            }));
    }

    private ref int GetPendingFlag(PendingEvent pendingEvent)
    {
        switch (pendingEvent)
        {
            case PendingEvent.Foreground:
                return ref _foregroundEventPending;
            case PendingEvent.WindowInventory:
                return ref _windowInventoryEventPending;
            case PendingEvent.DesktopShell:
                return ref _desktopShellEventPending;
            default:
                return ref _recycleBinEventPending;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellChangeNotifyEntry
    {
        internal nint Pidl;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Recursive;
    }

    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    private enum PendingEvent
    {
        Foreground,
        WindowInventory,
        DesktopShell,
        RecycleBin
    }

    // Receives foreground and minimize events globally. Location changes are
    // registered separately for only the current foreground window thread so
    // GPU-heavy applications do not publish a system-wide event stream here.
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint module,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    // Releases a global accessibility event hook during application shutdown.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    // Gets the active top-level window so location events from child windows
    // and background windows can be ignored.
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    // Resolves the foreground window's thread and process for a narrowly
    // scoped location-change accessibility hook.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    // Registers for Explorer broadcasts after the shell taskbar is recreated.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string messageName);

    // Resolves the Recycle Bin shell namespace without reading protected files.
    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(
        nint ownerWindow,
        int folderId,
        out nint itemIdList);

    // Subscribes the hidden monitor window to Recycle Bin namespace changes.
    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(
        nint window,
        int sources,
        int events,
        int message,
        int entryCount,
        ref ShellChangeNotifyEntry entries);

    // Removes the Recycle Bin shell change subscription.
    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(
        uint registrationId);

}
