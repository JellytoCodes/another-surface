using Vellume.Core;

namespace Vellume.Tests;

public sealed class VideoProxyCacheTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"VellumeVideoProxyTests-{Guid.NewGuid():N}");

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
            ".vellume-cache");
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
            "VELLUME_VIDEO_PROXY_TEST_PATH");
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

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
