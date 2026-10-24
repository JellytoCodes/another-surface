using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vellume.Windows;

/// <summary>
/// Registers the paint-free native class used by software-rasterized alpha
/// widgets. A null class brush prevents Windows from exposing a default gray
/// frame while the premultiplied BGRA surface is attached or resized.
/// </summary>
internal static class NativeAlphaPanelWindowClass
{
    internal const string ClassName =
        "Vellume.NativeAlphaPanel.v1";
    private static readonly object Sync = new();
    private static readonly NativeMethods.WindowProcedure
        WindowProcedureCallback = WindowProcedure;
    private static ushort _classAtom;

    internal static string EnsureRegistered()
    {
        lock (Sync)
        {
            if (_classAtom != 0)
            {
                return ClassName;
            }

            var windowClass = new NativeMethods.WindowClassEx
            {
                Size = unchecked((uint)Marshal.SizeOf<
                    NativeMethods.WindowClassEx>()),
                WindowProcedure =
                    Marshal.GetFunctionPointerForDelegate(
                        WindowProcedureCallback),
                Instance = NativeMethods.GetModuleHandleW(null),
                BackgroundBrush = nint.Zero,
                ClassName = ClassName
            };
            _classAtom = NativeMethods.RegisterClassExW(
                ref windowClass);
            if (_classAtom == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows에서 투명 위젯 창 클래스를 등록하지 못했습니다.");
            }

            return ClassName;
        }
    }

    private static nint WindowProcedure(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam)
    {
        // UpdateLayeredWindow supplies every visible pixel. Suppress background
        // erasure so resize and SetParent cannot inject a solid-color frame.
        if (message == NativeMethods.WmEraseBackground)
        {
            return new nint(1);
        }

        return NativeMethods.DefWindowProcW(
            windowHandle,
            message,
            wParam,
            lParam);
    }
}
