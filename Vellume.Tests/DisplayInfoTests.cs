using Vellume.Core;

namespace Vellume.Tests;

public sealed class DisplayInfoTests
{
    [Fact]
    public void WallpaperLabels_ShowStoppedWhenNoWallpaperIsAssigned()
    {
        var display = CreateDisplay();

        Assert.Equal("중지됨", display.WallpaperStatusLabel);
        Assert.Equal("일시정지", display.PauseButtonLabel);
    }

    [Fact]
    public void WallpaperLabels_ShowPlayingWhenWallpaperIsAssigned()
    {
        var display = CreateDisplay();
        display.HasAssignedWallpaper = true;

        Assert.Equal("재생 중", display.WallpaperStatusLabel);
        Assert.Equal("일시정지", display.PauseButtonLabel);
    }

    [Fact]
    public void WallpaperLabels_OfferResumeWhenDisplayIsPaused()
    {
        var display = CreateDisplay();
        display.HasAssignedWallpaper = true;
        display.IsWallpaperPaused = true;

        Assert.Equal("일시정지됨", display.WallpaperStatusLabel);
        Assert.Equal("재생", display.PauseButtonLabel);
    }

    private static DisplayInfo CreateDisplay() => new()
    {
        Id = "DISPLAY-1",
        Name = "기본 모니터",
        Width = 1920,
        Height = 1080,
        IsPrimary = true
    };
}
