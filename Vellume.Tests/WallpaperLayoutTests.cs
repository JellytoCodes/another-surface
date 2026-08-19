using System.Drawing;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WallpaperLayoutTests
{
    [Fact]
    public void Fill_CropsWideVideoAroundCenter()
    {
        var result = WallpaperLayout.Calculate(
            1920,
            1080,
            1024,
            1024,
            WallpaperFit.Fill);

        Assert.Equal(
            new Rectangle(-398, 0, 1820, 1024),
            result);
    }

    [Fact]
    public void Fit_LetterboxesWideVideoAroundCenter()
    {
        var result = WallpaperLayout.Calculate(
            1920,
            1080,
            1024,
            1024,
            WallpaperFit.Fit);

        Assert.Equal(
            new Rectangle(0, 224, 1024, 576),
            result);
    }

    [Fact]
    public void Stretch_UsesEntireTarget()
    {
        var result = WallpaperLayout.Calculate(
            1920,
            1080,
            1024,
            1024,
            WallpaperFit.Stretch);

        Assert.Equal(
            new Rectangle(0, 0, 1024, 1024),
            result);
    }
}
