using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class ProfileServiceTests
{
    private static AppSettings WithProfiles(params string[] names)
    {
        var s = new AppSettings();
        foreach (var n in names)
            s.Profiles.Add(new Profile { Name = n, Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        return s;
    }

    [Fact]
    public void Find_and_Exists_are_case_insensitive()
    {
        var s = WithProfiles("Lo-fi");
        Assert.True(ProfileService.Exists(s, "lo-fi"));
        Assert.NotNull(ProfileService.Find(s, "LO-FI"));
        Assert.False(ProfileService.Exists(s, "jazz"));
    }

    [Fact]
    public void Save_new_appends_and_returns_false()
    {
        var s = WithProfiles();
        var replaced = ProfileService.Save(s, new Profile { Name = "Lo-fi", Url = "https://youtu.be/dQw4w9WgXcQ" });
        Assert.False(replaced);
        Assert.Single(s.Profiles);
    }

    [Fact]
    public void Overwrite_confirm_names_the_profile_and_what_each_path_replaces()
    {
        // Polish review 2026-09-10 F-8: "Overwrite it?" on an accent button said neither what is
        // lost nor that it is destructive. Quick save keeps the old profile's settings and swaps
        // its link; a rename onto an existing name discards that whole profile.
        Assert.Contains("\"Trip\"", ProfileService.OverwriteOnSaveBody("Trip"));
        Assert.Contains("link", ProfileService.OverwriteOnSaveBody("Trip"));
        Assert.Contains("\"Trip\"", ProfileService.OverwriteOnRenameBody("Trip"));
        Assert.Contains("settings", ProfileService.OverwriteOnRenameBody("Trip"));
        Assert.NotEqual(ProfileService.OverwriteOnSaveBody("Trip"), ProfileService.OverwriteOnRenameBody("Trip"));
        Assert.Equal("Replace", ProfileService.OverwriteConfirmButton);
        Assert.Contains("Replace", ProfileService.OverwriteConfirmTitle);
        Assert.True(ProfileService.OverwriteConfirmDanger);
    }

    [Fact]
    public void Save_existing_overwrites_by_name_and_returns_true()
    {
        var s = WithProfiles("Lo-fi");
        var replaced = ProfileService.Save(s,
            new Profile { Name = "lo-fi", Url = "https://youtu.be/new12345678", Topmost = true });
        Assert.True(replaced);
        Assert.Single(s.Profiles);
        Assert.Equal("https://youtu.be/new12345678", s.Profiles[0].Url);
    }

    [Fact]
    public void CreateQuickSaveProfile_preserves_existing_profile_specific_options()
    {
        var bounds = new PlacementData
        {
            X = 10,
            Y = 20,
            Width = 900,
            Height = 560,
            Maximized = true,
            MonitorDeviceName = "DISPLAY1",
            DpiScale = 1.25,
        };
        var existing = new Profile
        {
            Name = "Violet",
            Url = "https://youtu.be/dQw4w9WgXcQ",
            Mode = "compact",
            Presentation = "focused",
            AccentColor = "#A78BFA",
            Topmost = false,
            FadeEnabled = false,
            Bounds = bounds,
        };

        var quickSave = ProfileService.CreateQuickSaveProfile(
            existing, "Violet", "https://youtu.be/new12345678", topmost: true);

        Assert.Equal("Violet", quickSave.Name);
        Assert.Equal("https://youtu.be/new12345678", quickSave.Url);
        Assert.Equal("compact", quickSave.Mode);
        Assert.Equal("focused", quickSave.Presentation);
        Assert.Equal("#A78BFA", quickSave.AccentColor);
        Assert.True(quickSave.Topmost);
        Assert.False(quickSave.FadeEnabled);
        Assert.Same(bounds, quickSave.Bounds);
    }

    [Fact]
    public void Remove_returns_true_only_when_present()
    {
        var s = WithProfiles("Lo-fi");
        Assert.True(ProfileService.Remove(s, "Lo-fi"));
        Assert.False(ProfileService.Remove(s, "Lo-fi"));
        Assert.Empty(s.Profiles);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not a url", false)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    public void ValidateUrl_accepts_only_supported_youtube_urls(string? url, bool ok)
    {
        Assert.Equal(ok, ProfileService.ValidateUrl(url).Ok);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("#00D4FF", true)]
    [InlineData("#787878", true)]
    [InlineData("#131820", true)]
    [InlineData("not-a-color", false)]
    public void ValidateAccent_accepts_null_or_valid_hex_only_REQ_PROFILE_01(string? accent, bool ok)
    {
        Assert.Equal(ok, ProfileService.ValidateAccent(accent));
    }

    [Fact]
    public void NormalizeAccentForStorage_drops_invalid_and_preserves_valid_hex_REQ_PROFILE_01()
    {
        Assert.Null(ProfileService.NormalizeAccentForStorage(null));
        Assert.Null(ProfileService.NormalizeAccentForStorage("not-a-color"));
        Assert.Equal("#00D4FF", ProfileService.NormalizeAccentForStorage("00d4ff"));

        Assert.Equal("#787878", ProfileService.NormalizeAccentForStorage("#787878"));
        Assert.Equal("#131820", ProfileService.NormalizeAccentForStorage("#131820"));
    }

    // --- Update (Phase 2 edit path): position-preserving + collision-aware ---

    [Fact]
    public void Update_changes_url_in_place_and_keeps_position()
    {
        var s = WithProfiles("A", "B", "C");
        var outcome = ProfileService.Update(s, "B",
            new Profile { Name = "B", Url = "https://youtu.be/new12345678" });

        Assert.Equal(ProfileUpdateOutcome.Updated, outcome);
        Assert.Equal(new[] { "A", "B", "C" }, s.Profiles.Select(p => p.Name)); // order kept
        Assert.Equal("https://youtu.be/new12345678", s.Profiles[1].Url);       // still at index 1
    }

    [Fact]
    public void Update_rename_to_free_name_keeps_position()
    {
        var s = WithProfiles("A", "B", "C");
        var outcome = ProfileService.Update(s, "B",
            new Profile { Name = "Beta", Url = "https://youtu.be/dQw4w9WgXcQ" });

        Assert.Equal(ProfileUpdateOutcome.Updated, outcome);
        Assert.Equal(new[] { "A", "Beta", "C" }, s.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void Update_rename_onto_other_profile_reports_conflict_and_does_not_mutate()
    {
        var s = WithProfiles("A", "B", "C");
        var outcome = ProfileService.Update(s, "B",
            new Profile { Name = "c", Url = "https://youtu.be/dQw4w9WgXcQ" }); // collides with "C"

        Assert.Equal(ProfileUpdateOutcome.NameConflict, outcome);
        Assert.Equal(new[] { "A", "B", "C" }, s.Profiles.Select(p => p.Name)); // unchanged
    }

    [Fact]
    public void Update_rename_with_overwrite_removes_collision_and_keeps_position()
    {
        var s = WithProfiles("A", "B", "C");
        var outcome = ProfileService.Update(s, "B",
            new Profile { Name = "A", Url = "https://youtu.be/new12345678" }, overwrite: true);

        // The colliding "A" (before B) is removed; the edited profile keeps B's relative slot.
        Assert.Equal(ProfileUpdateOutcome.Updated, outcome);
        Assert.Equal(new[] { "A", "C" }, s.Profiles.Select(p => p.Name));
        Assert.Equal("https://youtu.be/new12345678", ProfileService.Find(s, "A")!.Url);
    }

    [Fact]
    public void Update_unknown_original_returns_NotFound()
    {
        var s = WithProfiles("A", "B");
        var outcome = ProfileService.Update(s, "missing",
            new Profile { Name = "missing", Url = "https://youtu.be/dQw4w9WgXcQ" });

        Assert.Equal(ProfileUpdateOutcome.NotFound, outcome);
        Assert.Equal(new[] { "A", "B" }, s.Profiles.Select(p => p.Name));
    }
}
