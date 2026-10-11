using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

/// <summary>
/// The requested INI profile is NOT evidence of the profile actually measured by the game.
/// We OCR the graphics settings area of the benchmark's own result page independently.
/// </summary>
public static class ResultSettingsVerifier
{
    public sealed record Verification(bool Verified, string Detail);

    public static async Task<Verification> CheckAsync(string screenshot, BenchmarkProfile profile,
        string folder, CancellationToken ct)
    {
        var cropped = Path.Combine(folder, "settings-screen.png");
        using (var original = new Bitmap(screenshot))
        {
            // Both graphics-settings columns are on the RIGHT; the FPS OCR only sees the left.
            var src = Rectangle.FromLTRB((int)(original.Width * .555), (int)(original.Height * .215),
                Math.Min(original.Width, (int)(original.Width * .965)), (int)(original.Height * .72));
            var factor = Math.Min(2.4, Math.Min(1800.0 / src.Width, 1600.0 / src.Height));
            using var image = new Bitmap((int)(src.Width * factor), (int)(src.Height * factor));
            using var graphics = Graphics.FromImage(image);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, new Rectangle(Point.Empty, image.Size), src, GraphicsUnit.Pixel);
            image.Save(cropped, ImageFormat.Png);
        }
        var read = await ScreenOcr.RecognizeImageAsync(cropped, ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "settings-ocr.txt"), read.Text, Encoding.UTF8, ct);
        var status = Check(read, profile);
        await File.WriteAllTextAsync(Path.Combine(folder, "settings-verification.txt"), status.Detail, Encoding.UTF8, ct);
        return status;
    }

    public static Verification Check(OcrDocument read, BenchmarkProfile profile)
    {
        var resolution = ValueBelow(read, @"Display\s+Resolution", @"\d{3,4}\s*[x×X]\s*\d{3,4}");
        var graphics = ValueBelow(read, @"Graphics\s+Preset", @"(?:Very\s+High|Cinematic|Medium|High|Low|Custom)");
        var ray = ValueBelow(read, @"Full\s+Ray\s+Tracing", @"(?:Off|On|Disabled|Enabled)");
        var targetRes = $"{profile.Width}x{profile.Height}";
        var actualRes = resolution is null ? null : Regex.Replace(resolution, @"\s+|×", "").ToLowerInvariant();
        var actualQuality = graphics is null ? null : Regex.Replace(graphics, @"\s+", " ").Trim();
        bool resolutionOK = actualRes == targetRes.ToLowerInvariant();
        bool presetOK = string.Equals(actualQuality, profile.QualityName, StringComparison.OrdinalIgnoreCase);
        // A "Custom" graphics preset is only acceptable if individual quality values
        // prove that the game is actually using the requested (non-CPU) quality level.
        if (actualQuality?.Equals("Custom", StringComparison.OrdinalIgnoreCase) == true)
        {
            var qualityLabels = new[] { "View Distance Quality", "Shadow Quality", "Texture Quality" };
            presetOK = qualityLabels.All(label =>
                string.Equals(ValueBelow(read, Regex.Escape(label), @"(?:Very\s+High|Cinematic|Medium|High|Low)"),
                    profile.QualityName, StringComparison.OrdinalIgnoreCase));
        }
        var targetRay = profile.RayTracing ? "On" : "Off";
        bool rayOK = ray != null &&
            (ray.Equals(targetRay, StringComparison.OrdinalIgnoreCase) ||
             (profile.RayTracing && ray.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) ||
             (!profile.RayTracing && ray.Equals("Disabled", StringComparison.OrdinalIgnoreCase)));
        var ok = resolutionOK && presetOK && rayOK;
        return new Verification(ok,
            $"Requested: {targetRes}, {profile.QualityName}, RT {targetRay}\n" +
            $"Observed on result screen: {resolution ?? "NOT READ"}, {actualQuality ?? "NOT READ"}, RT {ray ?? "NOT READ"}\n" +
            $"Verification: {(ok ? "PASS" : "FAILED - FPS NOT ACCEPTED")}\n" +
            "If not recognized, review settings-screen.png and settings-ocr.txt.\n");
    }

    private static string? ValueBelow(OcrDocument ocr, string labelPattern, string valuePattern)
    {
        var label = new Regex(labelPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var value = new Regex(valuePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var item in ocr.Lines.OrderBy(l => l.Box.Y))
        {
            if (!label.IsMatch(item.Text)) continue;
            var onSameLine = value.Match(label.Replace(item.Text, ""));
            if (onSameLine.Success) return onSameLine.Value;
            var candidates = ocr.Lines.Where(x => x.Box.Y > item.Box.Y + 2 &&
                x.Box.Y - item.Box.Y < Math.Max(115, item.Box.Height * 4.5) &&
                Math.Abs(x.Box.X - item.Box.X) < Math.Max(95, item.Box.Width * .8))
                .OrderBy(x => x.Box.Y);
            foreach (var candidate in candidates)
            {
                var match = value.Match(candidate.Text);
                if (match.Success) return match.Value;
            }
        }
        // Some WinRT OCR builds combine title/value into one long text segment.
        var all = Regex.Match(ocr.Text, labelPattern + @"\s*[\r\n\s:]*\s*(" + valuePattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return all.Success ? all.Groups[1].Value : null;
    }
}
