using Microsoft.Web.WebView2.Core;

namespace PiPlay.Services;

/// <summary>
/// Privacy actions (spec 19, Phase 2). Owns the user-facing wording for both actions as
/// constants so "worded separately" (REQ-PRIVACY-02) and the login-kept / signed-out promises
/// are regression-testable, plus the browsing-data scope and a thin clear adapter. The Settings
/// window binds its visible text to these constants; MainWindow performs the work.
/// </summary>
public static class PrivacyService
{
    /// <summary>Clearing AllProfile (cookies + cache + site storage) signs the user out of YouTube.</summary>
    public const CoreWebView2BrowsingDataKinds ClearKinds = CoreWebView2BrowsingDataKinds.AllProfile;

    // --- Reset app state (REQ-PRIVACY-01) — keeps the YouTube session ---
    public const string ResetActionLabel = "Reset app state";
    public const string ResetDescription =
        "Clears PiPlay settings, saved profiles, and window placement. You'll stay signed in to YouTube.";
    public const string ResetConfirmTitle = "Reset app state?";
    public const string ResetConfirmBody =
        "This clears PiPlay settings, saved profiles, and window placement. Saved profiles can't be recovered." +
        "\n\nYou'll stay signed in to YouTube.";
    public const string ResetConfirmButton = "Reset app state";
    /// <summary>The reset discards saved profiles for good: destructive styling (polish review 2026-09-10 F-8).</summary>
    public const bool ResetConfirmDanger = true;
    public const string ResetDoneTitle = "App state reset";
    public const string ResetDoneBody = "PiPlay settings were reset. You're still signed in to YouTube.";

    // --- Clear browser data (REQ-PRIVACY-02) — separate, confirmed, signs the user out ---
    public const string ClearActionLabel = "Clear browser data";
    public const string ClearDescription =
        "Signs you out of YouTube and clears PiPlay's browsing data — cookies, cache, and site data. " +
        "You'll need to sign in again.";
    public const string ClearConfirmTitle = "Clear browser data?";
    public const string ClearConfirmBody =
        "This signs you out of YouTube and clears PiPlay's browsing data — cookies, cache, and site data." +
        "\n\nAn open Video Popout closes first. You'll need to sign in again next time.";
    public const string ClearConfirmButton = "Clear browser data";
    public const string ClearDoneTitle = "Browser data cleared";
    public const string ClearDoneBody = "Browser data cleared. You've been signed out of YouTube.";
    public const string ClearBrowserNotReady = "PiPlay's browser isn't ready yet. Try again in a moment.";
    public const string ClearFailed = "PiPlay couldn't clear the browser data. Please try again.";

    // Dialog TITLE for Clear result/status notices (not-ready, failed, timed-out) — kept distinct
    // from the confirmation question. Intentionally shares ClearActionLabel's value: a dialog title
    // and a button label are independent roles (mirrors ResetActionLabel / ResetConfirmButton), so
    // either can change without dragging the other. See
    // PrivacyServiceTests.Clear_result_titles_are_statements_not_questions.
    public const string ClearResultTitle = "Clear browser data";
    public const string ClearTimedOut =
        "Clearing browser data is taking longer than expected. It will finish in the background, " +
        "and you may be signed out of YouTube.";
    public const string ClearAlreadyRunning =
        "A previous browser-data clear is still finishing in the background. Wait for it to finish " +
        "before trying again.";
    // Tooltip on the disabled Clear button while the browser is still loading (set by SettingsWindow).
    public const string ClearNotReadyHint = "Available once the browser has finished loading.";
    public const string ClearAlreadyRunningHint =
        "Available after the previous browser-data clear finishes.";

    /// <summary>
    /// Hang-guard bound for the Clear operation (NOT a progress wait). 30 s is about 2x the ~15 s
    /// adverse worst-case clear (slow HDD, sub-GB profile; the cleared volume is disk cache + site
    /// storage, and Chromium caps the HTTP cache near 256-320 MB). On an SSD the clear finishes in
    /// about 1-2 s. Chosen high enough not to false-flag a slow-but-succeeding clear, and short of
    /// 60 s. Retune only from logged real durations (MainWindow logs the measured ms). The durable
    /// privacy behavior lives in docs/PiPlay_Product_Engineering_Spec.md.
    /// </summary>
    public static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Clear the shared WebView2 profile's browsing data (signs the user out). Needs a live core.</summary>
    public static Task ClearBrowserDataAsync(CoreWebView2 core) =>
        core.Profile.ClearBrowsingDataAsync(ClearKinds);
}
