using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;

namespace WukongBenchmarkRunner;

public sealed record ScreenFrame(string OriginalScreenshot, string OcrScreenshot,
    Rectangle ScreenRectangle, double OcrToScreenScaleX, double OcrToScreenScaleY);

public static class ScreenOcr
{
    private static readonly Lazy<string> EmbeddedOcrScript = new(LoadOcrScript);

    private static string LoadOcrScript()
    {
        using var stream = typeof(ScreenOcr).Assembly.GetManifestResourceStream("WukongBenchmarkRunner.windows-ocr.ps1")
            ?? throw new InvalidDataException("В исполняемом файле отсутствует скрипт OCR.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static ScreenFrame Capture(Rectangle screenBounds, string folder, string name, bool fullWindow = false)
    {
        Directory.CreateDirectory(folder);
        var fullPath = Path.Combine(folder, $"{name}-screen.png");
        var ocrPath = Path.Combine(folder, $"{name}-ocr.png");
        using var screenshot = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(screenshot))
            graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size, CopyPixelOperation.SourceCopy);
        screenshot.Save(fullPath, ImageFormat.Png);

        // Results are on the left. Crop the rightmost part to increase OCR precision.
        // Menu actions also live on the left on the benchmark's main menu.
        var crop = new Rectangle(0, 0, fullWindow ? screenshot.Width : (int)(screenshot.Width * 0.75), screenshot.Height);
        // Upscale low-resolution captures; do not exceed the Windows OCR image limit.
        var ratio = Math.Min(2.5, Math.Min(1950.0 / crop.Width, 1950.0 / crop.Height));
        var width = Math.Max(1, (int)(crop.Width * ratio));
        var height = Math.Max(1, (int)(crop.Height * ratio));
        using var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(screenshot, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);
        }
        image.Save(ocrPath, ImageFormat.Png);
        return new(fullPath, ocrPath, screenBounds, (double)crop.Width / width, (double)crop.Height / height);
    }

    public static async Task<OcrDocument> RecognizeAsync(ScreenFrame frame, CancellationToken cancellation = default)
    {
        // The OCR script is embedded as a resource so the published EXE is truly standalone.
        // Keep param(...) as the first command inside the script block.
        return await RecognizeImageAsync(frame.OcrScreenshot, cancellation);
    }

    public static async Task<OcrDocument> RecognizeImageAsync(string imagePath, CancellationToken cancellation = default, string language = "en-US")
    {
        var quotedImagePath = imagePath.Replace("'", "''");
        var scriptBlock = "& {\n" + EmbeddedOcrScript.Value + "\n} -ImagePath '" + quotedImagePath + "' -Language '" + language + "'";
        var output = await PowerShell.RunScriptAsync(scriptBlock, cancellation);
        return JsonSerializer.Deserialize<OcrDocument>(output)
            ?? throw new InvalidDataException("OCR вернул пустой ответ.");
    }
}
