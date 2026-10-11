using System.Text.Json.Serialization;

namespace WukongBenchmarkRunner;

public sealed record AppOptions(
    string? GamePath,
    string OutputDirectory,
    int TimeoutMinutes,
    string GpuRayTracing,
    bool SelfTest);

public sealed record HardwareInfo(
    string Cpu, string[] Gpu, double RamGb, string OperatingSystem,
    string GraphicsDriver, int DisplayWidth, int DisplayHeight);

public sealed record BenchmarkProfile(
    string Name, string Explanation, int Width, int Height, int Quality,
    bool RayTracing, int RayTracingLevel = 3)
{
    public string QualityName => Quality switch { 0 => "Low", 1 => "Medium", 2 => "High", 3 => "Very High", 4 => "Cinematic", _ => "Custom" };
}

public sealed record BenchmarkMetrics(
    double AverageFps, double MaximumFps, double MinimumFps,
    double Low5thFps, double? VramGb);

public sealed record BenchmarkRun(
    BenchmarkProfile Profile, BenchmarkMetrics? Metrics,
    DateTimeOffset FinishedAt, string Screenshot, string RecognizedText, string? Error = null);

public sealed record RunReport(
    string Program, DateTimeOffset StartedAt, HardwareInfo Hardware,
    string GameDirectory, BenchmarkRun CpuTest, BenchmarkRun GpuTest,
    string Warning);

public sealed class OcrDocument
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("lines")] public List<OcrLine> Lines { get; set; } = [];
}

public sealed class OcrLine
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("box")] public OcrBox Box { get; set; } = new();
    [JsonPropertyName("words")] public List<OcrWord> Words { get; set; } = [];
}

public sealed class OcrWord
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("box")] public OcrBox Box { get; set; } = new();
}

public sealed class OcrBox
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
