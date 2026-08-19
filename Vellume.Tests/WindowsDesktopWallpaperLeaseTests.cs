using System.Drawing;
using System.Text.Json;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WindowsDesktopWallpaperLeaseTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void WindowsPlatform_ReadsAttachedWallpaperState()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "VELLUME_DESKTOP_WALLPAPER_READ_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var platform =
            new WindowsDesktopWallpaperPlatform();
        var monitors = platform.GetMonitors();
        Assert.NotEmpty(monitors);
        foreach (var monitor in monitors.Where(
                     item =>
                         item.Bounds.Width > 0 &&
                         item.Bounds.Height > 0))
        {
            Assert.NotNull(
                platform.GetWallpaper(monitor.Id));
        }
    }

    [Fact]
    public void Lease_UsesMatchingMonitorAndRestoresOriginalWallpaper()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperLease-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var bitmap = new Bitmap(64, 36))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.MediumTurquoise);
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "left",
                        new Rectangle(-1920, 0, 1920, 1080)),
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 1920, 1080))
                ],
                new Dictionary<string, string>
                {
                    ["left"] = @"C:\Wallpapers\left.jpg",
                    ["primary"] = @"C:\Wallpapers\original.jpg"
                });

            using (var lease =
                   WindowsDesktopWallpaperLease.TryActivate(
                       safetyDirectory,
                       new Rectangle(0, 0, 1920, 1080),
                       bitmapHandle,
                       platform))
            {
                Assert.NotNull(lease);
                Assert.Equal(
                    "primary",
                    platform.SetCalls.Single().MonitorId);
                Assert.True(
                    File.Exists(
                        platform.SetCalls.Single().WallpaperPath));
                Assert.True(
                    File.Exists(
                        Path.Combine(
                            safetyDirectory,
                            "active-wallpaper-lease.json")));
                Assert.Empty(
                    Directory.EnumerateFiles(
                        safetyDirectory,
                        "*.tmp",
                        SearchOption.TopDirectoryOnly));
            }

            Assert.Equal(2, platform.SetCalls.Count);
            Assert.Equal(
                @"C:\Wallpapers\original.jpg",
                platform.SetCalls[1].WallpaperPath);
            Assert.False(
                File.Exists(
                    Path.Combine(
                        safetyDirectory,
                        "active-wallpaper-lease.json")));
            Assert.True(platform.IsDisposed);
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Lease_DoesNotReplacePreExistingRecoveryMarker()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperExisting-{Guid.NewGuid():N}");
        var markerPath = Path.Combine(
            safetyDirectory,
            "active-wallpaper-lease.json");
        const string existingMarker = "previous-session-recovery";
        nint bitmapHandle = nint.Zero;
        try
        {
            Directory.CreateDirectory(safetyDirectory);
            File.WriteAllText(markerPath, existingMarker);
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 1920, 1080))
                ],
                new Dictionary<string, string>
                {
                    ["primary"] = @"C:\Wallpapers\original.jpg"
                });

            var lease = WindowsDesktopWallpaperLease.TryActivate(
                safetyDirectory,
                new Rectangle(0, 0, 1920, 1080),
                bitmapHandle,
                platform);

            Assert.Null(lease);
            Assert.Equal(
                existingMarker,
                File.ReadAllText(markerPath));
            Assert.Empty(platform.SetCalls);
            Assert.False(platform.IsDisposed);
            Assert.Empty(
                Directory.EnumerateFiles(
                    safetyDirectory,
                    "*.bmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Lease_InSessionRefreshPreservesOriginalWallpaper()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperRefresh-{Guid.NewGuid():N}");
        nint firstBitmapHandle = nint.Zero;
        nint secondBitmapHandle = nint.Zero;
        try
        {
            using (var firstBitmap = new Bitmap(32, 18))
            {
                firstBitmapHandle = firstBitmap.GetHbitmap();
            }
            using (var secondBitmap = new Bitmap(32, 18))
            {
                secondBitmapHandle = secondBitmap.GetHbitmap();
            }

            var firstPlatform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 1920, 1080))
                ],
                new Dictionary<string, string>
                {
                    ["primary"] = @"C:\Wallpapers\original.jpg"
                });
            var firstLease = WindowsDesktopWallpaperLease.TryActivate(
                safetyDirectory,
                new Rectangle(0, 0, 1920, 1080),
                firstBitmapHandle,
                firstPlatform);
            Assert.NotNull(firstLease);
            var firstFramePath =
                firstPlatform.SetCalls.Single().WallpaperPath;
            firstLease.DetachForProcessExit();

            var refreshPlatform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 1920, 1080))
                ],
                new Dictionary<string, string>
                {
                    ["primary"] = firstFramePath
                });
            var refreshedLease =
                WindowsDesktopWallpaperLease.TryActivate(
                    safetyDirectory,
                    new Rectangle(0, 0, 1920, 1080),
                    secondBitmapHandle,
                    refreshPlatform,
                    replaceExistingLease: true);

            Assert.NotNull(refreshedLease);
            var refreshedFramePath =
                refreshPlatform.SetCalls.Single().WallpaperPath;
            Assert.NotEqual(firstFramePath, refreshedFramePath);
            Assert.False(File.Exists(firstFramePath));
            Assert.True(File.Exists(refreshedFramePath));
            using (var marker = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(
                           safetyDirectory,
                           "active-wallpaper-lease.json"))))
            {
                Assert.Equal(
                    @"C:\Wallpapers\original.jpg",
                    marker.RootElement
                        .GetProperty("OriginalWallpaperPath")
                        .GetString());
            }
            refreshedLease.DetachForProcessExit();

            var recoveryPlatform =
                new FakeDesktopWallpaperPlatform(
                    [
                        new DesktopWallpaperMonitor(
                            "primary",
                            new Rectangle(0, 0, 1920, 1080))
                    ],
                    new Dictionary<string, string>());
            Assert.True(
                WindowsDesktopWallpaperLease.TryRecover(
                    safetyDirectory,
                    recoveryPlatform));
            Assert.Equal(
                @"C:\Wallpapers\original.jpg",
                recoveryPlatform.SetCalls.Single().WallpaperPath);
        }
        finally
        {
            if (firstBitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(firstBitmapHandle);
            }
            if (secondBitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(secondBitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Lease_SelectsMonitorWithLargestIntersection()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperIntersection-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "left",
                        new Rectangle(0, 0, 100, 100)),
                    new DesktopWallpaperMonitor(
                        "right",
                        new Rectangle(100, 0, 100, 100))
                ],
                new Dictionary<string, string>
                {
                    ["left"] = @"C:\Wallpapers\left.jpg",
                    ["right"] = @"C:\Wallpapers\right.jpg"
                });

            using (var lease =
                   WindowsDesktopWallpaperLease.TryActivate(
                       safetyDirectory,
                       new Rectangle(40, 0, 140, 100),
                       bitmapHandle,
                       platform))
            {
                Assert.NotNull(lease);
                Assert.Equal(
                    "right",
                    platform.SetCalls.Single().MonitorId);
            }
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Lease_UsesTargetCenterToBreakEqualIntersection()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperCenter-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "left",
                        new Rectangle(0, 0, 100, 100)),
                    new DesktopWallpaperMonitor(
                        "right",
                        new Rectangle(100, 0, 100, 100))
                ],
                new Dictionary<string, string>
                {
                    ["left"] = @"C:\Wallpapers\left.jpg",
                    ["right"] = @"C:\Wallpapers\right.jpg"
                });

            using (var lease =
                   WindowsDesktopWallpaperLease.TryActivate(
                       safetyDirectory,
                       new Rectangle(50, 0, 100, 100),
                       bitmapHandle,
                       platform))
            {
                Assert.NotNull(lease);
                Assert.Equal(
                    "right",
                    platform.SetCalls.Single().MonitorId);
            }
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Lease_RejectsTargetThatDoesNotIntersectAnyMonitor()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperOutside-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 100, 100))
                ],
                new Dictionary<string, string>
                {
                    ["primary"] = @"C:\Wallpapers\original.jpg"
                });

            var lease = WindowsDesktopWallpaperLease.TryActivate(
                safetyDirectory,
                new Rectangle(300, 0, 100, 100),
                bitmapHandle,
                platform);

            Assert.Null(lease);
            Assert.Empty(platform.SetCalls);
            Assert.False(
                WindowsDesktopWallpaperLease.HasPendingRecovery(
                    safetyDirectory));
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void Recovery_RestoresMarkerLeftByInterruptedSession()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperRecovery-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        WindowsDesktopWallpaperLease? interruptedLease = null;
        try
        {
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var interruptedPlatform =
                new FakeDesktopWallpaperPlatform(
                    [
                        new DesktopWallpaperMonitor(
                            "primary",
                            new Rectangle(0, 0, 1920, 1080))
                    ],
                    new Dictionary<string, string>
                    {
                        ["primary"] =
                            @"C:\Wallpapers\before-crash.jpg"
                    });
            interruptedLease =
                WindowsDesktopWallpaperLease.TryActivate(
                    safetyDirectory,
                    new Rectangle(0, 0, 1920, 1080),
                    bitmapHandle,
                    interruptedPlatform);
            Assert.NotNull(interruptedLease);

            var recoveryPlatform =
                new FakeDesktopWallpaperPlatform(
                    [
                        new DesktopWallpaperMonitor(
                            "primary",
                            new Rectangle(0, 0, 1920, 1080))
                    ],
                    new Dictionary<string, string>());
            Assert.True(
                WindowsDesktopWallpaperLease.TryRecover(
                    safetyDirectory,
                    recoveryPlatform));
            Assert.Equal(
                @"C:\Wallpapers\before-crash.jpg",
                recoveryPlatform.SetCalls.Single().WallpaperPath);
            Assert.False(
                File.Exists(
                    Path.Combine(
                        safetyDirectory,
                        "active-wallpaper-lease.json")));
        }
        finally
        {
            interruptedLease?.Dispose();
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void DetachedLease_KeepsFrameUntilNextStartupRecovery()
    {
        var safetyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VellumeWallpaperDetach-{Guid.NewGuid():N}");
        nint bitmapHandle = nint.Zero;
        try
        {
            using (var bitmap = new Bitmap(32, 18))
            {
                bitmapHandle = bitmap.GetHbitmap();
            }

            var platform = new FakeDesktopWallpaperPlatform(
                [
                    new DesktopWallpaperMonitor(
                        "primary",
                        new Rectangle(0, 0, 1920, 1080))
                ],
                new Dictionary<string, string>
                {
                    ["primary"] = @"C:\Wallpapers\original.jpg"
                });
            var lease = WindowsDesktopWallpaperLease.TryActivate(
                safetyDirectory,
                new Rectangle(0, 0, 1920, 1080),
                bitmapHandle,
                platform);
            Assert.NotNull(lease);

            lease.DetachForProcessExit();
            lease.Dispose();

            Assert.Single(platform.SetCalls);
            Assert.True(platform.IsDisposed);
            Assert.True(
                File.Exists(
                    Path.Combine(
                        safetyDirectory,
                        "active-wallpaper-lease.json")));
            Assert.True(
                WindowsDesktopWallpaperLease.HasPendingRecovery(
                    safetyDirectory));

            var recoveryPlatform =
                new FakeDesktopWallpaperPlatform(
                    [
                        new DesktopWallpaperMonitor(
                            "primary",
                            new Rectangle(0, 0, 1920, 1080))
                    ],
                    new Dictionary<string, string>());
            Assert.True(
                WindowsDesktopWallpaperLease.TryRecover(
                    safetyDirectory,
                    recoveryPlatform));
            Assert.Equal(
                @"C:\Wallpapers\original.jpg",
                recoveryPlatform.SetCalls.Single().WallpaperPath);
            Assert.False(
                WindowsDesktopWallpaperLease.HasPendingRecovery(
                    safetyDirectory));
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            if (Directory.Exists(safetyDirectory))
            {
                Directory.Delete(
                    safetyDirectory,
                    recursive: true);
            }
        }
    }

    private sealed class FakeDesktopWallpaperPlatform :
        IDesktopWallpaperPlatform
    {
        private readonly IReadOnlyList<DesktopWallpaperMonitor>
            _monitors;
        private readonly Dictionary<string, string> _wallpapers;

        internal FakeDesktopWallpaperPlatform(
            IReadOnlyList<DesktopWallpaperMonitor> monitors,
            Dictionary<string, string> wallpapers)
        {
            _monitors = monitors;
            _wallpapers = wallpapers;
        }

        internal List<(string MonitorId, string WallpaperPath)>
            SetCalls { get; } = [];

        internal bool IsDisposed { get; private set; }

        public IReadOnlyList<DesktopWallpaperMonitor> GetMonitors()
            => _monitors;

        public string GetWallpaper(string monitorId)
            => _wallpapers.TryGetValue(
                monitorId,
                out var wallpaper)
                ? wallpaper
                : string.Empty;

        public void SetWallpaper(
            string monitorId,
            string wallpaperPath)
        {
            SetCalls.Add(
                (monitorId, wallpaperPath));
            _wallpapers[monitorId] = wallpaperPath;
        }

        public void Dispose()
            => IsDisposed = true;
    }
}
