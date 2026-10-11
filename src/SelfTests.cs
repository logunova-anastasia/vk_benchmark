namespace WukongBenchmarkRunner;

public static class SelfTests
{
    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("SELF TEST FAILED: " + message);
            checks++;
        }
        using (var resource = typeof(ScreenOcr).Assembly.GetManifestResourceStream("WukongBenchmarkRunner.windows-ocr.ps1"))
        {
            Assert(resource != null && resource.Length > 100, "Embedded OCR script is included in EXE");
        }
        var ini = "[/Script/GSGameSettings.GSGameUserSettings]\nResolutionSizeX=1920\n\n[ScalabilityGroups]\nsg.ViewDistanceQuality=2\n";
        var changed = GameSettings.SetIni(ini, "ScalabilityGroups", "sg.ViewDistanceQuality", "0");
        Assert(changed.Contains("sg.ViewDistanceQuality=0"), "Update existing INI entry");
        changed = GameSettings.SetIni(changed, "ScalabilityGroups", "sg.ShadowQuality", "4");
        Assert(changed.Contains("sg.ShadowQuality=4"), "Add key to section");
        Assert(changed.Contains("[/Script/GSGameSettings.GSGameUserSettings]"), "Preserve INI sections");

        Assert(IntroScreen.HasContinuePrompt("PRESS ANY BUTTON"), "Recognize English title prompt");
        Assert(IntroScreen.HasContinuePrompt("Нажмите любую кнопку"), "Recognize Russian title prompt");
        Assert(!IntroScreen.HasContinuePrompt("Benchmark Results\nAverage FPS"), "Do not confuse final results with title prompt");
        Assert(IntroScreen.ShouldDismiss(true, false, false, 0,
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(100)), "Dismiss title screen when OCR finds prompt");
        Assert(!IntroScreen.ShouldDismiss(false, true, false, 0,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100)), "Do not click title screen when benchmark menu is visible");
        Assert(!IntroScreen.ShouldDismiss(false, false, true, 0,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100)), "Do not dismiss title screen after benchmark started");
        Assert(!IntroScreen.ShouldDismiss(false, false, false, 0,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100)), "Do not click during first seconds of game load");
        Assert(IntroScreen.ShouldDismiss(false, false, false, 0,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(100)), "Fallback title click when OCR misses the prompt");
        Assert(!IntroScreen.ShouldDismiss(false, false, false, 1,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)), "Do not rapidly repeat title clicks");
        Assert(IntroScreen.ShouldDismiss(false, false, false, 1,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(25)), "Allow one delayed fallback retry");
        Assert(!IntroScreen.ShouldDismiss(true, false, false, 2,
            TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30)), "Never click initial screen more than twice");

        var ocr = new OcrDocument { Text = "Benchmark Results\nAverage\n100 FPS\nMaximum Minimum\n118 FPS 20 FPS\nLow 5th\n89 FPS\nTotal VRAM Used\n7.4 GB" };
        Add("Average", 50, 100, "100", 50, 128);
        Add("Maximum", 50, 190, "118", 50, 215);
        Add("Minimum", 350, 190, "20", 350, 215);
        Add("Low 5th", 50, 280, "89", 50, 307);
        Add("Total VRAM Used", 50, 370, "7.4", 50, 396);
        void Add(string label, double lx, double ly, string value, double vx, double vy)
        {
            var first = label.Split(' ')[0];
            ocr.Lines.Add(new OcrLine
            {
                Text = label, Box = new OcrBox { X = lx, Y = ly, Width = 120, Height = 16 },
                Words = [new OcrWord { Text = first, Box = new OcrBox { X = lx, Y = ly, Width = 70, Height = 16 } }]
            });
            ocr.Lines.Add(new OcrLine
            {
                Text = value + " FPS", Box = new OcrBox { X = vx, Y = vy, Width = 85, Height = 16 },
                Words = [new OcrWord { Text = value, Box = new OcrBox { X = vx, Y = vy, Width = 48, Height = 16 } }]
            });
        }
        Assert(ResultParser.LooksLikeResult(ocr), "Detect result screen");
        var result = ResultParser.Parse(ocr);
        Assert(result is { AverageFps: 100, MaximumFps: 118, MinimumFps: 20, Low5thFps: 89, VramGb: 7.4 },
            "Parse five metrics including shared-row maximum/minimum and decimal VRAM");
        // A localized result page should still be parsed by label + box positioning.
        var italian = new OcrDocument { Text = "Risultati benchmark\nMedia\n79 FPS\nMassimi Minimi\n95 FPS 68 FPS\nSotto il 5%\n70 FPS\nVRAM 9 GB" };
        AddLocalized(italian, "Media", 50, 90, "79", 50, 120);
        AddLocalized(italian, "Massimi", 50, 180, "95", 50, 210);
        AddLocalized(italian, "Minimi", 350, 180, "68", 350, 210);
        AddLocalized(italian, "Sotto il 5%", 50, 280, "70", 50, 310);
        Assert(ResultParser.LooksLikeResult(italian), "Detect localized results");
        var itResult = ResultParser.Parse(italian);
        Assert(itResult is { AverageFps: 79, MaximumFps: 95, MinimumFps: 68, Low5thFps: 70 }, "Parse Italian results");

        // Windows OCR can recognize the FPS values but misread the labels entirely.
        var layout = new OcrDocument { Text = "Benchmark Results\n79 FPS\n95 FPS\n68 FPS\n70 FPS" };
        AddFps(layout, "79", 50, 120);
        AddFps(layout, "95", 50, 210);
        AddFps(layout, "68", 350, 210);
        AddFps(layout, "70", 50, 300);
        var layoutResult = ResultParser.Parse(layout);
        Assert(layoutResult is { AverageFps: 79, MaximumFps: 95, MinimumFps: 68, Low5thFps: 70 },
            "Fall back to validated four-FPS result layout");
        // Deliberately contradictory FPS values must be rejected, not fabricated.
        var invalid = new OcrDocument { Text = "Benchmark Results\n10 FPS\n20 FPS\n60 FPS\n30 FPS" };
        AddFps(invalid, "10", 50, 120);
        AddFps(invalid, "20", 50, 210);
        AddFps(invalid, "60", 350, 210);
        AddFps(invalid, "30", 50, 300);
        Assert(ResultParser.Parse(invalid) == null, "Reject inconsistent FPS values");
        static void AddFps(OcrDocument doc, string value, double x, double y)
        {
            doc.Lines.Add(new OcrLine
            {
                Text = value + " FPS", Box = new OcrBox { X = x, Y = y, Width = 85, Height = 16 },
                Words = [new OcrWord { Text = value, Box = new OcrBox { X = x, Y = y, Width = 44, Height = 16 } }]
            });
        }
        static void AddLocalized(OcrDocument doc, string label, double lx, double ly,
            string value, double vx, double vy)
        {
            doc.Lines.Add(new OcrLine
            {
                Text = label, Box = new OcrBox { X = lx, Y = ly, Width = 120, Height = 16 },
                Words = [new OcrWord { Text = label.Split(' ')[0], Box = new OcrBox { X = lx, Y = ly, Width = 72, Height = 16 } }]
            });
            AddFps(doc, value, vx, vy);
        }
        // Real user result screenshot: 1 average, 2 maximum, 0 minimum,
        // 0 low fifth, 0 VRAM. Zero readings ARE valid numeric results.
        var lowFps = new OcrDocument { Text = "Benchmark Results\nAverage\n1 FPS\nMaximum Minimum\n2 FPS 0 FPS\nLow 5th\n0 FPS\nTotal VRAM Used\n0 GB" };
        AddSmall(lowFps, "Average", 50, 190, "1", 50, 224);
        AddSmall(lowFps, "Maximum", 50, 280, "2", 50, 315);
        AddSmall(lowFps, "Minimum", 285, 280, "0", 285, 315);
        AddSmall(lowFps, "Low 5th", 50, 365, "0", 50, 402);
        var tiny = ResultParser.Parse(lowFps);
        Assert(tiny is { AverageFps: 1, MaximumFps: 2, MinimumFps: 0, Low5thFps: 0 },
            "Accept actual 1/2/0/0 screenshot metrics");
        static void AddSmall(OcrDocument target, string title, double x, double y, string fps, double nx, double ny)
        {
            target.Lines.Add(new OcrLine { Text = title,
                Box = new OcrBox { X = x, Y = y, Height = 17, Width = 120 },
                Words = [new OcrWord { Text = title.Split(' ')[0],
                    Box = new OcrBox { X = x, Y = y, Height = 17, Width = 85 } }] });
            target.Lines.Add(new OcrLine { Text = fps + " FPS",
                Box = new OcrBox { X = nx, Y = ny, Height = 24, Width = 100 },
                Words = [new OcrWord { Text = fps,
                    Box = new OcrBox { X = nx, Y = ny, Height = 24, Width = 25 } }] });
        }
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "1\nFPS" }) == 1,
            "Read a number and FPS on separate OCR lines");
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "] FPS" }) == 1,
            "Read large 1 FPS OCR'd as right bracket");
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "| FPS" }) == 1,
            "Read large 1 FPS OCR'd as vertical bar");
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "O FPS" }) == 0,
            "Read an isolated zero OCR'd as O before FPS");
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "0" }) == 0,
            "Accept a zero from a clean, isolated numeric crop");
        Assert(ResultParser.ParseSingleValue(new OcrDocument { Text = "5 0" }) == null,
            "Reject ambiguous crops with two numeric candidates");
        var notDetectedByMain = new OcrDocument { Text = "Benchmark Results" };
        var detailed = new Dictionary<string, double> {
            ["average"] = 1, ["maximum"] = 2, ["minimum"] = 0, ["low5th"] = 0, ["vram"] = 0
        };
        var restored = ResultParser.ParseWithRegions(notDetectedByMain, detailed);
        Assert(restored is { AverageFps: 1, MaximumFps: 2, MinimumFps: 0, Low5thFps: 0, VramGb: 0 },
            "Read complete results from independent metric regions");

        // Regression: user screenshot (2026-10-08) contains 4 / 6 / 1 / 3 FPS.
        // Windows OCR often misses the large Average numeral while correctly
        // reading the other three fields. We must use an actual OCR reading
        // from the average-specific region rather than inferring an FPS.
        var partialPage = new OcrDocument {
            Text = "Benchmark Results\nMaximum\n6 FPS\nMinimum\n1 FPS\nLow 5th\n3 FPS"
        };
        var fourFps = new Dictionary<string, double> {
            ["average"] = 4, ["maximum"] = 6,
            ["minimum"] = 1, ["low5th"] = 3, ["vram"] = 0
        };
        Assert(ResultParser.ParseWithRegions(partialPage, fourFps) is {
            AverageFps: 4, MaximumFps: 6, MinimumFps: 1, Low5thFps: 3
        }, "Parse actual 4/6/1/3 result when Average came from focused OCR");

        foreach (var (w, h) in new[] { (1920, 1080), (1649, 928) })
        {
            var averageCrop = ResultRegionReader.AverageFallbackForTest(w, h);
            // The digit is around 34-37% of screen height, whereas the
            // Maximum label is around 40%: never clip either end of the 4
            // or accidentally include the next row heading.
            Assert(averageCrop.Top <= h * .342 && averageCrop.Bottom >= h * .372 &&
                   averageCrop.Bottom < h * .4,
                   $"Average crop is isolated at {w}x{h}");
        }

        // An actual 720p/Low/RT Off screenshot must NOT validate a GPU profile,
        // even when its FPS numbers are valid. Regression: fake 43 FPS GPU result.
        var screenSettings = new OcrDocument { Text = "Display Resolution\n1280 x 720\nGraphics Preset\nLow\nFull Ray Tracing\nOff" };
        void AddSetting(string title, string value, double x, double y)
        {
            screenSettings.Lines.Add(new OcrLine {
                Text = title, Box = new OcrBox { X = x, Y = y, Width = 150, Height = 20 } });
            screenSettings.Lines.Add(new OcrLine {
                Text = value, Box = new OcrBox { X = x, Y = y + 36, Width = 120, Height = 20 } });
        }
        AddSetting("Display Resolution", "1280 x 720", 10, 30);
        AddSetting("Graphics Preset", "Low", 10, 130);
        AddSetting("Full Ray Tracing", "Off", 350, 130);
        var cpuProfile = new BenchmarkProfile("CPU", "", 1280, 720, 0, false);
        var gpuProfile = new BenchmarkProfile("GPU", "", 2560, 1440, 3, false);
        Assert(ResultSettingsVerifier.Check(screenSettings, cpuProfile).Verified,
            "Verify matching CPU settings from end-screen OCR");
        Assert(!ResultSettingsVerifier.Check(screenSettings, gpuProfile).Verified,
            "Reject CPU results mislabelled as GPU results");
        var missingSettings = new OcrDocument { Text = "Benchmark Results\nAverage 43 FPS" };
        Assert(!ResultSettingsVerifier.Check(missingSettings, cpuProfile).Verified,
            "No result may be verified without observable graphics settings");

        // Regression: Steam's Russian "custom launch parameters" modal can have
        // Cyrillic text that Windows OCR cannot read without a language pack.
        // Detect the rendered blue Continue + grey Cancel pair instead.
        using (var steamFrame = new System.Drawing.Bitmap(1045, 547))
        using (var graphics = System.Drawing.Graphics.FromImage(steamFrame))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(37, 40, 46));
            using (var blueBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 145, 229)))
            using (var grayBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(61, 68, 80)))
            {
                graphics.FillRectangle(blueBrush, 578, 427, 180, 48);
                graphics.FillRectangle(grayBrush, 779, 427, 180, 48);
            }
            var target = SteamLaunchPrompt.FindSteamContinueByAppearance(steamFrame);
            Assert(target.HasValue && Math.Abs(target.Value.X - 668) < 5 &&
                Math.Abs(target.Value.Y - 450) < 5,
                "Steam Russian confirmation: identify blue Continue button without OCR");
            graphics.FillRectangle(System.Drawing.Brushes.Black, 779, 427, 180, 48);
            Assert(SteamLaunchPrompt.FindSteamContinueByAppearance(steamFrame) == null,
                "Never click a blue button without grey Cancel beside it");
        }

        // A 180x48 Steam button rendered inside a 4K-sized Steam window MUST
        // still be recognized (the previous implementation incorrectly required
        // widths over 268px and heights over 86px).
        using (var largeSteamWindow = new System.Drawing.Bitmap(3840, 2160))
        using (var graphics = System.Drawing.Graphics.FromImage(largeSteamWindow))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(37, 40, 46));
            using var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 145, 229));
            using var gray = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(61, 68, 80));
            graphics.FillRectangle(blue, 1678, 1050, 180, 48);
            graphics.FillRectangle(gray, 1879, 1050, 180, 48);
            var found = SteamLaunchPrompt.FindSteamContinueByAppearance(largeSteamWindow);
            Assert(found.HasValue && Math.Abs(found.Value.X - 1768) < 5 &&
                Math.Abs(found.Value.Y - 1074) < 5,
                "Find the real-sized Steam Continue button within a 4K Steam window");
        }

        var steamDialogRu = new OcrDocument { Text = "Steam\nПриложение запускается\nПродолжить" };
        steamDialogRu.Lines.Add(new OcrLine { Text = "Продолжить",
            Box = new OcrBox { X = 330, Y = 300, Width = 110, Height = 28 } });
        Assert(SteamLaunchPrompt.FindContinueButton(steamDialogRu) is { X: 330 },
            "Russian Steam launching dialog: detect Continue button");
        var steamDialogEn = new OcrDocument { Text = "Launching Black Myth: Wukong Benchmark Tool\nCancel Continue" };
        steamDialogEn.Lines.Add(new OcrLine { Text = "Cancel Continue",
            Words = [new OcrWord { Text = "Cancel", Box = new OcrBox { X = 10 } },
                     new OcrWord { Text = "Continue", Box = new OcrBox { X = 80 } }] });
        Assert(SteamLaunchPrompt.FindContinueButton(steamDialogEn) is { X: 80 },
            "English Steam dialog: locate Continue when OCR joins buttons");
        Assert(SteamLaunchPrompt.FindContinueButton(new OcrDocument {
            Text = "Continue\nSteam Store", Lines = steamDialogRu.Lines
        }) == null, "Never click Continue without a verified launch prompt");
        Assert(SteamLaunchPrompt.FindContinueButton(new OcrDocument {
            Text = "Launching game\nCancel", Lines = []
        }) == null, "Never click Steam without Continue button");

        Console.WriteLine($"SELF TESTS PASSED: {checks} checks");
        return 0;
    }
}
