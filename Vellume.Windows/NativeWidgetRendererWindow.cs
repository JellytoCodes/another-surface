using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vellume.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Vellume.Windows;

internal enum CodexCompanionVisualState
{
    Ready,
    Thinking,
    Working,
    Complete,
    Offline
}

/// <summary>
/// Coordinates one bounded alpha surface per widget. Independent small windows
/// preserve click-through pixels while permitting arbitrary monitor placement.
/// </summary>
internal sealed partial class NativeWidgetRendererWindow
{
    private const double ReferenceDisplayWidth = 1920;
    private const double ReferenceDisplayHeight = 1080;
    private const WidgetSide CodexPanelSide = WidgetSide.Left;
    private static readonly Color MintAccent =
        MikuPalette.Mint;
    private static readonly Color MintPanelStrong =
        MikuPalette.WithAlpha(MikuPalette.Mint, 190);
    private static readonly Color MintPanelMid =
        MikuPalette.WithAlpha(MikuPalette.Teal, 170);
    private static readonly Color MintPanelSoft =
        MikuPalette.WithAlpha(MikuPalette.Mint, 58);
    private static readonly Color ReferenceNavy =
        Color.FromRgb(38, 54, 79);
    private static readonly Color ReferenceCyan =
        Color.FromRgb(67, 231, 242);
    private static readonly Color ReferenceBlue =
        Color.FromRgb(36, 201, 223);
    private static readonly Color ReferenceDateBand =
        Color.FromArgb(178, 43, 132, 168);
    private static readonly Color ReferenceMonthBand =
        Color.FromArgb(190, 61, 210, 224);
    private static readonly Color ReferenceTimeBand =
        Color.FromArgb(160, 42, 116, 153);
    private static readonly Color ReferenceYearBand =
        Color.FromArgb(158, 43, 132, 168);
    private static readonly FontFamily ReferenceFontFamily =
        new("Bahnschrift, Pretendard, Malgun Gothic");
    private static readonly Lazy<IReadOnlyDictionary<
        CodexCompanionVisualState,
        ImageSource?>> CompanionImages =
        new(CreateCompanionImages);
    private readonly DispatcherTimer _clockTimer;
    private SystemInfoVisual? _systemInfoVisual;
    private readonly Dictionary<WidgetKind, WidgetHostState> _widgetHosts = [];
    private readonly IMonitorService _monitorService;
    private IReadOnlyList<DisplayInfo> _availableDisplays = [];
    private DisplayInfo? _attachedDisplay;
    private nint _interactiveHostHandle;
    private WidgetPanelRenderTarget? _clockRenderTarget;
    private WidgetPanelRenderTarget? _systemRenderTarget;
    private WidgetPanelRenderTarget? _codexRenderTarget;
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
    private TextBlock? _codexHeadline;
    private StackPanel? _codexProjects;
    private StackPanel? _codexUsageLimits;
    private Ellipse? _codexPulse;
    private Image? _codexCharacter;
    private CodexCompanionVisualState? _lastCodexVisualState;
    private string _lastCodexContentSignature = string.Empty;

    private SystemMetricsSnapshot? _lastMetrics;
    private CodexCompanionSnapshot _lastCodex =
        CodexCompanionSnapshot.Disconnected();

    internal NativeWidgetRendererWindow(IMonitorService? monitorService = null)
    {
        _monitorService = monitorService ?? new WindowsMonitorService();
        _clockTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += ClockTimerOnTick;
        _codexNoticeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _codexNoticeTimer.Tick += CodexNoticeTimerOnTick;
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
          (!_settings.ShowSystemInfo || !_settings.SystemInfoAppearance.HasVisibleItems || IsWidgetReady(WidgetKind.SystemInfo)) &&
          (!_settings.ShowCodexCompanion || IsWidgetReady(WidgetKind.CodexCompanion))));

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
                ApplyLastCodex();
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
        ApplyLastCodex();
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
           current.ShowCodexCompanion ==
           next.ShowCodexCompanion &&
           current.ClockSide == next.ClockSide &&
           current.SystemInfoSide == next.SystemInfoSide &&
           current.ClockStyle == next.ClockStyle &&
           current.ClockColor == next.ClockColor &&
           current.ClockScale == next.ClockScale && current.CodexScale == next.CodexScale &&
           current.SystemInfoAppearance == next.SystemInfoAppearance &&
           HasSamePlacement(current.ClockPlacement, next.ClockPlacement) &&
           HasSamePlacement(current.SystemInfoPlacement, next.SystemInfoPlacement) &&
           HasSamePlacement(current.CodexPlacement, next.CodexPlacement) &&
           HasSameStickers(current, next);

    private static bool HasSamePlacement(WidgetPlacement a, WidgetPlacement b)
        => a.Anchor == b.Anchor && a.IsLocked == b.IsLocked &&
           a.X == b.X && a.Y == b.Y &&
           string.Equals(a.DisplayId, b.DisplayId, StringComparison.OrdinalIgnoreCase);

    internal Task UpdateCodexAsync(CodexCompanionSnapshot codex)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        UpdateCodexCompletion(_lastCodex, codex);
        _lastCodex = codex;
        if (!_isPaused)
        {
            ApplyLastCodex();
        }
        return Task.CompletedTask;
    }

    internal Task PauseAsync()
    {
        _isPaused = true;
        _clockTimer.Stop();
        CloseCodexDetails();
        return Task.CompletedTask;
    }

    internal Task ResumeAsync()
    {
        _isPaused = false;
        UpdateClock();
        ApplyLastMetrics();
        ApplyLastCodex();
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
        if (!visible) CloseCodexDetails();
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
        _codexNoticeTimer.Stop();
        _codexNoticeTimer.Tick -= CodexNoticeTimerOnTick;
        CloseCodexDetails();
        DisposeWidgetHosts();
        _clockRenderTarget = null;
        _systemRenderTarget = null;
        _codexRenderTarget = null;
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
        CloseCodexDetails();
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
        if (_settings.ShowCodexCompanion)
        {
            CreateWidgetHost(WidgetKind.CodexCompanion, CreateCodexPanel(CodexPanelSide));
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
                case WidgetKind.CodexCompanion:
                    _codexRenderTarget = target;
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
        if (kind == WidgetKind.CodexCompanion) CloseCodexDetails();
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
            ApplyLastCodex();
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

    private void RenderCodexPanel()
        => RenderPanel(_codexRenderTarget);

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

    private void UpdateCodexProjectRows()
    {
        if (_codexProjects is null)
        {
            return;
        }

        _codexProjects.Children.Clear();
        var projects = _lastCodex.Projects.Take(3).ToArray();
        if (projects.Length == 0)
        {
            var empty = CreateText(
                12,
                FontWeights.SemiBold,
                Color.FromArgb(190, 255, 255, 255));
            empty.Text = "표시할 프로젝트가 없습니다";
            empty.Margin = new Thickness(0, 6, 0, 3);
            _codexProjects.Children.Add(empty);
            return;
        }

        for (var index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            var row = new Grid
            {
                Margin = new Thickness(0, 3, 0, 3)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(16)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            var marker = new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 6, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Fill = new SolidColorBrush(
                    GetCodexStateColor(project.State))
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var text = new StackPanel();
            var title = CreateText(
                13,
                FontWeights.Bold,
                Colors.White);
            title.Text = TruncateWithEllipsis(
                project.Title,
                30);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.MaxWidth = 235;
            var phase = CreateText(
                11,
                FontWeights.SemiBold,
                Color.FromArgb(205, 255, 255, 255));
            phase.Text = TruncateWithEllipsis(
                project.Phase,
                36);
            phase.TextTrimming = TextTrimming.CharacterEllipsis;
            phase.MaxWidth = 235;
            phase.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(title);
            text.Children.Add(phase);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _codexProjects.Children.Add(row);
        }
    }

    private void UpdateCodexUsageRows()
    {
        if (_codexUsageLimits is null)
        {
            return;
        }

        _codexUsageLimits.Children.Clear();
        var usage = _lastCodex.Usage;
        if (!usage.IsAvailable || usage.Limits.Count == 0)
        {
            var unavailable = CreateText(
                12,
                FontWeights.Bold,
                Colors.White);
            unavailable.Text = "사용량 확인 불가";
            var hint = CreateText(
                10.5,
                FontWeights.SemiBold,
                Color.FromArgb(220, 255, 255, 255));
            hint.Text = "Codex 연결 후 자동 갱신";
            hint.Margin = new Thickness(0, 4, 0, 0);
            _codexUsageLimits.Children.Add(unavailable);
            _codexUsageLimits.Children.Add(hint);
            return;
        }

        foreach (var limit in usage.Limits.Take(2))
        {
            var row = new StackPanel
            {
                Margin = _codexUsageLimits.Children.Count == 0
                    ? new Thickness(0)
                    : new Thickness(0, 10, 0, 0)
            };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            var model = CreateText(
                11.5,
                FontWeights.Bold,
                Colors.White);
            model.Text = limit.DisplayName;
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            model.MaxWidth = 138;
            var remaining = CreateText(
                11,
                FontWeights.Bold,
                Colors.White);
            remaining.Text = $"{limit.RemainingPercent}%";
            remaining.HorizontalAlignment =
                HorizontalAlignment.Right;
            Grid.SetColumn(remaining, 1);
            header.Children.Add(model);
            header.Children.Add(remaining);
            row.Children.Add(header);

            var reset = CreateText(
                10,
                FontWeights.SemiBold,
                Color.FromArgb(230, 255, 255, 255));
            reset.Text = limit.ResetsAtUtc is { } resetsAt
                ? $"초기화 {resetsAt.ToLocalTime():MM.dd HH:mm}"
                : "초기화 일시 확인 불가";
            reset.Margin = new Thickness(0, 2, 0, 4);
            row.Children.Add(reset);

            var progress = new Grid
            {
                Width = 189,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = true
            };
            progress.Children.Add(new Border
            {
                Width = 189,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(
                    Color.FromArgb(90, 27, 70, 94))
            });
            progress.Children.Add(new Border
            {
                Width = 189 *
                    Math.Clamp(
                        limit.RemainingPercent,
                        0,
                        100) / 100d,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(
                    MikuPalette.Pink)
            });
            row.Children.Add(progress);
            _codexUsageLimits.Children.Add(row);
        }
    }

    private static Color GetCodexStateColor(string state)
        => state switch
        {
            "complete" => MikuPalette.Mint,
            "offline" or "disconnected" =>
                MikuPalette.Silver,
            _ => MikuPalette.Pink
        };

    internal static string TruncateWithEllipsis(
        string? value,
        int maximumCharacters)
    {
        var text = value?.Trim() ?? string.Empty;
        if (maximumCharacters <= 1 ||
            text.Length <= maximumCharacters)
        {
            return text;
        }

        return string.Concat(
            text.AsSpan(0, maximumCharacters - 1),
            "…");
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

    private void AddPositioned(
        Canvas canvas,
        FrameworkElement element,
        WidgetSide side,
        double margin,
        double top,
        double scale)
    {
        element.RenderTransformOrigin = new Point(
            side == WidgetSide.Left ? 0 : 1,
            0);
        element.RenderTransform = new ScaleTransform(
            scale,
            scale);
        Canvas.SetLeft(
            element,
            side == WidgetSide.Left
                ? margin
                : _width - margin - element.Width);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
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
        CloseCodexDetails();
        _systemInfoVisual = null;
        _simpleClock = null;
        _codexHeadline = null;
        _codexProjects = null;
        _codexUsageLimits = null;
        _codexPulse = null;
        _codexCharacter = null;
        _lastCodexVisualState = null;
        _lastCodexContentSignature = string.Empty;
    }

    private static TextBlock CreateText(
        double fontSize,
        FontWeight fontWeight,
        Color color)
        => new()
        {
            FontFamily = new FontFamily(
                "Pretendard, Malgun Gothic, Segoe UI"),
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = new SolidColorBrush(color),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

    private static VectorTextBlock CreateVectorText(
        double fontSize,
        FontWeight fontWeight,
        Color color)
        => new()
        {
            FontFamily = ReferenceFontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FillColor = color,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            ClipToBounds = false
        };

    private static VectorTextBlock CreateOutlinedVectorText(
        double fontSize,
        FontWeight fontWeight,
        Color color,
        double outlineThickness,
        Color? outlineColor = null)
    {
        var text = CreateVectorText(
            fontSize,
            fontWeight,
            color);
        text.OutlineColor = outlineColor ?? Colors.White;
        text.OutlineThickness = outlineThickness;
        return text;
    }

    private static LinearGradientBrush CreateGradient(
        bool leftToRight,
        Color start,
        Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = leftToRight
                ? new Point(0, 0.5)
                : new Point(1, 0.5),
            EndPoint = leftToRight
                ? new Point(1, 0.5)
                : new Point(0, 0.5)
        };
        brush.GradientStops.Add(
            new GradientStop(start, 0));
        brush.GradientStops.Add(
            new GradientStop(end, 1));
        return brush;
    }

    internal static CodexCompanionVisualState
        ResolveCodexCompanionVisualState(
            CodexCompanionSnapshot snapshot)
    {
        var state = snapshot.State.Trim().ToLowerInvariant();
        if (state is "offline" or "disconnected")
        {
            return CodexCompanionVisualState.Offline;
        }
        if (state == "complete")
        {
            return CodexCompanionVisualState.Complete;
        }
        if (state == "thinking" ||
            ContainsThinkingSignal(snapshot.Headline) ||
            ContainsThinkingSignal(snapshot.Detail) ||
            snapshot.Projects.Any(project =>
                ContainsThinkingSignal(project.Phase)))
        {
            return CodexCompanionVisualState.Thinking;
        }
        if (state == "working")
        {
            return CodexCompanionVisualState.Working;
        }
        return CodexCompanionVisualState.Ready;
    }

    private static bool ContainsThinkingSignal(string value)
        => value.Contains(
               "생각",
               StringComparison.OrdinalIgnoreCase) ||
           value.Contains(
               "요청 분석",
               StringComparison.OrdinalIgnoreCase);

    private static ImageSource? GetCompanionImage(
        CodexCompanionVisualState state)
    {
        var images = CompanionImages.Value;
        return images.TryGetValue(state, out var image)
            ? image
            : images[CodexCompanionVisualState.Ready];
    }

    private static IReadOnlyDictionary<
        CodexCompanionVisualState,
        ImageSource?> CreateCompanionImages()
    {
        var ready = LoadCompanionImage(
            "vellume-companion.png",
            removeChromaKey: false);
        return new Dictionary<
            CodexCompanionVisualState,
            ImageSource?>
        {
            [CodexCompanionVisualState.Ready] = ready,
            [CodexCompanionVisualState.Thinking] =
                LoadCompanionImage(
                    "vellume-companion-thinking.png",
                    removeChromaKey: true) ?? ready,
            [CodexCompanionVisualState.Working] =
                LoadCompanionImage(
                    "vellume-companion-working.png",
                    removeChromaKey: true) ?? ready,
            [CodexCompanionVisualState.Complete] =
                LoadCompanionImage(
                    "vellume-companion-complete.png",
                    removeChromaKey: true) ?? ready,
            [CodexCompanionVisualState.Offline] =
                LoadCompanionImage(
                    "vellume-companion-offline.png",
                    removeChromaKey: true) ?? ready
        };
    }

    private static ImageSource? LoadCompanionImage(
        string fileName,
        bool removeChromaKey)
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Companion",
                fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(
                path,
                UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            if (!removeChromaKey)
            {
                return bitmap;
            }

            var converted = new FormatConvertedBitmap(
                bitmap,
                PixelFormats.Bgra32,
                null,
                0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[
                stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            ApplyCompanionChromaKey(pixels);
            var transparent = BitmapSource.Create(
                converted.PixelWidth,
                converted.PixelHeight,
                converted.DpiX,
                converted.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            transparent.Freeze();
            return transparent;
        }
        catch
        {
            return null;
        }
    }

    internal static void ApplyCompanionChromaKey(
        Span<byte> pixels)
    {
        const int softEdgeStart = 25;
        const int fullyTransparent = 85;
        for (var index = 0;
             index + 3 < pixels.Length;
             index += 4)
        {
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            var dominantGreen =
                green - Math.Max(red, blue);
            if (green < 100 ||
                dominantGreen <= softEdgeStart)
            {
                continue;
            }

            var alpha = dominantGreen >= fullyTransparent
                ? 0
                : (fullyTransparent - dominantGreen) * 255 /
                  (fullyTransparent - softEdgeStart);
            pixels[index + 3] = (byte)(
                pixels[index + 3] * alpha / 255);
            if (alpha > 0)
            {
                pixels[index + 1] = (byte)Math.Min(
                    green,
                    Math.Max(red, blue) + 12);
            }
        }
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
