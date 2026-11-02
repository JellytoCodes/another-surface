using System.Drawing;
using System.Drawing.Imaging;
using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WallpaperThumbnailServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"AnotherSurfaceThumbnailTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ClearMemoryAsync_ReleasesDecodedPreviewAndKeepsSourceAndDiskCache()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var service = CreateService();
        var first = await service.LoadAsync(item, CancellationToken.None);
        var files = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);
        var hashes = files.Select(path => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        await service.ClearMemoryAsync();
        var second = await service.LoadAsync(item, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(files, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
        for (var i = 0; i < files.Length; i++)
            Assert.Equal(hashes[i], System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(files[i])));
    }

    [Fact]
    public async Task LoadAsync_ReusesFrozenPreviewForUnchangedSource()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var service = CreateService();

        var first = await service.LoadAsync(item, CancellationToken.None);
        var second = await service.LoadAsync(item.Copy(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(first.IsFrozen);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task LoadAsync_RebuildsPreviewWhenItemSourceChangesToOlderFile()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var replacement = CreateImage("replacement.png", Color.Red);
        File.SetLastWriteTimeUtc(replacement.SourcePath, DateTime.UtcNow.AddDays(-1));
        var service = CreateService();
        var first = await service.LoadAsync(item, CancellationToken.None);

        item.SourcePath = replacement.SourcePath;
        var second = await service.LoadAsync(item, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.NotNull(second);
        var pixel = new byte[4];
        second.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[2]);
        Assert.Equal(0, pixel[0]);
    }

    [Fact]
    public async Task LoadAsync_DoesNotReturnMemoryPreviewWhenSourceWasDeleted()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var service = CreateService();
        await service.LoadAsync(item, CancellationToken.None);
        File.Delete(item.SourcePath);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.LoadAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_InvalidatesMemoryAndDiskPreview()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var service = CreateService();
        var first = await service.LoadAsync(item, CancellationToken.None);

        await service.RemoveAsync(item.Id);

        Assert.False(File.Exists(Path.Combine(_directory, "Previews", $"{item.Id:N}.png")));
        var second = await service.LoadAsync(item, CancellationToken.None);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task LoadAsync_EvictsLeastRecentlyUsedPreview()
    {
        var item = CreateImage("original.png", Color.CornflowerBlue);
        var service = CreateService();
        var first = await service.LoadAsync(item, CancellationToken.None);
        var recent = item.Copy();
        recent.Id = Guid.NewGuid();
        var recentPreview = await service.LoadAsync(recent, CancellationToken.None);
        for (var index = 0; index < 63; index++)
        {
            var additional = item.Copy();
            additional.Id = Guid.NewGuid();
            await service.LoadAsync(additional, CancellationToken.None);
        }

        Assert.Same(recentPreview, await service.LoadAsync(recent, CancellationToken.None));
        Assert.NotSame(first, await service.LoadAsync(item, CancellationToken.None));
    }

    private WallpaperThumbnailService CreateService() => new(Path.Combine(_directory, "Previews"));

    private WallpaperItem CreateImage(string name, Color color)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using (var source = new Bitmap(32, 18))
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(color);
            source.Save(path, ImageFormat.Png);
        }
        return new WallpaperItem
        {
            Id = Guid.NewGuid(), Name = name, SourcePath = path, Kind = WallpaperKind.Image
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
