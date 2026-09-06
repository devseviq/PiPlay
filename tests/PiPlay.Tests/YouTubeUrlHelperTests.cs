using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class YouTubeUrlHelperTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void Parses_video_id_from_common_url_shapes(string url, string expectedId)
    {
        Assert.True(YouTubeUrlHelper.TryParse(url, out var target));
        Assert.Equal(expectedId, target.VideoId);
    }

    [Fact]
    public void Parses_watch_with_playlist_and_timestamp()
    {
        Assert.True(YouTubeUrlHelper.TryParse(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1234567890&t=123s", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Equal("PL1234567890", t.PlaylistId);
        Assert.Equal(123, t.StartSeconds);
        Assert.Null(t.FallbackReason);
    }

    [Fact]
    public void Parses_playlist_only_page()
    {
        Assert.True(YouTubeUrlHelper.TryParse("https://www.youtube.com/playlist?list=PLabc", out var t));
        Assert.True(t.IsPlaylistOnly);
        Assert.Equal("PLabc", t.PlaylistId);
        Assert.Null(t.VideoId);
    }

    [Theory]
    [InlineData("x%26t%3D9999")]   // decoded '&'/'=' must never re-enter a built URL
    [InlineData("PL%20abc")]       // embedded space
    [InlineData("a")]              // too short to be a real list id
    public void Malformed_playlist_id_is_dropped_with_a_note_but_the_video_survives(string list)
    {
        Assert.True(YouTubeUrlHelper.TryParse(
            $"https://www.youtube.com/watch?v=dQw4w9WgXcQ&list={list}", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Null(t.PlaylistId);
        // The user pasted a playlist link and got a single video: say so (Q-6 non-blocking note)
        // instead of the pre-2026-08 silent drop.
        Assert.False(string.IsNullOrEmpty(t.FallbackReason));
        Assert.DoesNotContain("%", YouTubeUrlHelper.BuildWatchUrl(t));
        Assert.DoesNotContain("list", YouTubeUrlHelper.BuildWatchUrl(t));
    }

    // Malformed percent-escapes and absurd offsets (review 2026-09-05, PP-01 evidence table): the
    // parser must decide, never throw, so a hostile or mangled command-line/pipe payload cannot take
    // startup or the hand-off down with it.
    [Theory]
    [InlineData("%")]
    [InlineData("%zz")]
    [InlineData("abc%")]
    [InlineData("%E2%82")]
    [InlineData("%E2%82%")]
    [InlineData("https://www.youtube.com/watch?v=%")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%")]
    public void Malformed_inputs_are_rejected_without_throwing(string input)
    {
        var threw = Record.Exception(() => Assert.False(YouTubeUrlHelper.TryParse(input, out _)));
        Assert.Null(threw);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=%zz")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=%")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=%E2%82%s")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=99999999999999999999s")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=9999999999h")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=-5")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1e9")]
    public void Malformed_or_overflowing_offsets_keep_the_video_and_drop_the_timestamp(string input)
    {
        Assert.True(YouTubeUrlHelper.TryParse(input, out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Null(t.StartSeconds);
    }

    [Fact]
    public void Malformed_list_escape_keeps_the_video_and_reports_the_dropped_list()
    {
        Assert.True(YouTubeUrlHelper.TryParse(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=%E2%82", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Null(t.PlaylistId);
        Assert.False(string.IsNullOrEmpty(t.FallbackReason));
    }

    [Fact]
    public void Mix_radio_list_is_kept_and_carried_to_the_watch_url()
    {
        // list=RD... queues play natively on the full watch page, so Normal popouts carry them
        // like any playlist (docs/PiPlay_Product_Engineering_Spec.md §22.1). The old parse-time drop is gone.
        Assert.True(YouTubeUrlHelper.TryParse(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Equal("RDdQw4w9WgXcQ", t.PlaylistId);
        Assert.Null(t.FallbackReason);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ",
            YouTubeUrlHelper.BuildWatchUrl(t));
    }

    [Fact]
    public void Shell_and_embed_urls_omit_auto_generated_lists()
    {
        // The IFrame API cannot load auto-generated (RD) lists, so the compact tiers degrade to
        // the single video exactly where the limitation lives; regular lists are still carried.
        var mix = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ", PlaylistId = "RDdQw4w9WgXcQ" };
        var shellMix = YouTubeUrlHelper.BuildShellUrl(mix, null, "https://piplay.local/player.html");
        Assert.DoesNotContain("list=", shellMix);
        Assert.Contains("v=dQw4w9WgXcQ", shellMix);
        Assert.DoesNotContain("list=", YouTubeUrlHelper.BuildEmbedUrl(mix));

        var playlist = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ", PlaylistId = "PLabc" };
        Assert.Contains("list=PLabc",
            YouTubeUrlHelper.BuildShellUrl(playlist, null, "https://piplay.local/player.html"));
        Assert.Contains("list=PLabc", YouTubeUrlHelper.BuildEmbedUrl(playlist));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("1m30s", 90)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("0", 0)]
    public void Parses_timestamp_formats(string value, int expected)
    {
        Assert.Equal(expected, YouTubeUrlHelper.ParseTime(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.test@youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("evil.test@youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/")]
    public void Rejects_unsupported_input(string url)
    {
        Assert.False(YouTubeUrlHelper.TryParse(url, out _));
    }

    [Fact]
    public void Builds_watch_url_with_timestamp_and_playlist()
    {
        var target = new PiPlay.Models.YouTubeTarget
        {
            VideoId = "dQw4w9WgXcQ",
            PlaylistId = "PLabc",
        };
        var url = YouTubeUrlHelper.BuildWatchUrl(target, 42);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLabc&t=42s", url);
    }

    [Fact]
    public void Builds_watch_url_omits_zero_timestamp()
    {
        var target = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ" };
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", YouTubeUrlHelper.BuildWatchUrl(target, 0));
    }

    // --- Gap cases surfaced by the spec-conformance review ---

    [Theory]
    [InlineData("https://www.youtube.com/v/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void Parses_path_based_v_and_live_ids(string url, string expected)
    {
        Assert.True(YouTubeUrlHelper.TryParse(url, out var t));
        Assert.Equal(expected, t.VideoId);
    }

    [Fact]
    public void Parses_youtu_be_with_list_and_timestamp()
    {
        Assert.True(YouTubeUrlHelper.TryParse("https://youtu.be/dQw4w9WgXcQ?list=PLabc&t=1m", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Equal("PLabc", t.PlaylistId);
        Assert.Equal(60, t.StartSeconds);
    }

    [Fact]
    public void Parses_watch_with_start_param_alias()
    {
        Assert.True(YouTubeUrlHelper.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ&start=45", out var t));
        Assert.Equal(45, t.StartSeconds);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999h")]   // 11-digit hours: int.Parse used to THROW OverflowException
    [InlineData("999999999h")]     // 9-digit hours: int.Parse succeeded but the sum silently overflowed
    [InlineData("9999999999")]     // plain seconds past int range
    public void ParseTime_rejects_garbage_negatives_and_out_of_range(string value)
    {
        Assert.Null(YouTubeUrlHelper.ParseTime(value));
    }

    [Fact]
    public void TryParse_keeps_video_when_timestamp_is_out_of_range()
    {
        // A real link carrying an out-of-range t= must degrade to "no offset" and still navigate -
        // never throw into the generic crash dialog (spec 17: broken URLs fail gracefully).
        Assert.True(YouTubeUrlHelper.TryParse(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=99999999999h", out var t));
        Assert.Equal("dQw4w9WgXcQ", t.VideoId);
        Assert.Null(t.StartSeconds);
    }

    [Fact]
    public void Builds_watch_url_for_playlist_only_target()
    {
        var t = new PiPlay.Models.YouTubeTarget { PlaylistId = "PLabc", IsPlaylistOnly = true };
        Assert.Equal("https://www.youtube.com/playlist?list=PLabc", YouTubeUrlHelper.BuildWatchUrl(t));
    }

    [Fact]
    public void Builds_embed_url_with_autoplay_playlist_and_start()
    {
        var t = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ", PlaylistId = "PLabc" };
        Assert.Equal(
            "https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1&list=PLabc&start=30",
            YouTubeUrlHelper.BuildEmbedUrl(t, 30));
    }

    // --- Compact shell URL (spec 10.3): only non-sensitive target params on the shell base ---

    [Fact]
    public void Builds_shell_url_with_only_nonsensitive_target_params()
    {
        var t = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ", PlaylistId = "PLabc" };
        Assert.Equal(
            "https://piplay.local/player.html?v=dQw4w9WgXcQ&list=PLabc&start=42",
            YouTubeUrlHelper.BuildShellUrl(t, 42, "https://piplay.local/player.html"));
    }

    [Fact]
    public void Shell_url_omits_zero_start_and_absent_playlist()
    {
        var t = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ" };
        Assert.Equal(
            "https://piplay.local/player.html?v=dQw4w9WgXcQ",
            YouTubeUrlHelper.BuildShellUrl(t, 0, "https://piplay.local/player.html"));
    }

    [Fact]
    public void Shell_url_with_no_target_is_the_bare_base()
    {
        Assert.Equal(
            "https://piplay.local/player.html",
            YouTubeUrlHelper.BuildShellUrl(new PiPlay.Models.YouTubeTarget(), null, "https://piplay.local/player.html"));
    }

    [Fact]
    public void Shell_url_host_is_the_single_source_of_truth_allowlisted_on_the_player()
    {
        // The host in the navigated shell URL must equal the allowlist's ShellHost (drift = a blank
        // player), and the allowlist must permit it on the Popout Player only.
        var t = new PiPlay.Models.YouTubeTarget { VideoId = "dQw4w9WgXcQ" };
        var url = YouTubeUrlHelper.BuildShellUrl(t, null, WebViewEnvironmentService.ShellPlayerUrl);

        Assert.Equal(NavigationPolicy.ShellHost, new Uri(url).Host);
        Assert.True(NavigationPolicy.IsAllowed(new Uri(WebViewEnvironmentService.ShellPlayerUrl), NavigationSurface.Player));
        Assert.False(NavigationPolicy.IsAllowed(new Uri(WebViewEnvironmentService.ShellPlayerUrl), NavigationSurface.Source));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=30s", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]            // share link is a watch video
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", false)]  // Shorts must not pop
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", false)]
    [InlineData("https://www.youtube.com/playlist?list=PLabc", false)]
    [InlineData("https://www.youtube.com/results?search_query=lofi", false)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", false)]  // not a YouTube host
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWatchUrl_is_true_only_for_watch_pages_and_share_links(string? url, bool expected)
    {
        Assert.Equal(expected, YouTubeUrlHelper.IsWatchUrl(url));
    }
}
