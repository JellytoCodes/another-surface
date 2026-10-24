using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vellume.Core;

namespace Vellume.Windows;

public sealed class WindowsIconService
{
    private const int MaximumCachedIcons = 256;
    private const long CacheValidationIntervalMilliseconds = 30_000;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedIcon> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(
        string targetPath,
        string customIconPath = "",
        SystemShortcutKind? systemKind = null,
        bool dynamicState = false)
    {
        try
        {
            var isCustomBitmap = false;
            var hasExplicitCustomIcon =
                !string.IsNullOrWhiteSpace(customIconPath) &&
                File.Exists(customIconPath);
            if (hasExplicitCustomIcon)
            {
                var extension = Path.GetExtension(customIconPath);
                if (!extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = customIconPath;
                    isCustomBitmap = true;
                }
                else
                {
                    targetPath = customIconPath;
                }
            }

            if (!hasExplicitCustomIcon &&
                TryGetBundledSystemIconPath(
                    systemKind,
                    dynamicState) is { } bundledIconPath)
            {
                targetPath = bundledIconPath;
                isCustomBitmap = true;
            }

            var stockLocation = hasExplicitCustomIcon || isCustomBitmap
                ? null
                : systemKind switch
            {
                SystemShortcutKind.ThisPc =>
                    TryGetStockLocation(NativeMethods.StockIconDesktopPc),
                SystemShortcutKind.RecycleBin when dynamicState =>
                    TryGetStockLocation(NativeMethods.StockIconRecycleBinFull),
                SystemShortcutKind.RecycleBin =>
                    TryGetStockLocation(NativeMethods.StockIconRecycleBin),
                _ => null
            };
            var iconPath = stockLocation?.Path ?? targetPath;
            var iconIndex = stockLocation?.Index ?? 0;
            var normalizedIconPath = Path.GetFullPath(iconPath);
            var cacheKey =
                $"{normalizedIconPath}|{iconIndex}|{systemKind}|" +
                $"{dynamicState}|{isCustomBitmap}";
            var now = Environment.TickCount64;
            CachedIcon? cachedEntry;
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(cacheKey, out cachedEntry) &&
                    now - cachedEntry.LastValidatedTick <
                    CacheValidationIntervalMilliseconds)
                {
                    return cachedEntry.Icon;
                }
            }

            var stamp = GetFileStamp(normalizedIconPath);
            if (cachedEntry is not null &&
                string.Equals(
                    cachedEntry.FileStamp,
                    stamp,
                    StringComparison.Ordinal))
            {
                lock (_cacheGate)
                {
                    cachedEntry.LastValidatedTick = now;
                }
                return cachedEntry.Icon;
            }

            var icon = isCustomBitmap
                ? LoadBitmap(normalizedIconPath)
                : ExtractLargeIcon(normalizedIconPath, iconIndex);
            lock (_cacheGate)
            {
                if (_cache.Count >= MaximumCachedIcons)
                {
                    _cache.Clear();
                }

                _cache[cacheKey] = new CachedIcon(
                    icon,
                    stamp,
                    now);
            }

            return icon;
        }
        catch
        {
            return null;
        }
    }

    private sealed class CachedIcon(
        ImageSource? icon,
        string fileStamp,
        long lastValidatedTick)
    {
        internal ImageSource? Icon { get; } = icon;
        internal string FileStamp { get; } = fileStamp;
        internal long LastValidatedTick { get; set; } =
            lastValidatedTick;
    }

    private static ImageSource? LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 256;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageSource? ExtractLargeIcon(
        string path,
        int iconIndex)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var handles = new nint[1];
        var identifiers = new uint[1];
        if (NativeMethods.PrivateExtractIcons(
                path,
                iconIndex,
                256,
                256,
                handles,
                identifiers,
                1,
                0) == 0 ||
            handles[0] == nint.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                handles[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(256, 256));
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(handles[0]);
        }
    }

    private static string GetFileStamp(string path)
    {
        if (!File.Exists(path))
        {
            return "missing";
        }

        var file = new FileInfo(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }

    private static (string Path, int Index)? TryGetStockLocation(
        uint stockIconId)
    {
        var info = new NativeMethods.StockIconInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StockIconInfo>()
        };
        if (NativeMethods.SHGetStockIconInfo(
                stockIconId,
                NativeMethods.StockIconLocation,
                ref info) < 0 ||
            string.IsNullOrWhiteSpace(info.Path))
        {
            return null;
        }

        return (info.Path, info.IconIndex);
    }

    private static string? TryGetBundledSystemIconPath(
        SystemShortcutKind? systemKind,
        bool dynamicState)
    {
        var fileName = systemKind switch
        {
            SystemShortcutKind.ThisPc => "this-pc-companion.png",
            SystemShortcutKind.FileExplorer => "file-explorer-companion.png",
            SystemShortcutKind.RecycleBin when dynamicState =>
                "recycle-bin-full-companion.png",
            SystemShortcutKind.RecycleBin => "recycle-bin-empty-companion.png",
            SystemShortcutKind.PowerShell => "powershell-companion.png",
            _ => null
        };
        if (fileName is null)
        {
            return null;
        }

        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "SystemShortcuts",
            fileName);
        return File.Exists(path) ? path : null;
    }
}
