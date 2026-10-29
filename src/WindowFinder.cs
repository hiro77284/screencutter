using System.Runtime.InteropServices;

namespace ScreenCutter;

/// <summary>画面上のウィンドウの位置（ウィンドウ全体と描画ペイン）。座標はスクリーン座標の物理ピクセル。</summary>
public readonly record struct WindowInfo(IntPtr Handle, Rectangle Bounds, Rectangle Client);

/// <summary>表示中のトップレベルウィンドウを手前から順に集める。</summary>
public static class WindowFinder
{
    public static List<WindowInfo> EnumerateVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (TryGetInfo(hwnd, out WindowInfo info))
                windows.Add(info);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>EnumWindows は Z オーダー順（手前から）なので、最初に点を含むものがマウスの下のウィンドウ。</summary>
    public static WindowInfo? FindAt(IReadOnlyList<WindowInfo> windows, Point screenPoint)
    {
        foreach (WindowInfo w in windows)
        {
            if (w.Bounds.Contains(screenPoint))
                return w;
        }
        return null;
    }

    private static bool TryGetInfo(IntPtr hwnd, out WindowInfo info)
    {
        info = default;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

        // クリックが透過するウィンドウ（オーバーレイ類）は対象外
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExTransparent) != 0) return false;

        // 仮想デスクトップの別画面などで「隠されている」ウィンドウは対象外
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // 見た目どおりの枠（Windows 10/11 の透明なリサイズ枠を含まない）を使う
        Rectangle bounds;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
            bounds = frame.ToRectangle();
        else if (GetWindowRect(hwnd, out RECT wr))
            bounds = wr.ToRectangle();
        else
            return false;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        Rectangle client = Rectangle.Empty;
        if (GetClientRect(hwnd, out RECT cr))
        {
            var origin = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(hwnd, ref origin))
                client = Rectangle.Intersect(new Rectangle(origin.X, origin.Y, cr.Right - cr.Left, cr.Bottom - cr.Top), bounds);
        }
        if (client.Width <= 0 || client.Height <= 0) client = bounds;

        info = new WindowInfo(hwnd, bounds, client);
        return true;
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
