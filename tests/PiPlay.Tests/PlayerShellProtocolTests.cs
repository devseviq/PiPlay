using System.IO;
using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class PlayerShellProtocolTests
{
    // --- Source gate: only the compact shell's own origin may speak this protocol ---

    // The shell's origin, spelled out so these cases read as data. The real value is pinned
    // against the SSOT by Shell_source_gate_accepts_the_real_shell_page_at_the_real_origin below.
    private const string ShellOrigin = "https://piplay.local";

    [Theory]
    [InlineData("https://piplay.local/player.html", true)]              // the shell page itself
    [InlineData("https://piplay.local/player.html?v=dQw4w9WgXcQ&start=30", true)]   // the real launch URL
    [InlineData("https://piplay.local", true)]                          // a bare-origin source
    [InlineData("https://piplay.local:443/player.html", true)]          // the default https port IS the origin
    [InlineData("HTTPS://PIPLAY.LOCAL/player.html", true)]              // Uri lower-cases scheme/host first
    [InlineData("http://piplay.local/player.html", false)]              // scheme must match
    [InlineData("http://piplay.local:443/player.html", false)]          // ...on its own, not via the port
    [InlineData("https://piplay.local:8443/player.html", false)]        // port must match
    [InlineData("https://sub.piplay.local/player.html", false)]         // not a suffix match
    [InlineData("https://piplay.local.evil.test/player.html", false)]   // not a prefix match
    [InlineData("https://shell.local.evil/player.html", false)]         // not a substring match
    [InlineData("https://piplay.local@evil.test/player.html", false)]   // userinfo cannot spoof the host
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", false)]    // the nested iframe is not the shell
    [InlineData("https://evil.test/?next=https://piplay.local", false)] // the origin is not the query
    [InlineData("player.html", false)]                                  // a relative source is not an origin
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Shell_source_gate_matches_the_origin_exactly(string? source, bool expected)
    {
        // A foreign frame must not be able to send close / pinToggle / fullscreenToggle, a state,
        // or an error, so the gate compares scheme, host, and port and never a prefix or substring.
        Assert.Equal(expected, PlayerShellProtocol.IsTrustedShellSource(source, ShellOrigin));
    }

    [Fact]
    public void Shell_source_gate_accepts_the_real_shell_page_at_the_real_origin()
    {
        // Pins the SSOT, not a hand-copied literal: were ShellOrigin to grow a trailing slash or an
        // explicit port, the gate would fail closed in production and silently kill compact mode.
        Assert.True(PlayerShellProtocol.IsTrustedShellSource(
            WebViewEnvironmentService.ShellPlayerUrl, WebViewEnvironmentService.ShellOrigin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("piplay.local")]   // no scheme, so not an absolute origin
    public void Shell_source_gate_fails_closed_on_an_unusable_expected_origin(string? expectedOrigin)
    {
        Assert.False(PlayerShellProtocol.IsTrustedShellSource(
            "https://piplay.local/player.html", expectedOrigin));
    }

    [Fact]
    public void Shell_bridge_gates_the_source_before_it_reads_the_payload()
    {
        // The wiring needs a live WebView2 (CoreWebView2WebMessageReceivedEventArgs is not
        // constructible), so pin it as source text the way PlayerSurfaceScriptTests pins the drag
        // bridge. Ordering, not presence: a gate moved below the parse would still "contain" it.
        var source = File.ReadAllText(Path.Combine(
            XamlTestFiles.SrcDir, "Services", "PlayerShellBridge.cs"));

        var gate = source.IndexOf("IsTrustedShellSource", StringComparison.Ordinal);
        var readPayload = source.IndexOf("TryGetWebMessageAsString", StringComparison.Ordinal);
        Assert.True(gate >= 0 && readPayload > gate,
            "PlayerShellBridge must reject a foreign message source before it reads the payload.");
    }

    // --- Inbound (shell -> host) parsing ---

    [Fact]
    public void Parses_ready()
    {
        var msg = PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"ready\"}");
        Assert.Equal(ShellMessageKind.Ready, msg.Kind);
    }

    [Fact]
    public void Parses_state_fields()
    {
        var msg = PlayerShellProtocol.Parse(
            "{\"v\":3,\"type\":\"state\",\"currentTime\":42,\"playerState\":1,\"duration\":300}");
        Assert.Equal(ShellMessageKind.State, msg.Kind);
        Assert.Equal(42, msg.CurrentTime);
        Assert.Equal(1, msg.PlayerState);
        Assert.Equal(300, msg.Duration);
    }

    [Fact]
    public void Parses_state_with_missing_fields_using_safe_defaults()
    {
        var msg = PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"state\"}");
        Assert.Equal(ShellMessageKind.State, msg.Kind);
        Assert.Equal(0, msg.CurrentTime);
        Assert.Equal(-1, msg.PlayerState);   // YT "unstarted"
        Assert.Null(msg.Duration);           // live/unknown duration stays null
        Assert.Null(msg.VideoId);            // omitted while the shell has no current video (defaults fill)
    }

    [Fact]
    public void Parses_state_video_id_when_present()
    {
        // Protocol v3 (overhaul Task 3): the shell reports the CURRENT video so playlist
        // auto-advance and in-iframe clicks survive into the return state.
        var msg = PlayerShellProtocol.Parse(
            "{\"v\":3,\"type\":\"state\",\"currentTime\":42,\"playerState\":1,\"videoId\":\"dQw4w9WgXcQ\"}");
        Assert.Equal(ShellMessageKind.State, msg.Kind);
        Assert.Equal("dQw4w9WgXcQ", msg.VideoId);
    }

    [Theory]
    [InlineData("abc&evil=1//")]   // URL metacharacters — would ride into a watch URL downstream
    [InlineData("shortid")]        // too short
    [InlineData("dQw4w9WgXcQX")]   // too long (12)
    [InlineData("")]               // empty string is not an id
    [InlineData("dQw4w9WgXc!")]    // disallowed charset
    public void Malformed_state_video_ids_parse_as_absent(string hostile)
    {
        // FieldVideoId carries a YouTube id BY CONTRACT: the parser is the trust boundary, so a
        // malformed value from the (untrusted) shell never reaches ANY consumer — the host turns
        // this string into a source navigation target on close.
        var msg = PlayerShellProtocol.Parse(
            $"{{\"v\":3,\"type\":\"state\",\"currentTime\":42,\"videoId\":\"{hostile}\"}}");
        Assert.Equal(ShellMessageKind.State, msg.Kind);   // the state itself still parses
        Assert.Equal(42, msg.CurrentTime);
        Assert.Null(msg.VideoId);                         // the malformed id does not
    }

    [Fact]
    public void Parses_error_code()
    {
        var msg = PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"error\",\"code\":\"150\"}");
        Assert.Equal(ShellMessageKind.Error, msg.Kind);
        Assert.Equal("150", msg.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]                       // not an object
    [InlineData("\"a string\"")]                  // not an object
    [InlineData("{\"v\":3}")]                     // no type
    [InlineData("{\"type\":42}")]                 // non-string type
    [InlineData("{\"v\":3,\"type\":\"frobnicate\"}")] // unknown type
    public void Malformed_or_unknown_messages_parse_to_unknown(string? json)
    {
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(json).Kind);
    }

    [Fact]
    public void Recognized_type_with_wrong_typed_fields_degrades_to_defaults_not_a_throw()
    {
        // The ValueKind guards must keep "never throws; malformed -> safe default": a wrong-typed
        // PRESENT field (string/bool/array where a number is expected) falls back, it does not crash.
        var state = PlayerShellProtocol.Parse(
            "{\"v\":3,\"type\":\"state\",\"currentTime\":\"x\",\"playerState\":true,\"duration\":[1]}");
        Assert.Equal(ShellMessageKind.State, state.Kind);
        Assert.Equal(0, state.CurrentTime);
        Assert.Equal(-1, state.PlayerState);
        Assert.Null(state.Duration);

        // A numeric error code is not a string, so it degrades to null (no exception).
        var error = PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"error\",\"code\":150}");
        Assert.Equal(ShellMessageKind.Error, error.Kind);
        Assert.Null(error.ErrorCode);
    }

    [Fact]
    public void Parse_rejects_a_message_without_the_exact_version()
    {
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(
            "{\"type\":\"state\",\"currentTime\":1,\"playerState\":1}").Kind);
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(
            "{\"v\":2,\"type\":\"state\",\"currentTime\":1,\"playerState\":1}").Kind);
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(
            "{\"v\":4,\"type\":\"state\",\"currentTime\":1,\"playerState\":1}").Kind);
    }

    [Theory]
    [InlineData("{\"v\":3,\"type\":\"ready\",\"extra\":1}")]
    [InlineData("{\"v\":3,\"type\":\"state\",\"currentTime\":1,\"playerState\":1,\"duration\":10,\"videoId\":\"dQw4w9WgXcQ\",\"extra\":true}")]
    [InlineData("{\"v\":3,\"type\":\"error\",\"code\":\"x\",\"extra\":true}")]
    [InlineData("{\"v\":3,\"type\":\"request\",\"action\":\"close\",\"extra\":true}")]
    [InlineData("{\"v\":3,\"type\":\"state\",\"currentTime\":1,\"currentTime\":2,\"playerState\":1}")]
    public void Parse_rejects_unknown_top_level_fields(string json)
    {
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(json).Kind);
    }

    [Fact]
    public void Parse_still_accepts_the_exact_current_messages_with_optional_fields_omitted()
    {
        Assert.Equal(ShellMessageKind.Ready, PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"ready\"}").Kind);
        Assert.Equal(ShellMessageKind.State, PlayerShellProtocol.Parse(
            "{\"v\":3,\"type\":\"state\",\"currentTime\":1,\"playerState\":1}").Kind);
        Assert.Equal(ShellMessageKind.Error, PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"error\",\"code\":\"runtime\"}").Kind);
        Assert.Equal(ShellMessageKind.Request, PlayerShellProtocol.Parse("{\"v\":3,\"type\":\"request\",\"action\":\"close\"}").Kind);
    }

    // --- Request kind (Phase 4): allowlisted shell -> host window actions ---

    [Theory]
    [InlineData("close")]
    [InlineData("pinToggle")]
    [InlineData("fullscreenToggle")]
    public void Parses_allowlisted_request_actions(string action)
    {
        var msg = PlayerShellProtocol.Parse($"{{\"v\":3,\"type\":\"request\",\"action\":\"{action}\"}}");
        Assert.Equal(ShellMessageKind.Request, msg.Kind);
        Assert.Equal(action, msg.Action);
    }

    [Theory]
    [InlineData("{\"v\":3,\"type\":\"request\"}")]                           // no action at all
    [InlineData("{\"v\":3,\"type\":\"request\",\"action\":\"minimize\"}")]   // off-allowlist action
    [InlineData("{\"v\":3,\"type\":\"request\",\"action\":\"Close\"}")]      // exact tokens only (case)
    [InlineData("{\"v\":3,\"type\":\"request\",\"action\":42}")]             // wrong-typed action
    [InlineData("{\"v\":3,\"type\":\"request\",\"action\":\"\"}")]           // empty action
    public void Off_allowlist_requests_degrade_to_unknown(string json)
    {
        // The closed set is the security property (spec 12.5 / docs/YouTube_Compliance.md): an injected or future
        // action must die at the parse layer, before any host handler can see it.
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(json).Kind);
    }

    // --- Outbound (host -> shell) command shapes ---

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("requestState")]
    public void Commands_carry_the_version_and_type(string type)
    {
        var json = type switch
        {
            "play" => PlayerShellProtocol.Play(),
            "pause" => PlayerShellProtocol.Pause(),
            _ => PlayerShellProtocol.RequestState(),
        };
        Assert.Contains("\"v\":" + PlayerShellProtocol.Version, json);
        Assert.Contains("\"type\":\"" + type + "\"", json);

        // Round-trippable as a valid command object, and not mistaken for an inbound message.
        Assert.Equal(ShellMessageKind.Unknown, PlayerShellProtocol.Parse(json).Kind);
    }

    [Fact]
    public void Seek_includes_seconds_and_clamps_negative()
    {
        Assert.Contains("\"type\":\"seek\"", PlayerShellProtocol.Seek(90));
        Assert.Contains("\"seconds\":90", PlayerShellProtocol.Seek(90));
        Assert.Contains("\"seconds\":0", PlayerShellProtocol.Seek(-5));   // never seek to a negative time
    }
}
