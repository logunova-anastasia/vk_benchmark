using System.Diagnostics;

namespace WukongBenchmarkRunner;

/// <summary>Work only with processes belonging to this specific Steam game install.</summary>
public static class GameProcessLifecycle
{
    public static IReadOnlyList<Process> Find(string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsGameName(process.ProcessName) || process.HasExited)
                {
                    process.Dispose();
                    continue;
                }
                var path = process.MainModule?.FileName;
                if (path is not null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    found.Add(process);
                else process.Dispose();
            }
            catch { process.Dispose(); }
        }
        return found;
    }

    private static bool IsGameName(string name) =>
        name.Equals("b1_benchmark", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("b1-Win64-Shipping", StringComparison.OrdinalIgnoreCase);

    public static async Task WaitForAllExitedAsync(string root, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var live = Find(root);
            var info = string.Join(", ", live.Select(p => $"{p.ProcessName}:{p.Id}"));
            foreach (var p in live) p.Dispose();
            if (live.Count == 0) return;
            if ((int)sw.Elapsed.TotalSeconds % 10 < 2)
                Console.WriteLine("  Ожидаю завершения предыдущего процесса: " + info);
            await Task.Delay(2000, ct);
        }
        throw new IOException("Предыдущий экземпляр игры не закрылся; повторный прогон не запускается, чтобы не захватить старые FPS.");
    }

    public static HashSet<int> Snapshot(string root)
    {
        var game = Find(root);
        var pids = game.Select(p => p.Id).ToHashSet();
        foreach (var p in game) p.Dispose();
        return pids;
    }
}
