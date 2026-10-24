using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class LibraryMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"VellumeLibraryMedia-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(0, 11)]
    [InlineData(0, 0.05)]
    [InlineData(double.NaN, 2)]
    [InlineData(0, double.PositiveInfinity)]
    public void Range_RejectsInvalidBoundaries(double start, double end)
    {
        var item = new WallpaperItem { Kind = WallpaperKind.Video, VideoStartSeconds = start, VideoEndSeconds = end };
        Assert.Throws<ArgumentException>(() => item.ValidateVideoRange(10));
    }

    [Fact]
    public void Range_CopyAndJsonPreserveSelectionAndLegacyDefaults()
    {
        var item = new WallpaperItem { Kind = WallpaperKind.Video, VideoStartSeconds = 1.5, VideoEndSeconds = 3 };
        item.ValidateVideoRange(4);
        var restored = JsonSerializer.Deserialize<WallpaperItem>(JsonSerializer.Serialize(item.Copy()))!;
        Assert.Equal(1.5, restored.VideoStartSeconds);
        Assert.Equal(3, restored.VideoEndSeconds);
        Assert.True(restored.HasVideoRange);
        Assert.False(JsonSerializer.Deserialize<WallpaperItem>("{\"Kind\":2}")!.HasVideoRange);
        restored.VideoStartSeconds = null;
        restored.VideoEndSeconds = null;
        restored.ValidateVideoRange(4);
        Assert.Equal("전체 반복", restored.VideoRangeLabel);
    }

    [Theory]
    [InlineData(WallpaperKind.Image, "이미지")]
    [InlineData(WallpaperKind.Video, "영상")]
    [InlineData(WallpaperKind.Gif, "GIF")]
    [InlineData(WallpaperKind.Web, "웹")]
    public void Library_UsesDistinctKoreanMediaLabels(WallpaperKind kind, string label)
        => Assert.Equal(label, new WallpaperItem { Kind = kind }.KindLabel);

    [Theory]
    [InlineData(1600, 900, WallpaperKind.Image)]
    [InlineData(100, 1800, WallpaperKind.Image)]
    [InlineData(300, 200, WallpaperKind.Gif)]
    public async Task Thumbnail_IsBoundedFrozenCachedAndDoesNotLockSource(int width, int height, WallpaperKind kind)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, kind == WallpaperKind.Gif ? "source.gif" : "source.png");
        using (var bitmap = new System.Drawing.Bitmap(width, height))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.CornflowerBlue);
            bitmap.Save(source, kind == WallpaperKind.Gif ? System.Drawing.Imaging.ImageFormat.Gif : System.Drawing.Imaging.ImageFormat.Png);
        }
        var item = new WallpaperItem { Kind = kind, SourcePath = source };
        var directory = Path.Combine(_root, "Thumbnails");
        var service = new WallpaperThumbnailService(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await service.LoadAsync(item, timeout.Token);
        Assert.NotNull(first);
        Assert.True(first.IsFrozen);
        Assert.InRange(first.PixelWidth, 1, 320);
        Assert.InRange(first.PixelHeight, 1, 180);
        var cache = Assert.Single(Directory.GetFiles(directory));
        var timestamp = File.GetLastWriteTimeUtc(cache);
        var second = await service.LoadAsync(item, timeout.Token);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(cache));
        Assert.Equal(first.PixelWidth, second!.PixelWidth);
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await service.RemoveAsync(item.Id);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "LocalIntegration")]
    public async Task Segment_UsesRequestedFramesAndDuration_ReusesCache_AndFullPlaybackRestoresOriginal(bool optimize)
    {
        var source = Environment.GetEnvironmentVariable("VELLUME_MEDIA_TEST_VIDEO");
        if (string.IsNullOrEmpty(source)) return;
        Assert.True(File.Exists(source));
        Directory.CreateDirectory(_root);
        var store = new WallpaperLibraryStore(Path.Combine(_root, "Library"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var item = await store.ImportAsync(source, cancellationToken: timeout.Token);
        var originalBytes = await File.ReadAllBytesAsync(item.SourcePath);
        item.VideoStartSeconds = 2;
        item.VideoEndSeconds = 3;
        var prepared = await store.PrepareForPlaybackAsync(item, 80, 46, timeout.Token, optimize);
        Assert.True(prepared.GeneratedProxy);
        Assert.True(prepared.UsedProxy);
        Assert.False(prepared.Wallpaper.HasVideoRange);
        var media = new VideoProxyCache();
        var duration = await media.GetDurationAsync(prepared.Wallpaper.SourcePath, timeout.Token);
        Assert.InRange(duration, 0.9, 1.15);
        var preview = new WallpaperThumbnailService(Path.Combine(_root, "Thumbs"));
        var frame = await preview.LoadAsync(prepared.Wallpaper, timeout.Token);
        Assert.NotNull(frame);
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        var center = ((bgra.PixelHeight / 2) * bgra.PixelWidth + bgra.PixelWidth / 2) * 4;
        // Fixture: red for 0..2 seconds, then blue for 2..4 seconds.
        Assert.True(pixels[center] > 200 && pixels[center + 2] < 30, "Clip must begin in the selected blue interval, not at the original red start.");
        var again = await store.PrepareForPlaybackAsync(item, 80, 46, timeout.Token, optimize);
        Assert.False(again.GeneratedProxy);
        Assert.Equal(prepared.Wallpaper.SourcePath, again.Wallpaper.SourcePath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(item.SourcePath));
        item.VideoStartSeconds = null;
        item.VideoEndSeconds = null;
        var full = await store.PrepareForPlaybackAsync(item, 80, 46, timeout.Token, optimize: false);
        Assert.Equal(item.SourcePath, full.Wallpaper.SourcePath);
        var protectedCache = await store.CleanupVideoCacheAsync([prepared.Wallpaper.SourcePath], maximumBytes: 0);
        Assert.Equal(0, protectedCache.RemovedFiles);
        var cleanup = await store.CleanupVideoCacheAsync([], maximumBytes: 0);
        Assert.Equal(1, cleanup.RemovedFiles);
        Assert.True(File.Exists(item.SourcePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
