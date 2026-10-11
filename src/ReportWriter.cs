using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WukongBenchmarkRunner;

public static class ReportWriter
{
    /// <summary>Write each pass immediately, even if the other has not finished.</summary>
    public static void SavePass(BenchmarkRun pass, string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"),
            JsonSerializer.Serialize(pass, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        var metrics = pass.Metrics;
        var sb = new StringBuilder()
            .AppendLine($"{pass.Profile.Name} BENCHMARK")
            .AppendLine($"Finished: {pass.FinishedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Status: {(metrics is null ? "FAILED / NOT VERIFIED" : "COMPLETED / SETTINGS VERIFIED")}")
            .AppendLine($"Requested resolution: {pass.Profile.Width}x{pass.Profile.Height}")
            .AppendLine($"Requested quality: {pass.Profile.QualityName}")
            .AppendLine($"Requested ray tracing: {(pass.Profile.RayTracing ? "On" : "Off")}");
        if (metrics is not null)
        {
            sb.AppendLine($"Average FPS: {metrics.AverageFps.ToString(CultureInfo.InvariantCulture)}")
              .AppendLine($"Maximum FPS: {metrics.MaximumFps.ToString(CultureInfo.InvariantCulture)}")
              .AppendLine($"Minimum FPS: {metrics.MinimumFps.ToString(CultureInfo.InvariantCulture)}")
              .AppendLine($"Low 5th FPS: {metrics.Low5thFps.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            sb.AppendLine("FPS: NOT RECOGNIZED; see result-unparsed.png / last-ocr.txt")
              .AppendLine("Error: " + pass.Error);
        }
        File.WriteAllText(Path.Combine(folder, "report.txt"), sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"[{pass.Profile.Name}] Промежуточный отчёт: {Path.Combine(folder, "report.txt")}");
    }

    public static void Save(RunReport report, string output)
    {
        Directory.CreateDirectory(output);
        var jsonPath = Path.Combine(output, "result.json");
        var textPath = Path.Combine(output, "report.txt");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        var sb = new StringBuilder();
        sb.AppendLine("BLACK MYTH: WUKONG - AUTOMATED BENCHMARK")
          .AppendLine("=========================================")
          .AppendLine($"Started: {report.StartedAt:yyyy-MM-dd HH:mm:ss zzz}")
          .AppendLine($"CPU: {report.Hardware.Cpu}")
          .AppendLine($"GPU: {string.Join("; ", report.Hardware.Gpu)}")
          .AppendLine($"GPU driver: {report.Hardware.GraphicsDriver}")
          .AppendLine($"RAM: {report.Hardware.RamGb:F2} GB")
          .AppendLine($"OS: {report.Hardware.OperatingSystem}")
          .AppendLine($"Desktop: {report.Hardware.DisplayWidth}x{report.Hardware.DisplayHeight}")
          .AppendLine($"Tool: {report.GameDirectory}");
        foreach (var run in new[] { report.CpuTest, report.GpuTest })
        {
            var p = run.Profile;
            var m = run.Metrics;
            sb.AppendLine().AppendLine($"{p.Name.ToUpperInvariant()} TEST")
              .AppendLine(new string('-', 36))
              .AppendLine($"Status: {(m is null ? "FAILED / NOT VERIFIED" : "COMPLETED / SETTINGS VERIFIED")}")
              .AppendLine($"Resolution: {p.Width}x{p.Height}")
              .AppendLine($"Graphics: {p.QualityName} ({p.Quality})")
              .AppendLine($"Super-resolution scale: 100%")
              .AppendLine("Frame generation: Off")
              .AppendLine("V-Sync: Off | FPS cap: Off")
              .AppendLine($"Ray tracing: {(p.RayTracing ? "Very High" : "Off")}");
            if (m != null)
            {
                sb.AppendLine($"Average FPS: {m.AverageFps.ToString("F1", CultureInfo.InvariantCulture)}")
                  .AppendLine($"Maximum FPS: {m.MaximumFps.ToString("F1", CultureInfo.InvariantCulture)}")
                  .AppendLine($"Minimum FPS: {m.MinimumFps.ToString("F1", CultureInfo.InvariantCulture)}")
                  .AppendLine($"Low 5th FPS: {m.Low5thFps.ToString("F1", CultureInfo.InvariantCulture)}")
                  .AppendLine($"VRAM used: {(m.VramGb.HasValue ? m.VramGb.Value.ToString("F1", CultureInfo.InvariantCulture) + " GB" : "not recognized")}");
            }
            else
                sb.AppendLine("FPS: NOT RECOGNIZED (see screenshot and OCR)")
                  .AppendLine("OCR error: " + run.Error);
            if (!string.IsNullOrWhiteSpace(run.Screenshot))
                sb.AppendLine($"Screenshot: {Path.GetRelativePath(output, run.Screenshot)}");
            sb.AppendLine("Reason: " + p.Explanation);
        }
        sb.AppendLine().AppendLine("NOTE: CPU/GPU bottlenecks are intended, not confirmed by instrumentation. " + report.Warning);
        File.WriteAllText(textPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"\nФайлы результатов: {jsonPath}\n                    {textPath}");
    }
}
