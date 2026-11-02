using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WindowsDockRuntimeServiceTests
{
    [Fact]
    public void DockCapture_CancelledRequestDoesNotStartWindowScan()
    {
        using var service = new WindowsDockRuntimeService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(
            () => service.Capture(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void QuickSlotCapture_CancelledRequestDoesNotReadTargets()
    {
        using var service = new WindowsQuickSlotRuntimeService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(
            () => service.Capture([], [], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("SystemSettings")]
    [InlineData("ShellExperienceHost")]
    [InlineData("StartMenuExperienceHost")]
    public void ShellHosts_AreExcludedFromDockLaunches(string processName)
    {
        Assert.True(
            WindowsDockRuntimeService.IsShellHostProcessName(processName));
    }

    [Theory]
    [InlineData("whale")]
    [InlineData("firefox")]
    [InlineData("devenv")]
    public void NormalApplications_RemainAvailableInDock(string processName)
    {
        Assert.False(
            WindowsDockRuntimeService.IsShellHostProcessName(processName));
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", true)]
    [InlineData(@"c:\windows\EXPLORER.EXE", true)]
    [InlineData(@"C:\Apps\firefox.exe", false)]
    public void ExplorerExecutable_IsIdentifiedForPinnedSuppression(
        string executablePath,
        bool expected)
        => Assert.Equal(
            expected,
            WindowsDockRuntimeService.IsExplorerExecutable(
                executablePath));

    [Fact]
    public void ShellNotification_RaisesDockRefreshEvent()
    {
        using var service = new WindowsDockRuntimeService();
        var changeCount = 0;
        service.StateChanged += (_, _) => changeCount++;

        Assert.True(service.TryHandleShellMessage(
            service.ShellHookMessage,
            nint.Zero,
            nint.Zero));
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void ShellNotification_RaisesQuickSlotRefreshEvent()
    {
        using var service = new WindowsQuickSlotRuntimeService();
        var changeCount = 0;
        service.StateChanged += (_, _) => changeCount++;

        Assert.True(service.TryHandleShellMessage(
            service.ShellHookMessage,
            nint.Zero,
            nint.Zero));
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void DiscordUpdates_KeepOneDockApplicationKey()
    {
        var oldKey = WindowsDockRuntimeService.GetApplicationKey(
            @"C:\Users\User\AppData\Local\Discord\app-1.0.9249\Discord.exe");
        var currentKey = WindowsDockRuntimeService.GetApplicationKey(
            @"C:\Users\User\AppData\Local\Discord\app-1.0.9250\Discord.exe");

        Assert.Equal(oldKey, currentKey, ignoreCase: true);
    }

    [Fact]
    public void DiscordUpdate_ResolvesNewestSquirrelExecutable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceDiscord-{Guid.NewGuid():N}");
        var currentDirectory = Path.Combine(
            root,
            "app-1.0.9250");
        var currentExecutable = Path.Combine(
            currentDirectory,
            "Discord.exe");
        var staleExecutable = Path.Combine(
            root,
            "app-1.0.9249",
            "Discord.exe");
        try
        {
            Directory.CreateDirectory(currentDirectory);
            File.WriteAllBytes(currentExecutable, [0x4D, 0x5A]);
            Directory.CreateDirectory(
                Path.GetDirectoryName(staleExecutable)!);
            File.WriteAllBytes(staleExecutable, [0x4D, 0x5A]);

            var resolved =
                ExecutablePathIdentity.ResolveCurrentExecutablePath(
                    staleExecutable);

            Assert.Equal(
                currentExecutable,
                resolved,
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Discord_UsesSquirrelLauncherInsteadOfHiddenWindow()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"AnotherSurfaceDiscordLaunch-{Guid.NewGuid():N}");
        var appDirectory = Path.Combine(root, "app-1.0.9250");
        var executable = Path.Combine(appDirectory, "Discord.exe");
        var updater = Path.Combine(root, "Update.exe");
        try
        {
            Directory.CreateDirectory(appDirectory);
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            File.WriteAllBytes(updater, [0x4D, 0x5A]);

            var found =
                ExecutablePathIdentity.TryGetSquirrelLaunchCommand(
                    executable,
                    out var launcherPath,
                    out var arguments,
                    out var workingDirectory);

            Assert.True(found);
            Assert.Equal(updater, launcherPath, ignoreCase: true);
            Assert.Equal("--processStart Discord.exe", arguments);
            Assert.Equal(
                appDirectory,
                workingDirectory,
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowTitle_IsLimitedToConfiguredCharacterCount()
    {
        const string title =
            "AnotherSurface-0.8.23-fixed-usage-and-window-close - 파일 탐색기";

        var shortened =
            WindowsDockRuntimeService.TruncateWindowTitle(
                title,
                24);

        Assert.Equal(24, shortened.Length);
        Assert.EndsWith("…", shortened);
        Assert.Equal(
            "짧은 제목",
            WindowsDockRuntimeService.TruncateWindowTitle(
                "짧은 제목",
                24));
    }

    [Fact]
    public void ForegroundRefresh_UpdatesOnlyActivationState()
    {
        var application = new DockApplicationSnapshot(
            "sample",
            "Sample",
            @"C:\Apps\Sample.exe",
            [
                new DockWindowInfo(new nint(100), "First"),
                new DockWindowInfo(new nint(200), "Second")
            ],
            IsForeground: false,
            HasAttention: true);

        var foreground =
            WindowsDockRuntimeService.ApplyForegroundHandle(
                application,
                new nint(200));
        var unrelated =
            WindowsDockRuntimeService.ApplyForegroundHandle(
                application,
                new nint(300));

        Assert.True(foreground.IsForeground);
        Assert.False(unrelated.IsForeground);
        Assert.Equal(application.Windows, foreground.Windows);
        Assert.True(foreground.HasAttention);
    }
}
