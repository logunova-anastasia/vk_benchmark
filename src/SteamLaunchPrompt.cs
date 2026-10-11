using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WukongBenchmarkRunner;

/// <summary>
/// Confirms the blue Continue + grey Cancel dialog that Steam can show when
/// b1_benchmark.exe is started with additional arguments. Never sends blind keys.
/// </summary>
public static class SteamLaunchPrompt
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int maxCount);

    private static readonly Regex LaunchContext = new(
        @"(?:\blaunch(?:ing)?\b|\bstarting\b|\bopening\b|\bstart(?:ing)?\s+(?:application|app|game)\b|" +
        @"запуск\w*|запуска\w*|открыва\w*|открытие\s+приложени\w*|подготовка\s+к\s+запуск\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ContinueButton = new(
        @"^\s*(?:продолжить|continue)\s*[.!»>]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static OcrBox? FindContinueButton(OcrDocument ocr, string steamWindowTitle = "")
    {
        if (!LaunchContext.IsMatch(steamWindowTitle + "\n" + ocr.Text)) return null;
        foreach (var line in ocr.Lines)
        {
            if (ContinueButton.IsMatch(line.Text)) return line.Box;
            foreach (var word in line.Words)
                if (ContinueButton.IsMatch(word.Text)) return word.Box;
        }
        return null;
    }

    // IMPORTANT: fixed, pixel-sized button limits. Steam's 180x48 buttons remain
    // 180x48 even when the Steam client/window is 3840x2160. Earlier code used
    // >=7% of window width and >=4% of height; that rejected the real button.
    // Search the ENTIRE window: the confirmation is often a centered HTML modal.
    public static Point? FindSteamContinueByAppearance(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        if (w < 380 || h < 240) return null;

        static bool IsBlue(Color c) => c.B > 150 && c.B - c.R > 70 &&
            c.G - c.R > 32 && c.B - c.G > 25 && c.G > 70 && c.G < 215 && c.R < 155;
        static bool IsGrayButton(Color c) => c.R >= 45 && c.R <= 130 &&
            c.G - c.R >= 2 && c.G - c.R <= 22 &&
            c.B - c.G >= 5 && c.B - c.G <= 28 && c.B < 150;

        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w;)
            {
                if (!IsBlue(bitmap.GetPixel(x, y))) { x += 4; continue; }
                int left = x, right = x;
                while (left > 0 && IsBlue(bitmap.GetPixel(left - 1, y))) left--;
                while (right + 1 < w && IsBlue(bitmap.GetPixel(right + 1, y))) right++;
                x = right + 5;
                int width = right - left + 1;
                if (width < 90 || width > 440) continue;

                int probeX = left + Math.Min(12, width / 8);
                int top = y, bottom = y;
                while (top > 0 && IsBlue(bitmap.GetPixel(probeX, top - 1))) top--;
                while (bottom + 1 < h && IsBlue(bitmap.GetPixel(probeX, bottom + 1))) bottom++;
                int height = bottom - top + 1;
                if (height < 24 || height > 125) continue;

                // Steam displays Cancel immediately to the right of Continue.
                // Validate the matching grey button at four separate points.
                int inset = Math.Min(12, width / 8);
                int insetY = Math.Max(6, height / 5);
                int y1 = top + insetY, y2 = bottom - insetY;
                for (int gap = 8; gap <= Math.Min(75, width / 2); gap += 2)
                {
                    int greyLeft = right + gap;
                    int greyRight = greyLeft + width - 1;
                    if (greyRight >= w) break;
                    if (IsGrayButton(bitmap.GetPixel(greyLeft + inset, y1)) &&
                        IsGrayButton(bitmap.GetPixel(greyLeft + inset, y2)) &&
                        IsGrayButton(bitmap.GetPixel(greyRight - inset, y1)) &&
                        IsGrayButton(bitmap.GetPixel(greyRight - inset, y2)))
                        return new Point((left + right) / 2, (top + bottom) / 2);
                }
            }
        }
        return null;
    }

    private static bool IsSteamProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var process = Process.GetProcessById((int)pid);
            // The confirmation is usually owned by steamwebhelper.exe,
            // not necessarily by the main steam.exe process.
            return process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static async Task<bool> TryContinueAsync(string runFolder, CancellationToken ct)
    {
        var foreground = GetForegroundWindow();
        var windows = new List<IntPtr>();
        if (foreground != IntPtr.Zero) windows.Add(foreground);
        // An HTML confirmation popup is often a DIFFERENT top-level window from
        // the foreground main Steam client. Search all visible Steam windows.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != foreground && IsWindowVisible(hwnd) && IsSteamProcess(hwnd))
                windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        bool capturedForDiagnostics = false;
        foreach (var hwnd in windows.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsWindowVisible(hwnd) || !IsSteamProcess(hwnd)) continue;
            if (!GetWindowRect(hwnd, out var rect)) continue;
            var window = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            // Capture only the visible area, correctly handling multiple monitors.
            var screen = Screen.AllScreens.Select(s => Rectangle.Intersect(window, s.Bounds))
                .OrderByDescending(r => (long)r.Width * r.Height).FirstOrDefault();
            if (screen.Width < 380 || screen.Height < 240) continue;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);

            try
            {
                using var bitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy);

                var colorMatch = FindSteamContinueByAppearance(bitmap);
                if (colorMatch.HasValue)
                {
                    var location = new Point(screen.Left + colorMatch.Value.X, screen.Top + colorMatch.Value.Y);
                    // If the Steam window was covered by the game, the screenshot
                    // might contain an unrelated blue button. Confirm the actual
                    // TOPMOST owner of the click location is Steam/steamwebhelper.
                    if (!IsSteamProcess(WindowFromPoint(new NativePoint { X = location.X, Y = location.Y })))
                        continue;

                    var screenshot = Path.Combine(runFolder, "steam-launch-dialog.png");
                    bitmap.Save(screenshot, ImageFormat.Png);
                    await File.WriteAllTextAsync(Path.Combine(runFolder, "steam-launch-dialog.txt"),
                        $"Steam dialog found. Window: {title}\nProcess verified at click point.\n" +
                        $"Click: {location.X},{location.Y}\n", ct);
                    WindowControl.ClickRelative(new ScreenFrame(screenshot, screenshot, screen, 1, 1),
                        (double)colorMatch.Value.X / (screen.Width - 1),
                        (double)colorMatch.Value.Y / (screen.Height - 1));
                    return true;
                }

                // Save one diagnostic capture if no window matches; lets us
                // diagnose future UI changes instead of blindly clicking.
                if (!capturedForDiagnostics && hwnd == foreground)
                {
                    bitmap.Save(Path.Combine(runFolder, "steam-launch-unmatched.png"), ImageFormat.Png);
                    capturedForDiagnostics = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* One transient Steam window must not stop benchmark. */ }
        }

        // OCR fallback for a Steam theme with different button colors.
        // Limit to verified foreground Steam windows to avoid clicking other apps.
        if (IsSteamProcess(foreground) && GetWindowRect(foreground, out var r))
        {
            var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var screen = Screen.AllScreens.Select(s => Rectangle.Intersect(window, s.Bounds))
                .OrderByDescending(b => (long)b.Width * b.Height).FirstOrDefault();
            if (screen.Width >= 380 && screen.Height >= 240)
            {
                ScreenFrame? frame = null;
                try
                {
                    frame = ScreenOcr.Capture(screen, runFolder, "steam-launch-ocr", fullWindow: true);
                    var title = new StringBuilder(512);
                    GetWindowText(foreground, title, title.Capacity);
                    var languages = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                        ? new[] { "ru-RU", "en-US" } : new[] { "en-US", "ru-RU" };
                    foreach (var language in languages)
                    {
                        try
                        {
                            var doc = await ScreenOcr.RecognizeImageAsync(frame.OcrScreenshot, ct, language);
                            var button = FindContinueButton(doc, title.ToString());
                            if (button == null || !IsSteamProcess(GetForegroundWindow())) continue;
                            var point = new Point(frame.ScreenRectangle.Left + (int)(button.CenterX * frame.OcrToScreenScaleX),
                                frame.ScreenRectangle.Top + (int)(button.CenterY * frame.OcrToScreenScaleY));
                            if (!IsSteamProcess(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }))) continue;
                            File.Copy(frame.OriginalScreenshot, Path.Combine(runFolder, "steam-launch-dialog.png"), true);
                            WindowControl.Click(frame, button);
                            return true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* OCR language pack may be absent. */ }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* A Steam window can disappear during capture. */ }
                finally
                {
                    if (frame != null)
                    {
                        try { File.Delete(frame.OriginalScreenshot); } catch { }
                        try { File.Delete(frame.OcrScreenshot); } catch { }
                    }
                }
            }
        }
        return false;
    }
}
