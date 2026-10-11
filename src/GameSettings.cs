using System.Text;
using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

public sealed class GameSettings : IDisposable
{
    private readonly string _path;
    private readonly byte[] _original;
    private readonly Encoding _encoding;
    private bool _restored;

    public GameSettings(string root)
    {
        _path = Path.Combine(root, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        if (!File.Exists(_path)) throw new FileNotFoundException(
            "GameUserSettings.ini не найден. Один раз запустите Benchmark Tool вручную, завершите начальную настройку и закройте его. Затем запустите утилиту снова.", _path);
        _original = File.ReadAllBytes(_path);
        _encoding = _original.Length >= 2 && _original[0] == 0xFF && _original[1] == 0xFE
            ? Encoding.Unicode : new UTF8Encoding(false);
    }

    public string Apply(BenchmarkProfile profile)
    {
        var text = _encoding.GetString(_original).TrimStart('\uFEFF');
        // Change actual game-facing UI settings, not just the generic Unreal scalability group.
        var ui = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["QualityLevel"] = "6", // Custom. Explicitly chosen individual graphics values below.
            ["InsertFrame"] = "0",   // Disable generated frames.
            ["Rtx"] = profile.RayTracing ? "1" : "0",
            ["RtxLevel"] = profile.RayTracing ? profile.RayTracingLevel.ToString() : "0",
            ["LockFrameRate"] = "0", ["Vsync"] = "0",
            ["ViewDistance"] = profile.Quality.ToString(),
            ["AntiAliasing"] = profile.Quality.ToString(),
            ["PostProcessing"] = profile.Quality.ToString(),
            ["ShadowQuality"] = profile.Quality.ToString(),
            ["TextureQuality"] = profile.Quality.ToString(),
            ["FxQuality"] = profile.Quality.ToString(),
            ["MaterialQuality"] = profile.Quality.ToString(),
            ["VegetationQuality"] = profile.Quality.ToString(),
            ["GlobalIllumination"] = profile.Quality.ToString(),
            ["ReflectionQuality"] = profile.Quality.ToString()
        };
        foreach (var (key, value) in ui)
        {
            var escaped = Regex.Escape(key);
            text = Regex.Replace(text, $@"\(""{escaped}""\s*,\s*""[^""]*""\)",
                m => $"(\"{key}\", \"{value}\")", RegexOptions.IgnoreCase);
        }
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["ResolutionSizeX"] = profile.Width.ToString(), ["ResolutionSizeY"] = profile.Height.ToString(),
            ["LastUserConfirmedResolutionSizeX"] = profile.Width.ToString(),
            ["LastUserConfirmedResolutionSizeY"] = profile.Height.ToString(),
            ["FullscreenMode"] = "1", ["LastConfirmedFullscreenMode"] = "1",
            ["PreferredFullscreenMode"] = "1", ["bUseVSync"] = "False", ["bUseDynamicResolution"] = "False",
            ["FrameRateLimit"] = "0.000000"
        }) text = SetIni(text, "/Script/GSGameSettings.GSGameUserSettings", key, value);

        var level = profile.Quality.ToString();
        foreach (var quality in new[] { "ViewDistance", "AntiAliasing", "Shadow", "GlobalIllumination",
                     "Reflection", "PostProcess", "Texture", "Effects", "Foliage", "Shading" })
            text = SetIni(text, "ScalabilityGroups", $"sg.{quality}Quality", level);
        text = SetIni(text, "ScalabilityGroups", "sg.RayTracingQuality", profile.RayTracing ? profile.RayTracingLevel.ToString() : "0");
        text = SetIni(text, "ScalabilityGroups", "sg.ResolutionQuality", "100");
        text = SetIni(text, "RayTracing", "r.RayTracing.EnableInGame", profile.RayTracing ? "True" : "False");
        File.WriteAllText(_path, text, _encoding);
        return text;
    }

    public void Restore()
    {
        if (_restored) return;
        File.WriteAllBytes(_path, _original);
        _restored = true;
    }

    public void Dispose() => Restore();

    public static string SetIni(string input, string section, string key, string value)
    {
        var lines = Regex.Split(input, "\r\n|\n|\r").ToList();
        var inSection = false;
        var foundSection = false;
        var foundKey = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (inSection && !foundKey) { lines.Insert(i, key + "=" + value); foundKey = true; i++; }
                inSection = string.Equals(line[1..^1], section, StringComparison.OrdinalIgnoreCase);
                if (inSection) foundSection = true;
                continue;
            }
            if (inSection && line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                foundKey = true;
            }
        }
        if (!foundSection) { lines.Add("[" + section + "]"); lines.Add(key + "=" + value); }
        else if (!foundKey) lines.Add(key + "=" + value);
        return string.Join("\r\n", lines);
    }
}
