using Vellume.Core;

namespace Vellume.Tests;

public sealed class WallpaperValidatorTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"VellumeTests-{Guid.NewGuid():N}");

    public WallpaperValidatorTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Theory]
    [InlineData("wallpaper.JPG", WallpaperKind.Image)]
    [InlineData("wallpaper.jpeg", WallpaperKind.Image)]
    [InlineData("wallpaper.png", WallpaperKind.Image)]
    [InlineData("wallpaper.bmp", WallpaperKind.Image)]
    [InlineData("wallpaper.gif", WallpaperKind.Gif)]
    [InlineData("wallpaper.mp4", WallpaperKind.Video)]
    [InlineData("wallpaper.WEBM", WallpaperKind.Video)]
    public void DetectKind_RecognizesSupportedFiles(
        string fileName,
        WallpaperKind expectedKind)
    {
        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(path, string.Empty);

        Assert.Equal(expectedKind, WallpaperValidator.DetectKind(path));
    }

    [Fact]
    public void DetectKind_RejectsFolderContainingIndexHtml()
    {
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "index.html"),
            "<!doctype html>");

        Assert.Throws<NotSupportedException>(() => WallpaperValidator.DetectKind(_temporaryDirectory));
    }

    [Fact]
    public void DetectKind_RejectsUnsupportedFile()
    {
        var path = Path.Combine(_temporaryDirectory, "wallpaper.exe");
        File.WriteAllText(path, string.Empty);

        Assert.Throws<NotSupportedException>(
            () => WallpaperValidator.DetectKind(path));
    }

    [Theory]
    [InlineData("wallpaper.html")]
    [InlineData("wallpaper.htm")]
    public void DetectKind_RejectsHtmlFiles(string fileName)
    {
        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(path, "<!doctype html>");
        Assert.Throws<NotSupportedException>(() => WallpaperValidator.DetectKind(path));
    }

    [Fact]
    public void CreateItem_OnlyCreatesMetadata()
    {
        var path = Path.Combine(
            _temporaryDirectory,
            "original-wallpaper.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var filesBefore = Directory.GetFiles(
            _temporaryDirectory,
            "*",
            SearchOption.AllDirectories);

        var item = WallpaperValidator.CreateItem(path);

        Assert.Equal(Path.GetFullPath(path), item.SourcePath);
        Assert.Equal(WallpaperKind.Image, item.Kind);
        Assert.Equal(
            filesBefore,
            Directory.GetFiles(
                _temporaryDirectory,
                "*",
                SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
