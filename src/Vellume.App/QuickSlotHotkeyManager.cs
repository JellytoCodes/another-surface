using System.Runtime.InteropServices;

namespace Vellume.App;

internal sealed class QuickSlotHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyIdBase = 0x5600;
    private readonly List<int> _registeredIds = [];
    private nint _windowHandle;

    public event EventHandler<int>? SlotInvoked;

    public int Register(nint windowHandle)
    {
        DisposeRegistrations();
        _windowHandle = windowHandle;
        for (var slot = 1; slot <= 9; slot++)
        {
            var identifier = HotkeyIdBase + slot;
            var virtualKey = (uint)(0x30 + slot);
            if (RegisterHotKey(
                    _windowHandle,
                    identifier,
                    ModControl | ModAlt | ModNoRepeat,
                    virtualKey))
            {
                _registeredIds.Add(identifier);
            }
        }

        return _registeredIds.Count;
    }

    public bool TryHandleMessage(int message, nint wParam)
    {
        if (message != WmHotkey)
        {
            return false;
        }

        var identifier = wParam.ToInt32();
        var slot = identifier - HotkeyIdBase;
        if (slot is not (>= 1 and <= 9))
        {
            return false;
        }

        SlotInvoked?.Invoke(this, slot);
        return true;
    }

    public void Dispose()
    {
        DisposeRegistrations();
        SlotInvoked = null;
    }

    private void DisposeRegistrations()
    {
        if (_windowHandle != nint.Zero)
        {
            foreach (var identifier in _registeredIds)
            {
                UnregisterHotKey(_windowHandle, identifier);
            }
        }

        _registeredIds.Clear();
        _windowHandle = nint.Zero;
    }

    // Registers Ctrl+Alt+1..9 while Vellume is alive, including in the tray.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    // Releases only the global shortcuts registered by this Vellume window.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        nint windowHandle,
        int identifier);
}
