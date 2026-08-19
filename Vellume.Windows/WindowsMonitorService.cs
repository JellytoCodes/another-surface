using System.ComponentModel;
using System.Runtime.InteropServices;
using Vellume.Core;

namespace Vellume.Windows;

public sealed class WindowsMonitorService : IMonitorService
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty
            };

            if (NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                displays.Add(new DisplayInfo
                {
                    Id = info.DeviceName,
                    Name = info.DeviceName.Replace(@"\\.\", string.Empty),
                    X = info.Monitor.Left,
                    Y = info.Monitor.Top,
                    Width = info.Monitor.Right - info.Monitor.Left,
                    Height = info.Monitor.Bottom - info.Monitor.Top,
                    IsPrimary = (info.Flags & NativeMethods.MonitorInfoPrimary) != 0
                });
            }

            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows에서 연결된 모니터 목록을 가져오지 못했습니다.");
        }

        return displays
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.X)
            .ToArray();
    }
}
