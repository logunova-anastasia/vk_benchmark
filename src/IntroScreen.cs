using System.Text.RegularExpressions;

namespace WukongBenchmarkRunner;

/// <summary>
/// Decides when it is safe to dismiss the game's "press any button" title screen.
/// All state is passed in so this decision can be tested without a live game.
/// </summary>
public static class IntroScreen
{
    private static readonly Regex ContinuePrompt = new(
        @"(?:press\s+(?:any\s+)?(?:key|button)|click\s+(?:anywhere|to\s+continue|to\s+start)|" +
        @"tap\s+(?:anywhere|to\s+continue)|press\s+to\s+start|" +
        @"нажмит\w*\s+(?:люб\w*\s+)?(?:кнопк\w*|клавиш\w*|чтобы\s+продолжить)|" +
        @"щёлкнит\w*\s+(?:в\s+люб\w*\s+месте|для\s+продолжения)|" +
        @"按任意键|点击任意位置)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool HasContinuePrompt(string ocrText) => ContinuePrompt.IsMatch(ocrText);

    public static bool ShouldDismiss(
        bool promptVisible,
        bool benchmarkButtonVisible,
        bool benchmarkAlreadyStarted,
        int previousIntroClicks,
        TimeSpan visibleFor,
        TimeSpan sinceLastIntroClick)
    {
        // Never dismiss anything after starting the benchmark or once the actual
        // menu is visible. Two attempts maximum; no infinite blind-click loop.
        if (benchmarkAlreadyStarted || previousIntroClicks >= 2) return false;
        if (promptVisible)
            return visibleFor >= TimeSpan.FromSeconds(3) &&
                   (previousIntroClicks == 0 || sinceLastIntroClick >= TimeSpan.FromSeconds(8));
        if (benchmarkButtonVisible) return false;

        // OCR may not recognize the initial title prompt (fonts/languages differ).
        // Give the game time to load before clicking in an unused right-side area.
        if (previousIntroClicks == 0) return visibleFor >= TimeSpan.FromSeconds(18);
        return visibleFor >= TimeSpan.FromSeconds(55) &&
               sinceLastIntroClick >= TimeSpan.FromSeconds(20);
    }
}