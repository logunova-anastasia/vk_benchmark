using System.Windows.Forms;

namespace WukongBenchmarkRunner;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var options = Parse(args);
            if (options.SelfTest) return SelfTests.Run();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Приложение работает только на Windows 10/11.");
            var root = SteamLocator.Locate(options.GamePath);
            var output = Path.GetFullPath(options.OutputDirectory);
            Console.WriteLine("Black Myth: Wukong Benchmark Runner | Steam App 3132990");
            Console.WriteLine("Путь к игре: " + root);
            Console.WriteLine("Папка отчётов: " + output);
            Console.WriteLine("Не пользуйтесь мышью и клавиатурой во время двух тестов. Не сворачивайте игру.");
            var other = System.Diagnostics.Process.GetProcesses().Where(x =>
                x.ProcessName.Equals("b1_benchmark", StringComparison.OrdinalIgnoreCase) ||
                x.ProcessName.Equals("b1-Win64-Shipping", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (other.Length > 0)
                throw new InvalidOperationException("Сначала закройте уже запущенный Benchmark Tool / игру Wukong.");

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            var hardware = await HardwareProbe.ReadAsync();
            var cpu = new BenchmarkProfile("CPU",
                "720p, lowest graphics, no ray tracing; reduces GPU work. A CPU limit is not guaranteed on every machine.",
                Math.Min(1280, hardware.DisplayWidth), Math.Min(720, hardware.DisplayHeight), 0, false);
            if (options.GpuRayTracing == "on")
                throw new ArgumentException("--gpu-rt on недоступен в безопасном GPU-профиле; запустите без параметра.");
            // 4K + Cinematic + RT can terminate Benchmark Tool on 8 GB GPUs (e.g. RTX 2070).
            // Very High at up to 1440p with RT disabled is intentionally GPU-heavy,
            // yet avoids the extreme VRAM pressure of the original GPU profile.
            var gpu = new BenchmarkProfile("GPU",
                "Up to 2560x1440, Very High, RT off: heavier graphics than CPU profile without excessive 4K/RT VRAM usage.",
                Math.Min(2560, hardware.DisplayWidth), Math.Min(1440, hardware.DisplayHeight), 3, false);
            var startedAt = DateTimeOffset.Now;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "run-info.txt"),
                $"Started: {startedAt:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                $"Game: {root}{Environment.NewLine}" +
                "Progress: cpu/report.txt will appear after pass 1; gpu/report.txt after pass 2. " +
                "Top-level report.txt and result.json appear only after both passes." + Environment.NewLine);
            Console.WriteLine("Во время тестов окно консоли будет скрыто, чтобы не перекрывать FPS.");
            Console.WriteLine("Результат каждого прохода сохранится сразу в cpu/ или gpu/.");

            // Screen capture must never include this cmd.exe window.
            using var consoleGuard = new ConsoleWindowGuard();

            // Back up the original bytes, restore them on failure, cancellation, and normal completion.
            using var settings = new GameSettings(root);
            var runner = new BenchmarkRunner(root, output, options.TimeoutMinutes);
            // A failed screen read must not discard a completed pass or prevent
            // the second benchmark from running. Do not invent missing numbers.
            async Task<BenchmarkRun> RunBestEffortAsync(BenchmarkProfile profile)
            {
                settings.Apply(profile);
                try { return await runner.RunAsync(profile, cancellation.Token); }
                catch (Exception ex) when (ex is InvalidDataException or TimeoutException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Console.Error.WriteLine($"[{profile.Name}] Прогон НЕ ПОДТВЕРЖДЁН: {ex.Message}");
                    Console.Error.WriteLine("Показатели этого прогона не будут подставлены из другого теста.");
                    var folder = Path.Combine(output, profile.Name.ToLowerInvariant());
                    var screenshot = Path.Combine(folder, "result-unparsed.png");
                    if (!File.Exists(screenshot)) screenshot = Path.Combine(folder, "unverified-result.png");
                    var ocrFile = Path.Combine(folder, "last-ocr.txt");
                    var recognized = File.Exists(ocrFile) ? await File.ReadAllTextAsync(ocrFile) : "";
                    return new BenchmarkRun(profile, null, DateTimeOffset.Now,
                        File.Exists(screenshot) ? screenshot : "", recognized, ex.Message);
                }
            }
            var cpuResult = await RunBestEffortAsync(cpu);
            ReportWriter.SavePass(cpuResult, Path.Combine(output, "cpu"));
            // Do NOT change the INI while the CPU game instance is still around.
            // Steam needs time to switch the app from RUNNING to READY between launches.
            Console.WriteLine("[GPU] Жду завершения CPU-процесса и освобождения Steam...");
            BenchmarkRun gpuResult;
            try
            {
                await GameProcessLifecycle.WaitForAllExitedAsync(root, TimeSpan.FromSeconds(45), cancellation.Token);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation.Token);
                gpuResult = await RunBestEffortAsync(gpu);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("[GPU] Второй запуск заблокирован старым процессом: " + ex.Message);
                gpuResult = new BenchmarkRun(gpu, null, DateTimeOffset.Now, "", "", ex.Message);
            }
            ReportWriter.SavePass(gpuResult, Path.Combine(output, "gpu"));
            settings.Restore();
            var report = new RunReport("Black Myth: Wukong Benchmark Tool (Steam)", startedAt,
                hardware, root, cpuResult, gpuResult,
                "Results are OCR readings from the tool's own end screen; actual resource utilization was not measured. " +
                "Results accepted only after a new benchmark started AND screen graphics settings were independently verified. " +
                "Graphics settings are modified via GameUserSettings.ini.");
            ReportWriter.Save(report, output);
            if (cpuResult.Metrics == null || gpuResult.Metrics == null)
            {
                Console.Error.WriteLine("Отчёт сохранён, но результаты неполные. Пришлите result-unparsed.png и last-ocr.txt.");
                return 2;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено пользователем (настройки будут восстановлены).");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ОШИБКА: " + ex.Message);
            Console.Error.WriteLine("Подробности: " + ex);
            return 1;
        }
    }

    private static AppOptions Parse(string[] args)
    {
        string? path = null;
        var output = Path.Combine(Environment.CurrentDirectory, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var timeout = 12;
        var rt = "auto";
        var selfTest = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--path": path = Value(); break;
                case "--out": output = Value(); break;
                case "--timeout": timeout = int.Parse(Value()); break;
                case "--gpu-rt": rt = Value().ToLowerInvariant(); break;
                case "--self-test": selfTest = true; break;
                case "--help":
                    Console.WriteLine("Usage: WukongBenchmarkRunner.exe [--path GAME_ROOT] [--out DIR] [--timeout MINUTES] [--gpu-rt auto|off] [--self-test]");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException("Неизвестный аргумент: " + args[i]);
            }
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Нет значения после " + args[i - 1]);
        }
        if (timeout < 2 || timeout > 90) throw new ArgumentOutOfRangeException(nameof(timeout), "Допустимо 2–90 минут.");
        if (rt is not ("auto" or "on" or "off")) throw new ArgumentException("--gpu-rt = auto|on|off");
        return new(path, output, timeout, rt, selfTest);
    }
}
