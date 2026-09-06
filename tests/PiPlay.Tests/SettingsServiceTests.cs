using System.IO;
using System.Text.Json;
using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiPlayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var svc = new SettingsService(_path);
        var settings = svc.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.NotNull(settings.MainWindow);
        Assert.NotNull(settings.Player);
        Assert.Equal("https://www.youtube.com/", settings.LastUrl);
        Assert.Equal("cyan", settings.Player.PinAccent);
        Assert.Equal("cyan", settings.Player.FadeAccent);
        Assert.Equal(2500, settings.Player.FadeIdleDelayMs);
        Assert.Equal(ThemeCatalog.DefaultThemeId, settings.Theme.ThemeId);
        Assert.Equal(ThemeCatalog.DefaultAccentColor, settings.Theme.AccentColor);
        Assert.Equal(ThemeCatalog.DefaultFadeDelayPreset, settings.Theme.FadeDelayPreset);
        Assert.Null(settings.Theme.StripAutoHide);
        Assert.Null(settings.Theme.ActiveWindowOpacity);
        Assert.Null(settings.Theme.IdleWindowOpacity);
        Assert.Equal(ThemeCatalog.DefaultCornerStyle, settings.Theme.CornerStyle);
        Assert.False(settings.Player.FocusedPresentation);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var svc = new SettingsService(_path);
        var settings = new AppSettings { LastUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
        settings.Player.Topmost = true;
        settings.Player.PinAccent = "green";
        settings.Player.FadeAccent = "amber";
        settings.Player.FadeIdleDelayMs = 4000;
        settings.Profiles.Add(new Profile { Name = "Lo-fi", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        svc.Save(settings);

        Assert.True(File.Exists(_path));
        var loaded = svc.Load();
        Assert.Equal(settings.LastUrl, loaded.LastUrl);
        Assert.True(loaded.Player.Topmost);
        Assert.Equal("green", loaded.Player.PinAccent);
        Assert.Equal("amber", loaded.Player.FadeAccent);
        Assert.Equal(4000, loaded.Player.FadeIdleDelayMs);
        Assert.Single(loaded.Profiles);
        Assert.Equal("Lo-fi", loaded.Profiles[0].Name);
    }

    [Fact]
    public void Theme_settings_roundtrip_without_dropping_legacy_player_fields()
    {
        var svc = new SettingsService(_path);
        var settings = new AppSettings();
        settings.Player.PinAccent = "green";
        settings.Player.FadeAccent = "amber";
        settings.Player.FadeIdleDelayMs = 4000;
        settings.Theme = new ThemeSettings
        {
            ThemeId = "soft-glass",
            AccentColor = "#a78bfa",
            FadeDelayPreset = "short",
            StripAutoHide = true,
            ActiveWindowOpacity = 0.82,
            IdleWindowOpacity = 0.5,
            CornerStyle = "ROUND",
        };

        svc.Save(settings);
        var loaded = svc.Load();

        Assert.Equal("green", loaded.Player.PinAccent);
        Assert.Equal("amber", loaded.Player.FadeAccent);
        Assert.Equal(4000, loaded.Player.FadeIdleDelayMs);
        Assert.Equal("soft-glass", loaded.Theme.ThemeId);
        Assert.Equal("#A78BFA", loaded.Theme.AccentColor);
        Assert.Equal("short", loaded.Theme.FadeDelayPreset);
        Assert.True(loaded.Theme.StripAutoHide);
        Assert.Equal(0.82, loaded.Theme.ActiveWindowOpacity);
        Assert.Equal(0.5, loaded.Theme.IdleWindowOpacity);
        Assert.Equal("round", loaded.Theme.CornerStyle);   // normalized on save
    }

    /// <summary>
    /// A hand-edited settings.json cannot hand the theme layer an out-of-range accent dial. The
    /// derivation clamps too, so this is defence in depth — it keeps the REPAIRED value in the file
    /// rather than silently re-clamping a junk value on every read.
    /// </summary>
    [Fact]
    public void Load_repairs_an_out_of_range_accent_intensity()
    {
        var svc = new SettingsService(_path);
        var settings = new AppSettings();
        settings.Theme.AccentIntensity = 4000;

        svc.Save(settings);

        Assert.Equal(100, svc.Load().Theme.AccentIntensity);

        settings.Theme.AccentIntensity = -1;
        svc.Save(settings);

        Assert.Equal(0, svc.Load().Theme.AccentIntensity);
    }

    [Fact]
    public void Legacy_settings_without_theme_seed_theme_from_player_preferences()
    {
        File.WriteAllText(_path,
            "{\"schemaVersion\":2,\"player\":{\"pinAccent\":\"green\",\"fadeAccent\":\"amber\",\"fadeIdleDelayMs\":4000," +
            "\"stripAutoHide\":true,\"constantWindowOpacity\":0.82,\"idleWindowOpacity\":0.44}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal("#2DB57F", loaded.Theme.AccentColor);   // legacy "green" -> the deepened green
        Assert.Equal("long", loaded.Theme.FadeDelayPreset);
        // The legacy behavior values become EXPLICIT theme overrides at seed time (end-pass
        // review F2): the resolver's preset-default fallback must never change a migrated
        // user's configured look.
        Assert.True(loaded.Theme.StripAutoHide);
        Assert.Equal(0.82, loaded.Theme.ActiveWindowOpacity);
        Assert.Equal(0.44, loaded.Theme.IdleWindowOpacity);
        Assert.Equal(4000, ThemePreferenceResolver.FadeIdleDelayMs(loaded.Theme, loaded.Player));
        Assert.True(ThemePreferenceResolver.StripAutoHide(loaded.Theme, loaded.Player));
        Assert.Equal(0.82, ThemePreferenceResolver.ActiveWindowOpacity(loaded.Theme, loaded.Player));
        Assert.Equal(0.44, ThemePreferenceResolver.IdleWindowOpacity(loaded.Theme, loaded.Player));

        svc.Save(loaded);
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(doc.RootElement.TryGetProperty("theme", out _));
    }

    [Fact]
    public void Theme_settings_override_legacy_fields_when_present()
    {
        File.WriteAllText(_path,
            "{\"player\":{\"pinAccent\":\"amber\",\"fadeIdleDelayMs\":4000,\"stripAutoHide\":true," +
            "\"constantWindowOpacity\":0.9,\"idleWindowOpacity\":0.7}," +
            "\"theme\":{\"themeId\":\"minimal\",\"accentColor\":\"4d7ea8\",\"fadeDelayPreset\":\"short\"," +
            "\"stripAutoHide\":false,\"activeWindowOpacity\":0.6,\"idleWindowOpacity\":0.4}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal("#4D7EA8", ThemePreferenceResolver.AccentColor(loaded.Theme, loaded.Player));
        Assert.Equal(1500, ThemePreferenceResolver.FadeIdleDelayMs(loaded.Theme, loaded.Player));
        Assert.False(ThemePreferenceResolver.StripAutoHide(loaded.Theme, loaded.Player));
        Assert.Equal(0.6, ThemePreferenceResolver.ActiveWindowOpacity(loaded.Theme, loaded.Player));
        Assert.Equal(0.4, ThemePreferenceResolver.IdleWindowOpacity(loaded.Theme, loaded.Player));
    }

    [Fact]
    public void Invalid_theme_values_fall_back_to_safe_defaults()
    {
        File.WriteAllText(_path,
            "{\"theme\":{\"themeId\":\"hologram\",\"accentColor\":\"not-a-color\",\"fadeDelayPreset\":\"forever\"," +
            "\"cornerStyle\":\"dodecagon\",\"activeWindowOpacity\":5.0,\"idleWindowOpacity\":-1.0}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal(ThemeCatalog.DefaultThemeId, loaded.Theme.ThemeId);
        Assert.Equal(ThemeCatalog.DefaultAccentColor, loaded.Theme.AccentColor);
        Assert.Equal(ThemeCatalog.DefaultFadeDelayPreset, loaded.Theme.FadeDelayPreset);
        Assert.Equal(ThemeCatalog.DefaultCornerStyle, loaded.Theme.CornerStyle);
        // A schema-LESS hand-made file deserializes at the current schema (the C# initializer),
        // so the invalid opacities null out and resolve to preset defaults — only files that
        // explicitly say schemaVersion ≤ 2 (every real pre-upgrade PiPlay file does) get the
        // legacy backfill below.
        Assert.Null(loaded.Theme.ActiveWindowOpacity);
        Assert.Null(loaded.Theme.IdleWindowOpacity);
    }

    [Fact]
    public void Schema2_theme_nulls_backfill_from_legacy_player_values()
    {
        // A PR #18-era (schema 2) file: theme block exists but its null behavior values meant
        // "use the Player fields". The schema-3 upgrade must pin the OLD effective look as
        // explicit overrides — never let preset defaults change a configured window.
        File.WriteAllText(_path,
            "{\"schemaVersion\":2,\"player\":{\"constantWindowOpacity\":0.8,\"idleWindowOpacity\":0.7," +
            "\"stripAutoHide\":true},\"theme\":{\"themeId\":\"soft-glass\",\"accentColor\":\"#A78BFA\"}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal(0.8, loaded.Theme.ActiveWindowOpacity);
        Assert.Equal(0.7, loaded.Theme.IdleWindowOpacity);
        Assert.True(loaded.Theme.StripAutoHide);
        Assert.Equal(0.8, ThemePreferenceResolver.ActiveWindowOpacity(loaded.Theme, loaded.Player));
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Schema3_theme_nulls_resolve_to_preset_defaults()
    {
        // Same content at schema 3: nulls are deliberate "follow the preset" values — a
        // hand-edited soft-glass file is translucent without manual slider work (review F2).
        File.WriteAllText(_path,
            "{\"schemaVersion\":3,\"player\":{\"constantWindowOpacity\":0.8,\"idleWindowOpacity\":0.7}," +
            "\"theme\":{\"themeId\":\"soft-glass\",\"accentColor\":\"#A78BFA\"}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Null(loaded.Theme.ActiveWindowOpacity);
        Assert.Null(loaded.Theme.IdleWindowOpacity);
        var preset = ThemeCatalog.PresetFor("soft-glass");
        Assert.Equal(preset.DefaultActiveWindowOpacity, ThemePreferenceResolver.ActiveWindowOpacity(loaded.Theme, loaded.Player));
        Assert.Equal(preset.DefaultIdleWindowOpacity, ThemePreferenceResolver.IdleWindowOpacity(loaded.Theme, loaded.Player));
    }

    [Fact]
    public void Unknown_settings_json_is_preserved_across_save()
    {
        File.WriteAllText(_path,
            "{\"schemaVersion\":2,\"futureRoot\":{\"enabled\":true},\"player\":{\"pinAccent\":\"cyan\",\"futurePlayer\":42}," +
            "\"theme\":{\"themeId\":\"sharp-dark\",\"accentColor\":\"#2D6F8F\",\"futureTheme\":\"kept\"}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();
        svc.Save(loaded);

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(doc.RootElement.TryGetProperty("futureRoot", out var root));
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(42, doc.RootElement.GetProperty("player").GetProperty("futurePlayer").GetInt32());
        Assert.Equal("kept", doc.RootElement.GetProperty("theme").GetProperty("futureTheme").GetString());
    }

    [Fact]
    public void AutoPopout_defaults_off_and_roundtrips()
    {
        var svc = new SettingsService(_path);
        Assert.False(svc.Load().AutoPopout);   // off by default (and for a missing property)

        svc.Save(new AppSettings { AutoPopout = true });
        Assert.True(svc.Load().AutoPopout);     // survives the atomic round trip
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_defaults_returned()
    {
        File.WriteAllText(_path, "{ this is not valid json ]]]");
        var svc = new SettingsService(_path);

        var settings = svc.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        // The bad file was renamed aside, not left in place to break the next load.
        var quarantined = Directory.GetFiles(_dir, "*.corrupt.*.json");
        Assert.Single(quarantined);
    }

    [Fact]
    public void Sanitize_repairs_out_of_range_values()
    {
        File.WriteAllText(_path,
            "{\"schemaVersion\":0,\"lastUrl\":\"\",\"player\":{\"pinAccent\":\"hotpink\",\"fadeAccent\":\"\",\"fadeIdleDelayMs\":777,\"idleWindowOpacity\":5.0,\"constantWindowOpacity\":-0.5,\"lastWidth\":10,\"lastHeight\":10}}");
        var svc = new SettingsService(_path);

        var settings = svc.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("https://www.youtube.com/", settings.LastUrl);
        Assert.Equal("cyan", settings.Player.PinAccent);
        Assert.Equal("cyan", settings.Player.FadeAccent);
        Assert.Equal(2500, settings.Player.FadeIdleDelayMs);
        Assert.Equal(1.0, settings.Player.IdleWindowOpacity);
        Assert.Equal(1.0, settings.Player.ConstantWindowOpacity);
        Assert.Equal(960, settings.Player.LastWidth);
        Assert.Equal(540, settings.Player.LastHeight);
    }

    [Fact]
    public void Window_opacity_defaults_are_opaque_and_roundtrip()
    {
        var svc = new SettingsService(_path);
        var defaults = svc.Load();
        // Missing properties deserialize to 1.0, so every existing settings.json keeps today's look.
        Assert.Equal(1.0, defaults.Player.IdleWindowOpacity);
        Assert.Equal(1.0, defaults.Player.ConstantWindowOpacity);

        var s = new AppSettings();
        s.Player.IdleWindowOpacity = 0.62;
        s.Player.ConstantWindowOpacity = 0.85;
        svc.Save(s);
        var loaded = svc.Load();
        Assert.Equal(0.62, loaded.Player.IdleWindowOpacity);
        Assert.Equal(0.85, loaded.Player.ConstantWindowOpacity);
    }

    [Fact]
    public void Settings_file_without_opacity_keys_loads_as_opaque()
    {
        // The migration wire shape: every pre-Phase-4 settings.json has a player object but no
        // opacity keys. Deserialization (not just the C# initializer) must yield 1.0 for both.
        File.WriteAllText(_path, "{\"schemaVersion\":2,\"player\":{\"pinAccent\":\"cyan\",\"compactMode\":true}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal(1.0, loaded.Player.IdleWindowOpacity);
        Assert.Equal(1.0, loaded.Player.ConstantWindowOpacity);
        Assert.True(loaded.Player.CompactMode);   // proves the player object actually deserialized
    }

    [Fact]
    public void Hand_edited_opacity_below_the_ui_floor_is_honored()
    {
        // Spec 7.3 explicit unlock: the Settings sliders stop at 0.45, but a hand-edited
        // settings.json may go down to the 0.10 file floor and Sanitize must NOT repair it.
        File.WriteAllText(_path,
            "{\"player\":{\"idleWindowOpacity\":0.25,\"constantWindowOpacity\":0.10}}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal(0.25, loaded.Player.IdleWindowOpacity);
        Assert.Equal(0.10, loaded.Player.ConstantWindowOpacity);
    }

    [Fact]
    public void Profile_mode_is_normalized_on_load()
    {
        // Legacy "embed" -> durable "compact"; unknown -> null (use global); "compact"/"normal" kept;
        // an empty string is treated as unset (null). Proves SettingsService.Sanitize repairs modes.
        File.WriteAllText(_path,
            "{\"profiles\":[" +
            "{\"name\":\"Legacy\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"mode\":\"embed\"}," +
            "{\"name\":\"Bogus\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"mode\":\"hologram\"}," +
            "{\"name\":\"Keep\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"mode\":\"compact\"}," +
            "{\"name\":\"Blank\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"mode\":\"\"}]}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal("compact", loaded.Profiles.Single(p => p.Name == "Legacy").Mode);
        Assert.Null(loaded.Profiles.Single(p => p.Name == "Bogus").Mode);
        Assert.Equal("compact", loaded.Profiles.Single(p => p.Name == "Keep").Mode);
        Assert.Null(loaded.Profiles.Single(p => p.Name == "Blank").Mode);
    }

    [Fact]
    public void Profile_presentation_is_normalized_on_load_REQ_PROFILE_01()
    {
        File.WriteAllText(_path,
            "{\"profiles\":[" +
            "{\"name\":\"Standard\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"presentation\":\" STANDARD \"}," +
            "{\"name\":\"Focused\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"presentation\":\"Focused\"}," +
            "{\"name\":\"Bogus\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"presentation\":\"compact\"}," +
            "{\"name\":\"Blank\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"presentation\":\"\"}]} ");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Equal("standard", loaded.Profiles.Single(p => p.Name == "Standard").Presentation);
        Assert.Equal("focused", loaded.Profiles.Single(p => p.Name == "Focused").Presentation);
        Assert.Null(loaded.Profiles.Single(p => p.Name == "Bogus").Presentation);
        Assert.Null(loaded.Profiles.Single(p => p.Name == "Blank").Presentation);
    }

    [Fact]
    public void Profile_accent_and_active_profile_roundtrip()
    {
        var svc = new SettingsService(_path);
        var settings = new AppSettings { ActiveProfileName = "Violet" };
        settings.Profiles.Add(new Profile
        {
            Name = "Violet",
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            AccentColor = "#A78BFA",
        });

        svc.Save(settings);
        var loaded = svc.Load();

        Assert.Equal("Violet", loaded.ActiveProfileName);
        Assert.Equal("#A78BFA", loaded.Profiles.Single().AccentColor);
    }

    [Fact]
    public void Profile_accent_and_active_profile_are_sanitized_on_load()
    {
        File.WriteAllText(_path,
            "{\"activeProfileName\":\"Ghost\",\"profiles\":[" +
            "{\"name\":\"Bad\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"accentColor\":\"not-a-color\"}," +
            "{\"name\":\"Mid\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\",\"accentColor\":\"#787878\"}]}");
        var svc = new SettingsService(_path);

        var loaded = svc.Load();

        Assert.Null(loaded.ActiveProfileName);
        Assert.Null(loaded.Profiles.Single(p => p.Name == "Bad").AccentColor);
        Assert.Equal("#787878", loaded.Profiles.Single(p => p.Name == "Mid").AccentColor);
    }

    [Fact]
    public void Compact_mode_global_default_is_off_and_roundtrips()
    {
        var svc = new SettingsService(_path);
        Assert.False(svc.Load().Player.CompactMode);   // off by default (and for a missing property)

        var s = new AppSettings();
        s.Player.CompactMode = true;
        svc.Save(s);
        Assert.True(svc.Load().Player.CompactMode);
    }

    [Fact]
    public void Focused_presentation_global_default_is_off_and_roundtrips()
    {
        var svc = new SettingsService(_path);
        Assert.False(svc.Load().Player.FocusedPresentation);

        var settings = new AppSettings();
        settings.Player.FocusedPresentation = true;
        svc.Save(settings);

        Assert.True(svc.Load().Player.FocusedPresentation);
    }

    [Fact]
    public void Strip_auto_hide_default_is_off_and_roundtrips()
    {
        var svc = new SettingsService(_path);
        // Off by default AND for a missing property, so every pre-Phase-4 settings.json keeps
        // the always-reserved strip row (spec 7.2 / acceptance criterion 1).
        Assert.False(svc.Load().Player.StripAutoHide);

        var s = new AppSettings();
        s.Player.StripAutoHide = true;
        svc.Save(s);
        Assert.True(svc.Load().Player.StripAutoHide);
    }

    [Fact]
    public void Reset_recreates_the_file_with_defaults()
    {
        var svc = new SettingsService(_path);
        var populated = new AppSettings { LastUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
        populated.MainWindow.Topmost = true;
        populated.Profiles.Add(new Profile { Name = "Lo-fi", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        svc.Save(populated);

        var fresh = svc.Reset();

        // Returned object is defaults.
        Assert.Equal("https://www.youtube.com/", fresh.LastUrl);
        Assert.Empty(fresh.Profiles);
        Assert.False(fresh.MainWindow.Topmost);

        // On-disk file is defaults too (atomic rewrite, never absent).
        Assert.True(File.Exists(_path));
        var reloaded = svc.Load();
        Assert.Empty(reloaded.Profiles);
        Assert.Equal("https://www.youtube.com/", reloaded.LastUrl);
    }

    [Fact]
    public void Reset_preserves_sibling_logs_and_webview_user_data()
    {
        // The sacred invariant: reset never touches the browser session (login) or logs.
        var logsDir = Path.Combine(_dir, "logs");
        var webViewDir = Path.Combine(_dir, "WebView2UserData");
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(webViewDir);
        var logFile = Path.Combine(logsDir, "piplay.log");
        var sessionFile = Path.Combine(webViewDir, "session-cookie");
        File.WriteAllText(logFile, "log line");
        File.WriteAllText(sessionFile, "logged-in");

        new SettingsService(_path).Reset();

        Assert.True(File.Exists(logFile), "Reset must not delete logs.");
        Assert.True(File.Exists(sessionFile), "Reset must not delete the WebView2 session (login).");
        Assert.Equal("logged-in", File.ReadAllText(sessionFile));
    }

    [Fact]
    public void Reset_does_not_create_a_webview_user_data_dir()
    {
        var webViewDir = Path.Combine(_dir, "WebView2UserData");
        Assert.False(Directory.Exists(webViewDir));

        new SettingsService(_path).Reset();

        Assert.False(Directory.Exists(webViewDir), "Reset must never create/recreate the WebView2 folder.");
    }

    [Fact]
    public void Reset_removes_stale_corrupt_quarantine_files()
    {
        var corrupt = Path.Combine(_dir, "settings.json.corrupt.20200101-000000.json");
        File.WriteAllText(corrupt, "{}");

        new SettingsService(_path).Reset();

        Assert.False(File.Exists(corrupt), "Reset should clean up stale quarantines for a clean slate.");
    }

    public static TheoryData<Func<Exception>> ReadFailures => new()
    {
        () => new IOException("transient lock"),
        () => new UnauthorizedAccessException("access denied"),
    };

    // Readiness F-3: a read IO failure is not corruption. The original file must survive in
    // place — no quarantine, no overwrite from defaults — including saves made by a DIFFERENT
    // service instance (startup loads through one instance, windows save through their own).
    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void Read_io_failure_preserves_original_bytes_through_a_later_save_attempt(
        Func<Exception> failure)
    {
        const string original = "{\"schemaVersion\":3,\"lastUrl\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"," +
            "\"profiles\":[{\"name\":\"Lo-fi\",\"url\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"}]}";
        File.WriteAllText(_path, original);
        var svc = new SettingsService(_path, _ => throw failure());

        var loaded = svc.Load();

        Assert.Equal("https://www.youtube.com/", loaded.LastUrl);   // defaults, never the unread file
        Assert.Empty(loaded.Profiles);
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt.*.json")); // not treated as corruption

        svc.Save(new AppSettings());
        new SettingsService(_path).Save(new AppSettings());          // cross-instance save also refused

        Assert.Equal(original, File.ReadAllText(_path));             // original bytes intact
        var recovered = new SettingsService(_path).Load();           // and still loadable afterwards
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", recovered.LastUrl);
        Assert.Single(recovered.Profiles, p => p.Name == "Lo-fi");
    }

    [Fact]
    public void Save_resumes_after_a_successful_reload_follows_the_read_failure()
    {
        const string original = "{\"schemaVersion\":3,\"lastUrl\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"}";
        File.WriteAllText(_path, original);
        var svc = new SettingsService(_path, _ => throw new IOException("transient lock"));
        svc.Load();
        svc.Save(new AppSettings());
        Assert.Equal(original, File.ReadAllText(_path));             // still refused before a reload

        var settings = new SettingsService(_path).Load();            // successful read lifts the block
        settings.AutoPopout = true;
        new SettingsService(_path).Save(settings);

        Assert.True(new SettingsService(_path).Load().AutoPopout);   // the write went through
    }

    [Fact]
    public void Corrupt_json_still_quarantines_and_does_not_block_saves()
    {
        File.WriteAllText(_path, "{ this is not valid json ]]]");
        var svc = new SettingsService(_path);

        svc.Load();

        Assert.Single(Directory.GetFiles(_dir, "*.corrupt.*.json"));
        svc.Save(new AppSettings { LastUrl = "https://www.youtube.com/watch?v=y6120QOlsfU" });
        Assert.True(File.Exists(_path));
        Assert.Equal("https://www.youtube.com/watch?v=y6120QOlsfU", new SettingsService(_path).Load().LastUrl);
    }

    [Fact]
    public void Reset_is_an_explicit_replacement_and_succeeds_after_a_read_failure()
    {
        const string original = "{\"schemaVersion\":3,\"lastUrl\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"}";
        File.WriteAllText(_path, original);
        var svc = new SettingsService(_path, _ => throw new IOException("transient lock"));
        svc.Load();
        svc.Save(new AppSettings());
        Assert.Equal(original, File.ReadAllText(_path));

        var fresh = svc.Reset();                                      // deliberate user reset is allowed

        Assert.Equal("https://www.youtube.com/", fresh.LastUrl);
        Assert.Equal("https://www.youtube.com/", new SettingsService(_path).Load().LastUrl);
        svc.Save(new AppSettings { AutoPopout = true });              // unblocked by the reset
        Assert.True(new SettingsService(_path).Load().AutoPopout);
    }

    // Readiness F-3 isolation: the unread-file save block is keyed by settings path, so a read
    // failure on one file must never refuse saves aimed at a different file.
    [Fact]
    public void Read_failure_on_one_path_does_not_block_saves_on_another_path()
    {
        var dirA = Path.Combine(_dir, "a");
        var dirB = Path.Combine(_dir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var pathA = Path.Combine(dirA, "settings.json");
        var pathB = Path.Combine(dirB, "settings.json");
        const string originalA = "{\"schemaVersion\":3,\"lastUrl\":\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"}";
        File.WriteAllText(pathA, originalA);

        var svcA = new SettingsService(pathA, _ => throw new IOException("transient lock"));
        svcA.Load();                                                   // flags path A as unread

        var svcB = new SettingsService(pathB);
        svcB.Save(new AppSettings { LastUrl = "https://www.youtube.com/watch?v=y6120QOlsfU" });

        Assert.True(File.Exists(pathB));                               // B's save was not blocked
        Assert.Equal("https://www.youtube.com/watch?v=y6120QOlsfU", svcB.Load().LastUrl);
        svcA.Save(new AppSettings());                                  // A is still refused
        Assert.Equal(originalA, File.ReadAllText(pathA));
    }
}
