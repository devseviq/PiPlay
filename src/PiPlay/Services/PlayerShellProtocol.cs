using System.Text.Json;

namespace PiPlay.Services;

/// <summary>Kind of an inbound (shell -> host) message; <see cref="Unknown"/> for anything unrecognized.</summary>
public enum ShellMessageKind
{
    Unknown,
    Ready,
    State,
    Error,
    Request,
}

/// <summary>A parsed inbound message from the compact shell (spec 10.3). Nullable/defaulted fields
/// carry only what the <see cref="ShellMessageKind"/> defines; the rest stay at safe defaults.</summary>
public sealed record InboundShellMessage(
    ShellMessageKind Kind,
    int CurrentTime = 0,
    int PlayerState = -1,
    int? Duration = null,
    string? ErrorCode = null,
    string? Action = null,
    string? VideoId = null);

/// <summary>
/// The pure, versioned host&lt;-&gt;shell message contract for compact (PiPlay shell) mode
/// (spec 10.3). The single source of truth both <see cref="PlayerShellBridge"/> (host) and
/// <c>player-shell.js</c> (shell) follow. Minimal and local-only: the shell reports ready / state /
/// error; the host commands play / pause / seek / requestState. No credentials, cookies, or tokens
/// ever cross this channel. Parse enforces the exact current <see cref="Version"/> and the exact
/// per-type field set (readiness A-8, mirroring the Focused protocols): a wrong or missing version,
/// an unknown or duplicate top-level field, or any other malformed input fails closed to
/// <see cref="ShellMessageKind.Unknown"/>, never an exception (Q-3/Q-6).
/// </summary>
public static class PlayerShellProtocol
{
    // v3: state messages additionally carry the current videoId (overhaul Task 3 — the shell can
    // move off its launch video via playlist auto-advance or in-iframe clicks, and the host needs
    // the CURRENT video for return). Additive on the wire only: host and shell ship together, so
    // Parse now requires exactly this version and rejects older senders outright (readiness A-8);
    // known fields stay optional per type (defaults fill them) to keep field additions additive.
    public const int Version = 3;

    // Shell -> host message types.
    public const string TypeReady = "ready";
    public const string TypeState = "state";
    public const string TypeError = "error";
    public const string TypeRequest = "request";

    // Host -> shell command types.
    public const string TypePlay = "play";
    public const string TypePause = "pause";
    public const string TypeSeek = "seek";
    public const string TypeRequestState = "requestState";

    // Wire field names — the single source of truth for the JS<->host payload contract. Both this
    // parser and player-shell.js use these exact names; PlayerShellAssetTests pins them in the JS.
    public const string KeyVersion = "v";
    public const string KeyType = "type";
    public const string FieldCurrentTime = "currentTime";
    public const string FieldPlayerState = "playerState";
    public const string FieldDuration = "duration";
    public const string FieldCode = "code";
    public const string FieldSeconds = "seconds";
    public const string FieldAction = "action";
    public const string FieldVideoId = "videoId";

    // Allowlisted shell -> host window actions (spec 12.5 / docs/YouTube_Compliance.md): the shell may
    // REQUEST these; the host validates against this closed set and maps each to the existing
    // native handler. Anything else parses to Unknown — never an exception, never a new
    // capability. Both sides allowlist (player-shell.js mirrors this set).
    public const string ActionClose = "close";
    public const string ActionPinToggle = "pinToggle";
    public const string ActionFullscreenToggle = "fullscreenToggle";

    /// <summary>
    /// True when a web message came from the compact shell's own document. The shell is the only
    /// document served from <paramref name="expectedShellOrigin"/>, so anything else — the nested
    /// YouTube iframe, a stray frame, a look-alike host — is outside this contract and must never
    /// reach <see cref="Parse"/>, let alone an allowlisted window action. Exact origin equality:
    /// scheme, host, and port must all match (scheme and host case-insensitively per RFC 3986;
    /// userinfo, path, query, and fragment are not part of an origin), never a prefix, suffix, or
    /// substring of the host. The expected origin is passed in — the caller supplies
    /// <see cref="WebViewEnvironmentService.ShellOrigin"/> — so the decision stays pure and
    /// testable without a live WebView2. An unparseable source or expected origin is rejected.
    /// </summary>
    public static bool IsTrustedShellSource(string? messageSource, string? expectedShellOrigin)
    {
        if (!Uri.TryCreate(messageSource, UriKind.Absolute, out var source)
            || !Uri.TryCreate(expectedShellOrigin, UriKind.Absolute, out var expected))
        {
            return false;
        }

        return string.Equals(source.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && source.Port == expected.Port;
    }

    /// <summary>Parse one inbound JSON string from the shell. Never throws; unrecognized -> Unknown.</summary>
    public static InboundShellMessage Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new InboundShellMessage(ShellMessageKind.Unknown);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new InboundShellMessage(ShellMessageKind.Unknown);
            if (!root.TryGetProperty(KeyType, out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return new InboundShellMessage(ShellMessageKind.Unknown);
            if (!root.TryGetProperty(KeyVersion, out var versionEl)
                || versionEl.ValueKind != JsonValueKind.Number
                || !versionEl.TryGetInt32(out var version) || version != Version)
                return new InboundShellMessage(ShellMessageKind.Unknown);
            if (!HasExactSchema(root, typeEl.GetString())) return new InboundShellMessage(ShellMessageKind.Unknown);

            return typeEl.GetString() switch
            {
                TypeReady => new InboundShellMessage(ShellMessageKind.Ready),
                TypeState => new InboundShellMessage(
                    ShellMessageKind.State,
                    CurrentTime: ReadInt(root, FieldCurrentTime, 0),
                    PlayerState: ReadInt(root, FieldPlayerState, -1),
                    Duration: ReadNullableInt(root, FieldDuration),
                    VideoId: ReadVideoId(root)),
                TypeError => new InboundShellMessage(ShellMessageKind.Error, ErrorCode: ReadString(root, FieldCode)),
                TypeRequest => ParseRequest(root),
                _ => new InboundShellMessage(ShellMessageKind.Unknown),
            };
        }
        catch (JsonException)
        {
            return new InboundShellMessage(ShellMessageKind.Unknown);
        }
    }

    /// <summary>Host -> shell: resume playback.</summary>
    public static string Play() => Command(TypePlay);

    /// <summary>Host -> shell: pause playback.</summary>
    public static string Pause() => Command(TypePause);

    /// <summary>Host -> shell: request an immediate state message.</summary>
    public static string RequestState() => Command(TypeRequestState);

    /// <summary>Host -> shell: seek to <paramref name="seconds"/> (clamped to non-negative).</summary>
    public static string Seek(int seconds) =>
        $"{{\"{KeyVersion}\":{Version},\"{KeyType}\":\"{TypeSeek}\",\"{FieldSeconds}\":{Math.Max(0, seconds)}}}";

    private static string Command(string type) => $"{{\"{KeyVersion}\":{Version},\"{KeyType}\":\"{type}\"}}";

    /// <summary>A request is only a request when its action is on the allowlist; anything else
    /// degrades to Unknown so an injected or future action can never reach a host handler.</summary>
    private static InboundShellMessage ParseRequest(JsonElement root)
    {
        var action = ReadString(root, FieldAction);
        return action is ActionClose or ActionPinToggle or ActionFullscreenToggle
            ? new InboundShellMessage(ShellMessageKind.Request, Action: action)
            : new InboundShellMessage(ShellMessageKind.Unknown);
    }

    /// <summary>
    /// Exact top-level schema per message type, mirroring the Focused protocols (readiness A-8):
    /// membership rejects unknown fields, the seen-mask rejects duplicates (JSON duplicates yield
    /// multiple properties), and known fields stay optional per type (defaults fill them),
    /// preserving the additive-evolution contract.
    /// </summary>
    private static bool HasExactSchema(JsonElement root, string? type) => (type ?? "") switch
    {
        TypeReady => HasOnlyFields(root, KeyVersion, KeyType),
        TypeState => HasOnlyFields(root, KeyVersion, KeyType, FieldCurrentTime, FieldPlayerState,
            FieldDuration, FieldVideoId),
        TypeError => HasOnlyFields(root, KeyVersion, KeyType, FieldCode),
        TypeRequest => HasOnlyFields(root, KeyVersion, KeyType, FieldAction),
        _ => false,
    };

    private static bool HasOnlyFields(JsonElement root, params string[] allowed)
    {
        int seen = 0;
        foreach (var property in root.EnumerateObject())
        {
            var index = Array.IndexOf(allowed, property.Name);
            if (index < 0 || (seen & (1 << index)) != 0) return false;
            seen |= 1 << index;
        }
        return true;
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : fallback;

    private static int? ReadNullableInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>
    /// FieldVideoId carries a YouTube video id BY CONTRACT: a value that is not the 11-char id
    /// shape is malformed input from the (untrusted) shell and parses as absent — the gate lives
    /// in the parser so EVERY consumer of the parsed message is protected, not just the current
    /// one (the host later turns this string into a source navigation target on close).
    /// </summary>
    private static string? ReadVideoId(JsonElement root)
    {
        var raw = ReadString(root, FieldVideoId);
        return YouTubeUrlHelper.IsVideoId(raw) ? raw : null;
    }
}
