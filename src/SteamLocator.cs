using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongBenchmarkRunner;

public static class SteamLocator
{
    private const string AppId = "3132990";
    public static string Locate(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var root = File.Exists(overridePath) ? Path.GetDirectoryName(Path.GetFullPath(overridePath))! : Path.GetFullPath(overridePath);
            return Validate(root);
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamBase = (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string)
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        libraries.Add(steamBase);
        var libraryFile = Path.Combine(steamBase, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            var vdf = File.ReadAllText(libraryFile);
            foreach (Match match in Regex.Matches(vdf, "\"path\"\\s*\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase))
                libraries.Add(match.Groups["path"].Value.Replace("\\\\", "\\"));
        }
        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var contents = File.ReadAllText(manifest);
            var dir = Regex.Match(contents, "\"installdir\"\\s*\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase);
            var folder = dir.Success ? dir.Groups["name"].Value : "Black Myth Wukong Benchmark Tool";
            var full = Path.Combine(library, "steamapps", "common", folder);
            if (File.Exists(Path.Combine(full, "b1_benchmark.exe"))) return Validate(full);
        }
        throw new FileNotFoundException("Не найден Black Myth: Wukong Benchmark Tool (Steam App 3132990). Установите его из Steam или укажите --path \"...\\Black Myth Wukong Benchmark Tool\".");
    }

    private static string Validate(string root)
    {
        if (!File.Exists(Path.Combine(root, "b1_benchmark.exe")))
            throw new FileNotFoundException("Не найден b1_benchmark.exe: " + root);
        return root;
    }
}
