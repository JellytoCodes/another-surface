using Vellume.Core;

namespace Vellume.Tests;

public sealed class WallpaperLibraryStoreTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"VellumeLibraryTests-{Guid.NewGuid():N}");
    private readonly string _sourceDirectory;
    private readonly string _libraryDirectory;

    public WallpaperLibraryStoreTests()
    {
        _sourceDirectory = Path.Combine(_temporaryDirectory, "Source");
        _libraryDirectory = Path.Combine(_temporaryDirectory, "Library");
        Directory.CreateDirectory(_sourceDirectory);
    }

    [Fact]
    public async Task ImportAsync_CopiesFileIntoManagedItemDirectory()
    {
        var sourcePath = Path.Combine(_sourceDirectory, "scene.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);
        var store = new WallpaperLibraryStore(_libraryDirectory);

        var item = await store.ImportAsync(sourcePath);

        Assert.True(item.IsManaged);
        Assert.Equal(WallpaperKind.Video, item.Kind);
        Assert.Equal(Path.GetFullPath(sourcePath), item.OriginalSourcePath);
        Assert.True(store.OwnsItemPath(item));
        Assert.True(File.Exists(item.SourcePath));
        Assert.Equal(
            await File.ReadAllBytesAsync(sourcePath),
            await File.ReadAllBytesAsync(item.SourcePath));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task ImportAsync_RejectsHtmlFolderWithoutCopyingOrDeletingSource()
    {
        var webDirectory = Path.Combine(_sourceDirectory, "Dashboard");
        var assetsDirectory = Path.Combine(webDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(webDirectory, "index.html"),
            "<img src=\"assets/picture.png\">");
        await File.WriteAllBytesAsync(
            Path.Combine(assetsDirectory, "picture.png"),
            [9, 8, 7]);
        var store = new WallpaperLibraryStore(_libraryDirectory);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.ImportAsync(webDirectory));
        Assert.True(File.Exists(Path.Combine(webDirectory, "index.html")));
        Assert.False(Directory.Exists(_libraryDirectory) && Directory.GetDirectories(_libraryDirectory).Length > 0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesManagedCopyButPreservesOriginal()
    {
        var sourcePath = Path.Combine(_sourceDirectory, "still.png");
        await File.WriteAllBytesAsync(sourcePath, [4, 3, 2, 1]);
        var store = new WallpaperLibraryStore(_libraryDirectory);
        var item = await store.ImportAsync(sourcePath);
        var managedDirectory = Directory.GetParent(item.SourcePath)!.FullName;

        var removed = await store.DeleteAsync(item);

        Assert.True(removed);
        Assert.False(Directory.Exists(managedDirectory));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task DeleteAsync_DoesNotDeleteUnmanagedSource()
    {
        var sourcePath = Path.Combine(_sourceDirectory, "external.jpg");
        await File.WriteAllBytesAsync(sourcePath, [1]);
        var store = new WallpaperLibraryStore(_libraryDirectory);
        var item = WallpaperValidator.CreateItem(sourcePath);

        var removed = await store.DeleteAsync(item);

        Assert.False(removed);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task CleanupOrphans_OnlyRemovesUnreferencedItemDirectories()
    {
        var firstSource = Path.Combine(_sourceDirectory, "first.bmp");
        var secondSource = Path.Combine(_sourceDirectory, "second.bmp");
        await File.WriteAllBytesAsync(firstSource, [1]);
        await File.WriteAllBytesAsync(secondSource, [2]);
        var store = new WallpaperLibraryStore(_libraryDirectory);
        var retained = await store.ImportAsync(firstSource);
        var orphaned = await store.ImportAsync(secondSource);
        var unrelated = Path.Combine(_libraryDirectory, "notes");
        Directory.CreateDirectory(unrelated);

        var removed = await store.CleanupOrphansAsync([retained.Id]);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(retained.SourcePath));
        Assert.False(File.Exists(orphaned.SourcePath));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task SettingsRoundTrip_PreservesManagedWallpaperMetadata()
    {
        var sourcePath = Path.Combine(_sourceDirectory, "saved.webm");
        await File.WriteAllBytesAsync(sourcePath, [5, 4, 3]);
        var libraryStore = new WallpaperLibraryStore(_libraryDirectory);
        var imported = await libraryStore.ImportAsync(sourcePath);
        var settingsPath = Path.Combine(_temporaryDirectory, "settings.json");
        var settingsStore = new JsonSettingsStore(settingsPath);

        await settingsStore.SaveAsync(new AppSettings
        {
            Library = [imported],
            ActiveWallpaper = imported.Copy()
        });
        var restored = await settingsStore.LoadAsync();

        var restoredItem = Assert.Single(restored.Library);
        Assert.True(restoredItem.IsManaged);
        Assert.Equal(imported.Id, restoredItem.Id);
        Assert.Equal(imported.SourcePath, restoredItem.SourcePath);
        Assert.Equal(sourcePath, restoredItem.OriginalSourcePath);
        Assert.True(restored.ActiveWallpaper!.IsManaged);
        Assert.Equal(imported.SourcePath, restored.ActiveWallpaper.SourcePath);
    }

    [Fact]
    public async Task CleanupOrphansAsync_RemovesInterruptedImportDirectory()
    {
        var stagingDirectory = Path.Combine(
            _libraryDirectory,
            $".import-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(stagingDirectory, "partial.tmp"),
            "partial");
        var store = new WallpaperLibraryStore(_libraryDirectory);

        var removed = await store.CleanupOrphansAsync([]);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stagingDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
