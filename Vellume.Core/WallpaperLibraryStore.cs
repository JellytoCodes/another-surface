namespace Vellume.Core;

/// <summary>
/// Owns wallpaper files imported into Vellume's LocalAppData library.
/// Each item is isolated in an ID-named directory so removal never touches
/// an arbitrary user-selected source path.
/// </summary>
public sealed class WallpaperLibraryStore
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly string _libraryDirectory;
    private readonly VideoProxyCache _videoProxyCache;

    public WallpaperLibraryStore(
        string libraryDirectory,
        VideoProxyCache? videoProxyCache = null)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            throw new ArgumentException(
                "The library directory cannot be empty.",
                nameof(libraryDirectory));
        }

        _libraryDirectory = Path.GetFullPath(libraryDirectory);
        _videoProxyCache = videoProxyCache ?? new VideoProxyCache();
        Directory.CreateDirectory(_libraryDirectory);
    }

    public async Task<VideoPlaybackPreparation> PrepareForPlaybackAsync(
        WallpaperItem wallpaper,
        int maximumWidth,
        int maximumHeight,
        CancellationToken cancellationToken = default,
        bool optimize = true)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        if (wallpaper.Kind != WallpaperKind.Video ||
            !wallpaper.IsManaged ||
            !OwnsItemPath(wallpaper))
        {
            if (wallpaper.HasVideoRange)
                throw new InvalidOperationException("구간 재생은 Vellume 라이브러리에 복사된 영상에서 사용할 수 있습니다.");
            return new VideoPlaybackPreparation(
                    wallpaper.Copy(),
                    UsedProxy: false,
                    GeneratedProxy: false,
                    "원본 월페이퍼를 사용합니다.");
        }

        if (wallpaper.HasVideoRange)
            return await _videoProxyCache.PrepareSegmentAsync(wallpaper,
                optimize ? maximumWidth : 0, optimize ? maximumHeight : 0, cancellationToken).ConfigureAwait(false);
        return optimize ? await _videoProxyCache.PrepareAsync(
            wallpaper,
            maximumWidth,
            maximumHeight,
            cancellationToken).ConfigureAwait(false)
            : new VideoPlaybackPreparation(wallpaper.Copy(), false, false, "원본 월페이퍼를 사용합니다.");
    }

    public async Task<WallpaperItem> ImportAsync(
        string sourcePath,
        Guid? itemId = null,
        string? itemName = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = Path.GetFullPath(sourcePath);
        var metadata = WallpaperValidator.CreateItem(normalizedSource);
        if (IsWithinDirectory(normalizedSource, _libraryDirectory))
        {
            throw new InvalidOperationException(
                "A wallpaper already inside the managed library cannot be imported again.");
        }

        var id = itemId.GetValueOrDefault(Guid.NewGuid());
        if (id == Guid.Empty)
        {
            id = Guid.NewGuid();
        }

        var finalDirectory = GetItemDirectory(id);
        if (Directory.Exists(finalDirectory))
        {
            throw new IOException(
                $"The managed library item '{id:N}' already exists.");
        }

        var stagingDirectory = Path.Combine(
            _libraryDirectory,
            $".import-{id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            string relativeEntryPath;
            if (Directory.Exists(normalizedSource))
            {
                relativeEntryPath = "content";
                await CopyDirectoryAsync(
                    normalizedSource,
                    Path.Combine(stagingDirectory, relativeEntryPath),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                relativeEntryPath = Path.GetFileName(normalizedSource);
                await CopyFileAsync(
                    normalizedSource,
                    Path.Combine(stagingDirectory, relativeEntryPath),
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, finalDirectory);
            var managedSourcePath = Path.Combine(
                finalDirectory,
                relativeEntryPath);

            return new WallpaperItem
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(itemName)
                    ? metadata.Name
                    : itemName.Trim(),
                SourcePath = managedSourcePath,
                OriginalSourcePath = normalizedSource,
                Kind = metadata.Kind,
                IsManaged = true
            };
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public Task<VideoCacheCleanupResult> CleanupVideoCacheAsync(
        IReadOnlyCollection<string> protectedPaths,
        CancellationToken cancellationToken = default,
        long maximumBytes = VideoProxyCache.DefaultMaximumCacheBytes,
        TimeSpan? maximumUnusedAge = null)
    {
        ArgumentNullException.ThrowIfNull(protectedPaths);
        var snapshot = protectedPaths.ToArray();
        return Task.Run(() => VideoProxyCache.Cleanup(
            _libraryDirectory, snapshot, maximumBytes,
            maximumUnusedAge ?? VideoProxyCache.DefaultUnusedCacheAge,
            cancellationToken), cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        WallpaperItem wallpaper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        cancellationToken.ThrowIfCancellationRequested();
        if (!wallpaper.IsManaged)
        {
            return false;
        }

        var itemDirectory = GetItemDirectory(wallpaper.Id);
        if (!IsWithinDirectory(wallpaper.SourcePath, itemDirectory))
        {
            throw new InvalidOperationException(
                "The managed wallpaper path is outside its library item directory.");
        }

        if (!Directory.Exists(itemDirectory))
        {
            return false;
        }

        await Task.Run(
            () => Directory.Delete(itemDirectory, recursive: true),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public bool OwnsItemPath(WallpaperItem wallpaper)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        return wallpaper.Id != Guid.Empty &&
               !string.IsNullOrWhiteSpace(wallpaper.SourcePath) &&
               IsWithinDirectory(
                   wallpaper.SourcePath,
                   GetItemDirectory(wallpaper.Id));
    }

    public Task<int> CleanupOrphansAsync(
        IEnumerable<Guid> retainedItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedItemIds);
        var retained = retainedItemIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        return Task.Run(() =>
        {
            var removed = 0;
            foreach (var stagingDirectory in Directory.EnumerateDirectories(
                         _libraryDirectory,
                         ".import-*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(stagingDirectory, recursive: true);
                removed++;
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         _libraryDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(
                        Path.GetFileName(directory),
                        "N",
                        out var itemId) ||
                    retained.Contains(itemId))
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }

            return removed;
        }, cancellationToken);
    }

    private string GetItemDirectory(Guid id)
        => Path.Combine(_libraryDirectory, id.ToString("N"));

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceDirectory, destinationDirectory));

        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(current.Source);
            Directory.CreateDirectory(current.Destination);

            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(file);
                await CopyFileAsync(
                    file,
                    Path.Combine(current.Destination, Path.GetFileName(file)),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                pending.Push((
                    directory,
                    Path.Combine(
                        current.Destination,
                        Path.GetFileName(directory))));
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(
            destination,
            CopyBufferSize,
            cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException(
                $"Linked files or directories cannot be imported: {path}");
        }
    }

    private static bool IsWithinDirectory(
        string candidatePath,
        string directoryPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var directory = Path.GetFullPath(directoryPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A failed import must preserve its original exception. A stale
            // staging directory is safe and can be removed on a later run.
        }
    }
}
