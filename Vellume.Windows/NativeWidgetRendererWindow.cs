using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
using ShapePath = System.Windows.Shapes.Path;
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
/// Coordinates at most one bounded alpha surface per desktop side. Widgets
/// remain logically independent, but panels sharing a side are rasterized
/// into one premultiplied BGRA surface without a separate redirection parent.
/// </summary>
internal sealed class NativeWidgetRendererWindow
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
    private readonly Dictionary<string, GaugeVisual> _gauges =
        new(StringComparer.Ordinal);
    private BoundedDesktopPanelHost? _leftPanelHost;
    private BoundedDesktopPanelHost? _rightPanelHost;
    private FrameworkElement? _leftPanelGroupVisual;
    private FrameworkElement? _rightPanelGroupVisual;
    private FrameworkElement? _clockPanelVisual;
    private FrameworkElement? _systemPanelVisual;
    private FrameworkElement? _codexPanelVisual;
    private WidgetPanelRenderTarget? _clockRenderTarget;
    private WidgetPanelRenderTarget? _systemRenderTarget;
    private WidgetPanelRenderTarget? _codexRenderTarget;
    private int _leftPanelX;
    private int _leftPanelY;
    private int _rightPanelX;
    private int _rightPanelY;
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

    private VectorTextBlock? _clockYear;
    private VectorTextBlock? _clockWeekday;
    private VectorTextBlock? _clockDay;
    private VectorTextBlock? _clockMonth;
    private VectorTextBlock? _clockTime;
    private TextBlock? _codexHeadline;
    private StackPanel? _codexProjects;
    private StackPanel? _codexUsageLimits;
    private Ellipse? _codexPulse;
    private Image? _codexCharacter;
    private TranslateTransform? _codexTranslate;
    private CodexCompanionVisualState? _lastCodexVisualState;
    private string _lastCodexState = string.Empty;
    private string _lastCodexContentSignature = string.Empty;

    private SystemMetricsSnapshot? _lastMetrics;
    private CodexCompanionSnapshot _lastCodex =
        CodexCompanionSnapshot.Disconnected();

    internal NativeWidgetRendererWindow()
    {
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

    internal bool IsReady =>
        !_disposed &&
        _initialized &&
        _hostHandle != nint.Zero &&
        NativeMethods.IsWindow(_hostHandle) &&
        _compositionParentHandle != nint.Zero &&
        NativeMethods.IsWindow(_compositionParentHandle) &&
        (!_settings.IsEnabled ||
         !HasPanelsOnSide(WidgetSide.Left) ||
         _leftPanelHost?.IsReady == true) &&
        (!_settings.IsEnabled ||
         !HasPanelsOnSide(WidgetSide.Right) ||
         _rightPanelHost?.IsReady == true);

    internal void AttachTo(
        nint hostHandle,
        DisplayInfo display)
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

        if (_initialized)
        {
            if (sizeChanged)
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
        var panelLayoutChanged =
            !HasSameInformationPanelLayout(
                _settings,
                nextSettings);
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
           current.SystemInfoSide == next.SystemInfoSide;

    internal Task UpdateCodexAsync(CodexCompanionSnapshot codex)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

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
        StopCodexAnimation();
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
        ApplyWindowVisibility();
    }

    internal bool IsAttachedTo(nint hostHandle)
        => _initialized &&
           NativeMethods.IsWindow(hostHandle) &&
           _hostHandle == hostHandle &&
           (_leftPanelHost is null ||
            _leftPanelHost.IsAttachedTo(
                _compositionParentHandle)) &&
           (_rightPanelHost is null ||
            _rightPanelHost.IsAttachedTo(
                _compositionParentHandle));

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
        _initialized = false;
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimerOnTick;
        StopCodexAnimation();
        _leftPanelHost?.Dispose();
        _rightPanelHost?.Dispose();
        _leftPanelHost = null;
        _rightPanelHost = null;
        _leftPanelGroupVisual = null;
        _rightPanelGroupVisual = null;
        _clockPanelVisual = null;
        _systemPanelVisual = null;
        _codexPanelVisual = null;
        _clockRenderTarget = null;
        _systemRenderTarget = null;
        _codexRenderTarget = null;
        _hostHandle = nint.Zero;
        _compositionParentHandle = nint.Zero;
        _gauges.Clear();
        return Task.CompletedTask;
    }

    private void ApplyWindowVisibility()
    {
        foreach (var host in EnumeratePanelHosts())
        {
            host.SetWindowVisible(_isWindowVisible);
        }
    }

    private void RebuildAlphaPanels()
    {
        _leftPanelHost?.Dispose();
        _rightPanelHost?.Dispose();
        _leftPanelHost = null;
        _rightPanelHost = null;
        _leftPanelGroupVisual = null;
        _rightPanelGroupVisual = null;
        _clockPanelVisual = null;
        _systemPanelVisual = null;
        _codexPanelVisual = null;
        ResetVisualReferences();
        if (!_settings.IsEnabled ||
            !_initialized ||
            _hostHandle == nint.Zero ||
            !NativeMethods.IsWindow(_hostHandle) ||
            _compositionParentHandle == nint.Zero ||
            !NativeMethods.IsWindow(_compositionParentHandle))
        {
            return;
        }

        // DisplayInfo and alpha HWND bounds use physical pixels. Do not multiply
        // by Windows DPI again: scale the vector layout before rasterization.
        var scale = CalculateResolutionScale(_width, _height);
        var panels = new List<WidgetPanelPlacement>(4);
        if (_settings.ShowClock)
        {
            var prepared = PrepareLayeredPanel(
                CreateClock(_settings.ClockSide),
                scale);
            var panelX = CalculatePanelX(
                _settings.ClockSide,
                25 * scale,
                prepared.Width);
            _clockPanelVisual = prepared.Visual;
            panels.Add(new WidgetPanelPlacement(
                _settings.ClockSide,
                prepared.Visual,
                panelX,
                ScalePosition(28, scale),
                prepared.Width,
                prepared.Height));
        }

        if (_settings.ShowSystemInfo)
        {
            var prepared = PrepareLayeredPanel(
                CreateSystemPanel(_settings.SystemInfoSide),
                scale);
            var panelX = CalculatePanelX(
                _settings.SystemInfoSide,
                25 * scale,
                prepared.Width);
            _systemPanelVisual = prepared.Visual;
            panels.Add(new WidgetPanelPlacement(
                _settings.SystemInfoSide,
                prepared.Visual,
                panelX,
                ScalePosition(GetSystemPanelTop(), scale),
                prepared.Width,
                prepared.Height));
        }

        if (_settings.ShowCodexCompanion)
        {
            var side = CodexPanelSide;
            var prepared = PrepareLayeredPanel(
                CreateCodexPanel(side),
                scale);
            var panelX = CalculatePanelX(
                side,
                28 * scale,
                prepared.Width);
            _codexPanelVisual = prepared.Visual;
            panels.Add(new WidgetPanelPlacement(
                side,
                prepared.Visual,
                panelX,
                ScalePosition(28, scale),
                prepared.Width,
                prepared.Height));
        }

        CreateSidePanel(WidgetSide.Left, panels);
        CreateSidePanel(WidgetSide.Right, panels);
        ApplyLayerOrder();
        ApplyWindowVisibility();
    }

    private void CreateSidePanel(
        WidgetSide side,
        IReadOnlyList<WidgetPanelPlacement> panels)
    {
        var sidePanels = panels
            .Where(panel => panel.Side == side)
            .ToArray();
        if (sidePanels.Length == 0)
        {
            return;
        }

        var left = sidePanels.Min(panel => panel.X);
        var top = sidePanels.Min(panel => panel.Y);
        var right = sidePanels.Max(
            panel => panel.X + panel.Width);
        var bottom = sidePanels.Max(
            panel => panel.Y + panel.Height);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var group = new Canvas
        {
            Width = width,
            Height = height,
            ClipToBounds = false,
            Background = Brushes.Transparent
        };
        foreach (var panel in sidePanels)
        {
            Canvas.SetLeft(panel.Visual, panel.X - left);
            Canvas.SetTop(panel.Visual, panel.Y - top);
            group.Children.Add(panel.Visual);
        }

        var host = new BoundedDesktopPanelHost(
            side == WidgetSide.Left
                ? "Vellume Alpha Left Widgets"
                : "Vellume Alpha Right Widgets",
            _hostHandle,
            _relativeX + left,
            _relativeY + top,
            width,
            height);
        host.Render(group);
        foreach (var panel in sidePanels)
        {
            RegisterRenderTarget(panel, left, top);
        }
        if (side == WidgetSide.Left)
        {
            _leftPanelX = left;
            _leftPanelY = top;
            _leftPanelGroupVisual = group;
            _leftPanelHost = host;
        }
        else
        {
            _rightPanelX = left;
            _rightPanelY = top;
            _rightPanelGroupVisual = group;
            _rightPanelHost = host;
        }
    }

    private void ReattachAlphaPanels()
    {
        if (_leftPanelHost is not null)
        {
            _leftPanelHost.AttachTo(
                _hostHandle,
                _relativeX + _leftPanelX,
                _relativeY + _leftPanelY,
                _leftPanelHost.Width,
                _leftPanelHost.Height);
        }

        if (_rightPanelHost is not null)
        {
            _rightPanelHost.AttachTo(
                _hostHandle,
                _relativeX + _rightPanelX,
                _relativeY + _rightPanelY,
                _rightPanelHost.Width,
                _rightPanelHost.Height);
        }
    }

    private int CalculatePanelX(
        WidgetSide side,
        double margin,
        int panelWidth)
        => side == WidgetSide.Left
            ? (int)Math.Round(margin)
            : _width - (int)Math.Round(margin) - panelWidth;

    private int GetSystemPanelTop()
    {
        var clockOffset =
            _settings.ShowClock &&
            _settings.ClockSide ==
            _settings.SystemInfoSide;
        return clockOffset ? 370 : 330;
    }

    internal static double CalculateResolutionScale(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Uniform reference-canvas scaling preserves the FHD design and keeps
        // it inside wide/portrait displays without stretching text or characters.
        return Math.Min(width / ReferenceDisplayWidth, height / ReferenceDisplayHeight);
    }

    private static int ScalePosition(double referencePosition, double scale)
        => (int)Math.Round(referencePosition * scale);

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
        if (target is null)
        {
            return;
        }

        var host = GetSidePanelHost(target.Side);
        if (host?.IsReady == true &&
            target.Visual is not null)
        {
            host.RenderRegion(
                target.Visual,
                target.X,
                target.Y,
                target.Width,
                target.Height);
        }
    }

    private BoundedDesktopPanelHost? GetSidePanelHost(
        WidgetSide side)
        => side == WidgetSide.Left
            ? _leftPanelHost
            : _rightPanelHost;

    private void RegisterRenderTarget(
        WidgetPanelPlacement panel,
        int surfaceLeft,
        int surfaceTop)
    {
        var target = new WidgetPanelRenderTarget(
            panel.Side,
            panel.Visual,
            panel.X - surfaceLeft,
            panel.Y - surfaceTop,
            panel.Width,
            panel.Height);
        if (ReferenceEquals(panel.Visual, _clockPanelVisual))
        {
            _clockRenderTarget = target;
        }
        else if (ReferenceEquals(panel.Visual, _systemPanelVisual))
        {
            _systemRenderTarget = target;
        }
        else if (ReferenceEquals(panel.Visual, _codexPanelVisual))
        {
            _codexRenderTarget = target;
        }
    }

    internal FrameworkElement CreateClock(WidgetSide side)
    {
        var left = side == WidgetSide.Left;
        var textAlignment = left
            ? TextAlignment.Left
            : TextAlignment.Right;
        var canvas = new Canvas
        {
            Width = 500,
            Height = 340,
            ClipToBounds = false
        };
        var trace = new Border
        {
            Width = 340,
            Height = 2,
            Background = new SolidColorBrush(
                ReferenceCyan)
        };
        Canvas.SetLeft(trace, left ? 0 : 160);
        Canvas.SetTop(trace, 5);
        canvas.Children.Add(trace);

        var tilted = new Canvas
        {
            Width = 430,
            Height = 300,
            RenderTransformOrigin = new Point(
                left ? 0 : 1,
                0.5),
            RenderTransform = new RotateTransform(
                left ? 11 : -11),
            ClipToBounds = false
        };
        Canvas.SetLeft(tilted, 30);
        Canvas.SetTop(tilted, 15);
        canvas.Children.Add(tilted);

        var yearBand = new Border
        {
            Width = 350,
            Height = 58,
            Padding = new Thickness(14, 0, 14, 0),
            Background = new SolidColorBrush(
                ReferenceYearBand),
            BorderBrush = new SolidColorBrush(
                MikuPalette.WithAlpha(
                    Colors.White,
                    210)),
            BorderThickness = new Thickness(0, 1, 0, 1)
        };
        _clockYear = CreateOutlinedVectorText(
            52,
            FontWeights.SemiBold,
            ReferenceNavy,
            1.05);
        _clockYear.Width = 322;
        _clockYear.TextAlignment = textAlignment;
        _clockYear.VerticalAlignment =
            VerticalAlignment.Center;
        yearBand.Child = _clockYear;
        Canvas.SetLeft(yearBand, left ? 0 : 80);
        Canvas.SetTop(yearBand, 0);
        tilted.Children.Add(yearBand);

        var band = new Border
        {
            Width = 420,
            Height = 62,
            Background = new SolidColorBrush(
                ReferenceDateBand),
            BorderBrush = new SolidColorBrush(
                ReferenceCyan),
            BorderThickness = left
                ? new Thickness(7, 0, 0, 0)
                : new Thickness(0, 0, 7, 0)
        };
        var bandContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0)
        };
        _clockWeekday = CreateOutlinedVectorText(
            34,
            FontWeights.SemiBold,
            ReferenceCyan,
            0.85);
        _clockDay = CreateOutlinedVectorText(
            50,
            FontWeights.Bold,
            Colors.White,
            0.9,
            ReferenceNavy);
        if (left)
        {
            bandContent.Children.Add(_clockDay);
            bandContent.Children.Add(CreateSpacer(18));
            bandContent.Children.Add(_clockWeekday);
        }
        else
        {
            bandContent.Children.Add(_clockWeekday);
            bandContent.Children.Add(CreateSpacer(18));
            bandContent.Children.Add(_clockDay);
        }
        band.Child = bandContent;
        Canvas.SetTop(band, 58);
        tilted.Children.Add(band);

        var monthBand = new Border
        {
            Width = 330,
            Height = 40,
            Padding = new Thickness(14, 0, 14, 0),
            Background = new SolidColorBrush(
                ReferenceMonthBand),
            BorderThickness = new Thickness(0)
        };
        _clockMonth = CreateOutlinedVectorText(
            25,
            FontWeights.SemiBold,
            ReferenceNavy,
            0.8);
        _clockMonth.Width = 300;
        _clockMonth.TextAlignment = textAlignment;
        _clockMonth.VerticalAlignment =
            VerticalAlignment.Center;
        monthBand.Child = _clockMonth;
        Canvas.SetLeft(monthBand, left ? 0 : 90);
        Canvas.SetTop(monthBand, 120);
        tilted.Children.Add(monthBand);

        var timeBand = new Border
        {
            Width = 300,
            Height = 66,
            Padding = new Thickness(15, 0, 15, 0),
            Background = new SolidColorBrush(
                ReferenceTimeBand),
            BorderThickness = new Thickness(0)
        };
        _clockTime = CreateOutlinedVectorText(
            58,
            FontWeights.Light,
            ReferenceCyan,
            0.85);
        _clockTime.Width = 270;
        _clockTime.TextAlignment = textAlignment;
        _clockTime.HorizontalAlignment = left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        _clockTime.VerticalAlignment =
            VerticalAlignment.Center;
        timeBand.Child = _clockTime;
        Canvas.SetLeft(timeBand, left ? 0 : 120);
        Canvas.SetTop(timeBand, 160);
        tilted.Children.Add(timeBand);
        return canvas;
    }

    internal FrameworkElement CreateSystemPanel(WidgetSide side)
    {
        var root = new StackPanel
        {
            Width = 255
        };
        root.Children.Add(CreateGauge(
            "cpu",
            ReferenceCyan,
            "CPU 사용량",
            "실시간 프로세서",
            side));
        root.Children.Add(CreateGauge(
            "gpu",
            ReferenceCyan,
            "GPU 사용량",
            "GPU 엔진",
            side));
        root.Children.Add(CreateGauge(
            "memory",
            ReferenceCyan,
            "메모리",
            "--",
            side));
        root.Children.Add(CreateGauge(
            "driveC",
            ReferenceCyan,
            "C:\\ 드라이브",
            "--",
            side));
        root.Children.Add(CreateGauge(
            "driveD",
            ReferenceCyan,
            "D:\\ 드라이브",
            "--",
            side));
        return root;
    }

    private FrameworkElement CreateGauge(
        string key,
        Color accent,
        string title,
        string detail,
        WidgetSide side)
    {
        var left = side == WidgetSide.Left;
        var canvas = new Canvas
        {
            Width = 255,
            Height = 54,
            Background = Brushes.Transparent
        };
        const double lineWidth = 201;
        var lineLeft = left ? 54 : 0;
        var lineOutline = new Border
        {
            Width = lineWidth,
            Height = 3,
            Background = new SolidColorBrush(
                MikuPalette.WithAlpha(
                    Colors.White,
                    230))
        };
        Canvas.SetLeft(lineOutline, lineLeft);
        Canvas.SetTop(lineOutline, 29);
        canvas.Children.Add(lineOutline);
        var line = new Border
        {
            Width = lineWidth,
            Height = 1,
            Background = new SolidColorBrush(
                MikuPalette.WithAlpha(
                    ReferenceNavy,
                    195))
        };
        Canvas.SetLeft(line, lineLeft);
        Canvas.SetTop(line, 30);
        canvas.Children.Add(line);

        var ring = new Grid
        {
            Width = 54,
            Height = 54
        };
        ring.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(
                MikuPalette.WithAlpha(
                    ReferenceBlue,
                    48)),
            Stroke = new SolidColorBrush(ReferenceNavy),
            StrokeThickness = 2
        });
        ring.Children.Add(new Ellipse
        {
            Margin = new Thickness(7),
            Stroke = new SolidColorBrush(
                MikuPalette.WithAlpha(
                    ReferenceCyan,
                    155)),
            StrokeThickness = 1
        });
        var arc = new ShapePath
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        ring.Children.Add(arc);
        Canvas.SetLeft(ring, left ? 0 : 201);
        Canvas.SetTop(ring, 0);
        canvas.Children.Add(ring);

        var value = CreateOutlinedVectorText(
            18,
            FontWeights.Bold,
            ReferenceNavy,
            0.45);
        value.Text = "--%";
        value.Margin = new Thickness(5, 0, 0, 0);
        var copy = new Grid
        {
            Width = 190,
            Height = 54
        };
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right
        };
        var titleText = CreateOutlinedVectorText(
            18,
            FontWeights.Bold,
            ReferenceNavy,
            0.45);
        titleText.Text = title;
        if (left)
        {
            heading.Children.Add(value);
            heading.Children.Add(CreateSpacer(5));
            heading.Children.Add(titleText);
        }
        else
        {
            heading.Children.Add(titleText);
            heading.Children.Add(value);
        }
        var detailText = CreateOutlinedVectorText(
            11,
            FontWeights.Bold,
            ReferenceNavy,
            0.35);
        detailText.Text = detail;
        detailText.TextAlignment = left
            ? TextAlignment.Left
            : TextAlignment.Right;
        detailText.HorizontalAlignment =
            HorizontalAlignment.Stretch;
        detailText.VerticalAlignment =
            VerticalAlignment.Bottom;
        detailText.Margin = new Thickness(0, 0, 0, 1);
        heading.VerticalAlignment =
            VerticalAlignment.Top;
        copy.Children.Add(heading);
        copy.Children.Add(detailText);
        Canvas.SetLeft(copy, left ? 65 : 0);
        Canvas.SetTop(copy, 0);
        canvas.Children.Add(copy);

        var root = new Border
        {
            Width = 255,
            Height = 54,
            Margin = new Thickness(0, 0, 0, 3),
            Background = Brushes.Transparent,
            Child = canvas
        };
        _gauges[key] = new GaugeVisual(
            arc,
            value,
            detailText);
        return root;
    }

    private FrameworkElement CreateCodexPanel(WidgetSide side)
    {
        var canvas = new Canvas
        {
            Width = 520,
            Height = 420
        };
        var statusLeft = side == WidgetSide.Left ? 225d : 0d;
        var speechBubble = new ShapePath
        {
            Data = CreateCodexSpeechBubbleGeometry(
                statusLeft,
                18,
                290,
                394,
                side),
            Fill = CreateGradient(
                true,
                MikuPalette.WithAlpha(
                    ReferenceDateBand,
                    238),
                MikuPalette.WithAlpha(
                    ReferenceTimeBand,
                    228)),
            Stroke = new SolidColorBrush(
                Color.FromArgb(215, 134, 236, 232)),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = false
        };
        canvas.Children.Add(speechBubble);

        var statusContent = new StackPanel
        {
            Width = 252
        };
        var headline = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        _codexPulse = new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 5, 9, 0),
            Fill = new SolidColorBrush(
                ReferenceCyan)
        };
        _codexHeadline = CreateText(
            16,
            FontWeights.Bold,
            Colors.White);
        headline.Children.Add(_codexPulse);
        headline.Children.Add(_codexHeadline);
        statusContent.Children.Add(headline);
        statusContent.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 12, 0, 10),
            Background = new SolidColorBrush(
                Color.FromArgb(115, 255, 255, 255))
        });
        var projectLabel = CreateText(
            11.5,
            FontWeights.Bold,
            Color.FromArgb(220, 255, 255, 255));
        projectLabel.Text = "PROJECT · 최근 3개";
        statusContent.Children.Add(projectLabel);
        _codexProjects = new StackPanel
        {
            Margin = new Thickness(0, 7, 0, 0),
            MaxHeight = 140
        };
        statusContent.Children.Add(_codexProjects);

        var usageCard = new Border
        {
            Width = 252,
            Height = 122,
            Padding = new Thickness(11, 9, 11, 9),
            CornerRadius = new CornerRadius(12),
            Background = CreateGradient(
                true,
                MikuPalette.WithAlpha(
                    ReferenceCyan,
                    205),
                MikuPalette.WithAlpha(
                    ReferenceBlue,
                    215)),
            BorderBrush = new SolidColorBrush(
                Color.FromArgb(175, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        _codexUsageLimits = new StackPanel();
        usageCard.Child = _codexUsageLimits;
        Canvas.SetLeft(statusContent, statusLeft + 19);
        Canvas.SetTop(statusContent, 34);
        canvas.Children.Add(statusContent);
        Canvas.SetLeft(usageCard, statusLeft + 19);
        Canvas.SetTop(usageCard, 274);
        canvas.Children.Add(usageCard);

        var initialVisualState = ResolveCodexCompanionVisualState(
            _lastCodex);
        _codexCharacter = new Image
        {
            Width = 225,
            Height = 255,
            Stretch = Stretch.Uniform,
            Source = GetCompanionImage(initialVisualState),
            RenderTransformOrigin = new Point(0.5, 0.5),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        RenderOptions.SetBitmapScalingMode(
            _codexCharacter,
            BitmapScalingMode.HighQuality);
        _lastCodexVisualState = initialVisualState;
        _codexTranslate = new TranslateTransform();
        var transforms = new TransformGroup();
        if (side == WidgetSide.Left)
        {
            transforms.Children.Add(
                new ScaleTransform(-1, 1));
        }
        transforms.Children.Add(_codexTranslate);
        _codexCharacter.RenderTransform = transforms;
        Canvas.SetLeft(
            _codexCharacter,
            side == WidgetSide.Left ? 0 : 295);
        Canvas.SetTop(_codexCharacter, 18);
        canvas.Children.Add(_codexCharacter);

        UpdateCodexProjectRows();
        UpdateCodexUsageRows();
        return canvas;
    }

    private static Geometry CreateCodexSpeechBubbleGeometry(
        double left,
        double top,
        double width,
        double height,
        WidgetSide side)
    {
        const double radius = 18;
        const double tailHalfHeight = 14;
        const double tailLength = 16;
        var right = left + width;
        var bottom = top + height;
        var tailCenterY = top + 52;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(
                new Point(left + radius, top),
                isFilled: true,
                isClosed: true);
            context.LineTo(
                new Point(right - radius, top),
                isStroked: true,
                isSmoothJoin: false);
            context.ArcTo(
                new Point(right, top + radius),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: true);
            if (side == WidgetSide.Right)
            {
                context.LineTo(
                    new Point(right, tailCenterY - tailHalfHeight),
                    true,
                    false);
                context.LineTo(
                    new Point(right + tailLength, tailCenterY),
                    true,
                    true);
                context.LineTo(
                    new Point(right, tailCenterY + tailHalfHeight),
                    true,
                    true);
            }
            context.LineTo(
                new Point(right, bottom - radius),
                true,
                false);
            context.ArcTo(
                new Point(right - radius, bottom),
                new Size(radius, radius),
                0,
                false,
                SweepDirection.Clockwise,
                true,
                true);
            context.LineTo(
                new Point(left + radius, bottom),
                true,
                false);
            context.ArcTo(
                new Point(left, bottom - radius),
                new Size(radius, radius),
                0,
                false,
                SweepDirection.Clockwise,
                true,
                true);
            if (side == WidgetSide.Left)
            {
                context.LineTo(
                    new Point(left, tailCenterY + tailHalfHeight),
                    true,
                    false);
                context.LineTo(
                    new Point(left - tailLength, tailCenterY),
                    true,
                    true);
                context.LineTo(
                    new Point(left, tailCenterY - tailHalfHeight),
                    true,
                    true);
            }
            context.LineTo(
                new Point(left, top + radius),
                true,
                false);
            context.ArcTo(
                new Point(left + radius, top),
                new Size(radius, radius),
                0,
                false,
                SweepDirection.Clockwise,
                true,
                true);
        }

        geometry.Freeze();
        return geometry;
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
        if (_disposed)
        {
            return;
        }

        var now = DateTime.Now;
        if (_clockTime is not null)
        {
            var time = now.ToString(
                "HH:mm",
                CultureInfo.InvariantCulture);
            var year = now.Year.ToString(
                CultureInfo.InvariantCulture);
            var weekday = now.ToString(
                    "dddd",
                    CultureInfo.InvariantCulture)
                .ToUpperInvariant();
            var day = now.Day.ToString(
                "00",
                CultureInfo.InvariantCulture);
            var month = now.ToString(
                    "MMM",
                    CultureInfo.InvariantCulture)
                .ToUpperInvariant();
            if (string.Equals(
                    _clockTime.Text,
                    time,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _clockYear!.Text,
                    year,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _clockWeekday!.Text,
                    weekday,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _clockDay!.Text,
                    day,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _clockMonth!.Text,
                    month,
                    StringComparison.Ordinal))
            {
                return;
            }

            _clockTime.Text = time;
            _clockYear!.Text = year;
            _clockWeekday!.Text = weekday;
            _clockDay!.Text = day;
            _clockMonth!.Text = month;
            RenderClockPanel();
        }
    }

    private void ApplyLastMetrics()
    {
        var metrics = _lastMetrics;
        if (metrics is null)
        {
            return;
        }

        var cpu = Math.Round(metrics.CpuPercent);
        var gpu = metrics.GpuPercent is null
            ? 0
            : Math.Round(metrics.GpuPercent.Value);
        var memory = Math.Round(metrics.MemoryPercent);
        var driveC = Math.Round(metrics.DriveC.Percent);
        var driveD = Math.Round(metrics.DriveD.Percent);
        var changed = SetGauge(
            "cpu",
            cpu,
            $"{cpu}%",
            "실시간 프로세서");
        changed |= SetGauge(
            "gpu",
            gpu,
            metrics.GpuPercent is null
                ? "N/A"
                : $"{gpu}%",
            metrics.GpuPercent is null
                ? "측정 불가"
                : "GPU 엔진");
        changed |= SetGauge(
            "memory",
            memory,
            $"{memory}%",
            $"{FormatBytes(metrics.MemoryUsedBytes)} / " +
            $"{FormatBytes(metrics.MemoryTotalBytes)}");
        changed |= SetGauge(
            "driveC",
            driveC,
            metrics.DriveC.IsAvailable
                ? $"{driveC}%"
                : "N/A",
            FormatDrive(metrics.DriveC));
        changed |= SetGauge(
            "driveD",
            driveD,
            metrics.DriveD.IsAvailable
                ? $"{driveD}%"
                : "N/A",
            FormatDrive(metrics.DriveD));
        if (changed)
        {
            RenderSystemPanel();
        }
    }

    private void ApplyLastCodex()
    {
        if (_codexHeadline is not null)
        {
            var usage = _lastCodex.Usage;
            var visualState = ResolveCodexCompanionVisualState(
                _lastCodex);
            var visualChanged =
                _lastCodexVisualState != visualState;
            if (visualChanged && _codexCharacter is not null)
            {
                _codexCharacter.Source =
                    GetCompanionImage(visualState);
                _lastCodexVisualState = visualState;
            }
            var contentSignature = string.Join(
                "|",
                _lastCodex.State,
                _lastCodex.Headline,
                _lastCodex.Detail,
                usage.IsAvailable,
                string.Join(
                    ";",
                    usage.Limits
                        .Take(2)
                        .Select(limit =>
                            $"{limit.Id}:{limit.DisplayName}:" +
                            $"{limit.RemainingPercent}:" +
                            $"{limit.ResetsAtUtc?.UtcTicks ?? 0}")),
                string.Join(
                    ";",
                    _lastCodex.Projects
                        .Take(3)
                        .Select(project =>
                            $"{project.Id}:{project.State}:" +
                            $"{project.Title}:{project.Phase}:" +
                            $"{project.Workspace}")));
            var changed = visualChanged ||
                          !string.Equals(
                              _lastCodexContentSignature,
                              contentSignature,
                              StringComparison.Ordinal);
            _codexHeadline.Text =
                _lastCodex.State is "offline" or "disconnected"
                    ? "CODEX 연결 안 됨"
                    : "CODEX 연결됨";
            var color = GetCodexStateColor(
                _lastCodex.State);
            _codexPulse!.Fill = new SolidColorBrush(color);
            if (!string.Equals(
                    _lastCodexState,
                    _lastCodex.State,
                    StringComparison.Ordinal))
            {
                _lastCodexState = _lastCodex.State;
            }
            if (changed)
            {
                _lastCodexContentSignature = contentSignature;
                UpdateCodexProjectRows();
                UpdateCodexUsageRows();
                RenderCodexPanel();
            }
        }
    }

    private bool SetGauge(
        string key,
        double percent,
        string value,
        string detail)
    {
        if (!_gauges.TryGetValue(key, out var gauge))
        {
            return false;
        }
        if (string.Equals(
                gauge.Value.Text,
                value,
                StringComparison.Ordinal) &&
            string.Equals(
                gauge.Detail.Text,
                detail,
                StringComparison.Ordinal))
        {
            return false;
        }

        gauge.Value.Text = value;
        gauge.Detail.Text = detail;
        gauge.Arc.Data = CreateArcGeometry(percent);
        return true;
    }

    private static Geometry CreateArcGeometry(double percent)
    {
        var normalized = Math.Clamp(percent, 0, 100);
        if (normalized <= 0)
        {
            return Geometry.Empty;
        }

        const double center = 27;
        const double radius = 23;
        var angle = normalized >= 100
            ? 359.999
            : normalized * 3.6;
        var radians = (angle - 90) * Math.PI / 180;
        var end = new Point(
            center + radius * Math.Cos(radians),
            center + radius * Math.Sin(radians));
        var figure = new PathFigure
        {
            StartPoint = new Point(center, center - radius)
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = angle > 180,
            SweepDirection = SweepDirection.Clockwise
        });
        return new PathGeometry([figure]);
    }

    private void StartCodexAnimation()
    {
        // The companion is a high-quality layered bitmap. Keeping it static
        // avoids repeatedly resampling its transparent edges and wasting GPU.
        RenderCodexPanel();
    }

    private void StopCodexAnimation()
    {
        if (_codexTranslate is null)
        {
            return;
        }

        _codexTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        _codexTranslate.Y = 0;
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
    {
        if (_leftPanelHost is not null)
        {
            yield return _leftPanelHost;
        }

        if (_rightPanelHost is not null)
        {
            yield return _rightPanelHost;
        }
    }

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
            var nextMinute = new DateTime(
                    now.Year,
                    now.Month,
                    now.Day,
                    now.Hour,
                    now.Minute,
                    0,
                    now.Kind)
                .AddMinutes(1);
            _clockTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(
                    100,
                    (nextMinute - now).TotalMilliseconds + 25));
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }
    }

    private void ResetVisualReferences()
    {
        StopCodexAnimation();
        _gauges.Clear();
        _clockYear = null;
        _clockWeekday = null;
        _clockDay = null;
        _clockMonth = null;
        _clockTime = null;
        _codexHeadline = null;
        _codexProjects = null;
        _codexUsageLimits = null;
        _codexPulse = null;
        _codexCharacter = null;
        _codexTranslate = null;
        _lastCodexVisualState = null;
        _lastCodexState = string.Empty;
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

    private static FrameworkElement CreateSpacer(double width)
        => new Border
        {
            Width = width
        };

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

    private static string FormatBytes(ulong bytes)
    {
        const double gibibyte =
            1024d * 1024d * 1024d;
        return $"{bytes / gibibyte:0.0} GB";
    }

    private static string FormatDrive(
        DriveMetricsSnapshot drive)
        => drive.IsAvailable
            ? $"{FormatBytes(drive.UsedBytes)} / " +
              $"{FormatBytes(drive.TotalBytes)}"
            : "사용할 수 없음";

    private bool HasPanelsOnSide(WidgetSide side)
        => (_settings.ShowClock &&
            _settings.ClockSide == side) ||
           (_settings.ShowSystemInfo &&
            _settings.SystemInfoSide == side) ||
           (_settings.ShowCodexCompanion &&
            CodexPanelSide == side);

    private sealed record WidgetPanelPlacement(
        WidgetSide Side,
        FrameworkElement Visual,
        int X,
        int Y,
        int Width,
        int Height);

    private sealed record WidgetPanelRenderTarget(
        WidgetSide Side,
        FrameworkElement Visual,
        int X,
        int Y,
        int Width,
        int Height);

    private sealed record GaugeVisual(
        ShapePath Arc,
        VectorTextBlock Value,
        VectorTextBlock Detail);

    private sealed record LayeredPanelVisual(
        FrameworkElement Visual,
        int Width,
        int Height);
}
