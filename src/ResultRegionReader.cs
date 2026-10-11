using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

/// <summary>
/// Reads numbers from isolated regions on the results page. In particular,
/// "Average" is rendered larger than the other FPS fields and its title is
/// sometimes omitted by full-page OCR. Find its value relative to "Maximum"
/// instead of relying on the Average label being recognized.
/// </summary>
public static class ResultRegionReader
{
    private sealed record MetricRegion(string Name, string Pattern, bool IsVram);
    private sealed record Candidate(string Name, Rectangle Rect);

    private static readonly MetricRegion[] Regions =
    [
        new("average", @"\b(?:Average|Avg\.?|Средн\w*)\b", false),
        new("maximum", @"\b(?:Maximum|Max\.?|Максим\w*)\b", false),
        new("minimum", @"\b(?:Minimum|Min\.?|Миним\w*)\b", false),
        new("low5th", @"\b(?:Low|Нижн\w*|Низк\w*)\b", false),
        new("vram", @"\b(?:VRAM|Video\s*Memory|Видеопамят\w*)\b", true)
    ];

    public static async Task<BenchmarkMetrics?> ReadAsync(
        ScreenFrame frame, OcrDocument fullPage, string outputFolder, CancellationToken ct)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in Regions)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = LocateNumberRegions(frame, fullPage, metric).ToList();
            foreach (var candidate in candidates)
            {
                // Enhanced processing is applied to Average as a last resort.
                // Never fabricate an unreadable value from Maximum/Low5th.
                var modes = metric.Name == "average" ? new[] { false, true } : new[] { false };
                foreach (var highContrast in modes)
                {
                    ct.ThrowIfCancellationRequested();
                    var suffix = highContrast ? "-contrast" : "";
                    var prefix = $"detail-{metric.Name}-{candidate.Name}{suffix}";
                    try
                    {
                        var imagePath = Path.Combine(outputFolder, prefix + ".png");
                        SaveEnlargedCrop(frame.OriginalScreenshot, candidate.Rect, imagePath, highContrast);
                        var detailed = await ScreenOcr.RecognizeImageAsync(imagePath, ct);
                        await File.WriteAllTextAsync(Path.Combine(outputFolder, prefix + ".txt"), detailed.Text, ct);
                        var value = ResultParser.ParseSingleValue(detailed, metric.IsVram);
                        if (!value.HasValue) continue;
                        values[metric.Name] = value.Value;
                        Console.WriteLine($"  Детальный OCR {metric.Name}: {value.Value.ToString(CultureInfo.InvariantCulture)} ({candidate.Name}{suffix})");
                        // Keep familiar filenames for troubleshooting older versions.
                        File.Copy(imagePath, Path.Combine(outputFolder, $"detail-{metric.Name}.png"), true);
                        await File.WriteAllTextAsync(Path.Combine(outputFolder, $"detail-{metric.Name}.txt"), detailed.Text, ct);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  OCR {metric.Name}/{candidate.Name}{suffix}: {ex.Message}");
                    }
                }
                if (values.ContainsKey(metric.Name)) break;
            }
            if (metric.Name == "average" && !values.ContainsKey("average"))
                Console.WriteLine("  Average не распознан. Сохранены detail-average-*.png и .txt для диагностики.");
        }
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "detail-values.json"),
            JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }), ct);
        return ResultParser.ParseWithRegions(fullPage, values);
    }

    private static IEnumerable<Candidate> LocateNumberRegions(
        ScreenFrame frame, OcrDocument ocr, MetricRegion metric)
    {
        var width = frame.ScreenRectangle.Width;
        var height = frame.ScreenRectangle.Height;
        var bounds = new Rectangle(0, 0, width, height);
        var used = new List<Rectangle>();
        var candidates = new List<Candidate>();

        void Add(string name, double x, double y, double w, double h)
        {
            var rectangle = Rectangle.Intersect(Rectangle.FromLTRB(
                (int)Math.Floor(x), (int)Math.Floor(y),
                (int)Math.Ceiling(x + w), (int)Math.Ceiling(y + h)), bounds);
            if (rectangle.Width < 60 || rectangle.Height < 25) return;
            if (used.Any(old => Math.Abs(old.X - rectangle.X) < 7 &&
                                Math.Abs(old.Y - rectangle.Y) < 7)) return;
            used.Add(rectangle);
            candidates.Add(new Candidate(name, rectangle));
        }

        OcrBox? Label(string pattern, bool fullLine = false)
        {
            foreach (var line in ocr.Lines)
            {
                if (!Regex.IsMatch(line.Text, pattern, RegexOptions.IgnoreCase)) continue;
                var word = line.Words.FirstOrDefault(w => Regex.IsMatch(w.Text, pattern, RegexOptions.IgnoreCase));
                return fullLine ? line.Box : (word?.Box ?? line.Box);
            }
            return null;
        }

        double Sx(double x) => x * frame.OcrToScreenScaleX;
        double Sy(double y) => y * frame.OcrToScreenScaleY;

        // Prefer a precise, layout-calibrated Average region. The former
        // "above Low 5th" heuristic occasionally captured 3 FPS as Average.
        // This must run before any OCR-label-derived candidate.
        if (metric.Name == "average")
            Add("layout-fallback", width * .030, height * .326,
                width * .145, height * .068);

        if (metric.Name == "average")
        {
            // The most reliable anchor on actual Wukong result screens:
            // "Maximum" starts about 80/1080 screen heights below Average's
            // large number, and the entire FPS field is ABOVE that label.
            var maxLabel = Label(@"\b(?:Maximum|Max\.?|Максим\w*)\b");
            if (maxLabel is not null && Sx(maxLabel.X) < width * .32 &&
                Sy(maxLabel.Y) > height * .36 && Sy(maxLabel.Y) < height * .49)
                Add("above-maximum", Sx(maxLabel.X) - width * .010,
                    Sy(maxLabel.Y) - height * .080,
                    width * .145, height * .069);

            var avgLabel = Label(metric.Pattern);
            if (avgLabel is not null && Sx(avgLabel.X) < width * .32 &&
                Sy(avgLabel.Y) > height * .22 && Sy(avgLabel.Y) < height * .38)
                Add("below-average-label", Sx(avgLabel.X) - width * .012,
                    Sy(avgLabel.Y + avgLabel.Height) + height * .007,
                    width * .145, height * .076);

        }
        else
        {
            var label = Label(metric.Pattern, metric.IsVram);
            if (label is not null)
            {
                var fontHeight = Sy(label.Height);
                if (fontHeight is >= 5 and <= 80)
                {
                    var x = Sx(label.X);
                    var y = Sy(label.Y + label.Height);
                    Add("under-label", x - 10, y + 2,
                        Math.Clamp(fontHeight * 11, 125, 185) + 10,
                        Math.Clamp(fontHeight * 3.9, 48, 67) - 2);
                }
            }
        }

        // Fallback normalized to the GAME WINDOW (not the desktop), calibrated
        // against both 1920x1080 and 1649x928 result screenshots. The old
        // Average fallback started too low, clipped the '4' and included
        // "Maximum" beneath it, making OCR systematically fail.
        var (rx, ry, rw, rh) = metric.Name switch
        {
            "average" => (.030, .326, .145, .068),
            "maximum" => (.030, .432, .120, .047),
            "minimum" => (.160, .432, .120, .047),
            "low5th" => (.030, .499, .135, .047),
            "vram" => (.030, .576, .135, .051),
            _ => (0.0, 0.0, 0.0, 0.0)
        };
        if (metric.Name != "average")
            Add("layout-fallback", rx * width, ry * height, rw * width, rh * height);
        return candidates;
    }

    // Separate from OCR: pure geometry checkable with an offline screenshot.
    // Returns a direct crop matching the layout fallback; never a numeric value.
    public static Rectangle AverageFallbackForTest(int width, int height) =>
        Rectangle.FromLTRB((int)(width * .030), (int)(height * .326),
            (int)(width * .175), (int)(height * .394));

    private static void SaveEnlargedCrop(string sourcePath, Rectangle crop, string destination, bool highContrast)
    {
        using var screenshot = new Bitmap(sourcePath);
        crop = Rectangle.Intersect(crop, new Rectangle(0, 0, screenshot.Width, screenshot.Height));
        if (crop.Width < 40 || crop.Height < 25)
            throw new InvalidDataException("Слишком маленькая область результата.");

        using var cropped = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(cropped))
            graphics.DrawImage(screenshot, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);

        if (highContrast)
        {
            // In this UI the numerals are light-grey on a dark textured
            // background. Binarization removes texture and strengthens the 4.
            for (var y = 0; y < cropped.Height; y++)
            for (var x = 0; x < cropped.Width; x++)
            {
                var pixel = cropped.GetPixel(x, y);
                var intensity = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                cropped.SetPixel(x, y, intensity >= 125 ? Color.Black : Color.White);
            }
        }

        const int scale = 4;
        using var large = new Bitmap(crop.Width * scale, crop.Height * scale, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(large))
        {
            graphics.InterpolationMode = highContrast
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(cropped, new Rectangle(0, 0, large.Width, large.Height));
        }
        large.Save(destination, ImageFormat.Png);
    }
}
