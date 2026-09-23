using PiPlay.Models;
using PiPlay.Theme;

namespace PiPlay.Services;

/// <summary>Result of <see cref="ProfileService.Update"/> (the Phase 2 edit path).</summary>
public enum ProfileUpdateOutcome
{
    /// <summary>The named profile was found and replaced in place.</summary>
    Updated,
    /// <summary>No profile matched the original name; nothing changed.</summary>
    NotFound,
    /// <summary>The edited name now matches a different existing profile; nothing changed.
    /// The caller must resolve it (overwrite or cancel) and retry with <c>overwrite: true</c>.</summary>
    NameConflict,
}

/// <summary>
/// Basic profile management for the MVP (spec 17): find, upsert (overwrite by name),
/// remove, and graceful URL validation. Phase 2 adds <see cref="Update"/> for the edit path.
/// Profiles live in <see cref="AppSettings.Profiles"/>; persistence is owned by
/// <see cref="SettingsService"/>.
/// </summary>
public static class ProfileService
{
    public static Profile? Find(AppSettings settings, string name) =>
        settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Exists(AppSettings settings, string name) => Find(settings, name) is not null;

    // Overwrite confirm (polish review 2026-09-10 F-8): the copy says what each path replaces and
    // the button is destructive-styled, because both paths discard something the user saved.
    public const string OverwriteConfirmTitle = "Replace profile?";
    public const string OverwriteConfirmButton = "Replace";
    public const bool OverwriteConfirmDanger = true;

    /// <summary>Quick save updates the existing profile's link and Pin, preserving its other options.</summary>
    public static string OverwriteOnSaveBody(string name) =>
        $"A profile named \"{name}\" already exists. Replacing it updates that profile's link to the current page and its Pin to the Source Window's current Pin; its other settings stay.";

    /// <summary>Rename onto an existing name: that profile is discarded and this one takes its name.</summary>
    public static string OverwriteOnRenameBody(string name) =>
        $"A profile named \"{name}\" already exists. Replacing it discards that profile's link and settings; this profile takes its name.";

    /// <summary>Refresh the URL and Source Pin while preserving options quick save does not expose.</summary>
    public static Profile CreateQuickSaveProfile(Profile? existing, string name, string url, bool topmost) => new()
    {
        Name = name,
        Url = url,
        Mode = existing?.Mode,
        Presentation = existing?.Presentation,
        AccentColor = existing?.AccentColor,
        Topmost = topmost,
        FadeEnabled = existing?.FadeEnabled,
        Bounds = existing?.Bounds,
    };

    /// <summary>Insert or overwrite a profile by name (case-insensitive). Returns true if an existing one was replaced.</summary>
    public static bool Save(AppSettings settings, Profile profile)
    {
        var existing = Find(settings, profile.Name);
        if (existing is not null)
        {
            settings.Profiles.Remove(existing);
            settings.Profiles.Add(profile);
            return true;
        }
        settings.Profiles.Add(profile);
        return false;
    }

    public static bool Remove(AppSettings settings, string name)
    {
        var existing = Find(settings, name);
        if (existing is null) return false;
        settings.Profiles.Remove(existing);
        return true;
    }

    /// <summary>
    /// Edit the profile stored under <paramref name="originalName"/> in place, preserving its
    /// list position (unlike <see cref="Save"/>, which appends). If the edited name now matches a
    /// *different* existing profile, returns <see cref="ProfileUpdateOutcome.NameConflict"/> and
    /// makes no change unless <paramref name="overwrite"/> is set — then the colliding profile is
    /// removed and the edited profile keeps the original's slot. The caller surfaces the
    /// overwrite/rename prompt (spec 17: duplicate names prompt instead of silent clutter).
    /// </summary>
    public static ProfileUpdateOutcome Update(
        AppSettings settings, string originalName, Profile updated, bool overwrite = false)
    {
        var list = settings.Profiles;
        var index = list.FindIndex(p => string.Equals(p.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return ProfileUpdateOutcome.NotFound;

        // A rename that lands on a DIFFERENT existing profile is a collision.
        var collision = list.FindIndex(p => string.Equals(p.Name, updated.Name, StringComparison.OrdinalIgnoreCase));
        if (collision >= 0 && collision != index)
        {
            if (!overwrite) return ProfileUpdateOutcome.NameConflict;
            list.RemoveAt(collision);
            if (collision < index) index--;   // removing an earlier item shifts our slot down
        }

        list[index] = updated;
        return ProfileUpdateOutcome.Updated;
    }

    /// <summary>Validate a profile URL. Broken URLs must fail gracefully, even in the MVP (spec 17).</summary>
    public static (bool Ok, string? Error) ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Enter a YouTube URL.");
        if (!YouTubeUrlHelper.TryParse(url, out _))
            return (false, "Enter a supported YouTube video or playlist URL.");
        return (true, null);
    }

    public static bool ValidateAccent(string? hex) =>
        hex is null || ThemeCatalog.IsValidHex(hex);

    public static string? NormalizeAccentForStorage(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !ThemeCatalog.IsValidHex(hex)) return null;
        return ThemeCatalog.NormalizeAccentColor(hex);
    }
}
