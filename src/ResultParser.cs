using System.Globalization;
using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

public static class ResultParser
{
    // Windows OCR can split "79 FPS" into two words or return "79FPS" as a single word.
    private static readonly Regex Number = new(@"^\s*(\d{1,4}(?:[.,]\d{1,2})?)\s*(?:FPS|GB|ГБ)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Different Windows installations can show a localized benchmark result screen even
    // when the game's -culture=en switch was supplied. The patterns are intentionally bounded.
    private const string Avg = @"\b(?:Average|Avg\.?|Средн\w*|Media|Moyenne|Promedio|Durchschnitt\w*)\b";
    private const string Max = @"\b(?:Maximum|Max\.?|Максим\w*|Massim\w*|Máxim\w*|Höchst\w*)\b";
    private const string Min = @"\b(?:Minimum|Min\.?|Миним\w*|Minim\w*|Mínim\w*)\b";
    private const string Low = @"(?:\bLow\s*5(?:th|%)?\b|\b5\s*%\s*Low\b|\bНижн\w*\b|\bНизк\w*\b|\bSotto\s*(?:il\s*)?5\s*%|\bBottom\s*5\s*%)";
    private const string Vram = @"\b(?:VRAM|Video\s*Memory|Видеопамят\w*|Uso\s*totale\s*VRAM)\b";

    public static bool LooksLikeResult(OcrDocument doc)
    {
        if (Regex.IsMatch(doc.Text,
            @"Benchmark\s+Results?|Результат\w*\s+(?:тест\w*|проверк\w*|производительност\w*)|" +
            @"Risultati\s+benchmark|Résultats?\s+(?:du\s+)?benchmark|Resultados?\s+(?:del?\s+)?benchmark|Benchmark[- ]Ergebnisse",
            RegexOptions.IgnoreCase)) return true;
        var labels = new[] { Avg, Max, Min, Low }.Count(pattern => Has(doc, pattern));
        return labels >= 3 && (Has(doc, Vram) || CountFpsNumbers(doc) >= 3);
    }

    public static BenchmarkMetrics? Parse(OcrDocument doc)
    {
        if (!LooksLikeResult(doc)) return null;
        var average = FindValue(doc, Avg);
        var maximum = FindValue(doc, Max);
        var minimum = FindValue(doc, Min);
        var low = FindValue(doc, Low, skip5InLabel: true);
        var vram = FindValue(doc, Vram);

        if (Valid(average, maximum, minimum, low))
            return new BenchmarkMetrics(average!.Value, maximum!.Value, minimum!.Value, low!.Value, vram);

        // If Windows recognizes the FPS numbers but misreads one of the labels,
        // use the known result layout only if all four explicit FPS values are present
        // and their ordering and magnitudes are consistent.
        var layout = ParseByLayout(doc);
        return layout is null ? null : layout with { VramGb = vram };
    }

    /// <summary>
    /// Merge independent value-only OCR results with the whole-screen OCR.
    /// A missing detail OCR reading never becomes a made-up zero.
    /// </summary>
    public static BenchmarkMetrics? ParseWithRegions(
        OcrDocument page, IReadOnlyDictionary<string, double> regions)
    {
        double? Get(string name, string label, bool skipFive = false)
            => regions.TryGetValue(name, out var value) ? value : FindValue(page, label, skipFive);
        var avg = Get("average", Avg);
        var max = Get("maximum", Max);
        var min = Get("minimum", Min);
        var low = Get("low5th", Low, true);
        var vram = Get("vram", Vram);
        if (Valid(avg, max, min, low))
            return new BenchmarkMetrics(avg!.Value, max!.Value, min!.Value, low!.Value, vram);
        return Parse(page);
    }

    /// <summary>
    /// OCR of a small crop containing one large FPS/VRAM value, NOT the whole result page.
    /// Supports split lines and typical Windows OCR glyph confusion at tiny FPS values.
    /// </summary>
    public static double? ParseSingleValue(OcrDocument crop, bool vram = false)
    {
        var text = Regex.Replace(crop.Text, @"\s+", " ").Trim();
        var units = vram ? @"(?:GB|ГБ)" : @"FPS";
        var matched = Regex.Matches(text,
            @"(?<![\w])([0-9]{1,4}(?:[.,][0-9]{1,2})?|[OoIiLl\|\]\[!])\s*" + units + @"\b",
            RegexOptions.IgnoreCase);
        if (matched.Count == 1)
        {
            var raw = matched[0].Groups[1].Value;
            var normalized = raw.ToUpperInvariant() switch
            {
                "O" => "0",  // round zero OCR'd as the letter O
                "I" or "L" or "|" or "]" or "[" or "!" => "1", // thin 1 OCR'd as a stroke or bracket
                _ => raw
            };
            return ReadNumber(normalized);
        }
        if (matched.Count > 1) return null; // ambiguous ROI: do not guess

        // When OCR omits the FPS unit, accept the single isolated numeric token
        // from the carefully cropped region, never from a full-page screenshot.
        var numbers = Regex.Matches(text, @"(?<![\w])([0-9]{1,4}(?:[.,][0-9]{1,2})?)(?![\w])");
        if (numbers.Count == 1 && !Regex.IsMatch(text,
            @"\b(?:Average|Maximum|Minimum|Low|VRAM|Benchmark|Version|Results)\b",
            RegexOptions.IgnoreCase))
            return ReadNumber(numbers[0].Groups[1].Value);
        return null;
    }

    private static bool Valid(double? average, double? max, double? min, double? low)
        => average is > 0 and < 1000 && max is > 0 and < 1000 &&
           min is >= 0 and < 1000 && low is >= 0 and < 1000 &&
           max >= average && average >= low && low >= min;

    private static bool Has(OcrDocument doc, string pattern)
        => Regex.IsMatch(doc.Text, pattern, RegexOptions.IgnoreCase);

    private static double? FindValue(OcrDocument doc, string label, bool skip5InLabel = false)
    {
        foreach (var line in doc.Lines.Where(l => Regex.IsMatch(l.Text, label, RegexOptions.IgnoreCase)))
        {
            var labelMatch = Regex.Match(line.Text, label, RegexOptions.IgnoreCase);
            var trailing = line.Text[(labelMatch.Index + labelMatch.Length)..];
            // Some OCR versions return "Average 79 FPS" on one line.
            var inline = Regex.Match(trailing, @"^\s*[:=\-]?\s*(\d{1,4}(?:[.,]\d{1,2})?)\s*FPS\b", RegexOptions.IgnoreCase);
            if (inline.Success && ReadNumber(inline.Groups[1].Value) is { } inlineValue) return inlineValue;

            var labelWord = line.Words.FirstOrDefault(w => Regex.IsMatch(w.Text, label, RegexOptions.IgnoreCase));
            // Low 5th, or a split Russian label: the first non-numeric label word is the anchor.
            labelWord ??= line.Words.FirstOrDefault(w => Regex.IsMatch(w.Text, @"[A-Za-zА-Яа-яÀ-ÿ]"));
            var anchor = labelWord?.Box ?? line.Box;
            if (anchor.Height <= 0) continue;
            var candidates = doc.Lines.SelectMany(l => l.Words)
                .Select(w => (word: w, value: ReadNumber(w.Text)))
                .Where(x => x.value.HasValue &&
                    x.word.Box.CenterY >= anchor.CenterY - anchor.Height * 0.45 &&
                    x.word.Box.CenterY <= anchor.CenterY + Math.Max(75, anchor.Height * 5.5) &&
                    Math.Abs(x.word.Box.X - anchor.X) < Math.Max(105, anchor.Height * 5.5) &&
                    !(skip5InLabel && x.value == 5 && Math.Abs(x.word.Box.CenterY - anchor.CenterY) < anchor.Height))
                .OrderBy(x => Math.Abs(x.word.Box.X - anchor.X) * 0.8 +
                    Math.Abs(x.word.Box.CenterY - (anchor.CenterY + anchor.Height * 1.8)))
                .ToList();
            if (candidates.Count > 0) return candidates[0].value;
        }

        // Text-only fallback when OCR positions are unavailable. Require explicit FPS units.
        var pattern = label + @"\s*[:=\-]?\s*(\d{1,4}(?:[.,]\d{1,2})?)\s*FPS\b";
        var textMatch = Regex.Match(doc.Text, pattern, RegexOptions.IgnoreCase);
        return textMatch.Success ? ReadNumber(textMatch.Groups[1].Value) : null;
    }

    private static double? ReadNumber(string word)
    {
        // Never treat the 5 in "Low 5th" or the axis marks as measured FPS.
        var match = Number.Match(word);
        if (!match.Success) return null;
        return double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static int CountFpsNumbers(OcrDocument doc)
        => FindFpsValues(doc).Count;

    private sealed record FpsToken(double Number, double X, double Y, double Height);

    private static List<FpsToken> FindFpsValues(OcrDocument doc)
    {
        var items = new List<FpsToken>();
        foreach (var line in doc.Lines)
        {
            // Require FPS explicitly on the same recognized line, not random numbers
            // in the FPS graph or graphics settings on the right.
            if (!Regex.IsMatch(line.Text, @"\bFPS\b", RegexOptions.IgnoreCase)) continue;
            foreach (var word in line.Words)
            {
                var n = ReadNumber(word.Text);
                if (n is >= 0 and < 1000 && word.Box.Height > 0)
                    items.Add(new(n.Value, word.Box.X, word.Box.CenterY, word.Box.Height));
            }
        }
        return items.OrderBy(x => x.Y).ThenBy(x => x.X).ToList();
    }

    private static BenchmarkMetrics? ParseByLayout(OcrDocument doc)
    {
        var tokens = FindFpsValues(doc);
        if (tokens.Count < 4) return null;
        // On the actual Wukong results page these are the first three FPS rows:
        // [Average], [Maximum | Minimum], [Low 5th].
        for (var i = 0; i <= tokens.Count - 4; i++)
        {
            var four = tokens.Skip(i).Take(4).ToArray();
            var tolerance = Math.Max(8.0, four.Average(t => t.Height) * 0.8);
            if (!(four[0].Y + tolerance < four[1].Y &&
                  Math.Abs(four[1].Y - four[2].Y) <= tolerance &&
                  four[2].Y + tolerance < four[3].Y &&
                  four[1].X + Math.Max(20.0, tolerance) < four[2].X)) continue;
            var avg = four[0].Number;
            var max = four[1].Number;
            var min = four[2].Number;
            var low = four[3].Number;
            if (Valid(avg, max, min, low)) return new(avg, max, min, low, null);
        }
        return null;
    }
}
