using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Vellume.Windows;

/// <summary>Handles scoped desktop widget shortcuts; never performs UI work on the mouse hook thread.</summary>
internal sealed class DesktopWidgetPointerHook : IDisposable
{
    internal sealed record Target(int X, int Y, int Width, int Height, nint Editor,
        Func<int, int, bool> HitTest, Action<bool> SetLocked)
    {
        internal bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    }

    private sealed record Snapshot(Target[] Targets, nint[] DesktopRoots);
    private Snapshot _snapshot = new([], []);
    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private readonly HookProc _callback;
    private readonly HookProc _keyboardCallback;
    private readonly ManualResetEventSlim _ready = new();
    private Dispatcher? _hookDispatcher;
    private Exception? _failure;
    private nint _hook;
    private nint _keyboardHook;
    private Action? _deleteGesture;
    private bool _suppressDeleteUp;
    private bool _suppressButtonUp;
    private volatile bool _disposed;

    internal DesktopWidgetPointerHook(Dispatcher ui)
    {
        _ui = ui;
        _callback = OnMouse;
        _keyboardCallback = OnKeyboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "Vellume desktop widget shortcuts" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
        {
            Dispose();
            throw new TimeoutException("위젯 마우스 단축키를 초기화하지 못했습니다.");
        }
        if (_failure is not null) { Dispose(); throw new InvalidOperationException("위젯 마우스 단축키를 등록하지 못했습니다.", _failure); }
    }

    internal void Update(Target[] targets, params nint[] desktopHandles)
        => Volatile.Write(ref _snapshot, new Snapshot(targets,
            desktopHandles.Select(GetRoot).Where(root => root != nint.Zero).Distinct().ToArray()));

    internal void SetDeleteGesture(Action? hide) => Volatile.Write(ref _deleteGesture, hide);

    internal bool HandleDeleteKey(bool down, bool leftButtonHeld)
    {
        if (!down)
        {
            var suppress = _suppressDeleteUp;
            _suppressDeleteUp = false;
            return suppress;
        }
        if (_suppressDeleteUp) return true; // Swallow repeats until the matching key-up.
        if (!leftButtonHeld || _disposed || _ui.HasShutdownStarted) return false;
        var hide = Interlocked.Exchange(ref _deleteGesture, null);
        if (hide is null) return false;
        try { _ui.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!_disposed) hide(); })); }
        catch (InvalidOperationException) { return false; }
        _suppressDeleteUp = true;
        return true;
    }

    // Shared by the actual hook and the regression tests. A covering app always
    // wins; a locked widget only responds to Alt on a visible, painted pixel.
    internal Target? FindTarget(int x, int y, bool alt, nint topWindow)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var editor = snapshot.Targets.FirstOrDefault(target => target.Editor != nint.Zero &&
            target.Editor == topWindow && target.Contains(x, y));
        if (editor is not null) return editor;
        return alt && snapshot.DesktopRoots.Contains(topWindow)
            ? snapshot.Targets.FirstOrDefault(target => target.Contains(x, y) && target.HitTest(x - target.X, y - target.Y))
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _deleteGesture, null);
        Volatile.Write(ref _snapshot, new Snapshot([], []));
        _hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        if (_thread != Thread.CurrentThread && _thread.Join(TimeSpan.FromSeconds(2))) _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _hookDispatcher = Dispatcher.CurrentDispatcher;
            _hook = SetWindowsHookExW(14, _callback, NativeMethods.GetModuleHandleW(null), 0); // WH_MOUSE_LL
            if (_hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _keyboardHook = SetWindowsHookExW(13, _keyboardCallback, NativeMethods.GetModuleHandleW(null), 0); // WH_KEYBOARD_LL
            if (_keyboardHook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception exception) { _failure = exception; }
        finally { _ready.Set(); }
        try { if (_failure is null && !_disposed) Dispatcher.Run(); }
        finally
        {
            if (_hook != nint.Zero) UnhookWindowsHookEx(_hook);
            if (_keyboardHook != nint.Zero) UnhookWindowsHookEx(_keyboardHook);
        }
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && !_disposed && Marshal.ReadInt32(data) == 0x2E) // VK_DELETE
        {
            var down = message == 0x0100 || message == 0x0104;
            if ((down || message == 0x0101 || message == 0x0105) &&
                HandleDeleteKey(down, (GetAsyncKeyState(0x01) & 0x8000) != 0))
                return new nint(1);
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && !_disposed)
        {
            if (message == 0x0205 && _suppressButtonUp) // WM_RBUTTONUP
            {
                _suppressButtonUp = false;
                return new nint(1);
            }
            if (message == 0x0204) // WM_RBUTTONDOWN
            {
                var point = Marshal.PtrToStructure<MouseData>(data).Point;
                var snapshot = Volatile.Read(ref _snapshot);
                if (snapshot.Targets.Any(target => target.Contains(point.X, point.Y)))
                {
                    var alt = (GetAsyncKeyState(0x12) & 0x8000) != 0; // VK_MENU
                    var target = FindTarget(point.X, point.Y, alt, FindTopWindow(point));
                    if (target is not null && !_ui.HasShutdownStarted)
                    {
                        try
                        {
                            _ui.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                            {
                                if (!_disposed) target.SetLocked(!alt);
                            }));
                        }
                        catch (InvalidOperationException)
                        {
                            return CallNextHookEx(_hook, code, message, data);
                        }
                        _suppressButtonUp = true;
                        return new nint(1);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static nint FindTopWindow(NativeMethods.Point point)
    {
        nint found = nint.Zero;
        // Rectangle queries avoid synchronous WM_NCHITTEST messages to another
        // app's UI thread from inside a global mouse hook.
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) ||
                (NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExtendedStyle).ToInt64() & NativeMethods.WsExTransparent) != 0 ||
                !NativeMethods.GetWindowRect(window, out var bounds) ||
                point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)
                return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) >= 0 && cloaked != 0) return true;
            found = window;
            return false;
        }, nint.Zero);
        return found;
    }

    private static nint GetRoot(nint window) => window == nint.Zero ? nint.Zero : GetAncestor(window, 2);
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public NativeMethods.Point Point;
        public uint MouseInfo;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int hook, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
