using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace WukongBenchmarkRunner;

public static class HardwareProbe
{
    public static async Task<HardwareInfo> ReadAsync()
    {
        var monitor = Screen.PrimaryScreen ?? throw new InvalidOperationException("Не удалось получить параметры экрана.");
        var script = @"
$ErrorActionPreference='Stop'
$cpu=Get-CimInstance Win32_Processor | Select-Object -First 1
$gpus=@(Get-CimInstance Win32_VideoController | Where-Object { $_.Name } | Select-Object Name,DriverVersion)
$os=Get-CimInstance Win32_OperatingSystem
[pscustomobject]@{cpu=$cpu.Name;gpu=@($gpus | ForEach-Object {$_.Name});driver=(@($gpus | ForEach-Object {$_.DriverVersion}) -join '; ');ram=[math]::Round(([double]$os.TotalVisibleMemorySize/1MB),2);os=$os.Caption+' '+$os.Version} | ConvertTo-Json -Compress -Depth 4
";
        try
        {
            var json = await PowerShell.RunScriptAsync(script);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string key) => root.TryGetProperty(key, out var el) ? (el.GetString() ?? "неизвестно") : "неизвестно";
            string[] gpus = root.GetProperty("gpu").ValueKind switch
            {
                JsonValueKind.Array => root.GetProperty("gpu").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(),
                JsonValueKind.String => [root.GetProperty("gpu").GetString() ?? ""],
                _ => []
            };
            return new(Get("cpu"), gpus, root.GetProperty("ram").GetDouble(), Get("os"), Get("driver"), monitor.Bounds.Width, monitor.Bounds.Height);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Предупреждение: сведения WMI недоступны: " + ex.Message);
            return new("не определён", [], 0, Environment.OSVersion.ToString(), "не определён", monitor.Bounds.Width, monitor.Bounds.Height);
        }
    }
}
