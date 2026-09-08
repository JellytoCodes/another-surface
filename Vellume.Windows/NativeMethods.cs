using System.Runtime.InteropServices;

namespace Vellume.Windows;

internal static class NativeMethods
{
    // Temporarily opts a synchronous monitor query into physical-pixel coordinates.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetThreadDpiAwarenessContext(nint context);

    // Verifies that scoped DPI queries restore the caller's awareness context.
    [DllImport("user32.dll")]
    internal static extern nint GetThreadDpiAwarenessContext();

    internal const uint SpawnWorkerWMessage = 0x052C;
    internal const uint SmtoNormal = 0x0000;
    internal const int GwlStyle = -16;
    internal const int GwlExtendedStyle = -20;
    internal const uint DwmwaExcludedFromPeek = 12;
    internal const long WsChild = 0x40000000L;
    internal const long WsPopup = 0x80000000L;
    internal const long WsVisible = 0x10000000L;
    internal const long WsClipChildren = 0x02000000L;
    internal const long WsClipSiblings = 0x04000000L;
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoRedirectionBitmap = 0x00200000L;
    internal const uint LwaColorKey = 0x00000001;
    internal const uint LwaAlpha = 0x00000002;
    internal const uint UlwAlpha = 0x00000002;
    internal const byte AcSrcOver = 0x00;
    internal const byte AcSrcAlpha = 0x01;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpFrameChanged = 0x0020;
    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;
    internal const long SsBitmap = 0x0000000EL;
    internal const long SsBlackRect = 0x00000004L;
    internal const long SsTypeMask = 0x0000001FL;
    internal const uint StmSetImage = 0x0172;
    internal const nuint ImageBitmap = 0;
    internal const uint MonitorInfoPrimary = 0x00000001;
    internal const uint MonitorDefaultToNull = 0x00000000;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint GwHwndNext = 2;
    internal const uint GwHwndPrevious = 3;
    internal const uint CwpSkipInvisible = 0x0001;
    internal const uint CwpSkipDisabled = 0x0002;
    internal const uint CwpSkipTransparent = 0x0004;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLeftButtonDown = 0x0201;
    internal const uint WmLeftButtonUp = 0x0202;
    internal const uint WmEraseBackground = 0x0014;
    internal const uint WmPrintClient = 0x0318;
    internal const nint PrfClient = 0x00000004;
    internal const uint MkLeftButton = 0x0001;
    internal const uint CsVRedraw = 0x0001;
    internal const uint CsHRedraw = 0x0002;
    internal const int AlternateFillMode = 1;
    internal const int RegionOr = 2;
    internal const uint StockIconLocation = 0;
    internal const uint StockIconRecycleBin = 31;
    internal const uint StockIconRecycleBinFull = 32;
    internal const uint StockIconDesktopPc = 94;
    internal static readonly nint HwndBottom = new(1);
    internal static int TrueValue = 1;

    internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);
    internal delegate bool MonitorEnumProc(
        nint monitorHandle,
        nint deviceContext,
        nint monitorRectangle,
        nint parameter);

    // Creates the renderer as a native desktop child from its first frame.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentHandle,
        nint menuHandle,
        nint instanceHandle,
        nint parameter);

    // Destroys only Vellume's renderer host during Stop or application exit.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint windowHandle);

    // Hides GPU-backed child surfaces during exclusive editor rendering
    // without activating or terminating either application.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(
        nint windowHandle,
        int command);

    // Keeps an independent desktop widget out of Aero Peek without parenting
    // its per-pixel alpha surface to Explorer.
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint windowHandle,
        uint attribute,
        ref int attributeValue,
        int attributeSize);

    // Supplies this module handle when creating Vellume's native renderer host.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    // Locates Explorer's top-level Progman and WorkerW windows by class name.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowW(string? className, string? windowName);

    // Walks child or sibling windows while looking for the desktop icon host.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowExW(
        nint parentHandle,
        nint childAfter,
        string? className,
        string? windowName);

    // Reads a top-level window's class while diagnosing Explorer desktop layouts.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(
        nint windowHandle,
        System.Text.StringBuilder className,
        int maximumCount);

    // Enumerates Explorer windows without modifying or restarting Explorer.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    // Asks Progman to create its wallpaper WorkerW using Explorer's documented-in-practice message.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    // Assigns a rendered native bitmap to the wallpaper Static host.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageW(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    // Parents the renderer to WorkerW so desktop icons remain above it.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint childHandle, nint newParentHandle);

    // Reads the current parent to detect Explorer/WorkerW recreation.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetParent(nint childHandle);

    // Reads neighboring child windows to verify renderer z-order inside Progman.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindow(nint windowHandle, uint command);

    // Resolves the WebView2 descendant that would receive a click at a host-client point.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint ChildWindowFromPointEx(
        nint parentHandle,
        Point point,
        uint flags);

    // Converts diagnostic mouse coordinates while walking the WebView2 child hierarchy.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int MapWindowPoints(
        nint fromHandle,
        nint toHandle,
        ref Point point,
        uint pointCount);

    // Tracks pointer movement while a native quick-slot group is being dragged.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    // Sends diagnostic mouse messages without moving or terminating any user process.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    // Creates an empty region used to assemble the interactive skin silhouette.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateRectRgn(
        int left,
        int top,
        int right,
        int bottom);

    // Creates the compact category header polygon for a quick-slot host.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreatePolygonRgn(
        [In] Point[] points,
        int pointCount,
        int fillMode);

    // Creates each circular application tile in the compact launcher grid.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateEllipticRgn(
        int left,
        int top,
        int right,
        int bottom);

    // Extracts a large application icon so circular launchers remain sharp when cropped.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    internal static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        [Out] nint[] iconHandles,
        [Out] uint[] iconIdentifiers,
        uint iconCount,
        uint flags);

    // Locates Windows' current stock icons for This PC and Recycle Bin states.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHGetStockIconInfo(
        uint stockIconId,
        uint flags,
        ref StockIconInfo stockIconInfo);

    // Releases icon handles returned by PrivateExtractIcons.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint iconHandle);

    // Unions individual skin polygons into one click-preserving window region.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int CombineRgn(
        nint destination,
        nint sourceOne,
        nint sourceTwo,
        int combineMode);

    // Clips the native host to visible skin pieces without layered color-key input loss.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(
        nint windowHandle,
        nint regionHandle,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    // Releases temporary GDI regions after they are combined.
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint objectHandle);

    // Allocates a memory device context for an alpha-composited widget panel.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    // Releases the temporary memory device context used for a panel frame.
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    // Selects the 32-bit DIB backing store into the temporary device context.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint SelectObject(
        nint deviceContext,
        nint graphicsObject);

    // Creates a top-down premultiplied BGRA surface for UpdateLayeredWindow.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bitmapBits,
        nint section,
        uint offset);

    // Presents a complete widget panel with true per-pixel alpha.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        nint windowHandle,
        nint destinationDeviceContext,
        nint destinationPoint,
        ref NativeSize size,
        nint sourceDeviceContext,
        ref NativePoint sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    // Checks whether cached Explorer or renderer handles are still valid.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint windowHandle);

    // Excludes hidden helper WorkerW instances that cannot display wallpaper.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint windowHandle);

    // Detects a minimized top-level window without restoring or activating it.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint windowHandle);

    // Ensures a WorkerW candidate belongs to the same Explorer process as Progman.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    // Reads the active top-level window without changing focus.
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    // Detects ordinary maximized application windows such as Unreal Editor.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint windowHandle);

    // Resolves the monitor occupied by a borderless full-screen window.
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(
        nint windowHandle,
        uint flags);

    // Reads native window styles before converting the renderer into a child window.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    // Removes top-level styles and adds WS_CHILD after parenting to WorkerW.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);

    // Makes a raised-desktop child fully opaque while retaining WS_EX_LAYERED composition.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(
        nint windowHandle,
        uint colorKey,
        byte alpha,
        uint flags);

    // Verifies that Explorer's required opaque layered-window alpha remains active.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLayeredWindowAttributes(
        nint windowHandle,
        out uint colorKey,
        out byte alpha,
        out uint flags);

    // Registers the process-local class used by transparent widget bridges.
    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern ushort RegisterClassExW(
        ref WindowClassEx windowClass);

    // Supplies standard lifetime and child-window handling to that class.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProcW(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    // Creates its exact, theme-independent background brush.
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateSolidBrush(uint color);

    // Reads the full bridge client bounds before filling the transparent key.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(
        nint windowHandle,
        out Rect rectangle);

    // Paints every bridge pixel with the same literal color-key brush.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int FillRect(
        nint deviceContext,
        ref Rect rectangle,
        nint brush);

    // Acquires a bridge DC so its first hidden frame can be primed synchronously.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetDC(nint windowHandle);

    // Releases the temporary bridge DC after its transparent background is primed.
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(
        nint windowHandle,
        nint deviceContext);

    // Positions the native renderer in physical pixels within the WorkerW client area.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    // Gets WorkerW screen bounds so monitor coordinates can be converted to child coordinates.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out Rect rectangle);

    // Enumerates connected desktop monitors directly from Windows.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc callback,
        nint parameter);

    // Retrieves bounds, device name, and primary status for a monitor.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(
        nint monitorHandle,
        ref MonitorInfoEx monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        internal int Width;
        internal int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;

        internal Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StockIconInfo
    {
        internal uint Size;
        internal nint IconHandle;
        internal int SystemImageIndex;
        internal int IconIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }
}
