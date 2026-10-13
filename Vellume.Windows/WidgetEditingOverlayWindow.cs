using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Vellume.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace Vellume.Windows;

/// <summary>
/// A small, non-activating edit frame above desktop icons. It only exists while
/// its widget is unlocked; the actual widget surface always stays click-through.
/// </summary>
internal sealed class WidgetEditingOverlayWindow : Window
{
    private readonly WidgetKind _kind;
    private readonly Border _frame;
    private nint _handle;
    private nint _desktopHandle;
    private int _x;
    private int _y;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _dragging;
    private NativeMethods.Point _startCursor;
    private int _startX;
    private int _startY;
    private int _requestedX;
    private int _requestedY;
    private bool _closed;

    internal WidgetEditingOverlayWindow(WidgetKind kind)
    {
        _kind = kind;
        Title = $"Vellume {_kind} Position";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Width = 100;
        Height = 100;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(4, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(175, 154, 141, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.SizeAll
        };
        Content = _frame;
        _frame.MouseLeftButtonDown += BeginDrag;
        _frame.MouseMove += ContinueDrag;
        _frame.MouseLeftButtonUp += EndDrag;
        _frame.LostMouseCapture += OnLostMouseCapture;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExtendedStyle,
                new nint(style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
            HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
        };
        Closed += (_, _) => _closed = true;
    }

    internal event Action<WidgetKind, int, int, bool>? DragPositionChanged;

    internal nint Handle => _handle;

    internal void SetBounds(nint desktopHandle, int x, int y, int width, int height, bool visible)
    {
        _desktopHandle = desktopHandle;
        _x = x;
        _y = y;
        _pixelWidth = width;
        _pixelHeight = height;
        if (visible && !IsVisible)
        {
            Show();
        }
        if (_handle != nint.Zero)
        {
            NativeMethods.SetWindowPos(_handle, nint.Zero, x, y, width, height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
            ApplyDesktopZOrder();
        }
        if (!visible && IsVisible)
        {
            Hide();
        }
    }

    internal void SetVisible(bool visible)
    {
        if (!visible && _dragging)
        {
            _dragging = false;
            _frame.ReleaseMouseCapture();
            DragPositionChanged?.Invoke(_kind, _requestedX, _requestedY, true);
        }
        if (!_closed)
        {
            SetBounds(_desktopHandle, _x, _y, _pixelWidth, _pixelHeight, visible);
        }
    }

    internal void ApplyDesktopZOrder()
    {
        if (_handle == nint.Zero || _desktopHandle == nint.Zero)
        {
            return;
        }
        // Place the frame immediately above the desktop, below normal apps.
        // A bottom-most frame would be hidden under Explorer's icon hit surface.
        var predecessor = NativeMethods.GetWindow(_desktopHandle, NativeMethods.GwHwndPrevious);
        if (predecessor == _handle)
        {
            return;
        }
        NativeMethods.SetWindowPos(_handle, predecessor, 0, 0, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
    }

    internal void CloseSafely()
    {
        if (_closed)
        {
            return;
        }
        _dragging = false;
        _frame.ReleaseMouseCapture();
        DragPositionChanged = null;
        Close();
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !GetPhysicalCursorPos(out _startCursor))
        {
            return;
        }
        _startX = _x;
        _startY = _y;
        _requestedX = _x;
        _requestedY = _y;
        _dragging = _frame.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || !GetPhysicalCursorPos(out var cursor))
        {
            return;
        }
        _requestedX = _startX + cursor.X - _startCursor.X;
        _requestedY = _startY + cursor.Y - _startCursor.Y;
        DragPositionChanged?.Invoke(_kind, _requestedX, _requestedY, false);
        e.Handled = true;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        _frame.ReleaseMouseCapture();
        DragPositionChanged?.Invoke(_kind, _requestedX, _requestedY, true);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            DragPositionChanged?.Invoke(_kind, _requestedX, _requestedY, true);
        }
    }

    private static nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int mouseActivate = 0x21;
        if (message == mouseActivate)
        {
            handled = true;
            return new nint(3); // MA_NOACTIVATE
        }
        return nint.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativeMethods.Point point);
}
