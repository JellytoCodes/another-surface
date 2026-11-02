using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class ForegroundAppPausePolicyTests
{
    private static readonly ScreenBounds Monitor =
        new(0, 0, 1920, 1080);
    private static readonly ScreenBounds WorkArea =
        new(0, 0, 1920, 1040);

    [Fact]
    public void OrdinaryMaximizedApplication_PausesRendering()
    {
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: new(8, 8, 1912, 1040));

        Assert.True(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void MaximizedFlagWithoutWorkspaceCoverage_KeepsRendering()
    {
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: new(200, 120, 1500, 900));

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void BorderlessFullScreenApplication_PausesRendering()
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(0, 0, 1920, 1080));

        Assert.True(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void CustomWindowCoveringOnlyWorkArea_KeepsRendering()
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: WorkArea);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void WindowsDirect3DFullScreenSignal_PausesRendering()
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(100, 100, 1400, 900),
            isSystemDirect3DFullScreen: true);

        Assert.True(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Theory]
    [InlineData("UnrealEditor")]
    [InlineData("UnrealEditor-Win64-DebugGame")]
    [InlineData("UE4Editor")]
    [InlineData("UE5Editor")]
    public void RestoredUnrealEditor_KeepsRendering(
        string processName)
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(100, 100, 1500, 900),
            processName: processName);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void RestoredLightweightApplication_KeepsRendering()
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(100, 100, 1400, 900));

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileExplorerWindow_NeverPausesRendering(bool isMaximized)
    {
        var snapshot = Snapshot(
            isMaximized,
            isMaximized
                ? WorkArea
                : new ScreenBounds(100, 100, 1400, 900),
            windowClass: "CabinetWClass",
            processName: "explorer");

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void LargeButNotWorkspaceCoveringBrowser_KeepsRendering()
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(200, 100, 1500, 900),
            windowClass: "Chrome_WidgetWin_1",
            processName: "whale");

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("SystemSettings")]
    [InlineData("SearchHost")]
    public void WindowsShellHost_NeverPausesRendering(
        string processName)
    {
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: WorkArea,
            windowClass: "ApplicationFrameWindow",
            processName: processName);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Theory]
    [InlineData("devenv")]
    [InlineData("Unity")]
    [InlineData("blender")]
    [InlineData("Photoshop")]
    public void RestoredHeavyApplication_KeepsRendering(
        string processName)
    {
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(300, 120, 1500, 900),
            processName: processName);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    public void ExplorerDesktopSurfaces_DoNotPauseRendering(
        string windowClass)
    {
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: Monitor,
            windowClass: windowClass);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void AnotherSurfaceWindow_DoesNotPauseItsOwnRendering()
    {
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: Monitor,
            isOwnProcess: true);

        Assert.False(ForegroundAppPausePolicy.ShouldPause(snapshot));
    }

    [Fact]
    public void ExplicitKeepRunningRule_OverridesHeavyApplicationFallback()
    {
        var options = new PlaybackOptions
        {
            PauseWhenForegroundAppIsMaximized = true,
            ApplicationRules =
            [
                new ApplicationPlaybackRule
                {
                    ProcessName = "UnrealEditor",
                    Condition =
                        ApplicationRuleCondition.IsForeground,
                    Action =
                        ApplicationPlaybackAction.KeepRunning
                }
            ]
        };
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: WorkArea,
            processName: "UnrealEditor");

        var action =
            ForegroundAppPausePolicy.ResolveAction(
                snapshot,
                options);

        Assert.Equal(
            ApplicationPlaybackAction.KeepRunning,
            action);
    }

    [Fact]
    public void ExplicitForegroundStopRule_StopsRestoredLightweightApp()
    {
        var options = new PlaybackOptions
        {
            PauseWhenForegroundAppIsMaximized = true,
            ApplicationRules =
            [
                new ApplicationPlaybackRule
                {
                    ProcessName = "ExampleGame",
                    Condition =
                        ApplicationRuleCondition.IsForeground,
                    Action =
                        ApplicationPlaybackAction.StopFreeMemory
                }
            ]
        };
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(200, 120, 1400, 900),
            processName: "ExampleGame");

        var action =
            ForegroundAppPausePolicy.ResolveAction(
                snapshot,
                options);

        Assert.Equal(
            ApplicationPlaybackAction.StopFreeMemory,
            action);
    }

    [Fact]
    public void ShellSafety_OverridesExplicitStopRule()
    {
        var options = new PlaybackOptions
        {
            PauseWhenForegroundAppIsMaximized = true,
            ApplicationRules =
            [
                new ApplicationPlaybackRule
                {
                    ProcessName = "explorer",
                    Condition =
                        ApplicationRuleCondition.IsForeground,
                    Action =
                        ApplicationPlaybackAction.StopFreeMemory
                }
            ]
        };
        var snapshot = Snapshot(
            isMaximized: true,
            windowBounds: WorkArea,
            windowClass: "CabinetWClass",
            processName: "explorer");

        var action =
            ForegroundAppPausePolicy.ResolveAction(
                snapshot,
                options);

        Assert.Equal(
            ApplicationPlaybackAction.KeepRunning,
            action);
    }

    [Fact]
    public void ProcessNameNormalization_MatchesWhitespaceCaseAndExeSuffix()
    {
        var options = new PlaybackOptions
        {
            PauseWhenForegroundAppIsMaximized = true,
            ApplicationRules =
            [
                new ApplicationPlaybackRule
                {
                    ProcessName = "  ExampleGame.EXE  ",
                    Condition =
                        ApplicationRuleCondition.IsForeground,
                    Action = ApplicationPlaybackAction.Pause
                }
            ]
        };
        var snapshot = Snapshot(
            isMaximized: false,
            windowBounds: new(200, 120, 1400, 900),
            processName: "examplegame");

        var action =
            ForegroundAppPausePolicy.ResolveAction(
                snapshot,
                options);

        Assert.Equal(
            ApplicationPlaybackAction.Pause,
            action);
    }


    private static ForegroundWindowSnapshot Snapshot(
        bool isMaximized,
        ScreenBounds windowBounds,
        string windowClass = "TestWindow",
        bool isOwnProcess = false,
        string processName = "TestApplication",
        uint processId = 4242,
        bool isSystemDirect3DFullScreen = false)
        => new ForegroundWindowSnapshot(
            IsValid: true,
            IsVisible: true,
            IsOwnProcess: isOwnProcess,
            ProcessId: processId,
            ProcessName: processName,
            WindowClass: windowClass,
            IsMaximized: isMaximized,
            WindowBounds: windowBounds,
            MonitorBounds: Monitor,
            WorkAreaBounds: WorkArea)
        {
            IsSystemDirect3DFullScreen =
                isSystemDirect3DFullScreen
        };
}
