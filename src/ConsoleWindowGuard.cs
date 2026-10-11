using System.Runtime.InteropServices;

namespace WukongBenchmarkRunner;

/// <summary>
/// Desktop OCR uses CopyFromScreen, not an off-screen/window rendering API.
/// An overlapping cmd.exe console was hiding the Average FPS field and
/// contaminating every crop. Hide only the current console for the benchmark
/// session; restore it even if either pass fails.
/// </summary>
internal sealed class ConsoleWindowGuard : IDisposable
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);

    private readonly IntPtr _window;
    private readonly bool _wasVisible;

    public ConsoleWindowGuard()
    {
        _window = GetConsoleWindow();
        _wasVisible = _window != IntPtr.Zero && IsWindowVisible(_window);
        if (_wasVisible) ShowWindow(_window, 0); // SW_HIDE
    }

    public void Dispose()
    {
        if (_wasVisible && _window != IntPtr.Zero)
            ShowWindow(_window, 5); // SW_SHOW
    }
}
