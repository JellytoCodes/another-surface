using AnotherSurface.Core;

namespace AnotherSurface.Windows;

/// <summary>
/// Separates wallpaper playback from the controller so GPU-backed renderers
/// can live in a disposable child process while the tray and settings remain
/// available.
/// </summary>
internal interface IWallpaperRenderer
{
    nint Handle { get; }
    string BackendDisplayName { get; }
    bool IsReady { get; }

    string GetAttachmentDiagnostics();

    Task AttachToAsync(
        nint hostHandle,
        DisplayInfo display,
        bool raisedDesktop,
        nint shellViewHandle,
        nint backgroundWorkerHandle,
        CancellationToken cancellationToken);

    Task InitializeAndNavigateAsync(
        WallpaperItem wallpaper,
        PlaybackOptions options,
        CancellationToken cancellationToken);

    Task CapturePreviewAsync(
        string outputPath,
        CancellationToken cancellationToken);

    bool IsAttachedTo(nint hostHandle);
    Task SetWindowVisibleAsync(
        bool visible,
        CancellationToken cancellationToken);
    Task PauseAsync();
    Task ResumeAsync();
    Task UpdatePlaybackAsync(PlaybackOptions options);
    Task CloseSafelyAsync();
}
