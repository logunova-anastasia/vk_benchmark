using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WukongBenchmarkRunner;

public static class WindowControl
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static Process? LocateGameProcess(string root, Process launcher, IReadOnlySet<int>? excludedPids = null)
    {
        var candidates = Process.GetProcesses().Where(x =>
            x.ProcessName.Equals("b1_benchmark", StringComparison.OrdinalIgnoreCase) ||
            x.ProcessName.Equals("b1-Win64-Shipping", StringComparison.OrdinalIgnoreCase));
        foreach (var candidate in candidates)
        {
            try
            {
                if (excludedPids?.Contains(candidate.Id) == true || candidate.MainWindowHandle == IntPtr.Zero) continue;
                var executable = candidate.MainModule?.FileName;
                if (executable != null && Path.GetFullPath(executable).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch { /* Transient game process or access denied. */ }
        }
        try { if (excludedPids?.Contains(launcher.Id) != true && !launcher.HasExited && launcher.MainWindowHandle != IntPtr.Zero) return launcher; } catch { }
        return null;
    }

    public static Rectangle Bounds(Process process)
    {
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Нет окна приложения.");
        var primary = Screen.PrimaryScreen!.Bounds;
        if (!GetWindowRect(process.MainWindowHandle, out var r)) return primary;
        var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var screen = Screen.AllScreens.Select(s => s.Bounds)
            .OrderByDescending(s => Rectangle.Intersect(s, window).Width * Rectangle.Intersect(s, window).Height)
            .FirstOrDefault();
        return window.Width < 400 || window.Height < 300 ? screen : Rectangle.Intersect(window, screen);
    }

    public static void Focus(Process process)
    {
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero) return;
        ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
        SetForegroundWindow(process.MainWindowHandle);
    }

    public static void Click(ScreenFrame frame, OcrBox box)
    {
        int x = frame.ScreenRectangle.Left + (int)(box.CenterX * frame.OcrToScreenScaleX);
        int y = frame.ScreenRectangle.Top + (int)(box.CenterY * frame.OcrToScreenScaleY);
        ClickScreenPosition(x, y);
    }

    // The title screen asks for a click anywhere. Avoid menu buttons on the left;
    // use a quiet part of the visible game window, not a fixed desktop coordinate.
    public static void ClickRelative(ScreenFrame frame, double fractionX, double fractionY)
    {
        if (fractionX is < 0 or > 1 || fractionY is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(fractionX), "Coordinates must be between 0 and 1.");
        var rect = frame.ScreenRectangle;
        var x = rect.Left + (int)Math.Round((rect.Width - 1) * fractionX);
        var y = rect.Top + (int)Math.Round((rect.Height - 1) * fractionY);
        ClickScreenPosition(x, y);
    }

    public static void PressKey(Process game, byte virtualKey)
    {
        Focus(game);
        Thread.Sleep(250);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        Thread.Sleep(110);
        keybd_event(virtualKey, 0, 0x0002, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    private static void ClickScreenPosition(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
        Thread.Sleep(90);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
    }
}
