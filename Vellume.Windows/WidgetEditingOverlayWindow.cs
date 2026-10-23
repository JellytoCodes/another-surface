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
    private readonly WidgetKind? _kind;
    private readonly Border _frame;
    private nint _handle;
    private nint _desktopHandle;
    private int _x;
    private int _y;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _dragging;
    private bool _moved;
    private NativeMethods.Point _startCursor;
    private int _startX;
    private int _startY;
    private int _requestedX;
    private int _requestedY;
    private bool _closed;
    private int _transformMode;
    private double _centerX;
    private double _centerY;
    private double _startDistance;
    private double _startAngle;
    private double _factor = 1;
    private double _angle;
    private int _resizeStartWidth;
    private int _resizeStartHeight;

    internal WidgetEditingOverlayWindow(WidgetKind kind)
        : this($"Vellume {kind} Position", false, allowResize: true)
    {
        _kind = kind;
    }

    internal WidgetEditingOverlayWindow(string title, bool allowTransform, bool allowResize = false)
    {
        Title = title;
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
        var root = new Grid();
        root.Children.Add(_frame);
        if (allowTransform)
        {
            AddTransformHandle(root, "↻", "회전", 2, System.Windows.HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.Hand);
        }
        if (allowTransform || allowResize)
        {
            AddTransformHandle(root, "↘", "크기 조절", 1, System.Windows.HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        }
        Content = root;
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
    internal event Action<int, int, bool>? PositionChanged;
    internal event Action? TransformStarted;
    internal event Action<double, double, bool>? TransformChanged;
    internal event Action? Clicked;

    private void AddTransformHandle(Grid root, string text, string tooltip, int mode,
        System.Windows.HorizontalAlignment horizontal, VerticalAlignment vertical, System.Windows.Input.Cursor cursor)
    {
        var handle = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            Background = new SolidColorBrush(Color.FromRgb(97, 80, 185)), Cursor = cursor, ToolTip = tooltip,
            Child = new TextBlock { Text = text, Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (!GetPhysicalCursorPos(out _startCursor)) return;
            _transformMode = mode;
            _resizeStartWidth = _pixelWidth;
            _resizeStartHeight = _pixelHeight;
            _centerX = _x + _pixelWidth / 2d;
            _centerY = _y + _pixelHeight / 2d;
            _startDistance = Math.Max(1, Math.Sqrt(Math.Pow(_startCursor.X - _centerX, 2) + Math.Pow(_startCursor.Y - _centerY, 2)));
            _startAngle = Math.Atan2(_startCursor.Y - _centerY, _startCursor.X - _centerX);
            _factor = 1;
            _angle = 0;
            TransformStarted?.Invoke();
            _dragging = _frame.CaptureMouse();
            e.Handled = true;
        };
        root.Children.Add(handle);
    }

    private void RaiseChange(bool completed)
    {
        if (_transformMode != 0) TransformChanged?.Invoke(_factor, _angle, completed);
        else
        {
            if (_kind is { } kind) DragPositionChanged?.Invoke(kind, _requestedX, _requestedY, completed);
            PositionChanged?.Invoke(_requestedX, _requestedY, completed);
        }
    }

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
            RaiseChange(true);
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
        PositionChanged = null;
        TransformStarted = null;
        TransformChanged = null;
        Clicked = null;
        Close();
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !GetPhysicalCursorPos(out _startCursor))
        {
            return;
        }
        _startX = _x;
        _transformMode = 0;
        _startY = _y;
        _requestedX = _x;
        _requestedY = _y;
        _moved = false;
        _dragging = _frame.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || !GetPhysicalCursorPos(out var cursor))
        {
            return;
        }
        if (_transformMode == 1)
            _factor = _kind is not null
                ? Math.Max(0.01, 1 + ((double)_resizeStartWidth * (cursor.X - _startCursor.X) +
                    (double)_resizeStartHeight * (cursor.Y - _startCursor.Y)) /
                    ((double)_resizeStartWidth * _resizeStartWidth + (double)_resizeStartHeight * _resizeStartHeight))
                : Math.Sqrt(Math.Pow(cursor.X - _centerX, 2) + Math.Pow(cursor.Y - _centerY, 2)) / _startDistance;
        else if (_transformMode == 2)
            _angle = (Math.Atan2(cursor.Y - _centerY, cursor.X - _centerX) - _startAngle) * 180 / Math.PI;
        else
        {
            if (!_moved && Math.Abs(cursor.X - _startCursor.X) <= 4 && Math.Abs(cursor.Y - _startCursor.Y) <= 4) return;
            _moved = true;
            _requestedX = _startX + cursor.X - _startCursor.X;
            _requestedY = _startY + cursor.Y - _startCursor.Y;
        }
        RaiseChange(false);
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
        if (_transformMode == 0 && !_moved) Clicked?.Invoke();
        else RaiseChange(true);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            if (_transformMode != 0 || _moved) RaiseChange(true);
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
