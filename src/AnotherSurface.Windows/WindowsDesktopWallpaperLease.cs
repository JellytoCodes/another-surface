using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AnotherSurface.Windows;

internal readonly record struct DesktopWallpaperMonitor(
    string Id,
    Rectangle Bounds);

internal interface IDesktopWallpaperPlatform : IDisposable
{
    IReadOnlyList<DesktopWallpaperMonitor> GetMonitors();

    string GetWallpaper(string monitorId);

    void SetWallpaper(string monitorId, string wallpaperPath);
}

/// <summary>
/// Temporarily hands a static safety frame to Windows itself. While this
/// lease is active Another Surface can hide every full-screen layered HWND, leaving
/// Unreal Editor with no Another Surface GPU or DWM composition surface to overlap.
/// </summary>
internal sealed class WindowsDesktopWallpaperLease : IDisposable
{
    private const string MarkerFileName = "active-wallpaper-lease.json";
    private readonly IDesktopWallpaperPlatform _platform;
    private readonly string _safetyDirectory;
    private readonly string _markerPath;
    private readonly WallpaperLeaseMarker _marker;
    private bool _restored;

    private WindowsDesktopWallpaperLease(
        IDesktopWallpaperPlatform platform,
        string safetyDirectory,
        WallpaperLeaseMarker marker)
    {
        _platform = platform;
        _safetyDirectory = safetyDirectory;
        _markerPath = Path.Combine(
            safetyDirectory,
            MarkerFileName);
        _marker = marker;
    }

    internal static WindowsDesktopWallpaperLease? TryActivate(
        string safetyDirectory,
        Rectangle targetBounds,
        nint bitmapHandle,
        IDesktopWallpaperPlatform? platform = null,
        bool replaceExistingLease = false)
    {
        if (bitmapHandle == nint.Zero)
        {
            return null;
        }

        var ownsPlatform = platform is null;
        string? framePath = null;
        var markerCreated = false;
        WallpaperLeaseMarker? existingMarker = null;
        try
        {
            Directory.CreateDirectory(safetyDirectory);
            var markerPath = Path.Combine(
                safetyDirectory,
                MarkerFileName);
            if (File.Exists(markerPath))
            {
                if (!replaceExistingLease)
                {
                    // A previous interrupted handoff owns this recovery
                    // record. Normal callers must not replace its original
                    // wallpaper path.
                    return null;
                }

                existingMarker =
                    JsonSerializer.Deserialize<WallpaperLeaseMarker>(
                        File.ReadAllText(markerPath));
                if (existingMarker is null ||
                    string.IsNullOrWhiteSpace(
                        existingMarker.MonitorId) ||
                    string.IsNullOrWhiteSpace(
                        existingMarker.OriginalWallpaperPath))
                {
                    return null;
                }
            }

            platform ??= new WindowsDesktopWallpaperPlatform();
            var monitor = FindTargetMonitor(
                platform.GetMonitors(),
                targetBounds);
            if (string.IsNullOrWhiteSpace(monitor.Id))
            {
                throw new InvalidOperationException(
                    "The target Windows monitor could not be resolved.");
            }
            if (existingMarker is not null &&
                !existingMarker.MonitorId.Equals(
                    monitor.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The existing safety wallpaper belongs to another monitor.");
            }

            framePath = Path.Combine(
                safetyDirectory,
                $"unreal-safety-{Guid.NewGuid():N}.bmp");
            SaveBitmap(bitmapHandle, framePath);
            var marker = new WallpaperLeaseMarker(
                monitor.Id,
                existingMarker?.OriginalWallpaperPath ??
                platform.GetWallpaper(monitor.Id),
                framePath);
            if (existingMarker is null)
            {
                WriteMarkerAtomically(
                    safetyDirectory,
                    markerPath,
                    marker,
                    overwrite: false);
                markerCreated = true;
                platform.SetWallpaper(
                    monitor.Id,
                    framePath);
            }
            else
            {
                // Update the safety frame while the exclusive application
                // covers the desktop. Preserve the first session's original
                // wallpaper so normal Another Surface shutdown can still restore it.
                platform.SetWallpaper(
                    monitor.Id,
                    framePath);
                WriteMarkerAtomically(
                    safetyDirectory,
                    markerPath,
                    marker,
                    overwrite: true);
                markerCreated = true;
                DeleteSafetyFile(
                    safetyDirectory,
                    existingMarker.FramePath);
            }
            return new WindowsDesktopWallpaperLease(
                platform,
                safetyDirectory,
                marker);
        }
        catch
        {
            if (existingMarker is not null &&
                platform is not null &&
                File.Exists(existingMarker.FramePath))
            {
                try
                {
                    platform.SetWallpaper(
                        existingMarker.MonitorId,
                        existingMarker.FramePath);
                }
                catch
                {
                    // The existing recovery marker remains authoritative.
                }
            }
            DeleteSafetyFile(
                safetyDirectory,
                framePath);
            if (markerCreated && existingMarker is null)
            {
                DeleteMarkerIfOwned(
                    safetyDirectory,
                    Path.Combine(
                        safetyDirectory,
                        MarkerFileName),
                    framePath);
            }
            if (ownsPlatform)
            {
                platform?.Dispose();
            }
            return null;
        }
    }

    internal static bool TryRecover(
        string safetyDirectory,
        IDesktopWallpaperPlatform? platform = null)
    {
        var markerPath = Path.Combine(
            safetyDirectory,
            MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        var ownsPlatform = platform is null;
        try
        {
            var marker = JsonSerializer.Deserialize<WallpaperLeaseMarker>(
                File.ReadAllText(markerPath));
            if (marker is null ||
                string.IsNullOrWhiteSpace(marker.MonitorId))
            {
                return false;
            }

            platform ??= new WindowsDesktopWallpaperPlatform();
            platform.SetWallpaper(
                marker.MonitorId,
                marker.OriginalWallpaperPath);
            DeleteSafetyFile(
                safetyDirectory,
                marker.FramePath);
            DeleteSafetyFile(
                safetyDirectory,
                markerPath);
            return true;
        }
        catch
        {
            // Keep the marker so the next clean startup can retry recovery.
            return false;
        }
        finally
        {
            if (ownsPlatform)
            {
                platform?.Dispose();
            }
        }
    }

    internal static bool HasPendingRecovery(
        string safetyDirectory)
        => File.Exists(
            Path.Combine(
                safetyDirectory,
                MarkerFileName));

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        try
        {
            _platform.SetWallpaper(
                _marker.MonitorId,
                _marker.OriginalWallpaperPath);
            DeleteSafetyFile(
                _safetyDirectory,
                _marker.FramePath);
            DeleteSafetyFile(
                _safetyDirectory,
                _markerPath);
        }
        catch
        {
            // The marker remains so the next Another Surface startup can retry.
        }
        finally
        {
            _restored = true;
            _platform.Dispose();
        }
    }

    /// <summary>
    /// Releases only the COM client while intentionally leaving the temporary
    /// Windows wallpaper and recovery marker in place. This lets the WPF
    /// process disappear completely during an exclusive GPU application.
    /// A later Another Surface startup restores the original wallpaper from the marker.
    /// </summary>
    internal void DetachForProcessExit()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        _platform.Dispose();
    }

    private static DesktopWallpaperMonitor FindTargetMonitor(
        IReadOnlyList<DesktopWallpaperMonitor> monitors,
        Rectangle targetBounds)
    {
        var exact = monitors.FirstOrDefault(
            monitor => monitor.Bounds == targetBounds);
        if (!string.IsNullOrWhiteSpace(exact.Id))
        {
            return exact;
        }

        var candidates = monitors
            .Where(monitor =>
                monitor.Bounds.Width > 0 &&
                monitor.Bounds.Height > 0)
            .Select(monitor => new
            {
                Monitor = monitor,
                Intersection = IntersectionArea(
                    monitor.Bounds,
                    targetBounds),
                ContainsCenter = ContainsCenter(
                    monitor.Bounds,
                    targetBounds)
            })
            .OrderByDescending(candidate => candidate.Intersection)
            .ThenByDescending(candidate => candidate.ContainsCenter)
            .ToArray();
        var best = candidates.FirstOrDefault();
        if (best is null ||
            (best.Intersection <= 0 && !best.ContainsCenter))
        {
            return default;
        }

        return best.Monitor;
    }

    private static long IntersectionArea(
        Rectangle first,
        Rectangle second)
    {
        var intersection = Rectangle.Intersect(
            first,
            second);
        return Math.Max(0L, intersection.Width) *
               Math.Max(0L, intersection.Height);
    }

    private static bool ContainsCenter(
        Rectangle monitorBounds,
        Rectangle targetBounds)
    {
        var centerX =
            (long)targetBounds.Left + targetBounds.Width / 2L;
        var centerY =
            (long)targetBounds.Top + targetBounds.Height / 2L;
        return centerX >= monitorBounds.Left &&
               centerX < monitorBounds.Right &&
               centerY >= monitorBounds.Top &&
               centerY < monitorBounds.Bottom;
    }

    private static void SaveBitmap(
        nint bitmapHandle,
        string outputPath)
    {
        using var source = Image.FromHbitmap(bitmapHandle);
        using var bitmap = new Bitmap(source);
        bitmap.Save(
            outputPath,
            ImageFormat.Bmp);
    }

    private static void WriteMarkerAtomically(
        string safetyDirectory,
        string markerPath,
        WallpaperLeaseMarker marker,
        bool overwrite)
    {
        var temporaryPath = Path.Combine(
            safetyDirectory,
            $".{MarkerFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(marker));
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            // The rename is atomic on the same volume. Initial activation
            // never overwrites another recovery record; an explicitly
            // requested in-session refresh replaces only the same lease.
            File.Move(
                temporaryPath,
                markerPath,
                overwrite);
        }
        finally
        {
            DeleteSafetyFile(
                safetyDirectory,
                temporaryPath);
        }
    }

    private static void DeleteMarkerIfOwned(
        string safetyDirectory,
        string markerPath,
        string? framePath)
    {
        if (string.IsNullOrWhiteSpace(framePath))
        {
            return;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<WallpaperLeaseMarker>(
                File.ReadAllText(markerPath));
            if (marker is null ||
                !Path.GetFullPath(marker.FramePath).Equals(
                    Path.GetFullPath(framePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DeleteSafetyFile(
                safetyDirectory,
                markerPath);
        }
        catch
        {
            // Preserve an unverified marker. Recovery data is more valuable
            // than eager cleanup when another process may own the file.
        }
    }

    private static void DeleteSafetyFile(
        string safetyDirectory,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = Path.GetFullPath(safetyDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            if (candidate.StartsWith(
                    directory,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch
        {
            // Cleanup is best-effort; recovery never touches outside this
            // LocalAppData-owned safety directory.
        }
    }

    private sealed record WallpaperLeaseMarker(
        string MonitorId,
        string OriginalWallpaperPath,
        string FramePath);
}

internal sealed class WindowsDesktopWallpaperPlatform :
    IDesktopWallpaperPlatform
{
    private static readonly Guid DesktopWallpaperClassId =
        new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
    private object? _comObject;
    private IDesktopWallpaperNative? _native;

    internal WindowsDesktopWallpaperPlatform()
    {
        var type = Type.GetTypeFromCLSID(
            DesktopWallpaperClassId,
            throwOnError: true) ??
            throw new InvalidOperationException(
                "Windows DesktopWallpaper COM class is unavailable.");
        _comObject = Activator.CreateInstance(type) ??
            throw new InvalidOperationException(
                "Windows DesktopWallpaper COM class could not start.");
        _native = (IDesktopWallpaperNative)_comObject;
    }

    public IReadOnlyList<DesktopWallpaperMonitor> GetMonitors()
    {
        var native = GetNative();
        native.GetMonitorDevicePathCount(out var count);
        var result = new List<DesktopWallpaperMonitor>(
            checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            native.GetMonitorDevicePathAt(
                index,
                out var monitorId);
            native.GetMonitorRect(
                monitorId,
                out var rectangle);
            result.Add(
                new DesktopWallpaperMonitor(
                    monitorId,
                    Rectangle.FromLTRB(
                        rectangle.Left,
                        rectangle.Top,
                        rectangle.Right,
                        rectangle.Bottom)));
        }

        return result;
    }

    public string GetWallpaper(string monitorId)
    {
        GetNative().GetWallpaper(
            monitorId,
            out var wallpaperPath);
        return wallpaperPath ?? string.Empty;
    }

    public void SetWallpaper(
        string monitorId,
        string wallpaperPath)
        => GetNative().SetWallpaper(
            monitorId,
            wallpaperPath);

    public void Dispose()
    {
        _native = null;
        if (_comObject is not null &&
            Marshal.IsComObject(_comObject))
        {
            Marshal.FinalReleaseComObject(_comObject);
        }
        _comObject = null;
    }

    private IDesktopWallpaperNative GetNative()
        => _native ??
           throw new ObjectDisposedException(
               nameof(WindowsDesktopWallpaperPlatform));

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaperNative
    {
        void SetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)]
            string monitorId,
            [MarshalAs(UnmanagedType.LPWStr)]
            string wallpaperPath);

        void GetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)]
            string monitorId,
            [MarshalAs(UnmanagedType.LPWStr)]
            out string wallpaperPath);

        void GetMonitorDevicePathAt(
            uint monitorIndex,
            [MarshalAs(UnmanagedType.LPWStr)]
            out string monitorId);

        void GetMonitorDevicePathCount(
            out uint count);

        void GetMonitorRect(
            [MarshalAs(UnmanagedType.LPWStr)]
            string monitorId,
            out NativeRectangle rectangle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
