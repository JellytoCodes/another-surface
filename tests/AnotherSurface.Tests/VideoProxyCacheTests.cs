using AnotherSurface.Core;

namespace AnotherSurface.Tests;

public sealed class VideoProxyCacheTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"AnotherSurfaceVideoProxyTests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("av1", 3840, 2160, 30, true)]
    [InlineData("vp9", 1920, 1080, 30, true)]
    [InlineData("h264", 1920, 1080, 60, true)]
    [InlineData("h264", 1920, 1080, 30, false)]
    [InlineData("hevc", 1280, 720, 24, false)]
    public void Metadata_IdentifiesExpensivePlaybackSources(
        string codec,
        int width,
        int height,
        double framesPerSecond,
        bool expected)
    {
        var metadata = new VideoStreamMetadata(
            codec,
            width,
            height,
            framesPerSecond);

        Assert.Equal(
            expected,
            metadata.NeedsOptimization(1920, 1080));
    }

    [Fact]
    public async Task PrepareAsync_ReusesFreshDisplaySizedProxy()
    {
        var itemDirectory = Path.Combine(
            _temporaryDirectory,
            Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(
            itemDirectory,
            ".another-surface-cache");
        Directory.CreateDirectory(cacheDirectory);
        var sourcePath = Path.Combine(itemDirectory, "original.mp4");
        var proxyPath = Path.Combine(
            cacheDirectory,
            "video-1920x1080-30fps-h264-v2.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        await File.WriteAllBytesAsync(proxyPath, [9, 8, 7]);
        File.SetLastWriteTimeUtc(
            sourcePath,
            DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(proxyPath, DateTime.UtcNow);
        var wallpaper = new WallpaperItem
        {
            Id = Guid.NewGuid(),
            Name = "Video",
            SourcePath = sourcePath,
            OriginalSourcePath = sourcePath,
            Kind = WallpaperKind.Video,
            IsManaged = true
        };
        var cache = new VideoProxyCache();

        var preparation = await cache.PrepareAsync(
            wallpaper,
            1921,
            1081);

        Assert.True(preparation.UsedProxy);
        Assert.False(preparation.GeneratedProxy);
        Assert.Equal(proxyPath, preparation.Wallpaper.SourcePath);
        Assert.Equal(sourcePath, wallpaper.SourcePath);
    }

    [Fact]
    public async Task LibraryStore_DoesNotCacheUnmanagedVideo()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var sourcePath = Path.Combine(_temporaryDirectory, "external.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1]);
        var store = new WallpaperLibraryStore(
            Path.Combine(_temporaryDirectory, "Library"));
        var wallpaper = new WallpaperItem
        {
            Name = "External",
            SourcePath = sourcePath,
            Kind = WallpaperKind.Video,
            IsManaged = false
        };

        var preparation = await store.PrepareForPlaybackAsync(
            wallpaper,
            1920,
            1080);

        Assert.False(preparation.UsedProxy);
        Assert.Equal(sourcePath, preparation.Wallpaper.SourcePath);
    }

    [Fact]
    public async Task PrepareAsync_TranscodesConfiguredRealVideo()
    {
        var sourcePath = Environment.GetEnvironmentVariable(
            "ANOTHER_SURFACE_VIDEO_PROXY_TEST_PATH");
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath))
        {
            return;
        }

        var wallpaper = new WallpaperItem
        {
            Id = Guid.NewGuid(),
            Name = "Configured integration video",
            SourcePath = Path.GetFullPath(sourcePath),
            OriginalSourcePath = Path.GetFullPath(sourcePath),
            Kind = WallpaperKind.Video,
            IsManaged = true
        };
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(3));
        var cache = new VideoProxyCache();

        var preparation = await cache.PrepareAsync(
            wallpaper,
            1920,
            1080,
            timeout.Token);

        Assert.True(cache.IsAvailable);
        Assert.True(preparation.UsedProxy, preparation.Detail);
        Assert.True(File.Exists(preparation.Wallpaper.SourcePath));
        Assert.NotEqual(
            wallpaper.SourcePath,
            preparation.Wallpaper.SourcePath);
    }

    [Fact]
    public async Task Cleanup_ProtectsAllActiveDisplaysAndNeverDeletesOriginalsOrUnknownFiles()
    {
        var store = new WallpaperLibraryStore(_temporaryDirectory);
        var first = CreateProxy(Guid.NewGuid(), "video-1920x1080-30fps-h264-v2.mp4", 10, 60);
        var second = CreateProxy(Guid.NewGuid(), "video-3440x1440-30fps-h264-v2.mp4", 10, 60);
        var stale = CreateProxy(Guid.NewGuid(), "video-2400x1350-30fps-h264-v2.mp4", 10, 60);
        var original = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(stale))!, "original.mp4");
        var partial = Path.Combine(Path.GetDirectoryName(stale)!, ".video-test.partial.mp4");
        var unknown = Path.Combine(Path.GetDirectoryName(stale)!, "user-video.mp4");
        var unrelated = Path.Combine(_temporaryDirectory, "not-a-library-item", ".another-surface-cache", "video-320x180-30fps-h264-v2.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        foreach (var path in new[] { original, partial, unknown, unrelated })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var result = await store.CleanupVideoCacheAsync(
            [first.ToUpperInvariant(), second], maximumBytes: 0);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(10, result.RemovedBytes);
        Assert.Equal(20, result.RetainedBytes); // The budget is soft for active renderers.
        Assert.False(File.Exists(stale));
        foreach (var path in new[] { first, second, original, partial, unknown, unrelated })
        {
            Assert.True(File.Exists(path), path);
        }
    }

    [Fact]
    public async Task Cleanup_EvictsLeastRecentlyUsedVariantsToMeetBudget()
    {
        var itemId = Guid.NewGuid();
        var reused = CreateProxy(itemId, "video-1920x1080-30fps-h264-v2.mp4", 10, 3);
        var unused = CreateProxy(itemId, "video-2752x1152-30fps-h264-v2.mp4", 10, 2);
        var sourcePath = Path.Combine(_temporaryDirectory, itemId.ToString("N"), "original.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1]);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddDays(-10));
        var store = new WallpaperLibraryStore(_temporaryDirectory);
        var preparation = await store.PrepareForPlaybackAsync(new WallpaperItem
        {
            Id = itemId, SourcePath = sourcePath, IsManaged = true, Kind = WallpaperKind.Video
        }, 1920, 1080);
        Assert.True(preparation.UsedProxy);

        var result = await store.CleanupVideoCacheAsync([], maximumBytes: 10);

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(10, result.RetainedBytes);
        Assert.True(File.Exists(reused));
        Assert.False(File.Exists(unused));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredCacheEvenBelowBudget()
    {
        var expired = CreateProxy(Guid.NewGuid(), "video-1920x1080-30fps-h264-v2.mp4", 10, 40);
        var recent = CreateProxy(Guid.NewGuid(), "video-1920x1080-30fps-h264-v2.mp4", 10, 1);
        var store = new WallpaperLibraryStore(_temporaryDirectory);

        var result = await store.CleanupVideoCacheAsync([]);

        Assert.Equal(1, result.RemovedFiles);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Cleanup_LeavesFilesHeldByRendererAlone()
    {
        var held = CreateProxy(Guid.NewGuid(), "video-1920x1080-30fps-h264-v2.mp4", 10, 40);
        var store = new WallpaperLibraryStore(_temporaryDirectory);
        using var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await store.CleanupVideoCacheAsync([], maximumBytes: 0);

        Assert.Equal(0, result.RemovedFiles);
        Assert.Equal(10, result.RetainedBytes);
        Assert.True(File.Exists(held));
    }

    [Fact]
    public async Task Cleanup_CancellationLeavesCacheUntouched()
    {
        var path = CreateProxy(Guid.NewGuid(), "video-1920x1080-30fps-h264-v2.mp4", 10, 40);
        var store = new WallpaperLibraryStore(_temporaryDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CleanupVideoCacheAsync([], cancellation.Token, maximumBytes: 0));
        Assert.True(File.Exists(path));
    }

    private string CreateProxy(Guid itemId, string name, int bytes, int daysSinceUse)
    {
        var directory = Path.Combine(_temporaryDirectory, itemId.ToString("N"), ".another-surface-cache");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysSinceUse));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-daysSinceUse));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
