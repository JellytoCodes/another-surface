namespace Vellume.Core;

public interface IMonitorService
{
    IReadOnlyList<DisplayInfo> GetDisplays();
}

public interface IWallpaperController : IAsyncDisposable
{
    WallpaperPlaybackState State { get; }
    event EventHandler<WallpaperStateChangedEventArgs>? StateChanged;
    event EventHandler<int>? QuickSlotInvoked;
    event EventHandler<int>? QuickSlotSecondaryInvoked;
    event EventHandler<QuickSlotLayoutChangedEventArgs>? QuickSlotLayoutChanged;

    Task ApplyAsync(
        WallpaperItem wallpaper,
        DisplayInfo display,
        PlaybackOptions options,
        WidgetSettings widgets,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
    Task StopDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default);
    Task PauseDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default);
    Task ResumeDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task UpdatePlaybackAsync(
        PlaybackOptions options,
        CancellationToken cancellationToken = default);
    Task UpdateDisplayPlaybackAsync(
        string displayId,
        PlaybackOptions options,
        CancellationToken cancellationToken = default);
    Task UpdateWidgetsAsync(
        WidgetSettings widgets,
        DisplayInfo? display = null,
        CancellationToken cancellationToken = default);
    Task UpdateQuickSlotRuntimeAsync(
        WidgetSettings widgets,
        CancellationToken cancellationToken = default);
    Task<string> InstallCodexCompanionBridgeAsync(
        CancellationToken cancellationToken = default);
    Task CompleteDesktopWallpaperHandoffAsync(
        CancellationToken cancellationToken = default);
}

public interface ISystemShortcutService
{
    bool RecycleBinHasItems();
    Task LaunchAsync(
        SystemShortcutKind kind,
        CancellationToken cancellationToken = default);
    Task EmptyRecycleBinAsync(
        nint ownerWindow,
        CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    Task InfoAsync(string message);
    Task ErrorAsync(string message, Exception? exception = null);
}
