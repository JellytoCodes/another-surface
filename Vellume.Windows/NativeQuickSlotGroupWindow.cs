using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Vellume.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace Vellume.Windows;

/// <summary>
/// Draws one quick-slot category with native WPF primitives inside a child
/// HWND. Runtime state changes update only badges and never rebuild a browser.
/// </summary>
internal sealed class NativeQuickSlotGroupWindow
{
    private readonly string _groupId;
    private readonly WindowsIconService _iconService = new();
    private readonly Dictionary<int, SlotVisualState> _slotVisuals = [];
    private NativeWpfChildHost? _nativeHost;
    private HwndSource? _source;
    private nint _interactiveHostHandle;
    private int _relativeX;
    private int _relativeY;
    private int _displayOriginX;
    private int _displayOriginY;
    private int _displayWidth;
    private int _displayHeight;
    private int _width;
    private int _height;
    private bool _snapToGrid;
    private int _gridSize = 40;
    private QuickSlotGroup _group = new();
    private IReadOnlyList<QuickSlotItem> _items = [];
    private ImageSource? _baseTexture;
    private bool _dragging;
    private bool _isWindowVisible = true;
    private NativeMethods.Point _dragStartCursor;
    private int _dragStartX;
    private int _dragStartY;
    private bool _disposed;

    internal NativeQuickSlotGroupWindow(string groupId)
    {
        _groupId = groupId;
        _baseTexture = LoadBaseTexture();
    }

    internal nint Handle => _nativeHost?.Handle ?? nint.Zero;
    internal bool IsReady =>
        !_disposed &&
        _nativeHost?.IsReady == true;

    internal event EventHandler<int>? SlotInvoked;
    internal event EventHandler<int>? SlotSecondaryInvoked;
    internal event EventHandler<QuickSlotLayoutChangedEventArgs>?
        LayoutChanged;

    internal void AttachTo(
        nint interactiveHostHandle,
        DisplayInfo display,
        QuickSlotGroup group,
        int itemCount,
        bool snapToGrid,
        int gridSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.GetWindowRect(
                interactiveHostHandle,
                out var hostRectangle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 상호작용 데스크톱 영역을 읽지 못했습니다.");
        }

        _interactiveHostHandle = interactiveHostHandle;
        _group = group.Copy();
        var columns = Math.Clamp(group.Columns, 1, 10);
        var rows = Math.Max(
            1,
            (int)Math.Ceiling(itemCount / (double)columns));
        _width = 16 + columns * 68 + (columns - 1) * 7;
        _height = 46 + rows * 68 + (rows - 1) * 7;
        _displayOriginX = display.X - hostRectangle.Left;
        _displayOriginY = display.Y - hostRectangle.Top;
        _displayWidth = display.Width;
        _displayHeight = display.Height;
        _snapToGrid = snapToGrid;
        _gridSize = Math.Clamp(gridSize, 10, 200);
        var requestedX = _snapToGrid
            ? SnapCoordinate(group.X, _gridSize)
            : group.X;
        var requestedY = _snapToGrid
            ? SnapCoordinate(group.Y, _gridSize)
            : group.Y;
        _relativeX = _displayOriginX + Math.Clamp(
            requestedX,
            0,
            Math.Max(0, _displayWidth - _width));
        _relativeY = _displayOriginY + Math.Clamp(
            requestedY,
            0,
            Math.Max(0, _displayHeight - _height));

        if (_nativeHost is not null)
        {
            _nativeHost.AttachTo(
                interactiveHostHandle,
                _relativeX,
                _relativeY,
                _width,
                _height);
            BuildVisualTree();
            ApplyInteractiveRegion();
            ApplyWindowVisibility();
        }
    }

    internal Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_source is not null)
        {
            return Task.CompletedTask;
        }

        _nativeHost = new NativeWpfChildHost(
            $"Vellume Native Quick Slot {_groupId}",
            _interactiveHostHandle,
            _relativeX,
            _relativeY,
            _width,
            _height,
            clickThrough: false);
        _source = _nativeHost.Source;
        BuildVisualTree();
        ApplyInteractiveRegion();
        ApplyWindowVisibility();
        return Task.CompletedTask;
    }

    internal void SetWindowVisible(bool visible)
    {
        if (_disposed || _isWindowVisible == visible)
        {
            return;
        }

        _isWindowVisible = visible;
        ApplyWindowVisibility();
    }

    internal Task RenderAsync(
        QuickSlotGroup group,
        IReadOnlyList<QuickSlotItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _group = group.Copy();
        _items = items.Select(item => item.Copy()).ToArray();
        BuildVisualTree();
        ApplyInteractiveRegion();
        return Task.CompletedTask;
    }

    internal bool IsAttachedTo(nint interactiveHostHandle)
        => _nativeHost?.IsAttachedTo(
            interactiveHostHandle) == true;

    internal Task UpdateRuntimeStatesAsync(
        IReadOnlyList<QuickSlotItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        foreach (var item in items)
        {
            if (!_slotVisuals.TryGetValue(
                    item.Slot,
                    out var visual))
            {
                continue;
            }

            ApplyRuntimeState(
                visual,
                item.IsRunning,
                item.HasAttention);
        }

        return Task.CompletedTask;
    }

    internal string GetAttachmentDiagnostics()
    {
        var handle = Handle;
        var parent = NativeMethods.GetParent(handle);
        var above = NativeMethods.GetWindow(
            handle,
            NativeMethods.GwHwndPrevious);
        var below = NativeMethods.GetWindow(
            handle,
            NativeMethods.GwHwndNext);
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            handle,
            NativeMethods.GwlExtendedStyle).ToInt64();
        NativeMethods.GetWindowRect(handle, out var rectangle);
        return
            $"backend=native-wpf, group={_groupId}, " +
            $"handle=0x{handle:X}, parent=0x{parent:X}, " +
            $"above=0x{above:X}({GetWindowClass(above)}), " +
            $"below=0x{below:X}({GetWindowClass(below)}), " +
            $"ex-style=0x{extendedStyle:X8}, " +
            $"rect={rectangle.Left},{rectangle.Top}-" +
            $"{rectangle.Right},{rectangle.Bottom}";
    }

    internal Task CloseSafelyAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _disposed = true;
        _nativeHost?.Dispose();
        _nativeHost = null;
        _source = null;
        _slotVisuals.Clear();
        return Task.CompletedTask;
    }

    private void BuildVisualTree()
    {
        if (_source is null)
        {
            return;
        }

        var canvas = new Canvas
        {
            Width = _width,
            Height = _height,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        canvas.Children.Add(CreateHeader());
        _slotVisuals.Clear();
        var columns = Math.Clamp(_group.Columns, 1, 10);
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var column = index % columns;
            var row = index / columns;
            var slot = CreateSlot(item);
            Canvas.SetLeft(slot, 8 + column * 75);
            Canvas.SetTop(slot, 38 + row * 75);
            canvas.Children.Add(slot);
        }

        _source.RootVisual = canvas;
    }

    private void ApplyWindowVisibility()
    {
        var handle = Handle;
        if (handle == nint.Zero ||
            !NativeMethods.IsWindow(handle))
        {
            return;
        }

        NativeMethods.ShowWindow(
            handle,
            _isWindowVisible
                ? NativeMethods.SwShowNoActivate
                : NativeMethods.SwHide);
    }

    private FrameworkElement CreateHeader()
    {
        var width = CalculateHeaderWidth(_group.Name, _width);
        var text = new TextBlock
        {
            Text = _group.Name,
            Margin = new Thickness(12, 0, 12, 0),
            Foreground = new SolidColorBrush(
                Color.FromRgb(222, 255, 255)),
            FontFamily = new FontFamily(
                "Pretendard, Malgun Gothic"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Border
        {
            Width = width - 8,
            Height = 28,
            Margin = new Thickness(8, 1, 0, 0),
            CornerRadius = new CornerRadius(3, 9, 3, 9),
            Background = new SolidColorBrush(
                Color.FromArgb(225, 7, 38, 58)),
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(150, 85, 235, 247)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = text,
            Cursor = Cursors.SizeAll
        };
        header.MouseLeftButtonDown += HeaderOnMouseLeftButtonDown;
        header.MouseMove += HeaderOnMouseMove;
        header.MouseLeftButtonUp += HeaderOnMouseLeftButtonUp;
        return header;
    }

    private FrameworkElement CreateSlot(QuickSlotItem item)
    {
        var root = new Grid
        {
            Width = 68,
            Height = 68,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(item.Name)
                ? Path.GetFileNameWithoutExtension(item.TargetPath)
                : item.Name
        };
        var icon = _iconService.GetIcon(
            item.TargetPath,
            item.IconPath,
            item.SystemKind,
            item.IsDynamicStateActive);
        if (_baseTexture is not null)
        {
            root.Children.Add(new Image
            {
                Source = _baseTexture,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            });
        }

        var iconHost = new Grid
        {
            Margin = new Thickness(7),
            Clip = new EllipseGeometry(
                new Point(27, 27),
                27,
                27),
            IsHitTestVisible = false
        };
        if (icon is not null)
        {
            var iconImage = new Image
            {
                Source = icon,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(
                iconImage,
                BitmapScalingMode.HighQuality);
            iconHost.Children.Add(iconImage);
        }
        else
        {
            iconHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(27),
                Background = new SolidColorBrush(
                    Color.FromRgb(23, 80, 104)),
                Child = new TextBlock
                {
                    Text = GetMonogram(item.Name),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 18,
                    HorizontalAlignment =
                        HorizontalAlignment.Center,
                    VerticalAlignment =
                        VerticalAlignment.Center
                }
            });
        }
        root.Children.Add(iconHost);

        var running = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(
                Color.FromRgb(220, 252, 255)),
            Stroke = new SolidColorBrush(
                Color.FromRgb(6, 47, 65)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false
        };
        var attention = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(
                Color.FromRgb(255, 73, 121)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "!",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            IsHitTestVisible = false
        };
        root.Children.Add(running);
        root.Children.Add(attention);
        var visual = new SlotVisualState(running, attention);
        _slotVisuals[item.Slot] = visual;
        ApplyRuntimeState(
            visual,
            item.IsRunning,
            item.HasAttention);

        root.MouseLeftButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            SlotInvoked?.Invoke(this, item.Slot);
        };
        root.MouseRightButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            SlotSecondaryInvoked?.Invoke(this, item.Slot);
        };
        root.MouseEnter += (_, _) =>
        {
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = new ScaleTransform(1.06, 1.06);
        };
        root.MouseLeave += (_, _) =>
        {
            root.RenderTransform = Transform.Identity;
        };
        return root;
    }

    private void HeaderOnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!NativeMethods.GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        _dragging = true;
        _dragStartX = _relativeX;
        _dragStartY = _relativeY;
        ((UIElement)sender).CaptureMouse();
        eventArgs.Handled = true;
    }

    private void HeaderOnMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (!_dragging ||
            eventArgs.LeftButton != MouseButtonState.Pressed ||
            !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        MoveTo(
            _dragStartX + cursor.X - _dragStartCursor.X,
            _dragStartY + cursor.Y - _dragStartCursor.Y);
        eventArgs.Handled = true;
    }

    private void HeaderOnMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        SnapToPlacementGrid();
        LayoutChanged?.Invoke(
            this,
            new QuickSlotLayoutChangedEventArgs(
                _groupId,
                _relativeX - _displayOriginX,
                _relativeY - _displayOriginY));
        eventArgs.Handled = true;
    }

    private void MoveTo(int x, int y)
    {
        var maximumX =
            _displayOriginX + Math.Max(0, _displayWidth - _width);
        var maximumY =
            _displayOriginY + Math.Max(0, _displayHeight - _height);
        _relativeX = Math.Clamp(
            x,
            _displayOriginX,
            maximumX);
        _relativeY = Math.Clamp(
            y,
            _displayOriginY,
            maximumY);
        PositionWindow();
    }

    private void SnapToPlacementGrid()
    {
        if (!_snapToGrid)
        {
            return;
        }

        var displayX = _relativeX - _displayOriginX;
        var displayY = _relativeY - _displayOriginY;
        MoveTo(
            _displayOriginX +
            SnapCoordinate(displayX, _gridSize),
            _displayOriginY +
            SnapCoordinate(displayY, _gridSize));
    }

    private void PositionWindow()
    {
        if (_nativeHost is null)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 네이티브 퀵슬롯 위치를 지정하지 못했습니다.");
        }

        _nativeHost.Position(
            _relativeX,
            _relativeY,
            _width,
            _height);
    }

    private void ApplyInteractiveRegion()
    {
        if (_source is null)
        {
            return;
        }

        var combined = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (combined == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 퀵슬롯 영역을 만들지 못했습니다.");
        }

        try
        {
            var headerWidth = CalculateHeaderWidth(
                _group.Name,
                _width);
            AddPolygon(
                combined,
                [
                    new NativeMethods.Point(8, 1),
                    new NativeMethods.Point(headerWidth - 8, 1),
                    new NativeMethods.Point(headerWidth, 8),
                    new NativeMethods.Point(headerWidth, 29),
                    new NativeMethods.Point(16, 29),
                    new NativeMethods.Point(8, 21)
                ]);

            var columns = Math.Clamp(_group.Columns, 1, 10);
            for (var index = 0; index < _items.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var slotX = 8 + column * 75;
                var slotY = 38 + row * 75;
                AddEllipse(
                    combined,
                    slotX - 3,
                    slotY - 3,
                    slotX + 71,
                    slotY + 71);
            }

            if (NativeMethods.SetWindowRgn(
                    _nativeHost!.Handle,
                    combined,
                    true) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 퀵슬롯 영역을 적용하지 못했습니다.");
            }

            combined = nint.Zero;
        }
        finally
        {
            if (combined != nint.Zero)
            {
                NativeMethods.DeleteObject(combined);
            }
        }
    }

    private static void AddPolygon(
        nint destination,
        NativeMethods.Point[] points)
    {
        var polygon = NativeMethods.CreatePolygonRgn(
            points,
            points.Length,
            NativeMethods.AlternateFillMode);
        try
        {
            if (polygon == nint.Zero ||
                NativeMethods.CombineRgn(
                    destination,
                    destination,
                    polygon,
                    NativeMethods.RegionOr) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 퀵슬롯 헤더 영역을 합치지 못했습니다.");
            }
        }
        finally
        {
            if (polygon != nint.Zero)
            {
                NativeMethods.DeleteObject(polygon);
            }
        }
    }

    private static void AddEllipse(
        nint destination,
        int left,
        int top,
        int right,
        int bottom)
    {
        var ellipse = NativeMethods.CreateEllipticRgn(
            left,
            top,
            right,
            bottom);
        try
        {
            if (ellipse == nint.Zero ||
                NativeMethods.CombineRgn(
                    destination,
                    destination,
                    ellipse,
                    NativeMethods.RegionOr) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 퀵슬롯 아이콘 영역을 합치지 못했습니다.");
            }
        }
        finally
        {
            if (ellipse != nint.Zero)
            {
                NativeMethods.DeleteObject(ellipse);
            }
        }
    }

    private ImageSource? LoadBaseTexture()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Launcher",
                "quick-slot-base-1024.png");
            if (!File.Exists(path))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyRuntimeState(
        SlotVisualState visual,
        bool isRunning,
        bool hasAttention)
    {
        visual.Running.Visibility =
            isRunning && !hasAttention
                ? Visibility.Visible
                : Visibility.Collapsed;
        visual.Attention.Visibility =
            hasAttention
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static int CalculateHeaderWidth(
        string name,
        int windowWidth)
        => Math.Min(
            windowWidth,
            Math.Clamp(32 + name.Trim().Length * 9, 88, 180));

    internal static int SnapCoordinate(int value, int gridSize)
        => Math.Max(
            0,
            (int)Math.Round(
                value / (double)gridSize,
                MidpointRounding.AwayFromZero) * gridSize);

    private static string GetMonogram(string name)
    {
        var value = string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim();
        return string.Concat(value.Take(2)).ToUpperInvariant();
    }

    private static string GetWindowClass(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return "none";
        }

        var className = new System.Text.StringBuilder(128);
        return NativeMethods.GetClassNameW(
            windowHandle,
            className,
            className.Capacity) > 0
            ? className.ToString()
            : "unknown";
    }

    private sealed record SlotVisualState(
        Ellipse Running,
        Border Attention);
}
