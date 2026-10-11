using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

public sealed class BenchmarkRunner
{
    private readonly string _root;
    private readonly string _output;
    private readonly int _timeoutMinutes;

    public BenchmarkRunner(string root, string output, int timeoutMinutes)
    {
        _root = root;
        _output = output;
        _timeoutMinutes = timeoutMinutes;
    }

    public async Task<BenchmarkRun> RunAsync(BenchmarkProfile profile, CancellationToken ct)
    {
        var name = profile.Name.ToLowerInvariant();
        var runFolder = Path.Combine(_output, name);
        Directory.CreateDirectory(runFolder);
        Console.WriteLine($"\n[{profile.Name}] {profile.Width}x{profile.Height} / {profile.QualityName} / RT {(profile.RayTracing ? "Very High" : "Off")}");
        var launcherPath = Path.Combine(_root, "b1_benchmark.exe");
        var psi = new ProcessStartInfo(launcherPath)
        {
            WorkingDirectory = _root,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-culture=en"); // Unreal Engine culture switch; labels must be in English for OCR.
        var oldPids = GameProcessLifecycle.Snapshot(_root);
        if (oldPids.Count > 0)
            throw new IOException("Перед запуском " + profile.Name + " остался процесс старого прогона: " + string.Join(", ", oldPids));
        using var launcher = Process.Start(psi) ?? throw new IOException("Не удалось запустить " + launcherPath);
        Console.WriteLine($"  Новый запуск: launcher PID={launcher.Id}, профиль={profile.Name}");
        await File.WriteAllTextAsync(Path.Combine(runFolder, "launch.txt"),
            $"Profile: {profile.Name}\nStart: {DateTimeOffset.Now:O}\nLauncher PID: {launcher.Id}\nPrevious PIDs: {string.Join(",", oldPids)}\n", ct);
        Process? detected = null;
        int? gamePid = null;
        TimeSpan? noWindowSince = null;
        bool steamFallback = false;
        bool rerunPressed = false;
        bool sawScreenTransition = false;
        int settingsFailures = 0;
        var started = Stopwatch.StartNew();
        var lastClick = TimeSpan.FromSeconds(-100);
        var clicks = 0;
        var introClicks = 0;
        TimeSpan? firstVisibleWindow = null;
        var lastIntroClick = TimeSpan.FromSeconds(-100);
        var lastMenuWarning = TimeSpan.Zero;
        var confirmed = false;
        TimeSpan? firstIncompleteResult = null;
        var failedReads = 0;
        var focusedAttempts = 0;
        var deadline = TimeSpan.FromMinutes(_timeoutMinutes);
        var lastSteamDialogCheck = TimeSpan.FromSeconds(-100);
        try
        {
            while (started.Elapsed < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                // Steam may show its own "Launching application / Continue" modal before the
                // game appears. Only interact with a verified foreground Steam window.
                // Never interfere with the running benchmark or its results screen.
                if (clicks == 0 && started.Elapsed < TimeSpan.FromSeconds(135) &&
                    started.Elapsed - lastSteamDialogCheck >= TimeSpan.FromSeconds(7))
                {
                    lastSteamDialogCheck = started.Elapsed;
                    if (await SteamLaunchPrompt.TryContinueAsync(runFolder, ct))
                    {
                        Console.WriteLine("  Подтверждено окно запуска Steam (Продолжить).");
                        await File.AppendAllTextAsync(Path.Combine(runFolder, "launch.txt"),
                            "Steam launch confirmation: Continue clicked.\n", ct);
                    }
                }
                detected = WindowControl.LocateGameProcess(_root, launcher, oldPids);
                if (detected == null)
                {
                    if (gamePid.HasValue)
                    {
                        noWindowSince ??= started.Elapsed;
                        bool terminated;
                        try { using var previous = Process.GetProcessById(gamePid.Value); terminated = previous.HasExited; }
                        catch (ArgumentException) { terminated = true; }
                        if (terminated || started.Elapsed - noWindowSince.Value > TimeSpan.FromSeconds(45))
                            throw new IOException($"Окно игры PID={gamePid} исчезло до завершения {profile.Name}. Возможен вылет игры; FPS не сохраняем.");
                        continue; // Short, temporary loss of window during Unreal Engine loading.
                    }
                    if (!steamFallback && started.Elapsed > TimeSpan.FromSeconds(45))
                    {
                        steamFallback = true;
                        Console.WriteLine("  Прямой запуск не открыл окно за 45 секунд. Повторяю через Steam...");
                        await File.AppendAllTextAsync(Path.Combine(runFolder, "launch.txt"), "Steam fallback used.\n", ct);
                        using var steam = Process.Start(new ProcessStartInfo("steam://rungameid/3132990") { UseShellExecute = true });
                    }
                    if (started.Elapsed > TimeSpan.FromSeconds(130))
                        throw new IOException("Steam не создал новое окно игры за 130 секунд. GPU не запущен. См. launch.txt.");
                    if (started.Elapsed > TimeSpan.FromSeconds(60))
                        Console.WriteLine($"  Ожидание нового окна игры... {started.Elapsed:mm\\:ss}");
                    continue;
                }
                noWindowSince = null;
                if (!gamePid.HasValue)
                {
                    gamePid = detected.Id;
                    Console.WriteLine($"  Обнаружен экземпляр игры PID={gamePid}");
                    WindowControl.Focus(detected);
                    await Task.Delay(500, ct);
                    await File.AppendAllTextAsync(Path.Combine(runFolder, "launch.txt"), $"Detected game PID: {gamePid}\n", ct);
                }
                else if (detected.Id != gamePid.Value)
                {
                    // UE launchers may hand off to b1-Win64-Shipping before the menu.
                    if (clicks != 0)
                        throw new IOException("После начала измерения изменился PID игры. Не принимаю результат чужого окна.");
                    Console.WriteLine($"  Переход launcher -> game PID: {gamePid} -> {detected.Id}");
                    gamePid = detected.Id;
                }

                // Do not repeatedly run OCR while timed frames are being measured.
                // Allow a short post-click window for an optional confirmation screen.
                var timeSinceStartClick = started.Elapsed - lastClick;
                if (clicks > 0 && timeSinceStartClick > TimeSpan.FromSeconds(25) &&
                    timeSinceStartClick < TimeSpan.FromSeconds(75)) continue;
                try
                {
                    var bounds = WindowControl.Bounds(detected);
                    if (bounds.Width < 400 || bounds.Height < 300) continue;
                    firstVisibleWindow ??= started.Elapsed;
                    var frame = ScreenOcr.Capture(bounds, runFolder, "live");
                    OcrDocument ocr;
                    try { ocr = await ScreenOcr.RecognizeAsync(frame, ct); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Ошибка распознавания текста в Windows. См. снимок: " + frame.OriginalScreenshot + "\n" + ex.Message, ex);
                    }
                    await File.WriteAllTextAsync(Path.Combine(runFolder, "last-ocr.txt"), ocr.Text, ct);
                    if (ResultParser.LooksLikeResult(ocr))
                    {
                        if (clicks == 0)
                        {
                            // Results from a previous session can be shown without a new benchmark.
                            // The results page offers 'R Rerun'. Press this only ONCE.
                            if (!rerunPressed)
                            {
                                File.Copy(frame.OriginalScreenshot, Path.Combine(runFolder, "old-result-before-rerun.png"), true);
                                Console.WriteLine("  Открыт экран старых результатов, новый тест ещё не запускался. Нажимаю R (Rerun) один раз.");
                                WindowControl.PressKey(detected, 0x52); // R
                                rerunPressed = true;
                                clicks = 1;
                                lastClick = started.Elapsed;
                            }
                            continue;
                        }
                        if ((rerunPressed && !sawScreenTransition) || timeSinceStartClick < TimeSpan.FromSeconds(45))
                        {
                            Console.WriteLine("  Экран результатов виден слишком рано / без подтверждённого запуска теста. Прежние FPS игнорируются.");
                            continue;
                        }
                        var screenSettings = await ResultSettingsVerifier.CheckAsync(frame.OriginalScreenshot, profile, runFolder, ct);
                        if (!screenSettings.Verified)
                        {
                            settingsFailures++;
                            File.Copy(frame.OriginalScreenshot, Path.Combine(runFolder, "unverified-result.png"), true);
                            if (settingsFailures >= 2)
                                throw new InvalidDataException("Результат НЕ соответствует профилю " + profile.Name + ". " +
                                    screenSettings.Detail.Replace('\n', ' ') + " См. settings-screen.png и settings-ocr.txt");
                            Console.WriteLine("  Несовпадение фактических настроек. Повторяю проверку экрана... " +
                                screenSettings.Detail.Replace('\n', ' '));
                            continue;
                        }
                        var metrics = ResultParser.Parse(ocr);
                        // The overall image is OCR'd at reduced scale; on weak GPUs
                        // "1 FPS" and "0 FPS" often lose their small numeric glyph.
                        // Re-scan each number at 4x, beneath its detected label.
                        if (metrics is null && focusedAttempts < 2)
                        {
                            focusedAttempts++;
                            Console.WriteLine("  Общий OCR неполный. Проверяю увеличенные поля FPS...");
                            metrics = await ResultRegionReader.ReadAsync(frame, ocr, runFolder, ct);
                        }
                        if (metrics != null)
                        {
                            var finalScreen = Path.Combine(runFolder, "result.png");
                            File.Copy(frame.OriginalScreenshot, finalScreen, overwrite: true);
                            await File.WriteAllTextAsync(Path.Combine(runFolder, "result-ocr.json"),
                                JsonSerializer.Serialize(ocr, new JsonSerializerOptions { WriteIndented = true }), ct);
                            Console.WriteLine($"  Готово: Average {metrics.AverageFps}, Min {metrics.MinimumFps}, Max {metrics.MaximumFps}, Low 5th {metrics.Low5thFps} FPS");
                            return new(profile, metrics, DateTimeOffset.Now, finalScreen, ocr.Text);
                        }
                        // Keep the image and OCR for diagnosis, then retry: WinRT OCR is
                        // not deterministic on small font sizes / transitions / languages.
                        firstIncompleteResult ??= started.Elapsed;
                        failedReads++;
                        File.Copy(frame.OriginalScreenshot, Path.Combine(runFolder, "result-unparsed.png"), true);
                        await File.WriteAllTextAsync(Path.Combine(runFolder, "result-unparsed-ocr.json"),
                            JsonSerializer.Serialize(ocr, new JsonSerializerOptions { WriteIndented = true }), ct);
                        if (failedReads == 1 || failedReads % 3 == 0)
                            Console.WriteLine($"  Результаты видны, но OCR не прочитал все FPS. Повторная попытка {failedReads}...");
                        if (started.Elapsed - firstIncompleteResult.Value >= TimeSpan.FromSeconds(45))
                            throw new InvalidDataException(
                                "Итоговый экран виден, но после повторных попыток OCR показатели не распознаны. " +
                                "Пришлите файлы: " + Path.Combine(runFolder, "result-unparsed.png") +
                                " и " + Path.Combine(runFolder, "last-ocr.txt") +
                                ". Исходные данные сохранены, показатели FPS не выдуманы.");
                        continue;
                    }
                    if (clicks > 0 && timeSinceStartClick > TimeSpan.FromSeconds(7) &&
                        FindBenchmarkButton(ocr) == null && !IntroScreen.HasContinuePrompt(ocr.Text))
                        sawScreenTransition = true;
                    if (clicks > 0 && !confirmed && timeSinceStartClick < TimeSpan.FromSeconds(10))
                    {
                        var confirmation = FindConfirmationButton(ocr);
                        if (confirmation != null)
                        {
                            WindowControl.Focus(detected);
                            WindowControl.Click(frame, confirmation);
                            confirmed = true;
                            lastClick = started.Elapsed;
                            Console.WriteLine("  Подтверждён запуск бенчмарка.");
                            continue;
                        }
                    }
                    var button = FindBenchmarkButton(ocr);
                    var introPrompt = IntroScreen.HasContinuePrompt(ocr.Text);
                    if (IntroScreen.ShouldDismiss(
                        introPrompt, button != null, clicks > 0, introClicks,
                        started.Elapsed - firstVisibleWindow.Value,
                        started.Elapsed - lastIntroClick))
                    {
                        // This is NOT the benchmark button. It just opens the main
                        // menu from the game's initial "click anywhere" screen.
                        if (introClicks == 0)
                            File.Copy(frame.OriginalScreenshot,
                                Path.Combine(runFolder, "intro-before-click.png"), overwrite: true);
                        WindowControl.Focus(detected);
                        await Task.Delay(300, ct);
                        if (profile.Name == "GPU")
                            WindowControl.PressKey(detected, 0x20); // Space: avoid clicking Exit on second pass.
                        else
                            WindowControl.ClickRelative(frame, 0.85, 0.78); // Original, working CPU behavior.
                        introClicks++;
                        lastIntroClick = started.Elapsed;
                        Console.WriteLine($"  Закрыт стартовый экран нажатием в окне игры (попытка {introClicks}). Ожидаю меню Benchmark...");
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }
                    // If the continue prompt is still overlaid, do not click an OCR
                    // match from the main menu potentially visible behind it.
                    if (introPrompt && clicks == 0) continue;
                    if (button != null && clicks < 3 && started.Elapsed - lastClick > TimeSpan.FromSeconds(18))
                    {
                        WindowControl.Focus(detected);
                        await Task.Delay(250, ct);
                        WindowControl.Click(frame, button);
                        clicks++;
                        lastClick = started.Elapsed;
                        Console.WriteLine($"  Автоматически нажата кнопка запуска теста (попытка {clicks}).");
                    }
                    if (clicks == 0 && started.Elapsed > TimeSpan.FromSeconds(80) &&
                        started.Elapsed - lastMenuWarning > TimeSpan.FromSeconds(25))
                    {
                        lastMenuWarning = started.Elapsed;
                        Console.WriteLine("  Не вижу кнопку Benchmark. Проверьте первоначальную настройку и язык интерфейса.");
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is ArgumentException)
                {
                    Console.WriteLine("  Окно загружается: " + ex.Message);
                }
            }
            throw new TimeoutException($"Превышено {_timeoutMinutes} мин. Прогон {profile.Name} не завершён. Снимки и OCR оставлены в {runFolder}.");
        }
        finally
        {
            // Close this run before changing the configuration for the next run.
            try
            {
                if (detected != null && !detected.HasExited)
                {
                    detected.CloseMainWindow();
                    if (!detected.WaitForExit(8000)) detected.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) { Console.WriteLine("Предупреждение при закрытии игры: " + ex.Message); }
            try
            {
                if (!launcher.HasExited)
                {
                    launcher.CloseMainWindow();
                    if (!launcher.WaitForExit(5000)) launcher.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) { Console.WriteLine("Предупреждение при закрытии лаунчера: " + ex.Message); }
            try { await GameProcessLifecycle.WaitForAllExitedAsync(_root, TimeSpan.FromSeconds(35), CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine("  Предыдущий процесс мог не завершиться: " + ex.Message); }
            await Task.Delay(3000);
        }
    }

    private static OcrBox? FindConfirmationButton(OcrDocument ocr)
    {
        var labels = new[] { "Confirm", "Yes", "Start", "Start Benchmark", "Подтвердить", "Да" };
        foreach (var line in ocr.Lines)
            if (labels.Any(label => line.Text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)))
                return line.Box;
        return null;
    }

    private static OcrBox? FindBenchmarkButton(OcrDocument ocr)
    {
        // Intentionally strict: do not click headings, EULA, graphics settings or random controls.
        var labels = new[] { @"^Benchmark$", @"^Benchmark Test$", @"^Run Benchmark$",
                             @"^Начать тест$", @"^Тест производительности$", @"^Запустить тест$" };
        foreach (var line in ocr.Lines)
        {
            var normalized = Regex.Replace(line.Text.Trim(), @"\s+", " ");
            if (labels.Any(pattern => Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase))) return line.Box;
        }
        return null;
    }
}
