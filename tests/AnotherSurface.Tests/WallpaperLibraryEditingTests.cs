using AnotherSurface.Core;

namespace AnotherSurface.Tests;

public sealed class WallpaperLibraryEditingTests
{
    [Fact]
    public async Task Rename_PersistsLibraryAndDisplayNamesWithoutChangingMediaOrPlayback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AnotherSurfaceRename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "original.mp4");
            byte[] contents = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(source, contents);
            var item = new WallpaperItem
            {
                Name = "원래 이름", Kind = WallpaperKind.Video,
                SourcePath = source, OriginalSourcePath = @"C:\Originals\original.mp4",
                IsManaged = true, VideoStartSeconds = 1, VideoEndSeconds = 3
            };
            var other = new WallpaperItem { Name = "다른 배경" };
            var settings = new AppSettings
            {
                Library = [item, other], ActiveWallpaper = item.Copy(),
                SelectedMonitorId = "DISPLAY1",
                DisplayWallpapers =
                [
                    new() { DisplayId = "DISPLAY1", Wallpaper = item.Copy(),
                        Playback = new() { Fit = WallpaperFit.Fit, IsMuted = false, Volume = .7 } },
                    new() { DisplayId = "DISPLAY2", Wallpaper = item.Copy(),
                        Playback = new() { Fit = WallpaperFit.Stretch, IsMuted = true, Volume = .2 } },
                    new() { DisplayId = "DISPLAY3", Wallpaper = other.Copy() }
                ]
            };

            // A display name can contain characters that cannot be used in a file name.
            Assert.True(settings.RenameWallpaper(item.Id, "  봄 밤 / 4K: 배경  "));
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(settings);
            var restored = await store.LoadAsync();

            var renamed = restored.Library.Single(candidate => candidate.Id == item.Id);
            Assert.Equal("봄 밤 / 4K: 배경", renamed.Name);
            Assert.Equal(renamed.Name, restored.ActiveWallpaper!.Name);
            Assert.Equal(source, renamed.SourcePath);
            Assert.Equal(item.OriginalSourcePath, renamed.OriginalSourcePath);
            Assert.True(renamed.IsManaged);
            Assert.Equal(1, renamed.VideoStartSeconds);
            Assert.Equal(3, renamed.VideoEndSeconds);
            foreach (var profile in restored.DisplayWallpapers.Take(2))
            {
                Assert.Equal(renamed.Name, profile.Wallpaper!.Name);
                Assert.Equal(item.Id, profile.Wallpaper.Id);
                Assert.Equal(source, profile.Wallpaper.SourcePath);
            }
            Assert.Equal("다른 배경", restored.Library.Single(candidate => candidate.Id == other.Id).Name);
            Assert.Equal("다른 배경", restored.FindDisplayProfile("DISPLAY3")!.Wallpaper!.Name);
            var primary = restored.FindDisplayProfile("DISPLAY1")!.Playback;
            Assert.Equal(WallpaperFit.Fit, primary.Fit);
            Assert.False(primary.IsMuted);
            Assert.Equal(.7, primary.Volume);
            var secondary = restored.FindDisplayProfile("DISPLAY2")!.Playback;
            Assert.Equal(WallpaperFit.Stretch, secondary.Fit);
            Assert.True(secondary.IsMuted);
            Assert.Equal(.2, secondary.Volume);
            Assert.Equal(contents, await File.ReadAllBytesAsync(source));
            Assert.Single(Directory.GetFiles(directory, "*.mp4"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Rename_RejectsBlankNamesWithoutChangingTheItem(string? name)
    {
        var item = new WallpaperItem { Name = "원래 이름" };
        var settings = new AppSettings { Library = [item], ActiveWallpaper = item.Copy() };
        Assert.ThrowsAny<ArgumentException>(() => settings.RenameWallpaper(item.Id, name!));
        Assert.Equal("원래 이름", item.Name);
        Assert.Equal("원래 이름", settings.ActiveWallpaper.Name);
    }

    [Fact]
    public void Rename_LeavesAssignmentsAloneWhenTheItemIsNotInTheLibrary()
    {
        var item = new WallpaperItem { Name = "원래 이름" };
        var settings = new AppSettings
        {
            ActiveWallpaper = item,
            DisplayWallpapers = [new() { DisplayId = "DISPLAY1", Wallpaper = item.Copy() }]
        };
        Assert.False(settings.RenameWallpaper(item.Id, "새 이름"));
        Assert.Equal("원래 이름", item.Name);
        Assert.Equal("원래 이름", settings.DisplayWallpapers[0].Wallpaper!.Name);
    }
}
