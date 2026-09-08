using System.Runtime.InteropServices;
using Vellume.Core;
using Vellume.Windows;

namespace Vellume.Tests;

public sealed class WindowsMonitorServiceTests
{
    [Fact]
    public void GetDisplays_AlwaysReturnsPhysicalBoundsAndRestoresCallerDpi()
    {
        var original = NativeMethods.SetThreadDpiAwarenessContext(new nint(-4));
        Assert.NotEqual(nint.Zero, original);
        try
        {
            var expected = new Dictionary<string, NativeMethods.Rect>();
            NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new NativeMethods.MonitorInfoEx
                {
                    Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                    DeviceName = string.Empty
                };
                Assert.True(NativeMethods.GetMonitorInfoW(monitor, ref info));
                expected.Add(info.DeviceName, info.Monitor);
                return true;
            };
            Assert.True(NativeMethods.EnumDisplayMonitors(
                nint.Zero, nint.Zero, callback, nint.Zero));
            Assert.NotEmpty(expected);

            foreach (var awareness in new[] { -1, -2, -4 })
            {
                Assert.NotEqual(nint.Zero,
                    NativeMethods.SetThreadDpiAwarenessContext(new nint(awareness)));
                var before = NativeMethods.GetThreadDpiAwarenessContext();
                var actual = new WindowsMonitorService().GetDisplays();
                Assert.Equal(before, NativeMethods.GetThreadDpiAwarenessContext());
                Assert.Equal(expected.Count, actual.Count);
                foreach (var display in actual)
                {
                    var bounds = expected[display.Id];
                    Assert.Equal(bounds.Left, display.X);
                    Assert.Equal(bounds.Top, display.Y);
                    Assert.Equal(bounds.Right - bounds.Left, display.Width);
                    Assert.Equal(bounds.Bottom - bounds.Top, display.Height);
                }
            }
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(original);
        }
    }

    [Fact]
    public void GeometryComparison_DetectsResizeMoveAndPrimaryReassignment()
    {
        var original = new DisplayInfo
        {
            Id = "DISPLAY2", X = 0, Y = -1440,
            Width = 3440, Height = 1440, IsPrimary = false
        };
        Assert.True(original.HasSameGeometry(new DisplayInfo
        {
            Id = "display2", X = 0, Y = -1440,
            Width = 3440, Height = 1440, IsPrimary = false,
            AssignedWallpaperName = "다른 이미지", IsWallpaperPaused = true
        }));
        Assert.False(original.HasSameGeometry(new DisplayInfo
        {
            Id = "DISPLAY2", X = 0, Y = -1800,
            Width = 4300, Height = 1800, IsPrimary = false
        }));
        Assert.False(original.HasSameGeometry(new DisplayInfo
        {
            Id = "DISPLAY2", X = -3440, Y = 0,
            Width = 3440, Height = 1440, IsPrimary = false
        }));
        Assert.False(original.HasSameGeometry(new DisplayInfo
        {
            Id = "DISPLAY2", X = 0, Y = -1440,
            Width = 3440, Height = 1440, IsPrimary = true
        }));
    }
}
