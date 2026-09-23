using PiPlay.Theme;
using Xunit.Abstractions;

namespace PiPlay.Tests;

/// <summary>
/// Layer 2 (Logic) — a RUNNABLE WCAG contrast report, not just a gate. It reuses the canonical
/// <see cref="Wcag.ContrastRatio"/> formula (same assembly/namespace, no InternalsVisibleTo) so the
/// ratios it prints are the EXACT ratios the catalog/markup gates enforce — never a reimplementation.
///
/// During theme design we repeatedly hand-compute contrast for candidate hex pairs: the base palette
/// AND the Phase-B *derived* tokens (AccentPressed/AccentMuted — see CON-1 in
/// docs/Theme_Preset_Differences.md), where a wrong by-hand number emits a false
/// "WCAG-safe" verdict. Paste a candidate pair into the [InlineData] rows below, set its floor, then:
///
///   dotnet test PiPlay.sln --filter "FullyQualifiedName~ContrastReportTests" --logger "console;verbosity=detailed"
///
/// Each row prints "label: fg on bg = R.RR:1 (floor F.F)" and asserts ratio &gt;= floor, so the same run
/// both reports the numbers and fails if a candidate breaches its floor. (Without the --logger flag the
/// gate still runs, but xUnit suppresses ITestOutputHelper output on a passing test.)
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class ContrastReportTests
{
    private readonly ITestOutputHelper _out;

    public ContrastReportTests(ITestOutputHelper output) => _out = output;

    [Theory]
    // --- Base pairs already proven by the shipping gates (kept so the report mirrors them) ---
    [InlineData("#FF06141A", "#E45D75", "OnDanger dark text on Danger (sharp/soft-glass rose)", 4.5)]
    [InlineData("#FF06141A", "#E8564C", "OnDanger dark text on Danger (minimal warm)", 4.5)]
    [InlineData("#FF06141A", "#2BAED0", "dark button text on accent cyan (catalog default)", 4.5)]
    [InlineData("#FF06141A", "#4A8FAB", "dark button text on accent steel (dimmest chip)", 4.5)]
    // --- CON-1 candidate rows (Phase B): paste DERIVED tokens here to validate them BEFORE they ship.
    //     The Phase-B mixes (ThemeAccentProfile) do not exist in src yet, so these stay COMMENTED — a
    //     live [InlineData] would red CI before Phase B lands the corrected mix. The hexes below are
    //     ILLUSTRATIVE (recompute from the real mix when Phase B exists); per the review they FAIL the
    //     4.5 floor today, which is exactly the CON-1 breach to fix:
    //   [InlineData("#FF06141A", "<AccentPressed-steel>", "AccentPressed steel under dark text (CON-1 ~3.82)", 4.5)]
    //   [InlineData("#FF06141A", "<AccentMuted-steel>",   "AccentMuted steel under dark text (CON-1 ~1.98)", 4.5)]
    public void Contrast_report_for_candidate_pairs(string fg, string bg, string label, double floor)
    {
        var ratio = Wcag.ContrastRatio(fg, bg);
        _out.WriteLine($"{label}: {fg} on {bg} = {ratio:F2}:1 (floor {floor:F1})");
        Assert.True(ratio >= floor, $"{label}: {fg} on {bg} = {ratio:F2}:1, below required {floor:F1}:1.");
    }

    /// <summary>
    /// PIN TEST — the report must AGREE with the real shipping gates, so it can never silently diverge
    /// from the contrast the app actually enforces. There is exactly one formula (Wcag.cs):
    /// (a) reproduce a published, third-party-checkable reference value to lock the formula, and
    /// (b) recompute — from the LIVE catalog — the worst dark-text-on-accent pair the catalog gate
    /// asserts (steel, the dimmest accent), proving this file sees the same data the gate sees. If the
    /// formula or the catalog's steel value drifts, this fails before any candidate row is trusted.
    /// </summary>
    [Fact]
    public void Report_agrees_with_the_shipping_gates()
    {
        // (a) Published reference: white #FFFFFF on the sharp/soft-glass rose #E45D75 is 3.43:1 (the
        //     value that put white below the 4.5 text floor and moved DangerButton to OnDanger).
        //     Tolerance < 0.005 = the published 2-dp precision, and avoids a banker's-rounding
        //     dependency on the exact tie.
        var rose = Wcag.ContrastRatio("#FFFFFF", "#E45D75");
        Assert.True(System.Math.Abs(rose - 3.43) < 0.005,
            $"published reference white-on-#E45D75 should be 3.43:1, got {rose:F4}:1.");

        // (b) Steel is the dimmest offered accent. ThemeCatalogTests.Preset_palettes_meet_contrast_minimums
        //     asserts Wcag.ContrastRatio("#FF06141A", accent.HexColor) >= 4.5 for every accent. Recompute the
        //     steel pair here from the live catalog and assert the same floor — same path, same verdict.
        var steel = ThemeCatalog.AccentOptions.Single(o => o.Key == "steel").HexColor;
        var ratio = Wcag.ContrastRatio("#FF06141A", steel);
        Assert.True(ratio >= 4.5,
            $"pin: dark text on accent steel ({steel}) = {ratio:F2}:1 — this report disagrees with the live catalog gate.");
    }
}
