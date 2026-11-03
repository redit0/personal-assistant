using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Assistant.App;

/// <summary>
/// Shows the logo without its background in a window's title bar. Only the small icon changes: the taskbar uses
/// the large one, which keeps the tile so the dark mark stays visible on a dark taskbar.
/// </summary>
public static class TitleBarIcon
{
    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;

    private static readonly string IconFile = Path.Combine(AppContext.BaseDirectory, "assistant-small.ico");

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.DpiChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !File.Exists(IconFile))
        {
            return;
        }

        // Pick the frame that matches the title bar at this window's DPI (16 px at 100%, 24 at 150%, and so on).
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(hwnd));
        var icon = LoadImage(IntPtr.Zero, IconFile, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (icon != IntPtr.Zero)
        {
            // The window keeps using it, so it isn't destroyed; one icon per DPI change is a negligible leak.
            SendMessage(hwnd, WM_SETICON, ICON_SMALL, icon);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, int wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
