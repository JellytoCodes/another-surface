using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vellume.Core;
using Size = System.Windows.Size;

namespace Vellume.Windows;

/// <summary>
/// Coordinates one bounded alpha surface per widget. Independent small windows
/// preserve click-through pixels while permitting arbitrary monitor placement.
/// </summary>
internal sealed partial class NativeWidgetRendererWindow
{
    private const double ReferenceDisplayWidth = 1920;
    private const double ReferenceDisplayHeight = 1080;
    private readonly DispatcherTimer _clockTimer;
    private SystemInfoVisual? _systemInfoVisual;
    private readonly Dictionary<WidgetKind, WidgetHostState> _widgetHosts = [];
    private readonly IMonitorService _monitorService;
    private IReadOnlyList<DisplayInfo> _availableDisplays = [];
    private DisplayInfo? _attachedDisplay;
    private nint _interactiveHostHandle;
    private WidgetPanelRenderTarget? _clockRenderTarget;
    private WidgetPanelRenderTarget? _systemRenderTarget;
    private WidgetSettings _settings = new();
    private nint _hostHandle;
    private nint _compositionParentHandle;
    private int _relativeX;
    private int _relativeY;
    private int _width;
    private int _height;
    private bool _isPaused;
    private bool _isWindowVisible = true;
    private bool _initialized;
    private bool _disposed;

    private SimpleClockVisual? _simpleClock;

    private SystemMetricsSnapshot? _lastMetrics;

    internal NativeWidgetRendererWindow(IMonitorService? monitorService = null)
    {
        _monitorService = monitorService ?? new WindowsMonitorService();
        _clockTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimerOnTick;
    }

    internal nint Handle =>
        EnumeratePanelHosts()
            .FirstOrDefault(host => host.IsReady)?
            .Handle ?? nint.Zero;

    internal IReadOnlyList<nint> PanelHandles =>
        EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .Select(host => host.Handle)
            .ToArray();

    internal IReadOnlyList<nint> PanelSurfaceHandles =>
        EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .Select(host => host.SurfaceHandle)
            .ToArray();

    internal IReadOnlyList<long> PanelPresentationGenerations =>
        EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .Select(host => host.PresentationGeneration)
            .ToArray();

    internal IReadOnlyList<int> PanelNonTransparentPixelCounts =>
        EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .Select(host => host.LastNonTransparentPixelCount)
            .ToArray();

    internal bool AllPanelsPresented =>
        EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .All(host => host.LastPresentSucceeded);

    internal bool IsPaused => _isPaused;

    internal event EventHandler<WidgetPlacementChangedEventArgs>? PlacementChanged;

    internal bool IsReady =>
        !_disposed &&
        _initialized &&
        _hostHandle != nint.Zero &&
        NativeMethods.IsWindow(_hostHandle) &&
        _compositionParentHandle != nint.Zero &&
        NativeMethods.IsWindow(_compositionParentHandle) &&
        (!_settings.IsEnabled ||
         ((!_settings.ShowClock || IsWidgetReady(WidgetKind.Clock)) &&
          (!_settings.ShowSystemInfo || !_settings.SystemInfoAppearance.HasVisibleItems || IsWidgetReady(WidgetKind.SystemInfo))));

    private bool IsWidgetReady(WidgetKind kind)
        => _widgetHosts.TryGetValue(kind, out var state) && state.Host.IsReady;

    internal void AttachTo(
        nint hostHandle,
        DisplayInfo display,
        nint interactiveHostHandle = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = CalculateResolutionScale(display.Width, display.Height);
        // Retain Explorer only as a lifetime token. Widget pixels are presented
        // by independent WS_POPUP layered windows, never by layered children.
        var compositionParentHandle = hostHandle;
        if (compositionParentHandle == nint.Zero ||
            !NativeMethods.IsWindow(compositionParentHandle))
        {
            compositionParentHandle = hostHandle;
        }
        if (!NativeMethods.GetWindowRect(
                compositionParentHandle,
                out var compositionParentRectangle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 위젯 합성 부모 영역을 읽지 못했습니다.");
        }

        var sizeChanged =
            _width != display.Width ||
            _height != display.Height;
        var nextRelativeX = display.X;
        var nextRelativeY = display.Y;
        var attachmentChanged =
            !IsAttachedTo(hostHandle) ||
            _compositionParentHandle != compositionParentHandle ||
            _relativeX != nextRelativeX ||
            _relativeY != nextRelativeY;
        _hostHandle = hostHandle;
        _compositionParentHandle = compositionParentHandle;
        _relativeX = nextRelativeX;
        _relativeY = nextRelativeY;
        _width = display.Width;
        _height = display.Height;
        _attachedDisplay = display;
        _interactiveHostHandle = interactiveHostHandle != nint.Zero
            ? interactiveHostHandle : hostHandle;
        RefreshAvailableDisplays();

        if (_initialized)
        {
            if (sizeChanged || WidgetDisplayBoundsChanged())
            {
                RebuildAlphaPanels();
                UpdateClock();
                ApplyLastMetrics();
            }
            else if (attachmentChanged)
            {
                ReattachAlphaPanels();
            }

            ApplyLayerOrder();
            ApplyWindowVisibility();
        }
    }

    internal Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            return Task.CompletedTask;
        }
        if (_hostHandle == nint.Zero ||
            !NativeMethods.IsWindow(_hostHandle))
        {
            throw new InvalidOperationException(
                "네이티브 위젯 호스트가 연결되지 않았습니다.");
        }

        _initialized = true;
        RebuildAlphaPanels();
        ApplyLayerOrder();
        ApplyWindowVisibility();
        UpdateClock();
        return Task.CompletedTask;
    }

    internal Task ApplySettingsAsync(
        WidgetSettings widgets,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nextSettings = widgets.Copy();
        RefreshAvailableDisplays();
        var panelLayoutChanged =
            !HasSameInformationPanelLayout(
                _settings,
                nextSettings) || WidgetDisplayBoundsChanged();
        _settings = nextSettings;
        if (panelLayoutChanged)
        {
            RebuildAlphaPanels();
        }
        UpdateClock();
        ApplyLastMetrics();
        UpdateClockTimer();
        return Task.CompletedTask;
    }

    internal Task UpdateSystemMetricsAsync(
        SystemMetricsSnapshot metrics)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _lastMetrics = metrics;
        if (!_isPaused)
        {
            ApplyLastMetrics();
        }
        return Task.CompletedTask;
    }

    private static bool HasSameInformationPanelLayout(
        WidgetSettings current,
        WidgetSettings next)
        => current.IsEnabled == next.IsEnabled &&
           current.ShowClock == next.ShowClock &&
           current.ShowSystemInfo == next.ShowSystemInfo &&
           current.ClockSide == next.ClockSide &&
           current.SystemInfoSide == next.SystemInfoSide &&
           current.ClockStyle == next.ClockStyle &&
           current.ClockColor == next.ClockColor &&
           current.ClockScale == next.ClockScale &&
           current.SystemInfoAppearance == next.SystemInfoAppearance &&
           HasSamePlacement(current.ClockPlacement, next.ClockPlacement) &&
           HasSamePlacement(current.SystemInfoPlacement, next.SystemInfoPlacement) &&
           HasSameStickers(current, next);

    private static bool HasSamePlacement(WidgetPlacement a, WidgetPlacement b)
        => a.Anchor == b.Anchor && a.IsLocked == b.IsLocked &&
           a.X == b.X && a.Y == b.Y &&
           string.Equals(a.DisplayId, b.DisplayId, StringComparison.OrdinalIgnoreCase);

    internal Task PauseAsync()
    {
        _isPaused = true;
        _clockTimer.Stop();
        return Task.CompletedTask;
    }

    internal Task ResumeAsync()
    {
        _isPaused = false;
        UpdateClock();
        ApplyLastMetrics();
        UpdateClockTimer();
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

    internal bool IsAttachedTo(nint hostHandle)
        => _initialized &&
           NativeMethods.IsWindow(hostHandle) &&
           _hostHandle == hostHandle &&
           EnumeratePanelHosts().All(host => host.IsAttachedTo(_compositionParentHandle));

    internal string GetAttachmentDiagnostics()
    {
        var resolutionScale = _width > 0 && _height > 0
            ? CalculateResolutionScale(_width, _height) : 0;
        var panels = EnumeratePanelHosts()
            .Where(host => host.IsReady)
            .Select(host =>
            {
                var parent = NativeMethods.GetParent(host.Handle);
                var above = NativeMethods.GetWindow(
                    host.Handle,
                    NativeMethods.GwHwndPrevious);
                var below = NativeMethods.GetWindow(
                    host.Handle,
                    NativeMethods.GwHwndNext);
                return
                    $"0x{host.Handle:X}[parent=0x{parent:X}," +
                    $"surface=0x{host.SurfaceHandle:X}," +
                    $"surfaceParent=0x{NativeMethods.GetParent(host.SurfaceHandle):X}," +
                    $"above=0x{above:X},below=0x{below:X}," +
                    $"size={host.Width}x{host.Height}," +
                    $"frame={host.PresentationGeneration}," +
                    $"alphaPixels={host.LastNonTransparentPixelCount}," +
                    $"presented={host.LastPresentSucceeded}," +
                    $"visible={NativeMethods.IsWindowVisible(host.Handle)}]";
            });
        return
            $"backend=native-top-level-alpha-skins, " +
            $"desktop=0x{_hostHandle:X}, " +
            $"desktopLifetimeToken=0x{_compositionParentHandle:X}, " +
            $"topLevelPanels=true, " +
            $"fullDisplaySurface=false, " +
            $"display={_width}x{_height}, " +
            $"resolutionScale={resolutionScale.ToString("0.###", CultureInfo.InvariantCulture)}, " +
            $"panelCount={PanelHandles.Count}, " +
            $"panels={string.Join(';', panels)}";
    }

    internal Task CloseSafelyAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _disposed = true;
        _pointerHook?.Dispose();
        _pointerHook = null;
        _initialized = false;
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimerOnTick;
        DisposeWidgetHosts();
        _clockRenderTarget = null;
        _systemRenderTarget = null;
        _hostHandle = nint.Zero;
        _compositionParentHandle = nint.Zero;
        _systemInfoVisual = null;
        return Task.CompletedTask;
    }

    private void ApplyWindowVisibility()
    {
        foreach (var host in EnumeratePanelHosts())
        {
            host.SetWindowVisible(_isWindowVisible);
        }
        foreach (var state in _widgetHosts.Values.ToArray())
        {
            state.Editor?.SetVisible(_isWindowVisible);
        }
        foreach (var state in _stickerHosts.Values.ToArray())
            state.Editor?.SetVisible(_isWindowVisible);
        RefreshPointerTargets();
    }

    private void DisposeWidgetHosts()
    {
        _pointerHook?.Update([]);
        DisposeStickerHosts();
        foreach (var state in _widgetHosts.Values)
        {
            state.Editor?.CloseSafely();
            state.Host.Dispose();
        }
        _widgetHosts.Clear();
    }

    private void RefreshAvailableDisplays()
    {
        try
        {
            _availableDisplays = _monitorService.GetDisplays();
        }
        catch (Win32Exception)
        {
            _availableDisplays = _attachedDisplay is null ? [] : [_attachedDisplay];
        }
    }

    private DisplayInfo ResolveDisplay(WidgetPlacement placement)
        => _availableDisplays.FirstOrDefault(display =>
               !string.IsNullOrWhiteSpace(placement.DisplayId) &&
               display.Id.Equals(placement.DisplayId, StringComparison.OrdinalIgnoreCase))
           ?? _attachedDisplay
           ?? _availableDisplays.First();

    private bool WidgetDisplayBoundsChanged()
        => _widgetHosts.Values.Any(state =>
        {
            var display = ResolveDisplay(_settings.GetPlacement(state.Kind));
            return display.Id != state.Display.Id || display.X != state.Display.X ||
                   display.Y != state.Display.Y || display.Width != state.Display.Width ||
                   display.Height != state.Display.Height;
        }) || _stickerHosts.Values.Any(state =>
        {
            var display = ResolveDisplay(state.Item.Placement);
            return display.Id != state.Display.Id || display.X != state.Display.X ||
                display.Y != state.Display.Y || display.Width != state.Display.Width || display.Height != state.Display.Height;
        });

    private void RebuildAlphaPanels()
    {
        DisposeWidgetHosts();
        ResetVisualReferences();
        if (!_settings.IsEnabled || !_initialized || _attachedDisplay is null ||
            _hostHandle == nint.Zero || !NativeMethods.IsWindow(_hostHandle) ||
            _compositionParentHandle == nint.Zero || !NativeMethods.IsWindow(_compositionParentHandle))
        {
            return;
        }
        RefreshAvailableDisplays();
        CreateStickerHosts();
        if (_settings.ShowClock)
        {
            CreateWidgetHost(WidgetKind.Clock, CreateClock(_settings.ClockSide));
        }
        if (_settings.ShowSystemInfo && _settings.SystemInfoAppearance.HasVisibleItems)
        {
            CreateWidgetHost(WidgetKind.SystemInfo, CreateSystemPanel(_settings.SystemInfoSide));
        }
        ApplyLayerOrder();
        ApplyWindowVisibility();
    }

    private void CreateWidgetHost(WidgetKind kind, FrameworkElement visual)
    {
        var placement = _settings.GetPlacement(kind);
        var display = ResolveDisplay(placement);
        var resolutionScale = CalculateResolutionScale(display.Width, display.Height);
        var scale = resolutionScale * _settings.GetScale(kind);
        var prepared = PrepareLayeredPanel(visual, scale);
        var position = WidgetPlacementLayout.Resolve(placement,
            display.Width, display.Height, prepared.Width, prepared.Height, 24 * resolutionScale);
        var x = display.X + (int)Math.Round(position.X);
        var y = display.Y + (int)Math.Round(position.Y);
        var host = new BoundedDesktopPanelHost($"Vellume Alpha {kind} Widget",
            _hostHandle, x, y, prepared.Width, prepared.Height);
        try
        {
            host.Render(prepared.Visual);
            var naturalWidth = double.IsNaN(visual.Width) ? visual.DesiredSize.Width : visual.Width;
            var naturalHeight = double.IsNaN(visual.Height) ? visual.DesiredSize.Height : visual.Height;
            var state = new WidgetHostState(kind, host, display, x, y, naturalWidth, naturalHeight, visual);
            _widgetHosts.Add(kind, state);
            var target = new WidgetPanelRenderTarget(kind, prepared.Visual, prepared.Width, prepared.Height);
            switch (kind)
            {
                case WidgetKind.Clock:
                    _clockRenderTarget = target;
                    break;
                case WidgetKind.SystemInfo:
                    _systemRenderTarget = target;
                    break;
            }
            if (!placement.IsLocked) CreateWidgetEditor(state);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    internal void MoveWidget(WidgetKind kind, int requestedX, int requestedY, bool completed)
    {
        if (_disposed || !_widgetHosts.TryGetValue(kind, out var state) ||
            _settings.GetPlacement(kind).IsLocked)
        {
            return;
        }
        // Choose the display under the widget centre. Native bounds are physical
        // pixels, including negative origins and displays with differing DPI.
        var centerX = requestedX + state.Host.Width / 2;
        var centerY = requestedY + state.Host.Height / 2;
        var display = _availableDisplays.FirstOrDefault(candidate =>
            centerX >= candidate.X && centerX < candidate.X + candidate.Width &&
            centerY >= candidate.Y && centerY < candidate.Y + candidate.Height) ?? state.Display;
        var resolutionScale = CalculateResolutionScale(display.Width, display.Height);
        var scale = resolutionScale * _settings.GetScale(kind);
        var placementWidth = completed
            ? Math.Max(1, (int)Math.Ceiling(state.NaturalWidth * scale)) : state.Host.Width;
        var placementHeight = completed
            ? Math.Max(1, (int)Math.Ceiling(state.NaturalHeight * scale)) : state.Host.Height;
        var placement = WidgetPlacementLayout.FromPosition(
            requestedX - display.X, requestedY - display.Y,
            display.Width, display.Height, placementWidth, placementHeight,
            24 * resolutionScale, isLocked: false, displayId: display.Id);
        var position = WidgetPlacementLayout.Resolve(placement,
            display.Width, display.Height, placementWidth, placementHeight, 24 * resolutionScale);
        state.X = display.X + (int)Math.Round(position.X);
        state.Y = display.Y + (int)Math.Round(position.Y);
        state.Display = display;
        state.Host.MoveTo(state.X, state.Y);
        state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y,
            state.Host.Width, state.Host.Height, _isWindowVisible);
        RefreshPointerTargets();
        if (completed)
        {
            _settings.SetPlacement(kind, placement);
            RebuildAlphaPanels();
            UpdateClock();
            ApplyLastMetrics();
            PlacementChanged?.Invoke(this, new WidgetPlacementChangedEventArgs(kind, placement));
        }
    }

    internal IReadOnlyList<nint> EditingHandles => _widgetHosts.Values
        .Where(state => state.Editor is not null)
        .Select(state => state.Editor!.Handle)
        .ToArray();

    private void ReattachAlphaPanels()
    {
        ReattachStickerHosts();
        foreach (var state in _widgetHosts.Values)
        {
            state.Host.AttachTo(_hostHandle, state.X, state.Y, state.Host.Width, state.Host.Height);
            state.Editor?.SetBounds(_interactiveHostHandle, state.X, state.Y,
                state.Host.Width, state.Host.Height, _isWindowVisible);
        }
    }

    internal static double CalculateResolutionScale(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Uniform reference-canvas scaling preserves the FHD design and keeps
        // it inside wide/portrait displays without stretching text or characters.
        return Math.Min(width / ReferenceDisplayWidth, height / ReferenceDisplayHeight);
    }

    private static LayeredPanelVisual PrepareLayeredPanel(
        FrameworkElement element,
        double scale)
    {
        element.Measure(new Size(
            double.PositiveInfinity,
            double.PositiveInfinity));
        var naturalWidth = double.IsNaN(element.Width)
            ? element.DesiredSize.Width
            : element.Width;
        var naturalHeight = double.IsNaN(element.Height)
            ? element.DesiredSize.Height
            : element.Height;
        var width = Math.Max(
            1,
            (int)Math.Ceiling(naturalWidth * scale));
        var height = Math.Max(
            1,
            (int)Math.Ceiling(naturalHeight * scale));
        if (Math.Abs(scale - 1) < 0.001)
        {
            return new LayeredPanelVisual(
                element,
                width,
                height);
        }

        return new LayeredPanelVisual(
            new Viewbox
            {
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                Child = element
            },
            width,
            height);
    }

    private void RenderClockPanel()
        => RenderPanel(_clockRenderTarget);

    private void RenderSystemPanel()
        => RenderPanel(_systemRenderTarget);

    private void RenderPanel(WidgetPanelRenderTarget? target)
    {
        if (target is not null &&
            _widgetHosts.TryGetValue(target.Kind, out var state) && state.Host.IsReady)
        {
            state.Host.Render(target.Visual);
        }
    }

    internal FrameworkElement CreateClock(WidgetSide side)
    {
        _simpleClock = new SimpleClockVisual(_settings.ClockStyle, _settings.ClockColor);
        _simpleClock.Update(DateTime.Now);
        return _simpleClock;
    }

    internal FrameworkElement CreateSystemPanel(WidgetSide side)
    {
        _systemInfoVisual = new SystemInfoVisual(_settings.SystemInfoAppearance with { Scale = 1 });
        return _systemInfoVisual;
    }

    internal void UpdateClock()
    {
        if (!_disposed && _simpleClock?.Update(DateTime.Now) == true)
        {
            RenderClockPanel();
        }
    }

    private void ApplyLastMetrics()
    {
        if (_lastMetrics is not { } metrics || _systemInfoVisual is null) return;
        _systemInfoVisual.Update(metrics);
        RenderSystemPanel();
    }

    private void ApplyLayerOrder()
    {
        if (!_initialized)
        {
            return;
        }

        var flags =
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate;
        ApplyBoundedPanelLayerOrder(flags);
    }

    private void ApplyBoundedPanelLayerOrder(uint flags)
    {
        foreach (var panelHost in EnumeratePanelHosts())
        {
            if (!panelHost.IsReady)
            {
                continue;
            }

            var insertAfter = NativeMethods.HwndBottom;
            if (!NativeMethods.SetWindowPos(
                    panelHost.Handle,
                    insertAfter,
                    0,
                    0,
                    0,
                    0,
                    flags))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 독립 위젯 창의 바탕화면 Z 순서를 설정하지 못했습니다.");
            }
        }
    }

    private IEnumerable<BoundedDesktopPanelHost> EnumeratePanelHosts()
        => _widgetHosts.Values.Select(state => state.Host)
            // Each successive HWND is sent to the bottom. Follow the persisted
            // front-to-back list explicitly rather than dictionary enumeration.
            .Concat(_settings.Stickers.Where(item => _stickerHosts.ContainsKey(item.Id))
                .Select(item => _stickerHosts[item.Id].Host));

    private void ClockTimerOnTick(
        object? sender,
        EventArgs eventArgs)
    {
        _clockTimer.Stop();
        UpdateClock();
        UpdateClockTimer();
    }

    private void UpdateClockTimer()
    {
        if (!_isPaused &&
            _settings.IsEnabled &&
            _settings.ShowClock)
        {
            _clockTimer.Stop();
            var now = DateTime.Now;
            var nextTick = new DateTime(
                    now.Year,
                    now.Month,
                    now.Day,
                    now.Hour,
                    now.Minute,
                    _simpleClock?.NeedsSeconds == true ? now.Second : 0,
                    now.Kind)
                .AddSeconds(_simpleClock?.NeedsSeconds == true ? 1 : 60);
            _clockTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(
                    100,
                    (nextTick - now).TotalMilliseconds + 25));
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }
    }

    private void ResetVisualReferences()
    {
        _systemInfoVisual = null;
        _simpleClock = null;
    }

    private sealed class WidgetHostState(
        WidgetKind kind, BoundedDesktopPanelHost host, DisplayInfo display, int x, int y,
        double naturalWidth, double naturalHeight, FrameworkElement content)
    {
        internal FrameworkElement Content { get; } = content;
        internal WidgetKind Kind { get; } = kind;
        internal BoundedDesktopPanelHost Host { get; } = host;
        internal DisplayInfo Display { get; set; } = display;
        internal int X { get; set; } = x;
        internal int Y { get; set; } = y;
        internal double NaturalWidth { get; } = naturalWidth;
        internal double NaturalHeight { get; } = naturalHeight;
        internal WidgetEditingOverlayWindow? Editor { get; set; }
    }

    private sealed record WidgetPanelRenderTarget(
        WidgetKind Kind,
        FrameworkElement Visual,
        int Width,
        int Height);

    private sealed record LayeredPanelVisual(
        FrameworkElement Visual,
        int Width,
        int Height);
}
