using AnotherSurface.Core;
using AnotherSurface.Windows;

namespace AnotherSurface.Tests;

public sealed class WallpaperRendererBackendPolicyTests
{
    [Fact]
    public void DefaultMp4_UsesNativeVideo()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.mp4");

        Assert.True(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                new PlaybackOptions()));
    }

    [Fact]
    public void WebM_UsesNativeVideoFirst()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.webm");

        Assert.True(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                new PlaybackOptions()));
    }

    [Fact]
    public void Gif_UsesNativeGifFirst()
    {
        var wallpaper = new WallpaperItem
        {
            Name = "Animated",
            SourcePath = @"C:\Wallpapers\sample.gif",
            Kind = WallpaperKind.Gif
        };

        Assert.True(
            WallpaperRendererBackendPolicy.ShouldUseNativeGif(
                wallpaper));
    }

    [Theory]
    [InlineData(0, 33)]
    [InlineData(1, 33)]
    [InlineData(10, 100)]
    [InlineData(2000, 10000)]
    public void GifFrameDelay_IsBounded(
        int hundredths,
        int expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            NativeGifPlayer.NormalizeFrameDelay(hundredths)
                .TotalMilliseconds);
    }

    [Fact]
    public void VideoFilter_UsesManagedVideoFallback()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.mp4");
        var options = new PlaybackOptions
        {
            Saturation = 0.7
        };

        Assert.False(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                options));
    }

    [Fact]
    public void Stretch_UsesNativeVideo()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.mp4");
        var options = new PlaybackOptions
        {
            Fit = WallpaperFit.Stretch
        };

        Assert.True(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                options));
    }

    [Fact]
    public void DimmedMp4_UsesNativeVideo()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.mp4");
        var options = new PlaybackOptions
        {
            Brightness = 0.596
        };

        Assert.True(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                options));
    }

    [Fact]
    public void BrightenedMp4_UsesManagedVideoFallback()
    {
        var wallpaper = Video(@"C:\Wallpapers\sample.mp4");
        var options = new PlaybackOptions
        {
            Brightness = 1.2
        };

        Assert.False(
            WallpaperRendererBackendPolicy.ShouldUseNativeVideo(
                wallpaper,
                options));
    }

    private static WallpaperItem Video(string sourcePath)
        => new()
        {
            Name = "Test",
            SourcePath = sourcePath,
            Kind = WallpaperKind.Video
        };
}
