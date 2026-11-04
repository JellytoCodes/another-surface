using System.Windows.Threading;
using AnotherSurface.Core;

namespace AnotherSurface.Windows;

/// <summary>All displays share one STA render thread inside the application.</summary>
internal sealed class InProcessWallpaperRenderer : IWallpaperRenderer
{
    private static readonly Lazy<Dispatcher> RenderDispatcher = new(CreateDispatcher);
    private readonly Dispatcher _dispatcher = RenderDispatcher.Value;
    private readonly CancellationTokenSource _lifetime = new();
    private WallpaperRendererWindow? _renderer;
    private volatile RenderState _state = new(nint.Zero, "준비 중", "in-process, waiting-for-source", false);
    private int _closed;

    public nint Handle => _state.Handle;
    public string BackendDisplayName => _state.Backend;
    public bool IsReady => _state.Ready && NativeMethods.IsWindow(Handle);
    public string GetAttachmentDiagnostics() => _state.Diagnostics;
    public bool IsAttachedTo(nint hostHandle) => NativeMethods.IsWindow(Handle) && NativeMethods.GetParent(Handle) == hostHandle;

    public Task AttachToAsync(nint hostHandle, DisplayInfo display, bool raisedDesktop,
        nint shellViewHandle, nint backgroundWorkerHandle, CancellationToken cancellationToken)
        => RunAsync(renderer => renderer.AttachToAsync(hostHandle, display, raisedDesktop,
            shellViewHandle, backgroundWorkerHandle, cancellationToken), cancellationToken);

    public Task InitializeAndNavigateAsync(WallpaperItem wallpaper, PlaybackOptions options, CancellationToken cancellationToken)
        => RunAsync(async renderer =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await renderer.InitializeAndNavigateAsync(wallpaper.Copy(), options.Copy(), linked.Token);
        }, cancellationToken);

    public Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken)
        => RunAsync(renderer => renderer.CapturePreviewAsync(outputPath, cancellationToken), cancellationToken);
    public Task SetWindowVisibleAsync(bool visible, CancellationToken cancellationToken)
        => RunAsync(renderer => renderer.SetWindowVisibleAsync(visible, cancellationToken), cancellationToken);
    public Task PauseAsync() => RunAsync(renderer => renderer.PauseAsync());
    public Task ResumeAsync() => RunAsync(renderer => renderer.ResumeAsync());
    public Task UpdatePlaybackAsync(PlaybackOptions options) => RunAsync(renderer => renderer.UpdatePlaybackAsync(options.Copy()));

    private Task RunAsync(Func<IWallpaperRenderer, Task> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return _dispatcher.InvokeAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var renderer = _renderer ??= new WallpaperRendererWindow();
            try { await action(renderer); }
            finally
            {
                _state = new RenderState(renderer.Handle, renderer.BackendDisplayName,
                    $"process={Environment.ProcessId}, inProcess=true, {renderer.GetAttachmentDiagnostics()}", renderer.IsReady);
            }
        }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public async Task CloseSafelyAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _lifetime.Cancel();
        await _dispatcher.InvokeAsync(async () =>
        {
            if (_renderer is not null) await ((IWallpaperRenderer)_renderer).CloseSafelyAsync();
            _renderer = null;
            _state = new RenderState(nint.Zero, "중지됨", "in-process, closed", false);
        }).Task.Unwrap().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static Dispatcher CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "AnotherSurface.Render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    private sealed record RenderState(nint Handle, string Backend, string Diagnostics, bool Ready);
}

internal static class WallpaperRendererFactory
{
    internal static IWallpaperRenderer Create(string _) => new InProcessWallpaperRenderer();
}
