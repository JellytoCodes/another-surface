using System.IO;
using System.Windows.Media.Imaging;
using Vellume.Core;

namespace Vellume.Windows;

/// <summary>Small, static previews; decoding and video extraction never run on the UI thread.</summary>
public sealed class WallpaperThumbnailService
{
    // Reuse decoded previews across card reloads without retaining the entire library.
    private const int MaximumMemoryPreviews = 64;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VideoProxyCache _video = new();
    private readonly Dictionary<Guid, CachedPreview> _previews = [];
    private readonly LinkedList<Guid> _recentPreviews = [];

    public WallpaperThumbnailService(string directory) => _directory = Path.GetFullPath(directory);

    public async Task<BitmapSource?> LoadAsync(WallpaperItem item, CancellationToken cancellationToken)
    {
        if (item.Kind == WallpaperKind.Web) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = new FileInfo(item.SourcePath);
                if (!source.Exists) throw new FileNotFoundException("원본 파일이 없습니다.", item.SourcePath);
                var sourceChanged = false;
                if (_previews.TryGetValue(item.Id, out var previous))
                {
                    if (previous.Kind == item.Kind &&
                        StringComparer.OrdinalIgnoreCase.Equals(previous.SourcePath, source.FullName) &&
                        previous.SourceLength == source.Length &&
                        previous.SourceLastWriteUtc == source.LastWriteTimeUtc)
                    {
                        _recentPreviews.Remove(previous.RecentNode);
                        _recentPreviews.AddFirst(previous.RecentNode);
                        return previous.Bitmap;
                    }
                    sourceChanged = true;
                    ForgetPreview(item.Id);
                }
                Directory.CreateDirectory(_directory);
                var cached = Path.Combine(_directory, $"{item.Id:N}.png");
                if (sourceChanged || !File.Exists(cached) || File.GetLastWriteTimeUtc(cached) < source.LastWriteTimeUtc)
                {
                    var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.png");
                    try
                    {
                        if (item.Kind == WallpaperKind.Video)
                            await _video.CreateVideoThumbnailAsync(source.FullName, temporary, cancellationToken).ConfigureAwait(false);
                        else
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(Decode(source.FullName)));
                            using var stream = File.Create(temporary);
                            encoder.Save(stream);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporary, cached, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                var bitmap = Decode(cached);
                cancellationToken.ThrowIfCancellationRequested();
                var node = _recentPreviews.AddFirst(item.Id);
                _previews.Add(item.Id, new CachedPreview(
                    source.FullName, source.Length, source.LastWriteTimeUtc, item.Kind, bitmap, node));
                if (_previews.Count > MaximumMemoryPreviews)
                    ForgetPreview(_recentPreviews.Last!.Value);
                return bitmap;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearMemoryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _previews.Clear(); _recentPreviews.Clear(); }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid itemId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ForgetPreview(itemId);
            await Task.Run(() =>
            {
                var path = Path.Combine(_directory, $"{itemId:N}.png");
                if (File.Exists(path)) File.Delete(path);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // All cache accesses share the same gate as disk generation and removal.
    private void ForgetPreview(Guid itemId)
    {
        if (_previews.Remove(itemId, out var preview))
            _recentPreviews.Remove(preview.RecentNode);
    }

    private sealed record CachedPreview(
        string SourcePath,
        long SourceLength,
        DateTime SourceLastWriteUtc,
        WallpaperKind Kind,
        BitmapSource Bitmap,
        LinkedListNode<Guid> RecentNode);

    private static BitmapSource Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        var constrainWidth = (long)frame.PixelWidth * 180 >= (long)frame.PixelHeight * 320;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (constrainWidth) bitmap.DecodePixelWidth = 320;
        else bitmap.DecodePixelHeight = 180;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
