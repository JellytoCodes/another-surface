using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vellume.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace Vellume.Windows;

internal sealed partial class NativeWidgetRendererWindow
{
    private Window? _codexDetailsWindow;
    private TextBlock? _codexDetailHeadline;
    private TextBlock? _codexDetailDescription;
    private readonly DispatcherTimer _codexNoticeTimer;
    private bool _codexCompletionVisible;

    internal string CodexSummary => _codexHeadline?.Text ?? string.Empty;
    internal nint CodexDetailsHandle => _codexDetailsWindow is { } details
        ? new WindowInteropHelper(details).Handle : nint.Zero;

    private FrameworkElement CreateCodexPanel(WidgetSide side)
    {
        var canvas = new Canvas { Width = 180, Height = 148 };
        _codexCharacter = new Image
        {
            Width = 112, Height = 116, Stretch = Stretch.Uniform,
            Source = GetCompanionImage(ResolveCodexCompanionVisualState(_lastCodex))
        };
        RenderOptions.SetBitmapScalingMode(_codexCharacter, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(_codexCharacter, 34);
        Canvas.SetTop(_codexCharacter, 2);
        canvas.Children.Add(_codexCharacter);

        var badge = new Border
        {
            Width = 180, Height = 28, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromArgb(235, 27, 35, 49)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(75, 158, 233, 222)),
            BorderThickness = new Thickness(1)
        };
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        _codexPulse = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(MikuPalette.Silver),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        _codexHeadline = CreateText(11, FontWeights.SemiBold, Colors.White);
        _codexHeadline.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_codexHeadline, 1);
        row.Children.Add(_codexPulse);
        row.Children.Add(_codexHeadline);
        badge.Child = row;
        Canvas.SetTop(badge, 120);
        canvas.Children.Add(badge);
        return canvas;
    }

    internal static string GetCodexSummary(CodexCompanionSnapshot snapshot, CodexCompanionVisualState state)
    {
        var label = state switch
        {
            CodexCompanionVisualState.Thinking => "생각 중",
            CodexCompanionVisualState.Working => "작업 중",
            CodexCompanionVisualState.Complete => "작업 완료",
            CodexCompanionVisualState.Offline => "연결 안 됨",
            _ => "대기 중"
        };
        if (state == CodexCompanionVisualState.Offline) return label;
        var project = snapshot.Projects.FirstOrDefault(item => item.State is "working" or "thinking")
                      ?? snapshot.Projects.FirstOrDefault();
        var workspace = string.IsNullOrWhiteSpace(snapshot.Workspace) ? project?.Workspace : snapshot.Workspace;
        var name = workspace?.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(name)) name = project?.Title;
        return string.IsNullOrWhiteSpace(name) ? label : $"{label} · {TruncateWithEllipsis(name, 14)}";
    }

    private void UpdateCodexCompletion(CodexCompanionSnapshot previous, CodexCompanionSnapshot current)
    {
        if (!string.Equals(current.State, "complete", StringComparison.OrdinalIgnoreCase))
        {
            _codexCompletionVisible = false;
            _codexNoticeTimer.Stop();
        }
        else if (!string.Equals(previous.State, "complete", StringComparison.OrdinalIgnoreCase))
        {
            _codexCompletionVisible = true;
            _codexNoticeTimer.Start();
        }
    }

    private void CodexNoticeTimerOnTick(object? sender, EventArgs e)
    {
        _codexNoticeTimer.Stop();
        _codexCompletionVisible = false;
        if (!_disposed && !_isPaused) ApplyLastCodex();
    }

    private void ApplyLastCodex()
    {
        if (_codexHeadline is null) return;
        var state = ResolveCodexCompanionVisualState(_lastCodex);
        if (state == CodexCompanionVisualState.Complete && !_codexCompletionVisible)
            state = CodexCompanionVisualState.Ready;
        var summary = GetCodexSummary(_lastCodex, state);
        var signature = string.Join("|", _lastCodex.State, _lastCodex.Headline, _lastCodex.Detail, summary,
            string.Join(";", _lastCodex.Projects.Take(3).Select(project =>
                $"{project.Id}:{project.State}:{project.Title}:{project.Phase}:{project.Workspace}")),
            _lastCodex.Usage.IsAvailable,
            string.Join(";", _lastCodex.Usage.Limits.Take(2).Select(limit =>
                $"{limit.Id}:{limit.DisplayName}:{limit.RemainingPercent}:{limit.ResetsAtUtc?.UtcTicks}")));
        if (_lastCodexVisualState == state && _lastCodexContentSignature == signature) return;
        _codexCharacter!.Source = GetCompanionImage(state);
        _lastCodexVisualState = state;
        _lastCodexContentSignature = signature;
        _codexHeadline.Text = summary;
        _codexPulse!.Fill = new SolidColorBrush(state switch
        {
            CodexCompanionVisualState.Offline => MikuPalette.Silver,
            CodexCompanionVisualState.Working => MikuPalette.Pink,
            CodexCompanionVisualState.Thinking => ReferenceCyan,
            _ => MikuPalette.Mint
        });
        UpdateCodexDetails();
        RenderCodexPanel();
    }

    internal void ToggleCodexDetails()
    {
        if (_codexDetailsWindow is not null) { CloseCodexDetails(); return; }
        if (_disposed || _isPaused || !_isWindowVisible || !_widgetHosts.TryGetValue(WidgetKind.CodexCompanion, out var state)) return;
        var content = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = new System.Windows.Controls.Button
        {
            Content = "×", Width = 26, Height = 26, FontSize = 18,
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            ToolTip = "닫기", Cursor = System.Windows.Input.Cursors.Hand
        };
        close.Click += (_, _) => CloseCodexDetails();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var title = CreateText(12, FontWeights.Bold, MikuPalette.Mint);
        title.Text = "CODEX";
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        content.Children.Add(header);
        _codexDetailHeadline = CreateText(16, FontWeights.Bold, Colors.White);
        _codexDetailDescription = CreateText(12, FontWeights.Normal, Color.FromRgb(185, 196, 211));
        _codexDetailDescription.TextWrapping = TextWrapping.Wrap;
        _codexDetailDescription.MaxHeight = 52;
        _codexDetailDescription.Margin = new Thickness(0, 6, 0, 14);
        content.Children.Add(_codexDetailHeadline);
        content.Children.Add(_codexDetailDescription);
        var projectTitle = CreateText(11, FontWeights.Bold, MikuPalette.Mint);
        projectTitle.Text = "프로젝트";
        content.Children.Add(projectTitle);
        _codexProjects = new StackPanel { Margin = new Thickness(0, 6, 0, 14) };
        content.Children.Add(_codexProjects);
        var usageTitle = CreateText(11, FontWeights.Bold, MikuPalette.Mint);
        usageTitle.Text = "사용량";
        content.Children.Add(usageTitle);
        _codexUsageLimits = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(_codexUsageLimits);
        var window = new Window
        {
            Title = "Vellume Codex 상세", Width = 304, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(27, 35, 49)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 158, 233, 222)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(16),
                Child = new ScrollViewer { Content = content, MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            }
        };
        _codexDetailsWindow = window;
        window.Deactivated += (_, _) => CloseCodexDetails();
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseCodexDetails(); };
        window.SizeChanged += (_, _) => PositionCodexDetails();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_codexDetailsWindow, window)) _codexDetailsWindow = null;
            _pointerHook?.ObservePrimaryPress(null);
            _codexProjects = null;
            _codexUsageLimits = null;
            _codexDetailHeadline = null;
            _codexDetailDescription = null;
        };
        UpdateCodexDetails();
        window.Show();
        PositionCodexDetails();
        _pointerHook?.ObservePrimaryPress(DismissCodexDetailsOutside);
        window.Activate();
    }

    private void DismissCodexDetailsOutside(int x, int y)
    {
        if (_codexDetailsWindow is not { } window ||
            !NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds)) return;
        if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return;
        if (_widgetHosts.TryGetValue(WidgetKind.CodexCompanion, out var state) &&
            x >= state.X && x < state.X + state.Host.Width && y >= state.Y && y < state.Y + state.Host.Height &&
            (state.Editor is not null || state.Host.HitTest(x - state.X, y - state.Y))) return;
        CloseCodexDetails();
    }

    private void PositionCodexDetails()
    {
        if (_codexDetailsWindow is not { IsVisible: true } window ||
            !_widgetHosts.TryGetValue(WidgetKind.CodexCompanion, out var state)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var display = state.Display;
        var x = state.X + state.Host.Width + 8;
        if (x + width > display.X + display.Width - 8) x = state.X - width - 8;
        x = Math.Clamp(x, display.X + 8, Math.Max(display.X + 8, display.X + display.Width - width - 8));
        var y = Math.Clamp(state.Y, display.Y + 8, Math.Max(display.Y + 8, display.Y + display.Height - height - 8));
        NativeMethods.SetWindowPos(handle, nint.Zero, x, y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void UpdateCodexDetails()
    {
        if (_codexDetailHeadline is null) return;
        // Retain the last completion in the details after its transient badge expires.
        _codexDetailHeadline.Text = GetCodexSummary(_lastCodex, ResolveCodexCompanionVisualState(_lastCodex));
        _codexDetailDescription!.Text = _lastCodex.Detail;
        UpdateCodexProjectRows();
        UpdateCodexUsageRows();
    }

    private void CloseCodexDetails()
    {
        var details = _codexDetailsWindow;
        _codexDetailsWindow = null;
        details?.Close();
    }
}
