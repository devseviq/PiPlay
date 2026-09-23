using Microsoft.Web.WebView2.Core;
using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class PrivacyServiceTests
{
    private static bool Has(string s, string sub) =>
        s.Contains(sub, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Reset_wording_says_the_user_stays_signed_in()
    {
        foreach (var s in new[]
                 {
                     PrivacyService.ResetDescription,
                     PrivacyService.ResetConfirmBody,
                     PrivacyService.ResetDoneBody,
                 })
        {
            Assert.True(Has(s, "signed in"), $"Reset wording should reassure login is kept: '{s}'");
            Assert.False(Has(s, "sign out") || Has(s, "signed out"),
                $"Reset wording must NOT imply logout: '{s}'");
        }
    }

    [Fact]
    public void Clear_wording_says_the_user_is_signed_out()
    {
        foreach (var s in new[]
                 {
                     PrivacyService.ClearDescription,
                     PrivacyService.ClearConfirmBody,
                     PrivacyService.ClearDoneBody,
                 })
        {
            // Assert the PHRASE, not "sign" and "out" as independent tokens (which a reassuring
            // "stay signed in ... log out of other apps" string could satisfy).
            Assert.True(Has(s, "signed out") || Has(s, "signs you out") || Has(s, "sign you out"),
                $"Clear wording should state the user is signed out (as a phrase): '{s}'");
        }
    }

    [Fact]
    public void Reset_and_clear_are_worded_distinctly()
    {
        // REQ-PRIVACY-02: the two actions must be worded separately.
        Assert.NotEqual(PrivacyService.ResetActionLabel, PrivacyService.ClearActionLabel);
        Assert.NotEqual(PrivacyService.ResetDescription, PrivacyService.ClearDescription);
        Assert.NotEqual(PrivacyService.ResetConfirmBody, PrivacyService.ClearConfirmBody);
    }

    [Fact]
    public void Reset_confirm_says_profiles_cannot_be_recovered_and_is_destructive()
    {
        // Polish review 2026-09-10 F-8: the reset wipes saved profiles for good, yet its confirm
        // wore the accent style of an ordinary action.
        Assert.True(Has(PrivacyService.ResetConfirmBody, "can't be recovered"),
            $"Reset wording should say profiles are gone for good: '{PrivacyService.ResetConfirmBody}'");
        Assert.True(PrivacyService.ResetConfirmDanger);
    }

    [Fact]
    public void Clear_confirm_says_the_popout_closes_first()
    {
        // Polish review 2026-09-10 F-10: the clear closes an open Video Popout before it runs
        // (PP-06); a confirm that omits it makes the closing look like a crash.
        Assert.True(Has(PrivacyService.ClearConfirmBody, "Popout"),
            $"Clear wording should say the Video Popout closes: '{PrivacyService.ClearConfirmBody}'");
    }

    [Fact]
    public void Clear_uses_the_all_profile_browsing_data_kind()
    {
        // AllProfile clears cookies + cache + site storage, which logs the user out.
        Assert.Equal(CoreWebView2BrowsingDataKinds.AllProfile, PrivacyService.ClearKinds);
    }

    [Fact]
    public void Clear_result_titles_are_statements_not_questions()
    {
        // Result/status notices must read as outcomes, not the confirmation question, so a user
        // on a privacy action is never left unsure whether their data was actually cleared.
        foreach (var title in new[] { PrivacyService.ClearResultTitle, PrivacyService.ClearDoneTitle })
        {
            Assert.False(title.TrimEnd().EndsWith("?"),
                $"Clear result/status title should be a statement, not a question: '{title}'");
        }

        // The confirmation prompt, by contrast, is allowed (and expected) to be a question.
        Assert.EndsWith("?", PrivacyService.ClearConfirmTitle);
    }
}
