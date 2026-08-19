using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vellume.Core;

namespace Vellume.Windows;

internal sealed class NativeWidgetRendererWindow
{
	private sealed record GaugeVisual(System.Windows.Shapes.Path Arc, VectorTextBlock Value, VectorTextBlock Detail);

	private sealed record LayeredPanelVisual(FrameworkElement Visual, int Width, int Height);

	[CompilerGenerated]
	private sealed class <EnumeratePanelHosts>d__130 : IEnumerable<BoundedDesktopPanelHost>, IEnumerable, IEnumerator<BoundedDesktopPanelHost>, IEnumerator, IDisposable
	{
		private int <>1__state;

		private BoundedDesktopPanelHost <>2__current;

		private int <>l__initialThreadId;

		public NativeWidgetRendererWindow <>4__this;

		BoundedDesktopPanelHost IEnumerator<BoundedDesktopPanelHost>.Current
		{
			[DebuggerHidden]
			get
			{
				return <>2__current;
			}
		}

		object IEnumerator.Current
		{
			[DebuggerHidden]
			get
			{
				return <>2__current;
			}
		}

		[DebuggerHidden]
		public <EnumeratePanelHosts>d__130(int <>1__state)
		{
			this.<>1__state = <>1__state;
			<>l__initialThreadId = Environment.CurrentManagedThreadId;
		}

		[DebuggerHidden]
		void IDisposable.Dispose()
		{
			<>1__state = -2;
		}

		private bool MoveNext()
		{
			int num = <>1__state;
			NativeWidgetRendererWindow nativeWidgetRendererWindow = <>4__this;
			switch (num)
			{
			default:
				return false;
			case 0:
				<>1__state = -1;
				if (nativeWidgetRendererWindow._clockPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._clockPanelHost;
					<>1__state = 1;
					return true;
				}
				goto IL_0055;
			case 1:
				<>1__state = -1;
				goto IL_0055;
			case 2:
				<>1__state = -1;
				goto IL_0079;
			case 3:
				<>1__state = -1;
				goto IL_009d;
			case 4:
				{
					<>1__state = -1;
					break;
				}
				IL_0055:
				if (nativeWidgetRendererWindow._systemPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._systemPanelHost;
					<>1__state = 2;
					return true;
				}
				goto IL_0079;
				IL_009d:
				if (nativeWidgetRendererWindow._codexPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._codexPanelHost;
					<>1__state = 4;
					return true;
				}
				break;
				IL_0079:
				if (nativeWidgetRendererWindow._mediaPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._mediaPanelHost;
					<>1__state = 3;
					return true;
				}
				goto IL_009d;
			}
			return false;
		}

		bool IEnumerator.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			return this.MoveNext();
		}

		[DebuggerHidden]
		void IEnumerator.Reset()
		{
			throw new NotSupportedException();
		}

		[DebuggerHidden]
		IEnumerator<BoundedDesktopPanelHost> IEnumerable<BoundedDesktopPanelHost>.GetEnumerator()
		{
			<EnumeratePanelHosts>d__130 result;
			if (<>1__state == -2 && <>l__initialThreadId == Environment.CurrentManagedThreadId)
			{
				<>1__state = 0;
				result = this;
			}
			else
			{
				result = new <EnumeratePanelHosts>d__130(0)
				{
					<>4__this = <>4__this
				};
			}
			return result;
		}

		[DebuggerHidden]
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<BoundedDesktopPanelHost>)this).GetEnumerator();
		}
	}

	private const double MediaFrameIntervalMilliseconds = 83.33333333333333;

	private static readonly Color MintAccent = MikuPalette.Mint;

	private static readonly Color MintPanelStrong = MikuPalette.WithAlpha(MikuPalette.Mint, 190);

	private static readonly Color MintPanelMid = MikuPalette.WithAlpha(MikuPalette.Teal, 170);

	private static readonly Color MintPanelSoft = MikuPalette.WithAlpha(MikuPalette.Mint, 58);

	private static readonly Color ReferenceNavy = Color.FromRgb(38, 54, 79);

	private static readonly Color ReferenceCyan = Color.FromRgb(67, 231, 242);

	private static readonly Color ReferenceBlue = Color.FromRgb(36, 201, 223);

	private static readonly Color ReferenceDateBand = Color.FromArgb(178, 43, 132, 168);

	private static readonly Color ReferenceMonthBand = Color.FromArgb(190, 61, 210, 224);

	private static readonly Color ReferenceTimeBand = Color.FromArgb(160, 42, 116, 153);

	private static readonly Color ReferenceYearBand = Color.FromArgb(158, 43, 132, 168);

	private static readonly FontFamily ReferenceFontFamily = new FontFamily("Bahnschrift, Pretendard, Malgun Gothic");

	private readonly DispatcherTimer _clockTimer;

	private readonly DispatcherTimer _mediaRenderTimer;

	private readonly Dictionary<string, GaugeVisual> _gauges = new Dictionary<string, GaugeVisual>(StringComparer.Ordinal);

	private BoundedDesktopPanelHost? _clockPanelHost;

	private BoundedDesktopPanelHost? _systemPanelHost;

	private BoundedDesktopPanelHost? _mediaPanelHost;

	private BoundedDesktopPanelHost? _codexPanelHost;

	private FrameworkElement? _clockPanelVisual;

	private FrameworkElement? _systemPanelVisual;

	private FrameworkElement? _mediaPanelVisual;

	private FrameworkElement? _codexPanelVisual;

	private int _clockPanelX;

	private int _clockPanelY;

	private int _systemPanelX;

	private int _systemPanelY;

	private int _mediaPanelX;

	private int _mediaPanelY;

	private int _codexPanelX;

	private int _codexPanelY;

	private WidgetSettings _settings = new WidgetSettings();

	private nint _hostHandle;

	private nint _shellViewHandle;

	private nint _backgroundWorkerHandle;

	private int _relativeX;

	private int _relativeY;

	private int _width;

	private int _height;

	private bool _isRaisedDesktop;

	private bool _isPaused;

	private bool _isWindowVisible = true;

	private bool _initialized;

	private bool _disposed;

	private bool _mediaRenderPending;

	private long _lastMediaRenderTick;

	private VectorTextBlock? _clockYear;

	private VectorTextBlock? _clockWeekday;

	private VectorTextBlock? _clockDay;

	private VectorTextBlock? _clockMonth;

	private VectorTextBlock? _clockTime;

	private Image? _mediaArtwork;

	private TextBlock? _mediaArtworkPlaceholder;

	private TextBlock? _mediaTitle;

	private TextBlock? _mediaArtist;

	private Border? _mediaProgress;

	private System.Windows.Shapes.Path? _mediaToggleGlyph;

	private Border[] _spectrumBars = Array.Empty<Border>();

	private string _lastArtworkDataUri = string.Empty;

	private MediaSessionSnapshot? _appliedMedia;

	private TextBlock? _codexHeadline;

	private TextBlock? _codexDetail;

	private TextBlock? _codexMeta;

	private Ellipse? _codexPulse;

	private TranslateTransform? _codexTranslate;

	private string _lastCodexState = string.Empty;

	private SystemMetricsSnapshot? _lastMetrics;

	private MediaSessionSnapshot _lastMedia = new MediaSessionSnapshot(IsAvailable: false, IsPlaying: false, "재생 중인 미디어 없음", "미디어 앱에서 음악을 재생하세요", string.Empty, 0.0, string.Empty);

	private float[] _lastSpectrum = new float[24];

	private CodexCompanionSnapshot _lastCodex = CodexCompanionSnapshot.Disconnected();

	internal nint Handle => EnumeratePanelHosts().FirstOrDefault((BoundedDesktopPanelHost host) => host.IsReady)?.Handle ?? IntPtr.Zero;

	internal IReadOnlyList<nint> PanelHandles => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.Handle).ToArray();

	internal IReadOnlyList<nint> PanelSurfaceHandles => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.SurfaceHandle).ToArray();

	internal IReadOnlyList<long> PanelPresentationGenerations => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.PresentationGeneration).ToArray();

	internal IReadOnlyList<int> PanelNonTransparentPixelCounts => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.LastNonTransparentPixelCount).ToArray();

	internal bool AllPanelsPresented => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host).All((BoundedDesktopPanelHost host) => host.LastPresentSucceeded);

	internal bool UsesFullDisplayCompositionSurface => false;

	internal bool IsReady
	{
		get
		{
			if (!_disposed && _initialized && _hostHandle != IntPtr.Zero && NativeMethods.IsWindow(_hostHandle))
			{
				if (_settings.IsEnabled && _settings.ShowClock)
				{
					BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
					if (clockPanelHost == null || !clockPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowSystemInfo)
				{
					BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
					if (systemPanelHost == null || !systemPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowMediaStatus)
				{
					BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
					if (mediaPanelHost == null || !mediaPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowCodexCompanion)
				{
					return _codexPanelHost?.IsReady ?? false;
				}
				return true;
			}
			goto IL_00f1;
			IL_00f1:
			return false;
		}
	}

	internal NativeWidgetRendererWindow()
	{
		_clockTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_clockTimer.Tick += ClockTimerOnTick;
		_mediaRenderTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(83.33333333333333)
		};
		_mediaRenderTimer.Tick += MediaRenderTimerOnTick;
	}

	internal void AttachTo(nint hostHandle, DisplayInfo display, bool isRaisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.GetWindowRect(hostHandle, out var rectangle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯 영역을 읽지 못했습니다.");
		}
		if (isRaisedDesktop && (shellViewHandle == IntPtr.Zero || backgroundWorkerHandle == IntPtr.Zero))
		{
			throw new InvalidOperationException("확장 데스크톱에 ShellView 또는 WorkerW 레이어가 없습니다.");
		}
		bool flag = _width != display.Width || _height != display.Height;
		int num = display.X - rectangle.Left;
		int num2 = display.Y - rectangle.Top;
		bool flag2 = !IsAttachedTo(hostHandle) || _relativeX != num || _relativeY != num2;
		_hostHandle = hostHandle;
		_shellViewHandle = shellViewHandle;
		_backgroundWorkerHandle = backgroundWorkerHandle;
		_isRaisedDesktop = isRaisedDesktop;
		_relativeX = num;
		_relativeY = num2;
		_width = display.Width;
		_height = display.Height;
		if (_initialized)
		{
			if (flag)
			{
				RebuildAlphaPanels();
				UpdateClockAndCalendar();
				ApplyLastMetrics();
				ApplyLastAddOns();
			}
			else if (flag2)
			{
				ReattachAlphaPanels();
			}
			ApplyLayerOrder();
			ApplyWindowVisibility();
		}
	}

	internal Task InitializeAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_initialized)
		{
			return Task.CompletedTask;
		}
		if (_hostHandle == IntPtr.Zero || !NativeMethods.IsWindow(_hostHandle))
		{
			throw new InvalidOperationException("네이티브 위젯 호스트가 연결되지 않았습니다.");
		}
		_initialized = true;
		RebuildAlphaPanels();
		ApplyLayerOrder();
		ApplyWindowVisibility();
		UpdateClockAndCalendar();
		return Task.CompletedTask;
	}

	internal Task ApplySettingsAsync(WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed, this);
		WidgetSettings widgetSettings = widgets.Copy();
		widgetSettings.ShowCalendar = false;
		bool num = !HasSameInformationPanelLayout(_settings, widgetSettings);
		_settings = widgetSettings;
		if (num)
		{
			RebuildAlphaPanels();
		}
		UpdateClockAndCalendar();
		ApplyLastMetrics();
		ApplyLastAddOns();
		UpdateClockTimer();
		return Task.CompletedTask;
	}

	internal Task UpdateSystemMetricsAsync(SystemMetricsSnapshot metrics)
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_lastMetrics = metrics;
		ApplyLastMetrics();
		return Task.CompletedTask;
	}

	private static bool HasSameInformationPanelLayout(WidgetSettings current, WidgetSettings next)
	{
		if (current.IsEnabled == next.IsEnabled && current.ShowClock == next.ShowClock && current.ShowSystemInfo == next.ShowSystemInfo && current.ShowMediaStatus == next.ShowMediaStatus && current.ShowCodexCompanion == next.ShowCodexCompanion && current.ClockSide == next.ClockSide && current.SystemInfoSide == next.SystemInfoSide && current.MediaStatusSide == next.MediaStatusSide)
		{
			return current.CodexCompanionSide == next.CodexCompanionSide;
		}
		return false;
	}

	internal Task UpdateAddOnsAsync(MediaSessionSnapshot media, IReadOnlyList<float> spectrum, bool audioCaptureAvailable, CodexCompanionSnapshot codex)
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_lastMedia = media;
		_lastSpectrum = spectrum.Take(24).Concat(Enumerable.Repeat(0f, 24)).Take(24)
			.ToArray();
		_lastCodex = codex;
		ApplyLastAddOns();
		return Task.CompletedTask;
	}

	internal Task PauseAsync()
	{
		_isPaused = true;
		_clockTimer.Stop();
		_mediaRenderTimer.Stop();
		StopCodexAnimation();
		Border[] spectrumBars = _spectrumBars;
		foreach (Border obj in spectrumBars)
		{
			obj.Height = 2.0;
			obj.Opacity = 0.25;
		}
		return Task.CompletedTask;
	}

	internal Task ResumeAsync()
	{
		_isPaused = false;
		UpdateClockAndCalendar();
		ApplySpectrum();
		RequestMediaPanelRender(immediate: true);
		RenderCodexPanel();
		UpdateClockTimer();
		return Task.CompletedTask;
	}

	internal void SetWindowVisible(bool visible)
	{
		if (!_disposed && _isWindowVisible != visible)
		{
			_isWindowVisible = visible;
			if (!visible)
			{
				_mediaRenderTimer.Stop();
			}
			ApplyWindowVisibility();
			if (visible && _mediaRenderPending)
			{
				ApplySpectrum(requestRender: false);
				RequestMediaPanelRender(immediate: true);
			}
		}
	}

	internal bool IsAttachedTo(nint hostHandle)
	{
		if (_initialized && NativeMethods.IsWindow(hostHandle) && _hostHandle == hostHandle && (_clockPanelHost == null || _clockPanelHost.IsAttachedTo(hostHandle)) && (_systemPanelHost == null || _systemPanelHost.IsAttachedTo(hostHandle)) && (_mediaPanelHost == null || _mediaPanelHost.IsAttachedTo(hostHandle)))
		{
			if (_codexPanelHost != null)
			{
				return _codexPanelHost.IsAttachedTo(hostHandle);
			}
			return true;
		}
		return false;
	}

	internal string GetAttachmentDiagnostics()
	{
		IEnumerable<string> values = (from host in EnumeratePanelHosts()
			where host.IsReady
			select host).Select(delegate(BoundedDesktopPanelHost host)
		{
			nint parent = NativeMethods.GetParent(host.Handle);
			nint window = NativeMethods.GetWindow(host.Handle, 3u);
			nint window2 = NativeMethods.GetWindow(host.Handle, 2u);
			return $"0x{host.Handle:X}[parent=0x{parent:X},surface=0x{host.SurfaceHandle:X},surfaceParent=0x{NativeMethods.GetParent(host.SurfaceHandle):X},above=0x{window:X},below=0x{window2:X},size={host.Width}x{host.Height},frame={host.PresentationGeneration},alphaPixels={host.LastNonTransparentPixelCount},presented={host.LastPresentSucceeded},visible={NativeMethods.IsWindowVisible(host.Handle)}]";
		});
		return $"backend=native-bounded-redirected-skins, desktop=0x{_hostHandle:X}, fullDisplaySurface=false, panels={string.Join(';', values)}";
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
		_mediaRenderTimer.Stop();
		_mediaRenderTimer.Tick -= MediaRenderTimerOnTick;
		StopCodexAnimation();
		_clockPanelHost?.Dispose();
		_systemPanelHost?.Dispose();
		_mediaPanelHost?.Dispose();
		_codexPanelHost?.Dispose();
		_clockPanelHost = null;
		_systemPanelHost = null;
		_mediaPanelHost = null;
		_codexPanelHost = null;
		_clockPanelVisual = null;
		_systemPanelVisual = null;
		_mediaPanelVisual = null;
		_codexPanelVisual = null;
		_hostHandle = IntPtr.Zero;
		_shellViewHandle = IntPtr.Zero;
		_backgroundWorkerHandle = IntPtr.Zero;
		_gauges.Clear();
		_spectrumBars = Array.Empty<Border>();
		_mediaRenderPending = false;
		return Task.CompletedTask;
	}

	private void ApplyWindowVisibility()
	{
		foreach (BoundedDesktopPanelHost item in EnumeratePanelHosts())
		{
			item.SetWindowVisible(_isWindowVisible);
		}
	}

	private void RebuildAlphaPanels()
	{
		_mediaRenderTimer.Stop();
		_mediaRenderPending = false;
		_lastMediaRenderTick = 0L;
		_clockPanelHost?.Dispose();
		_systemPanelHost?.Dispose();
		_mediaPanelHost?.Dispose();
		_codexPanelHost?.Dispose();
		_clockPanelHost = null;
		_systemPanelHost = null;
		_mediaPanelHost = null;
		_codexPanelHost = null;
		_clockPanelVisual = null;
		_systemPanelVisual = null;
		_mediaPanelVisual = null;
		_codexPanelVisual = null;
		ResetVisualReferences();
		if (_settings.IsEnabled && _initialized && _hostHandle != IntPtr.Zero && NativeMethods.IsWindow(_hostHandle))
		{
			if (_settings.ShowClock)
			{
				double scale = ((_width <= 1450) ? 0.82 : 1.0);
				LayeredPanelVisual layeredPanelVisual = PrepareLayeredPanel(CreateClock(_settings.ClockSide), scale);
				_clockPanelX = CalculatePanelX(_settings.ClockSide, 25.0, layeredPanelVisual.Width);
				_clockPanelY = 28;
				_clockPanelVisual = layeredPanelVisual.Visual;
				_clockPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Clock", _hostHandle, _relativeX + _clockPanelX, _relativeY + _clockPanelY, layeredPanelVisual.Width, layeredPanelVisual.Height);
				_clockPanelHost.Render(_clockPanelVisual);
			}
			if (_settings.ShowSystemInfo)
			{
				double scale2 = ((_width <= 1450) ? 0.92 : 1.0);
				LayeredPanelVisual layeredPanelVisual2 = PrepareLayeredPanel(CreateSystemPanel(_settings.SystemInfoSide), scale2);
				_systemPanelX = CalculatePanelX(_settings.SystemInfoSide, 25.0, layeredPanelVisual2.Width);
				_systemPanelY = GetSystemPanelTop();
				_systemPanelVisual = layeredPanelVisual2.Visual;
				_systemPanelHost = new BoundedDesktopPanelHost("Vellume Alpha System", _hostHandle, _relativeX + _systemPanelX, _relativeY + _systemPanelY, layeredPanelVisual2.Width, layeredPanelVisual2.Height);
				_systemPanelHost.Render(_systemPanelVisual);
			}
			if (_settings.ShowMediaStatus)
			{
				WidgetSide mediaStatusSide = _settings.MediaStatusSide;
				double scale3 = ((_width <= 1450) ? 0.82 : 1.0);
				LayeredPanelVisual layeredPanelVisual3 = PrepareLayeredPanel(CreateMediaPanel(mediaStatusSide), scale3);
				_mediaPanelX = CalculatePanelX(mediaStatusSide, (mediaStatusSide == WidgetSide.Left) ? 30 : 42, layeredPanelVisual3.Width);
				_mediaPanelY = ((mediaStatusSide == WidgetSide.Left) ? 28 : 188);
				_mediaPanelVisual = layeredPanelVisual3.Visual;
				_mediaPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Media", _hostHandle, _relativeX + _mediaPanelX, _relativeY + _mediaPanelY, layeredPanelVisual3.Width, layeredPanelVisual3.Height);
				_mediaPanelHost.Render(_mediaPanelVisual);
			}
			if (_settings.ShowCodexCompanion)
			{
				WidgetSide codexCompanionSide = _settings.CodexCompanionSide;
				double scale4 = ((_width <= 1450) ? 0.84 : 1.0);
				LayeredPanelVisual layeredPanelVisual4 = PrepareLayeredPanel(CreateCodexPanel(codexCompanionSide), scale4);
				_codexPanelX = CalculatePanelX(codexCompanionSide, 28.0, layeredPanelVisual4.Width);
				_codexPanelY = _height - 26 - layeredPanelVisual4.Height;
				_codexPanelVisual = layeredPanelVisual4.Visual;
				_codexPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Codex", _hostHandle, _relativeX + _codexPanelX, _relativeY + _codexPanelY, layeredPanelVisual4.Width, layeredPanelVisual4.Height);
				_codexPanelHost.Render(_codexPanelVisual);
			}
			ApplyLayerOrder();
			ApplyWindowVisibility();
		}
	}

	private void ReattachAlphaPanels()
	{
		if (_clockPanelHost != null)
		{
			_clockPanelHost.AttachTo(_hostHandle, _relativeX + _clockPanelX, _relativeY + _clockPanelY, _clockPanelHost.Width, _clockPanelHost.Height);
		}
		if (_systemPanelHost != null)
		{
			_systemPanelHost.AttachTo(_hostHandle, _relativeX + _systemPanelX, _relativeY + _systemPanelY, _systemPanelHost.Width, _systemPanelHost.Height);
		}
		if (_mediaPanelHost != null)
		{
			_mediaPanelHost.AttachTo(_hostHandle, _relativeX + _mediaPanelX, _relativeY + _mediaPanelY, _mediaPanelHost.Width, _mediaPanelHost.Height);
		}
		if (_codexPanelHost != null)
		{
			_codexPanelHost.AttachTo(_hostHandle, _relativeX + _codexPanelX, _relativeY + _codexPanelY, _codexPanelHost.Width, _codexPanelHost.Height);
		}
	}

	private int CalculatePanelX(WidgetSide side, double margin, int panelWidth)
	{
		if (side != 0)
		{
			return _width - (int)Math.Round(margin) - panelWidth;
		}
		return (int)Math.Round(margin);
	}

	private int GetSystemPanelTop()
	{
		bool flag = _settings.ShowClock && _settings.ClockSide == _settings.SystemInfoSide;
		if (_width > 1450)
		{
			if (!flag)
			{
				return 330;
			}
			return 370;
		}
		if (!flag)
		{
			return 285;
		}
		return 330;
	}

	private static LayeredPanelVisual PrepareLayeredPanel(FrameworkElement element, double scale)
	{
		element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		double num = (double.IsNaN(element.Width) ? element.DesiredSize.Width : element.Width);
		double num2 = (double.IsNaN(element.Height) ? element.DesiredSize.Height : element.Height);
		int num3 = Math.Max(1, (int)Math.Ceiling(num * scale));
		int num4 = Math.Max(1, (int)Math.Ceiling(num2 * scale));
		if (Math.Abs(scale - 1.0) < 0.001)
		{
			return new LayeredPanelVisual(element, num3, num4);
		}
		return new LayeredPanelVisual(new Viewbox
		{
			Width = num3,
			Height = num4,
			Stretch = Stretch.Fill,
			Child = element
		}, num3, num4);
	}

	private void RenderClockPanel()
	{
		BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
		if (clockPanelHost != null && clockPanelHost.IsReady && _clockPanelVisual != null)
		{
			_clockPanelHost.Render(_clockPanelVisual);
		}
	}

	private void RenderSystemPanel()
	{
		BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
		if (systemPanelHost != null && systemPanelHost.IsReady && _systemPanelVisual != null)
		{
			_systemPanelHost.Render(_systemPanelVisual);
		}
	}

	private void RenderMediaPanel()
	{
		RequestMediaPanelRender(immediate: true);
	}

	private void CommitMediaPanelRender()
	{
		_mediaRenderTimer.Stop();
		if (!_disposed && !_isPaused && _isWindowVisible)
		{
			BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
			if (mediaPanelHost != null && mediaPanelHost.IsReady && _mediaPanelVisual != null && _mediaPanelHost.IsReady && _mediaPanelVisual != null)
			{
				_mediaPanelHost.Render(_mediaPanelVisual);
				_mediaRenderPending = false;
				_lastMediaRenderTick = Environment.TickCount64;
			}
		}
	}

	private void RequestMediaPanelRender(bool immediate)
	{
		_mediaRenderPending = true;
		if (!_disposed && !_isPaused && _isWindowVisible)
		{
			BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
			if (mediaPanelHost != null && mediaPanelHost.IsReady && _mediaPanelVisual != null)
			{
				double num = ((_lastMediaRenderTick == 0L) ? double.MaxValue : ((double)(Environment.TickCount64 - _lastMediaRenderTick)));
				if (num >= 83.33333333333333)
				{
					CommitMediaPanelRender();
					return;
				}
				double value = Math.Max(1.0, 83.33333333333333 - num);
				_mediaRenderTimer.Stop();
				_mediaRenderTimer.Interval = TimeSpan.FromMilliseconds(value);
				_mediaRenderTimer.Start();
				return;
			}
		}
		_mediaRenderTimer.Stop();
	}

	private void RenderCodexPanel()
	{
		BoundedDesktopPanelHost? codexPanelHost = _codexPanelHost;
		if (codexPanelHost != null && codexPanelHost.IsReady && _codexPanelVisual != null)
		{
			_codexPanelHost.Render(_codexPanelVisual);
		}
	}

	internal FrameworkElement CreateClock(WidgetSide side)
	{
		bool flag = side == WidgetSide.Left;
		TextAlignment textAlignment = ((!flag) ? TextAlignment.Right : TextAlignment.Left);
		Canvas obj = new Canvas
		{
			Width = 500.0,
			Height = 340.0,
			ClipToBounds = false
		};
		Border element = new Border
		{
			Width = 340.0,
			Height = 2.0,
			Background = new SolidColorBrush(ReferenceCyan)
		};
		Canvas.SetLeft(element, (!flag) ? 160 : 0);
		Canvas.SetTop(element, 5.0);
		obj.Children.Add(element);
		Canvas canvas = new Canvas
		{
			Width = 430.0,
			Height = 300.0,
			RenderTransformOrigin = new Point((!flag) ? 1 : 0, 0.5),
			RenderTransform = new RotateTransform(flag ? 11 : (-11)),
			ClipToBounds = false
		};
		Canvas.SetLeft(canvas, 30.0);
		Canvas.SetTop(canvas, 15.0);
		obj.Children.Add(canvas);
		Border border = new Border
		{
			Width = 350.0,
			Height = 58.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = new SolidColorBrush(ReferenceYearBand),
			BorderBrush = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 210)),
			BorderThickness = new Thickness(0.0, 1.0, 0.0, 1.0)
		};
		_clockYear = CreateOutlinedVectorText(52.0, FontWeights.SemiBold, ReferenceNavy, 1.05);
		_clockYear.Width = 322.0;
		_clockYear.TextAlignment = textAlignment;
		_clockYear.VerticalAlignment = VerticalAlignment.Center;
		border.Child = _clockYear;
		Canvas.SetLeft(border, (!flag) ? 80 : 0);
		Canvas.SetTop(border, 0.0);
		canvas.Children.Add(border);
		Border border2 = new Border
		{
			Width = 420.0,
			Height = 62.0,
			Background = new SolidColorBrush(ReferenceDateBand),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = (flag ? new Thickness(7.0, 0.0, 0.0, 0.0) : new Thickness(0.0, 0.0, 7.0, 0.0))
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(18.0, 0.0, 18.0, 0.0)
		};
		_clockWeekday = CreateOutlinedVectorText(34.0, FontWeights.SemiBold, ReferenceCyan, 0.85);
		_clockDay = CreateOutlinedVectorText(50.0, FontWeights.Bold, Colors.White, 0.9, ReferenceNavy);
		if (flag)
		{
			stackPanel.Children.Add(_clockDay);
			stackPanel.Children.Add(CreateSpacer(18.0));
			stackPanel.Children.Add(_clockWeekday);
		}
		else
		{
			stackPanel.Children.Add(_clockWeekday);
			stackPanel.Children.Add(CreateSpacer(18.0));
			stackPanel.Children.Add(_clockDay);
		}
		border2.Child = stackPanel;
		Canvas.SetTop(border2, 58.0);
		canvas.Children.Add(border2);
		Border border3 = new Border
		{
			Width = 330.0,
			Height = 40.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = new SolidColorBrush(ReferenceMonthBand),
			BorderThickness = new Thickness(0.0)
		};
		_clockMonth = CreateOutlinedVectorText(25.0, FontWeights.SemiBold, ReferenceNavy, 0.8);
		_clockMonth.Width = 300.0;
		_clockMonth.TextAlignment = textAlignment;
		_clockMonth.VerticalAlignment = VerticalAlignment.Center;
		border3.Child = _clockMonth;
		Canvas.SetLeft(border3, (!flag) ? 90 : 0);
		Canvas.SetTop(border3, 120.0);
		canvas.Children.Add(border3);
		Border border4 = new Border
		{
			Width = 300.0,
			Height = 66.0,
			Padding = new Thickness(15.0, 0.0, 15.0, 0.0),
			Background = new SolidColorBrush(ReferenceTimeBand),
			BorderThickness = new Thickness(0.0)
		};
		_clockTime = CreateOutlinedVectorText(58.0, FontWeights.Light, ReferenceCyan, 0.85);
		_clockTime.Width = 270.0;
		_clockTime.TextAlignment = textAlignment;
		_clockTime.HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left);
		_clockTime.VerticalAlignment = VerticalAlignment.Center;
		border4.Child = _clockTime;
		Canvas.SetLeft(border4, (!flag) ? 120 : 0);
		Canvas.SetTop(border4, 160.0);
		canvas.Children.Add(border4);
		return obj;
	}

	internal FrameworkElement CreateSystemPanel(WidgetSide side)
	{
		return new StackPanel
		{
			Width = 255.0,
			Children = 
			{
				(UIElement)CreateGauge("cpu", ReferenceCyan, "CPU 사용량", "실시간 프로세서", side),
				(UIElement)CreateGauge("gpu", ReferenceCyan, "GPU 사용량", "GPU 엔진", side),
				(UIElement)CreateGauge("memory", ReferenceCyan, "메모리", "--", side),
				(UIElement)CreateGauge("driveC", ReferenceCyan, "C:\\ 드라이브", "--", side),
				(UIElement)CreateGauge("driveD", ReferenceCyan, "D:\\ 드라이브", "--", side)
			}
		};
	}

	private FrameworkElement CreateGauge(string key, Color accent, string title, string detail, WidgetSide side)
	{
		bool flag = side == WidgetSide.Left;
		Canvas canvas = new Canvas
		{
			Width = 255.0,
			Height = 54.0,
			Background = Brushes.Transparent
		};
		int num = (flag ? 54 : 0);
		Border element = new Border
		{
			Width = 201.0,
			Height = 3.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 230))
		};
		Canvas.SetLeft(element, num);
		Canvas.SetTop(element, 29.0);
		canvas.Children.Add(element);
		Border element2 = new Border
		{
			Width = 201.0,
			Height = 1.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceNavy, 195))
		};
		Canvas.SetLeft(element2, num);
		Canvas.SetTop(element2, 30.0);
		canvas.Children.Add(element2);
		Grid grid = new Grid
		{
			Width = 54.0,
			Height = 54.0
		};
		grid.Children.Add(new Ellipse
		{
			Fill = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceBlue, 48)),
			Stroke = new SolidColorBrush(ReferenceNavy),
			StrokeThickness = 2.0
		});
		grid.Children.Add(new Ellipse
		{
			Margin = new Thickness(7.0),
			Stroke = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceCyan, 155)),
			StrokeThickness = 1.0
		});
		System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
		{
			Stroke = new SolidColorBrush(accent),
			StrokeThickness = 4.0,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		};
		grid.Children.Add(path);
		Canvas.SetLeft(grid, (!flag) ? 201 : 0);
		Canvas.SetTop(grid, 0.0);
		canvas.Children.Add(grid);
		VectorTextBlock vectorTextBlock = CreateOutlinedVectorText(18.0, FontWeights.Bold, ReferenceNavy, 0.45);
		vectorTextBlock.Text = "--%";
		vectorTextBlock.Margin = new Thickness(5.0, 0.0, 0.0, 0.0);
		Grid grid2 = new Grid
		{
			Width = 190.0,
			Height = 54.0
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left)
		};
		VectorTextBlock vectorTextBlock2 = CreateOutlinedVectorText(18.0, FontWeights.Bold, ReferenceNavy, 0.45);
		vectorTextBlock2.Text = title;
		if (flag)
		{
			stackPanel.Children.Add(vectorTextBlock);
			stackPanel.Children.Add(CreateSpacer(5.0));
			stackPanel.Children.Add(vectorTextBlock2);
		}
		else
		{
			stackPanel.Children.Add(vectorTextBlock2);
			stackPanel.Children.Add(vectorTextBlock);
		}
		VectorTextBlock vectorTextBlock3 = CreateOutlinedVectorText(11.0, FontWeights.Bold, ReferenceNavy, 0.35);
		vectorTextBlock3.Text = detail;
		vectorTextBlock3.TextAlignment = ((!flag) ? TextAlignment.Right : TextAlignment.Left);
		vectorTextBlock3.HorizontalAlignment = HorizontalAlignment.Stretch;
		vectorTextBlock3.VerticalAlignment = VerticalAlignment.Bottom;
		vectorTextBlock3.Margin = new Thickness(0.0, 0.0, 0.0, 1.0);
		stackPanel.VerticalAlignment = VerticalAlignment.Top;
		grid2.Children.Add(stackPanel);
		grid2.Children.Add(vectorTextBlock3);
		Canvas.SetLeft(grid2, flag ? 65 : 0);
		Canvas.SetTop(grid2, 0.0);
		canvas.Children.Add(grid2);
		Border result = new Border
		{
			Width = 255.0,
			Height = 54.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 3.0),
			Background = Brushes.Transparent,
			Child = canvas
		};
		_gauges[key] = new GaugeVisual(path, vectorTextBlock, vectorTextBlock3);
		return result;
	}

	internal FrameworkElement CreateMediaPanel(WidgetSide side)
	{
		Canvas canvas = new Canvas
		{
			Width = 430.0,
			Height = 146.0
		};
		Border element = new Border
		{
			Width = 430.0,
			Height = 101.0,
			CornerRadius = new CornerRadius(5.0),
			Background = CreateGradient(side == WidgetSide.Left, MikuPalette.WithAlpha(ReferenceDateBand, 222), MikuPalette.WithAlpha(ReferenceTimeBand, 205)),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = ((side == WidgetSide.Left) ? new Thickness(3.0, 0.0, 0.0, 0.0) : new Thickness(0.0, 0.0, 3.0, 0.0))
		};
		Canvas.SetLeft(element, 0.0);
		Canvas.SetTop(element, 0.0);
		canvas.Children.Add(element);
		Grid grid = new Grid();
		_mediaArtwork = new Image
		{
			Stretch = Stretch.UniformToFill,
			Visibility = Visibility.Collapsed,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RenderOptions.SetBitmapScalingMode(_mediaArtwork, BitmapScalingMode.HighQuality);
		_mediaArtworkPlaceholder = CreateText(31.0, FontWeights.Bold, MikuPalette.Mint);
		_mediaArtworkPlaceholder.Text = "♫";
		_mediaArtworkPlaceholder.TextAlignment = TextAlignment.Center;
		_mediaArtworkPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
		_mediaArtworkPlaceholder.VerticalAlignment = VerticalAlignment.Center;
		grid.Children.Add(new Border
		{
			Background = CreateRadialBackground(),
			Child = _mediaArtworkPlaceholder
		});
		grid.Children.Add(_mediaArtwork);
		Border element2 = new Border
		{
			Width = 80.0,
			Height = 80.0,
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = new Thickness(2.0),
			Child = grid
		};
		Canvas.SetLeft(element2, 5.0);
		Canvas.SetTop(element2, 8.0);
		canvas.Children.Add(element2);
		TextAlignment textAlignment = ((side != 0) ? TextAlignment.Right : TextAlignment.Left);
		_mediaTitle = CreateText(18.0, FontWeights.Bold, Colors.White);
		_mediaTitle.Width = 324.0;
		_mediaTitle.TextTrimming = TextTrimming.CharacterEllipsis;
		_mediaTitle.TextAlignment = textAlignment;
		Canvas.SetLeft(_mediaTitle, 96.0);
		Canvas.SetTop(_mediaTitle, 4.0);
		canvas.Children.Add(_mediaTitle);
		_mediaArtist = CreateText(12.0, FontWeights.Medium, Colors.White);
		_mediaArtist.Width = 324.0;
		_mediaArtist.TextTrimming = TextTrimming.CharacterEllipsis;
		_mediaArtist.TextAlignment = textAlignment;
		Canvas.SetLeft(_mediaArtist, 96.0);
		Canvas.SetTop(_mediaArtist, 31.0);
		canvas.Children.Add(_mediaArtist);
		System.Windows.Shapes.Path glyph;
		FrameworkElement element3 = CreateMediaVisualButton(MediaPlaybackCommand.Previous, isPlaying: false, out glyph);
		FrameworkElement element4 = CreateMediaVisualButton(MediaPlaybackCommand.Toggle, _lastMedia.IsPlaying, out _mediaToggleGlyph);
		FrameworkElement element5 = CreateMediaVisualButton(MediaPlaybackCommand.Next, isPlaying: false, out glyph);
		Canvas.SetLeft(element3, 96.0);
		Canvas.SetTop(element3, 56.0);
		Canvas.SetLeft(element4, 133.0);
		Canvas.SetTop(element4, 56.0);
		Canvas.SetLeft(element5, 170.0);
		Canvas.SetTop(element5, 56.0);
		canvas.Children.Add(element3);
		canvas.Children.Add(element4);
		canvas.Children.Add(element5);
		Grid grid2 = new Grid
		{
			Width = 419.0,
			Height = 35.0
		};
		_spectrumBars = new Border[24];
		for (int i = 0; i < 24; i++)
		{
			grid2.ColumnDefinitions.Add(new ColumnDefinition());
			Border border = new Border
			{
				Height = 2.0,
				Margin = new Thickness(1.4, 0.0, 1.4, 0.0),
				VerticalAlignment = VerticalAlignment.Bottom,
				Background = CreateGradient(leftToRight: false, Colors.White, ReferenceCyan)
			};
			Grid.SetColumn(border, i);
			grid2.Children.Add(border);
			_spectrumBars[i] = border;
		}
		Canvas.SetLeft(grid2, 5.0);
		Canvas.SetTop(grid2, 105.0);
		canvas.Children.Add(grid2);
		Border element6 = new Border
		{
			Width = 419.0,
			Height = 2.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 225))
		};
		Canvas.SetLeft(element6, 5.0);
		Canvas.SetTop(element6, 141.0);
		canvas.Children.Add(element6);
		Border border2 = new Border
		{
			Width = 324.0,
			Height = 3.0,
			Background = Brush(150, byte.MaxValue, byte.MaxValue, byte.MaxValue)
		};
		_mediaProgress = new Border
		{
			Width = 0.0,
			Height = 3.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			Background = new SolidColorBrush(ReferenceCyan)
		};
		border2.Child = _mediaProgress;
		Canvas.SetLeft(border2, 96.0);
		Canvas.SetTop(border2, 94.0);
		canvas.Children.Add(border2);
		return canvas;
	}

	private static FrameworkElement CreateMediaVisualButton(MediaPlaybackCommand command, bool isPlaying, out System.Windows.Shapes.Path glyph)
	{
		glyph = new System.Windows.Shapes.Path
		{
			Data = NativeMediaControlsWindow.CreateGlyphGeometry(command, isPlaying),
			Fill = Brushes.White,
			Stretch = Stretch.None,
			Width = 16.0,
			Height = 16.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RadialGradientBrush radialGradientBrush = new RadialGradientBrush
		{
			Center = new Point(0.38, 0.3),
			GradientOrigin = new Point(0.38, 0.3),
			RadiusX = 0.8,
			RadiusY = 0.8
		};
		radialGradientBrush.GradientStops.Add(new GradientStop(ReferenceCyan, 0.0));
		radialGradientBrush.GradientStops.Add(new GradientStop(ReferenceBlue, 0.74));
		return new Border
		{
			Width = 30.0,
			Height = 30.0,
			CornerRadius = new CornerRadius(15.0),
			BorderThickness = new Thickness(1.2),
			BorderBrush = new SolidColorBrush(Colors.White),
			Background = radialGradientBrush,
			Child = glyph
		};
	}

	private FrameworkElement CreateCodexPanel(WidgetSide side)
	{
		Canvas obj = new Canvas
		{
			Width = 360.0,
			Height = 280.0
		};
		Border border = new Border
		{
			Width = 202.0,
			MinHeight = 92.0,
			Padding = new Thickness(15.0, 13.0, 15.0, 13.0),
			Background = CreateGradient(leftToRight: true, MikuPalette.WithAlpha(ReferenceDateBand, 230), MikuPalette.WithAlpha(ReferenceTimeBand, 215)),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = ((side == WidgetSide.Left) ? new Thickness(0.0, 0.0, 3.0, 0.0) : new Thickness(3.0, 0.0, 0.0, 0.0))
		};
		StackPanel stackPanel = new StackPanel();
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		_codexPulse = new Ellipse
		{
			Width = 7.0,
			Height = 7.0,
			Margin = new Thickness(0.0, 3.0, 7.0, 0.0),
			Fill = new SolidColorBrush(ReferenceCyan)
		};
		_codexHeadline = CreateText(12.0, FontWeights.Bold, Colors.White);
		stackPanel2.Children.Add(_codexPulse);
		stackPanel2.Children.Add(_codexHeadline);
		_codexDetail = CreateText(11.0, FontWeights.Normal, Colors.White);
		_codexDetail.TextWrapping = TextWrapping.Wrap;
		_codexDetail.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		_codexMeta = CreateText(8.0, FontWeights.SemiBold, MikuPalette.WithAlpha(Colors.White, 225));
		_codexMeta.TextWrapping = TextWrapping.Wrap;
		_codexMeta.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		stackPanel.Children.Add(stackPanel2);
		stackPanel.Children.Add(_codexDetail);
		stackPanel.Children.Add(_codexMeta);
		border.Child = stackPanel;
		Canvas.SetLeft(border, (side == WidgetSide.Left) ? 158 : 0);
		Canvas.SetTop(border, 39.0);
		obj.Children.Add(border);
		Image image = new Image
		{
			Width = 235.0,
			Height = 255.0,
			Stretch = Stretch.Uniform,
			Source = LoadCompanionImage(),
			RenderTransformOrigin = new Point(0.5, 0.5),
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		_codexTranslate = new TranslateTransform();
		TransformGroup transformGroup = new TransformGroup();
		if (side == WidgetSide.Left)
		{
			transformGroup.Children.Add(new ScaleTransform(-1.0, 1.0));
		}
		transformGroup.Children.Add(_codexTranslate);
		image.RenderTransform = transformGroup;
		Canvas.SetLeft(image, (side != 0) ? 125 : 0);
		Canvas.SetTop(image, 25.0);
		obj.Children.Add(image);
		return obj;
	}

	internal void UpdateClockAndCalendar()
	{
		if (_disposed)
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (_clockTime != null)
		{
			string text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
			string text2 = now.Year.ToString(CultureInfo.InvariantCulture);
			string text3 = now.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant();
			string text4 = now.Day.ToString("00", CultureInfo.InvariantCulture);
			string text5 = now.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
			if (!string.Equals(_clockTime.Text, text, StringComparison.Ordinal) || !string.Equals(_clockYear.Text, text2, StringComparison.Ordinal) || !string.Equals(_clockWeekday.Text, text3, StringComparison.Ordinal) || !string.Equals(_clockDay.Text, text4, StringComparison.Ordinal) || !string.Equals(_clockMonth.Text, text5, StringComparison.Ordinal))
			{
				_clockTime.Text = text;
				_clockYear.Text = text2;
				_clockWeekday.Text = text3;
				_clockDay.Text = text4;
				_clockMonth.Text = text5;
				RenderClockPanel();
			}
		}
	}

	private void ApplyLastMetrics()
	{
		SystemMetricsSnapshot lastMetrics = _lastMetrics;
		if ((object)lastMetrics != null)
		{
			double num = Math.Round(lastMetrics.CpuPercent);
			double num2 = ((!lastMetrics.GpuPercent.HasValue) ? 0.0 : Math.Round(lastMetrics.GpuPercent.Value));
			double num3 = Math.Round(lastMetrics.MemoryPercent);
			double num4 = Math.Round(lastMetrics.DriveC.Percent);
			double num5 = Math.Round(lastMetrics.DriveD.Percent);
			if (SetGauge("cpu", num, $"{num}%", "실시간 프로세서") | SetGauge("gpu", num2, (!lastMetrics.GpuPercent.HasValue) ? "N/A" : $"{num2}%", (!lastMetrics.GpuPercent.HasValue) ? "측정 불가" : "GPU 엔진") | SetGauge("memory", num3, $"{num3}%", FormatBytes(lastMetrics.MemoryUsedBytes) + " / " + FormatBytes(lastMetrics.MemoryTotalBytes)) | SetGauge("driveC", num4, lastMetrics.DriveC.IsAvailable ? $"{num4}%" : "N/A", FormatDrive(lastMetrics.DriveC)) | SetGauge("driveD", num5, lastMetrics.DriveD.IsAvailable ? $"{num5}%" : "N/A", FormatDrive(lastMetrics.DriveD)))
			{
				RenderSystemPanel();
			}
		}
	}

	private void ApplyLastAddOns()
	{
		if (_mediaTitle != null)
		{
			bool flag = !object.Equals(_appliedMedia, _lastMedia);
			if (flag)
			{
				_mediaTitle.Text = _lastMedia.Title;
				_mediaArtist.Text = _lastMedia.Artist;
				if (_mediaToggleGlyph != null)
				{
					_mediaToggleGlyph.Data = NativeMediaControlsWindow.CreateGlyphGeometry(MediaPlaybackCommand.Toggle, _lastMedia.IsPlaying);
				}
				_mediaProgress.Width = 324.0 * Math.Clamp(_lastMedia.Progress, 0.0, 1.0);
				UpdateArtwork(_lastMedia.ArtworkDataUri);
				_appliedMedia = _lastMedia;
			}
			ApplySpectrum(!flag);
			if (flag)
			{
				RequestMediaPanelRender(immediate: true);
			}
		}
		if (_codexHeadline != null)
		{
			string text = string.Join(" // ", new string[2] { _lastCodex.Workspace, _lastCodex.Model }.Where((string value) => !string.IsNullOrWhiteSpace(value)));
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "로컬 상태 전용";
			}
			bool flag2 = !string.Equals(_codexHeadline.Text, _lastCodex.Headline, StringComparison.Ordinal) || !string.Equals(_codexDetail.Text, _lastCodex.Detail, StringComparison.Ordinal) || !string.Equals(_codexMeta.Text, text, StringComparison.Ordinal) || !string.Equals(_lastCodexState, _lastCodex.State, StringComparison.Ordinal);
			_codexHeadline.Text = _lastCodex.Headline;
			_codexDetail.Text = _lastCodex.Detail;
			_codexMeta.Text = text;
			Color color;
			switch (_lastCodex.State)
			{
			case "complete":
				color = MikuPalette.Mint;
				break;
			case "offline":
			case "disconnected":
				color = MikuPalette.Silver;
				break;
			default:
				color = MikuPalette.Pink;
				break;
			}
			Color color2 = color;
			_codexPulse.Fill = new SolidColorBrush(color2);
			if (!string.Equals(_lastCodexState, _lastCodex.State, StringComparison.Ordinal))
			{
				_lastCodexState = _lastCodex.State;
			}
			if (flag2)
			{
				RenderCodexPanel();
			}
		}
	}

	private void ApplySpectrum(bool requestRender = true)
	{
		if (_isPaused || !_isWindowVisible)
		{
			return;
		}
		bool flag = false;
		for (int i = 0; i < _spectrumBars.Length; i++)
		{
			double num = Math.Pow(Math.Clamp(_lastSpectrum.ElementAtOrDefault(i) * 2.35f, 0f, 1f), 0.55);
			double num2 = Math.Max(2.0, num * 35.0);
			double num3 = (_lastMedia.IsPlaying ? 1.0 : 0.26);
			Border border = _spectrumBars[i];
			if (!(Math.Abs(border.Height - num2) < 0.01) || !(Math.Abs(border.Opacity - num3) < 0.01))
			{
				border.Height = num2;
				border.Opacity = num3;
				flag = true;
			}
		}
		if (flag && requestRender)
		{
			RequestMediaPanelRender(immediate: false);
		}
	}

	private void UpdateArtwork(string dataUri)
	{
		if (_mediaArtwork != null && !string.Equals(dataUri, _lastArtworkDataUri, StringComparison.Ordinal))
		{
			_lastArtworkDataUri = dataUri;
			ImageSource imageSource = DecodeDataUri(dataUri);
			_mediaArtwork.Source = imageSource;
			_mediaArtwork.Visibility = ((imageSource == null) ? Visibility.Collapsed : Visibility.Visible);
			if (_mediaArtworkPlaceholder != null)
			{
				_mediaArtworkPlaceholder.Visibility = ((imageSource != null) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
	}

	private bool SetGauge(string key, double percent, string value, string detail)
	{
		if (!_gauges.TryGetValue(key, out GaugeVisual value2))
		{
			return false;
		}
		if (string.Equals(value2.Value.Text, value, StringComparison.Ordinal) && string.Equals(value2.Detail.Text, detail, StringComparison.Ordinal))
		{
			return false;
		}
		value2.Value.Text = value;
		value2.Detail.Text = detail;
		value2.Arc.Data = CreateArcGeometry(percent);
		return true;
	}

	private static Geometry CreateArcGeometry(double percent)
	{
		double num = Math.Clamp(percent, 0.0, 100.0);
		if (num <= 0.0)
		{
			return Geometry.Empty;
		}
		double num2 = ((num >= 100.0) ? 359.999 : (num * 3.6));
		double num3 = (num2 - 90.0) * Math.PI / 180.0;
		Point point = new Point(27.0 + 23.0 * Math.Cos(num3), 27.0 + 23.0 * Math.Sin(num3));
		return new PathGeometry(new <>z__ReadOnlySingleElementList<PathFigure>(new PathFigure
		{
			StartPoint = new Point(27.0, 4.0),
			Segments = { (PathSegment)new ArcSegment
			{
				Point = point,
				Size = new Size(23.0, 23.0),
				IsLargeArc = (num2 > 180.0),
				SweepDirection = SweepDirection.Clockwise
			} }
		}));
	}

	private void StartCodexAnimation()
	{
		RenderCodexPanel();
	}

	private void StopCodexAnimation()
	{
		if (_codexTranslate != null)
		{
			_codexTranslate.BeginAnimation(TranslateTransform.YProperty, null);
			_codexTranslate.Y = 0.0;
		}
	}

	private void AddPositioned(Canvas canvas, FrameworkElement element, WidgetSide side, double margin, double top, double scale)
	{
		element.RenderTransformOrigin = new Point((side != WidgetSide.Left) ? 1 : 0, 0.0);
		element.RenderTransform = new ScaleTransform(scale, scale);
		Canvas.SetLeft(element, (side == WidgetSide.Left) ? margin : ((double)_width - margin - element.Width));
		Canvas.SetTop(element, top);
		canvas.Children.Add(element);
	}

	private void ApplyLayerOrder()
	{
		if (!_initialized)
		{
			return;
		}
		uint flags = 19u;
		ApplyBoundedPanelLayerOrder(flags);
		if (!UsesFullDisplayCompositionSurface)
		{
			return;
		}
		if (_isRaisedDesktop)
		{
			if (!NativeMethods.SetWindowPos(Handle, _shellViewHandle, 0, 0, 0, 0, flags))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯 Z 순서를 설정하지 못했습니다.");
			}
		}
		else if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯을 월페이퍼 위에 배치하지 못했습니다.");
		}
		BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
		if (clockPanelHost != null && clockPanelHost.IsReady && !NativeMethods.SetWindowPos(_clockPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "시계 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
		if (systemPanelHost != null && systemPanelHost.IsReady && !NativeMethods.SetWindowPos(_systemPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "시스템 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
		if (mediaPanelHost != null && mediaPanelHost.IsReady && !NativeMethods.SetWindowPos(_mediaPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "미디어 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? codexPanelHost = _codexPanelHost;
		if (codexPanelHost == null || !codexPanelHost.IsReady || NativeMethods.SetWindowPos(_codexPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			return;
		}
		throw new Win32Exception(Marshal.GetLastWin32Error(), "Codex 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
	}

	private void ApplyBoundedPanelLayerOrder(uint flags)
	{
		foreach (BoundedDesktopPanelHost item in EnumeratePanelHosts())
		{
			if (item.IsReady)
			{
				nint insertAfter = (_isRaisedDesktop ? _shellViewHandle : IntPtr.Zero);
				if (!NativeMethods.SetWindowPos(item.Handle, insertAfter, 0, 0, 0, 0, flags))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 독립 위젯 창의 바탕화면 Z 순서를 설정하지 못했습니다.");
				}
			}
		}
	}

	[IteratorStateMachine(typeof(<EnumeratePanelHosts>d__130))]
	private IEnumerable<BoundedDesktopPanelHost> EnumeratePanelHosts()
	{
		//yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
		return new <EnumeratePanelHosts>d__130(-2)
		{
			<>4__this = this
		};
	}

	private void ClockTimerOnTick(object? sender, EventArgs eventArgs)
	{
		_clockTimer.Stop();
		UpdateClockAndCalendar();
		UpdateClockTimer();
	}

	private void MediaRenderTimerOnTick(object? sender, EventArgs eventArgs)
	{
		_mediaRenderTimer.Stop();
		if (_mediaRenderPending)
		{
			CommitMediaPanelRender();
		}
	}

	private void UpdateClockTimer()
	{
		if (!_isPaused && _settings.IsEnabled && _settings.ShowClock)
		{
			_clockTimer.Stop();
			DateTime now = DateTime.Now;
			DateTime dateTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1.0);
			_clockTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100.0, (dateTime - now).TotalMilliseconds + 25.0));
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
		_mediaArtwork = null;
		_mediaArtworkPlaceholder = null;
		_mediaTitle = null;
		_mediaArtist = null;
		_mediaProgress = null;
		_mediaToggleGlyph = null;
		_spectrumBars = Array.Empty<Border>();
		_codexHeadline = null;
		_codexDetail = null;
		_codexMeta = null;
		_codexPulse = null;
		_codexTranslate = null;
		_lastCodexState = string.Empty;
		_lastArtworkDataUri = string.Empty;
		_appliedMedia = null;
	}

	private static TextBlock CreateText(double fontSize, FontWeight fontWeight, Color color)
	{
		return new TextBlock
		{
			FontFamily = new FontFamily("Pretendard, Malgun Gothic, Segoe UI"),
			FontSize = fontSize,
			FontWeight = fontWeight,
			Foreground = new SolidColorBrush(color),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
	}

	private static VectorTextBlock CreateVectorText(double fontSize, FontWeight fontWeight, Color color)
	{
		return new VectorTextBlock
		{
			FontFamily = ReferenceFontFamily,
			FontSize = fontSize,
			FontWeight = fontWeight,
			FillColor = color,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false,
			ClipToBounds = false
		};
	}

	private static VectorTextBlock CreateOutlinedVectorText(double fontSize, FontWeight fontWeight, Color color, double outlineThickness, Color? outlineColor = null)
	{
		VectorTextBlock vectorTextBlock = CreateVectorText(fontSize, fontWeight, color);
		vectorTextBlock.OutlineColor = outlineColor ?? Colors.White;
		vectorTextBlock.OutlineThickness = outlineThickness;
		return vectorTextBlock;
	}

	private static FrameworkElement CreateSpacer(double width)
	{
		return new Border
		{
			Width = width
		};
	}

	private static LinearGradientBrush CreateGradient(bool leftToRight, Color start, Color end)
	{
		return new LinearGradientBrush
		{
			StartPoint = (leftToRight ? new Point(0.0, 0.5) : new Point(1.0, 0.5)),
			EndPoint = (leftToRight ? new Point(1.0, 0.5) : new Point(0.0, 0.5)),
			GradientStops = 
			{
				new GradientStop(start, 0.0),
				new GradientStop(end, 1.0)
			}
		};
	}

	private static RadialGradientBrush CreateRadialBackground()
	{
		return new RadialGradientBrush
		{
			Center = new Point(0.45, 0.4),
			GradientOrigin = new Point(0.45, 0.4),
			RadiusX = 0.75,
			RadiusY = 0.75,
			GradientStops = 
			{
				new GradientStop(MikuPalette.Teal, 0.0),
				new GradientStop(MikuPalette.Graphite, 0.72)
			}
		};
	}

	private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
	{
		return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
	}

	private static ImageSource? DecodeDataUri(string dataUri)
	{
		if (string.IsNullOrWhiteSpace(dataUri))
		{
			return null;
		}
		try
		{
			int num = dataUri.IndexOf(',');
			if (num < 0)
			{
				return null;
			}
			int num2 = num + 1;
			using MemoryStream streamSource = new MemoryStream(Convert.FromBase64String(dataUri.Substring(num2, dataUri.Length - num2)));
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.StreamSource = streamSource;
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource? LoadCompanionImage()
	{
		try
		{
			string text = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Companion", "vellume-companion.png");
			if (!File.Exists(text))
			{
				return null;
			}
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.UriSource = new Uri(text, UriKind.Absolute);
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static string FormatBytes(ulong bytes)
	{
		return $"{(double)bytes / 1073741824.0:0.0} GB";
	}

	private static string FormatDrive(DriveMetricsSnapshot drive)
	{
		if (!drive.IsAvailable)
		{
			return "사용할 수 없음";
		}
		return FormatBytes(drive.UsedBytes) + " / " + FormatBytes(drive.TotalBytes);
	}
}
