namespace Vellume.Windows;

internal sealed class DesktopHostLocator
{
    private const long WsExNoRedirectionBitmap = 0x00200000L;

    public bool IsRaisedDesktop { get; private set; }
    public nint ShellViewHandle { get; private set; }
    public nint BackgroundWorkerHandle { get; private set; }
    public nint InteractiveHostHandle { get; private set; }

    public nint FindWallpaperHost()
    {
        IsRaisedDesktop = false;
        ShellViewHandle = nint.Zero;
        BackgroundWorkerHandle = nint.Zero;
        InteractiveHostHandle = nint.Zero;

        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == nint.Zero)
        {
            return nint.Zero;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(
            progman,
            NativeMethods.GwlExtendedStyle).ToInt64();
        IsRaisedDesktop =
            (extendedStyle & WsExNoRedirectionBitmap) != 0;

        // These messages cover both classic Explorer and newer Windows 11 WorkerW layouts.
        // They only ask the running shell to expose its wallpaper host; Explorer is never restarted.
        NativeMethods.SendMessageTimeoutW(
            progman,
            NativeMethods.SpawnWorkerWMessage,
            new nint(0xD),
            new nint(0x1),
            NativeMethods.SmtoNormal,
            1000,
            out _);

        if (IsRaisedDesktop)
        {
            ShellViewHandle = NativeMethods.FindWindowExW(
                progman,
                nint.Zero,
                "SHELLDLL_DefView",
                null);
            BackgroundWorkerHandle = NativeMethods.FindWindowExW(
                progman,
                nint.Zero,
                "WorkerW",
                null);

            if (ShellViewHandle != nint.Zero &&
                BackgroundWorkerHandle != nint.Zero)
            {
                InteractiveHostHandle = progman;
                return progman;
            }

            return nint.Zero;
        }

        NativeMethods.GetWindowThreadProcessId(progman, out var explorerProcessId);
        NativeMethods.GetWindowRect(progman, out var desktopRectangle);
        nint wallpaperHost = nint.Zero;
        nint iconHost = nint.Zero;
        var workerWindows = new List<nint>();
        NativeMethods.EnumWindows((topLevel, _) =>
        {
            if (IsUsableWorker(
                    topLevel,
                    explorerProcessId,
                    desktopRectangle))
            {
                workerWindows.Add(topLevel);
            }

            var shellView = NativeMethods.FindWindowExW(
                topLevel,
                nint.Zero,
                "SHELLDLL_DefView",
                null);

            if (shellView == nint.Zero)
            {
                return true;
            }

            iconHost = topLevel;
            ShellViewHandle = shellView;
            var backgroundSibling = NativeMethods.FindWindowExW(
                nint.Zero,
                topLevel,
                "WorkerW",
                null);
            if (IsUsableWorker(
                    backgroundSibling,
                    explorerProcessId,
                    desktopRectangle))
            {
                wallpaperHost = backgroundSibling;
            }

            return true;
        }, nint.Zero);

        if (wallpaperHost != nint.Zero)
        {
            InteractiveHostHandle =
                iconHost != nint.Zero ? iconHost : progman;
            return wallpaperHost;
        }

        // Some current Explorer builds put SHELLDLL_DefView and our renderer in the
        // same WorkerW. HWND_BOTTOM keeps the renderer behind the icon child.
        if (IsUsableWorker(
                iconHost,
                explorerProcessId,
                desktopRectangle))
        {
            InteractiveHostHandle = iconHost;
            return iconHost;
        }

        // If Progman owns the icons, prefer a separate WorkerW created by the messages.
        var separateWorker = workerWindows.FirstOrDefault(handle => handle != iconHost);
        if (separateWorker != nint.Zero)
        {
            InteractiveHostHandle =
                iconHost != nint.Zero ? iconHost : progman;
            return separateWorker;
        }

        return nint.Zero;
    }

    private static bool IsUsableWorker(
        nint windowHandle,
        uint explorerProcessId,
        NativeMethods.Rect desktopRectangle)
    {
        if (windowHandle == nint.Zero ||
            !GetWindowClass(windowHandle).Equals(
                "WorkerW",
                StringComparison.OrdinalIgnoreCase) ||
            !NativeMethods.IsWindowVisible(windowHandle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId != explorerProcessId ||
            !NativeMethods.GetWindowRect(windowHandle, out var rectangle))
        {
            return false;
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        var desktopWidth = Math.Max(
            1,
            desktopRectangle.Right - desktopRectangle.Left);
        var desktopHeight = Math.Max(
            1,
            desktopRectangle.Bottom - desktopRectangle.Top);
        return width >= Math.Min(640, desktopWidth / 2) &&
               height >= Math.Min(360, desktopHeight / 2);
    }

    private static string GetWindowClass(nint windowHandle)
    {
        var className = new System.Text.StringBuilder(256);
        return NativeMethods.GetClassNameW(
            windowHandle,
            className,
            className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}
