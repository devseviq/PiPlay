using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PiPlay.Services;

/// <summary>Ad posture used to gate host page writes. Unknown fails closed (YouTube_Compliance.md).</summary>
public enum YouTubeAdState
{
    Clear,
    Ad,
    Unknown
}

/// <summary>A snapshot of the YouTube &lt;video&gt; element. Duration is nullable (live/unknown).</summary>
public sealed record PlayerState(
    int CurrentTime,
    bool Paused,
    int? Duration,
    double? Volume,
    bool? Muted,
    double? PlaybackRate);

/// <summary>
/// The ONE place that talks to the YouTube page DOM (spec 12.5, Q-3). All JavaScript is
/// centralized here, uses the resilient selector from spec 26.3, and is best-effort:
/// every call swallows and logs failures so script errors can never crash the app.
/// </summary>
public static class YouTubeDomBridge
{
    internal static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(5);

    // Resilient video selector (spec 26.3).
    private const string VideoSelector =
        "(document.querySelector('#movie_player video.html5-main-video')" +
        "||document.querySelector('video.html5-main-video')" +
        "||document.querySelector('video'))";

    // YouTube_Compliance.md: while the player element carries an ad class, PiPlay must not write
    // currentTime, change playback rate, or invoke Next; a missing player is unknown and fails
    // closed. Host writers embed this guard so the probe and the write are one atomic script.
    // The Focused overlay's isAdActive uses the same selector and class names.
    private const string AdPlayerSelector = "#movie_player,.html5-video-player";
    private const string AdShowingClass = "ad-showing";
    private const string AdInterruptingClass = "ad-interrupting";

    private static readonly string AdStateGuardScript = $@"
  const adPlayer = document.querySelector('{AdPlayerSelector}');
  const adState = !adPlayer ? 'unknown'
    : (adPlayer.classList.contains('{AdShowingClass}') || adPlayer.classList.contains('{AdInterruptingClass}')) ? 'ad' : 'clear';";

    private static readonly string ReadStateScript = $@"
(() => {{
  const v = {VideoSelector};
  if (!v) return null;
  return {{
    currentTime: Math.floor(v.currentTime || 0),
    paused: !!v.paused,
    duration: Number.isFinite(v.duration) ? Math.floor(v.duration) : null,
    volume: Number.isFinite(v.volume) ? v.volume : null,
    muted: typeof v.muted === 'boolean' ? v.muted : null,
    playbackRate: Number.isFinite(v.playbackRate) ? v.playbackRate : null
  }};
}})()";

    private const string CanonicalUrlScript = @"
(() => {
  const link = document.querySelector('link[rel=""canonical""]');
  if (link && link.href) return link.href;
  return location.href;
})()";

    // Header renderers (the Play All button) come before list rows in document order, and
    // querySelectorAll returns document order — so the playlist's own start link wins when present.
    private const string FirstPlaylistItemScript = @"
(() => {
  const links = document.querySelectorAll(
    'ytd-playlist-header-renderer a[href*=""/watch""], ' +
    'yt-page-header-renderer a[href*=""/watch""], ' +
    'ytd-playlist-video-renderer a[href*=""/watch""], ' +
    'yt-lockup-view-model a[href*=""/watch""]');
  for (const a of links) {
    if (a.href) return a.href;
  }
  return null;
})()";

    private static readonly string SuppressPlaybackScript = $@"
(() => {{
  const v = {VideoSelector};
  if (!v) return false;
  try {{
    v.muted = true;
    v.pause();
    return v.muted === true && v.paused === true;
  }} catch (e) {{
    return false;
  }}
}})()";

    // A success in one operation must not reopen the log gate for an unrelated persistent failure
    // (for example, a healthy 4 Hz player-state read beside a failing 1 Hz Source suppression).
    // Weak keys keep closed/disposed WebViews collectible.
    private static readonly ConditionalWeakTable<CoreWebView2, DomFailureState> FailureStates = new();

    /// <summary>Read current time / paused / duration, or null if no video or the read failed.</summary>
    public static async Task<PlayerState?> ReadPlayerStateAsync(CoreWebView2 webView)
    {
        var raw = await ExecuteAsync(webView, ReadStateScript, "player-state read");
        if (raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var currentTime = root.GetProperty("currentTime").GetInt32();
            var paused = root.GetProperty("paused").GetBoolean();
            int? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt32()
                : null;
            double? volume = root.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble()
                : null;
            bool? muted = root.TryGetProperty("muted", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? m.GetBoolean()
                : null;
            double? playbackRate = root.TryGetProperty("playbackRate", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetDouble()
                : null;
            return new PlayerState(currentTime, paused, duration, volume, muted, playbackRate);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to parse YouTube player state.", ex);
            return null;
        }
    }

    public static Task PauseAsync(CoreWebView2 webView) =>
        ExecuteVoidAsync(webView, $"(() => {{ const v = {VideoSelector}; if (v) v.pause(); }})()", "pause");

    /// <summary>
    /// Mute and pause Source playback. True means the script found a video and observed both
    /// post-command states; false means no video, script refusal, or host execution failure.
    /// </summary>
    public static async Task<bool> SuppressPlaybackAsync(CoreWebView2 webView)
    {
        var result = await ExecuteRawAsync(webView, SuppressPlaybackScript, "source suppression");
        return result.Succeeded && IsTrue(result.Value);
    }

    /// <summary>Injected executor seam for the suppression acknowledgement contract.</summary>
    internal static async Task<bool> SuppressPlaybackAsync(
        Func<string, Task<string>> executeScriptAsync,
        TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(executeScriptAsync);
        try
        {
            var value = await AsyncOperationDeadline.RunAsync(
                _ => executeScriptAsync(SuppressPlaybackScript),
                executionTimeout ?? ExecutionTimeout);
            return IsTrue(value);
        }
        catch { return false; }
    }

    public static Task PlayAsync(CoreWebView2 webView) =>
        ExecuteVoidAsync(webView,
            $"(() => {{ const v = {VideoSelector}; if (v) {{ const p = v.play(); if (p && p.catch) p.catch(() => {{}}); }} }})()",
            "play");

    /// <summary>
    /// Classify the page's current ad posture. Clear means host seek/rate writes may run;
    /// Ad and Unknown (including execution failure) must keep them disabled.
    /// </summary>
    public static async Task<YouTubeAdState> ReadAdStateAsync(CoreWebView2 webView)
    {
        var raw = await ExecuteAsync(webView, BuildAdStateProbeScript(), "ad-state read");
        return ClassifyAdStateResult(raw);
    }

    /// <summary>Injected executor seam for the ad-state classification contract.</summary>
    internal static async Task<YouTubeAdState> ReadAdStateAsync(
        Func<string, Task<string>> executeScriptAsync,
        TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(executeScriptAsync);
        try
        {
            var value = await AsyncOperationDeadline.RunAsync(
                _ => executeScriptAsync(BuildAdStateProbeScript()),
                executionTimeout ?? ExecutionTimeout);
            return ClassifyAdStateResult(value);
        }
        catch { return YouTubeAdState.Unknown; }
    }

    internal static string BuildAdStateProbeScript() => $@"
(() => {{
  {AdStateGuardScript}
  return adState;
}})()";

    private static YouTubeAdState ClassifyAdStateResult(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return YouTubeAdState.Unknown;
        try
        {
            return JsonSerializer.Deserialize<string?>(raw) switch
            {
                "clear" => YouTubeAdState.Clear,
                "ad" => YouTubeAdState.Ad,
                _ => YouTubeAdState.Unknown
            };
        }
        catch
        {
            return YouTubeAdState.Unknown;
        }
    }

    public static Task SeekAsync(CoreWebView2 webView, int seconds) =>
        ExecuteVoidAsync(webView, BuildSeekScript(seconds), "seek");

    internal static string BuildSeekScript(int seconds) => $@"
(() => {{
  {AdStateGuardScript}
  const v = {VideoSelector};
  if (v && adState === 'clear') {{ try {{ v.currentTime = {seconds.ToString(CultureInfo.InvariantCulture)}; }} catch (e) {{}} }}
}})()";

    public static Task SeekAndPauseAsync(CoreWebView2 webView, int seconds) =>
        ExecuteVoidAsync(webView, BuildSeekAndPauseScript(seconds), "seek-and-pause");

    internal static string BuildSeekAndPauseScript(int seconds) => $@"
(() => {{
  {AdStateGuardScript}
  const v = {VideoSelector};
  if (!v) return;
  if (adState === 'clear') {{ try {{ v.currentTime = {seconds.ToString(CultureInfo.InvariantCulture)}; }} catch (e) {{}} }}
  v.pause();
}})()";

    public static Task SeekAndPlayAsync(CoreWebView2 webView, int seconds) =>
        ExecuteVoidAsync(webView, BuildSeekAndPlayScript(seconds), "seek-and-play");

    internal static string BuildSeekAndPlayScript(int seconds) => $@"
(() => {{
  {AdStateGuardScript}
  const v = {VideoSelector};
  if (!v) return;
  if (adState === 'clear') {{ try {{ v.currentTime = {seconds.ToString(CultureInfo.InvariantCulture)}; }} catch (e) {{}} }}
  const p = v.play(); if (p && p.catch) p.catch(() => {{}});
}})()";

    public static Task ApplyPlaybackSettingsAsync(
        CoreWebView2 webView, double? volume, bool? muted, double? playbackRate)
    {
        var script = BuildPlaybackSettingsScript(volume, muted, playbackRate);
        return script is null ? Task.CompletedTask : ExecuteVoidAsync(webView, script, "playback-settings apply");
    }

    internal static string? BuildPlaybackSettingsScript(double? volume, bool? muted, double? playbackRate)
    {
        if (volume is null && muted is null && playbackRate is null) return null;

        static string Js(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var volumeScript = volume is null
            ? string.Empty
            : $"const volume = {Js(Math.Clamp(volume.Value, 0.0, 1.0))}; if (Number.isFinite(volume)) v.volume = volume;";
        var mutedScript = muted is null
            ? string.Empty
            : $"v.muted = {(muted.Value ? "true" : "false")};";
        var rateScript = playbackRate is null
            ? string.Empty
            : $"const rate = {Js(playbackRate.Value)}; if (Number.isFinite(rate) && rate > 0) {{ try {{ v.playbackRate = rate; }} catch (e) {{}} }}";

        return $@"
(() => {{
  {AdStateGuardScript}
  const v = {VideoSelector};
  if (!v) return;
  {volumeScript}
  {mutedScript}
  if (adState === 'clear') {{
    {rateScript}
  }}
}})()";
    }

    /// <summary>Read the page's canonical URL (or location.href) for the currently playing item.</summary>
    public static async Task<string?> ReadCanonicalUrlAsync(CoreWebView2 webView)
    {
        var raw = await ExecuteAsync(webView, CanonicalUrlScript, "canonical-url read");
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Read the first playable item's watch link off a playlist browse page (the header's Play All,
    /// else the first list row), or null when nothing playable is rendered. Best-effort (Q-3): the
    /// caller degrades to popping the playlist page itself. The result is a page-provided string —
    /// callers must strictly re-parse it (PopoutTargetResolver.WithFirstPlaylistItem) and never
    /// navigate it raw.
    /// </summary>
    public static async Task<string?> ReadFirstPlaylistItemUrlAsync(CoreWebView2 webView)
    {
        var raw = await ExecuteAsync(webView, FirstPlaylistItemScript, "playlist first-item read");
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Build the document-created script used by <see cref="PlayerSurfaceDragBridge"/>. Kept here
    /// with every other YouTube DOM selector (Q-3): the bridge owns WebView lifecycle/security,
    /// while this class remains the one auditable source for page behavior.
    /// </summary>
    internal static string BuildPassiveSurfaceDragScript(
        string nonce,
        double horizontalThresholdDip,
        double verticalThresholdDip)
    {
        var nonceJson = JsonSerializer.Serialize(nonce);
        var horizontal = Math.Max(1.0, horizontalThresholdDip).ToString("R", CultureInfo.InvariantCulture);
        var vertical = Math.Max(1.0, verticalThresholdDip).ToString("R", CultureInfo.InvariantCulture);

        return $$"""
(() => {
  "use strict";
  if (window.top !== window || window.__piplaySurfaceDragInstalled) return;
  window.__piplaySurfaceDragInstalled = true;

  const nonce = {{nonceJson}};
  const thresholdX = {{horizontal}};
  const thresholdY = {{vertical}};
  const interactiveSelector = [
    "a", "button", "input", "select", "textarea",
    "[role='button']", "[role='slider']", "[role='menuitem']", "[contenteditable='true']",
    "[data-piplay-no-drag]", ".piplay-focused-overlay",
    ".ytp-progress-bar-container", ".ytp-volume-area",
    ".ytp-subtitles-button", ".ytp-settings-button", ".ytp-fullscreen-button",
    ".ytp-popup", ".ytp-settings-menu", ".ytp-menuitem",
    ".ytp-ce-element", ".ytp-endscreen-content", ".ytp-cards-button", ".ytp-cards-teaser",
    ".ytp-caption-window-container", ".ytp-ad-overlay-container", ".ytp-ad-player-overlay"
  ].join(",");

  let armed = null;
  let suppressClick = false;
  let documentToken = null;

  window.__piplaySurfaceDrag = {
    authorizeDocument(nextToken) {
      documentToken = typeof nextToken === "string" && nextToken.length > 0 ? nextToken : null;
      clearGesture();
      clearSuppression();
    }
  };

  function pathFor(event) {
    return typeof event.composedPath === "function" ? event.composedPath() : [event.target];
  }

  function isElement(value) {
    return value && value.nodeType === Node.ELEMENT_NODE;
  }

  function isPassivePlayerPixel(event) {
    const path = pathFor(event);
    let insidePlayer = false;
    for (const item of path) {
      if (!isElement(item)) continue;
      if (item.matches && item.matches(interactiveSelector)) return false;
      if (item.id === "movie_player" || (item.classList && item.classList.contains("html5-video-player")))
        insidePlayer = true;
    }
    if (!insidePlayer && event.target && event.target.closest)
      insidePlayer = !!event.target.closest("#movie_player,.html5-video-player");
    return insidePlayer;
  }

  function clearGesture() {
    armed = null;
  }

  function clearSuppression() {
    suppressClick = false;
  }

  window.addEventListener("pointerdown", event => {
    clearSuppression();
    clearGesture();
    if (!documentToken) return;
    if (!event.isTrusted || !event.isPrimary || event.button !== 0) return;
    if (event.pointerType !== "mouse" && event.pointerType !== "pen") return;
    if (document.fullscreenElement || document.pointerLockElement) return;
    if (!isPassivePlayerPixel(event)) return;
    armed = { id: event.pointerId, x: event.clientX, y: event.clientY,
      captureTarget: event.target, posted: false };
  }, true);

  window.addEventListener("pointermove", event => {
    if (!event.isTrusted || !armed || armed.id !== event.pointerId || armed.posted) return;
    if ((event.buttons & 1) !== 1) { clearGesture(); return; }
    const dx = Math.abs(event.clientX - armed.x);
    const dy = Math.abs(event.clientY - armed.y);
    if (dx < thresholdX && dy < thresholdY) return;

    try {
      const target = armed.captureTarget;
      if (target && typeof target.hasPointerCapture === "function" &&
          target.hasPointerCapture(armed.id)) target.releasePointerCapture(armed.id);
    } catch (_) { /* the asynchronous native move command remains the safe fallback */ }
    armed.posted = true;
    suppressClick = true;
    event.preventDefault();
    event.stopImmediatePropagation();
    try {
      const host = window.chrome && window.chrome.webview;
      if (host) host.postMessage(JSON.stringify({
        channel: "piplay.window", v: 1, type: "dragStart", nonce: nonce,
        documentToken: documentToken
      }));
    } catch (_) { /* best-effort: native strip remains the recovery drag path */ }
  }, { capture: true, passive: false });

  window.addEventListener("pointerup", clearGesture, true);
  window.addEventListener("pointercancel", clearGesture, true);
  window.addEventListener("blur", () => { clearGesture(); clearSuppression(); }, true);
  window.addEventListener("click", event => {
    if (!suppressClick) return;
    clearSuppression();
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);
})();
""";
    }

    /// <summary>Authorize the passive-drag script in the matching completed document.</summary>
    internal static string BuildPassiveSurfaceDocumentTokenScript(string token)
    {
        var tokenJson = JsonSerializer.Serialize(token);
        return $$"""
(() => {
  const documentToken = {{tokenJson}};
  const surface = window.__piplaySurfaceDrag;
  if (!surface || typeof surface.authorizeDocument !== "function") return false;
  surface.authorizeDocument(documentToken);
  return true;
})()
""";
    }

    /// <summary>
    /// Build the optional Focused-presentation script. It keeps the real watch-page video and
    /// native YouTube controls, but gives the player the viewport and adds a pointer-transparent,
    /// media-first control layer. Every selector is best-effort and idempotent (Q-3/Q-5).
    /// </summary>
    internal static string BuildPlayerFirstSurfaceScript(
        string nonce,
        string accentColor,
        bool fadeEnabled,
        int fadeDelayMs,
        bool topmost = false)
    {
        var nonceJson = JsonSerializer.Serialize(nonce);
        var accentJson = JsonSerializer.Serialize(accentColor);
        var delay = Math.Clamp(fadeDelayMs, 500, 10_000);
        var fade = fadeEnabled ? "true" : "false";
        var adShowingJson = JsonSerializer.Serialize(AdShowingClass);
        var adInterruptingJson = JsonSerializer.Serialize(AdInterruptingClass);

        return $$"""
(() => {
  "use strict";
  if (window.top !== window || window.__piplayFocusedSurface) return;

  const nonce = {{nonceJson}};
  const STYLE_ID = "piplay-focused-surface-style";
  const ROOT_ID = "piplay-focused-overlay";
  const ACTIVE_CLASS = "piplay-focused-active";
  const config = { accent: {{accentJson}}, fadeEnabled: {{fade}}, fadeDelayMs: {{delay}}, topmost: {{(topmost ? "true" : "false")}} };
  let root = null;
  let controls = null;
  let boundPlayer = null;
  let boundMedia = null;
  let adObserver = null;
  let hideTimer = 0;
  let updateTimer = 0;
  let mutationQueued = false;
  let surfaceActive = null;
  let documentToken = null;
  let lastPaused = null;
  let lastPointerRevealAt = 0;

  const icons = {
    volume: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 9v6h4l5 4V5L8 9H4zm12.2-.8a5.4 5.4 0 0 1 0 7.6M18.8 5.6a9 9 0 0 1 0 12.8"/></svg>',
    pin: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3h8l-1 5 3 3v2h-5v8l-2-2v-6H6v-2l3-3-1-5z"/></svg>',
    settings: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9L7 7M17 17l2.1 2.1M19.1 4.9L17 7M7 17l-2.1 2.1"/></svg>',
    expand: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5"/></svg>',
    close: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5l14 14M19 5L5 19"/></svg>',
    play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path class="fill" d="M8 5l11 7-11 7z"/></svg>',
    pause: '<svg viewBox="0 0 24 24" aria-hidden="true"><path class="fill" d="M7 5h4v14H7zM13 5h4v14h-4z"/></svg>',
    next: '<svg viewBox="0 0 24 24" aria-hidden="true"><path class="fill" d="M5 5l10 7-10 7zM16 5h3v14h-3z"/></svg>'
  };

  function ensureStyle() {
    if (document.getElementById(STYLE_ID) || !document.head) return;
    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      html.${ACTIVE_CLASS}, html.${ACTIVE_CLASS} body {
        overflow: hidden !important; background: #000 !important;
      }
      html.${ACTIVE_CLASS} #movie_player,
      html.${ACTIVE_CLASS} .html5-video-player {
        position: fixed !important; inset: 0 !important;
        width: 100vw !important; height: 100vh !important;
        min-width: 0 !important; min-height: 0 !important;
        max-width: none !important; max-height: none !important;
        margin: 0 !important; z-index: 2147483000 !important; background: #000 !important;
      }
      html.${ACTIVE_CLASS} #movie_player .html5-video-container {
        position: absolute !important; inset: 0 !important;
        width: 100% !important; height: 100% !important;
      }
      html.${ACTIVE_CLASS} #movie_player video.html5-main-video,
      html.${ACTIVE_CLASS} video.html5-main-video {
        position: absolute !important; inset: 0 !important;
        width: 100% !important; height: 100% !important;
        left: 0 !important; top: 0 !important;
        object-fit: contain !important; background: #000 !important;
      }
      .piplay-focused-overlay {
        --piplay-accent: ${config.accent};
        position: fixed; inset: 0; z-index: 2147483646;
        color: #fff; font: 500 13px/1.2 "Segoe UI", system-ui, sans-serif;
        pointer-events: none; opacity: 0; transition: opacity 180ms ease;
      }
      .piplay-focused-overlay::before,
      .piplay-focused-overlay::after {
        content: ""; position: absolute; left: 0; right: 0; pointer-events: none;
        opacity: 0; transition: opacity 180ms ease;
      }
      .piplay-focused-overlay::before {
        top: 0; height: 92px; background: linear-gradient(to bottom, rgba(0,0,0,.58), transparent);
      }
      .piplay-focused-overlay::after {
        bottom: 0; height: 105px; background: linear-gradient(to top, rgba(0,0,0,.64), transparent);
      }
      .piplay-focused-overlay.is-visible,
      .piplay-focused-overlay.is-visible::before,
      .piplay-focused-overlay.is-visible::after { opacity: 1; }
      .piplay-focused-top,
      .piplay-focused-center,
      .piplay-focused-bottom {
        position: absolute; display: flex; align-items: center; pointer-events: none;
        transition: transform 180ms ease;
      }
      .piplay-focused-top { top: 16px; left: 16px; right: 16px; justify-content: space-between; }
      .piplay-focused-center { inset: 50% auto auto 50%; transform: translate(-50%,-46%); gap: 12px; }
      /* Sit above YouTube's native bottom chrome so quality/settings/fullscreen remain reachable. */
      .piplay-focused-bottom { left: 20px; right: 20px; bottom: 64px; gap: 10px; }
      .piplay-focused-overlay:not(.is-visible) .piplay-focused-top { transform: translateY(-8px); }
      .piplay-focused-overlay:not(.is-visible) .piplay-focused-center { transform: translate(-50%,-38%); }
      .piplay-focused-overlay:not(.is-visible) .piplay-focused-bottom { transform: translateY(8px); }
      .piplay-focused-group { display: flex; align-items: center; gap: 8px; pointer-events: none; }
      .piplay-focused-button {
        width: 38px; height: 38px; padding: 0; border-radius: 12px;
        border: 1px solid rgba(255,255,255,.18); color: #fff;
        background: rgba(8,12,18,.54); backdrop-filter: blur(14px);
        display: grid; place-items: center; pointer-events: none; cursor: pointer;
        transition: background 120ms ease, border-color 120ms ease, transform 120ms ease;
      }
      .piplay-focused-button:hover { background: rgba(35,43,55,.8); border-color: rgba(255,255,255,.34); }
      .piplay-focused-button:active { transform: scale(.94); }
      .piplay-focused-button:focus-visible { outline: 2px solid var(--piplay-accent); outline-offset: 2px; }
      .piplay-focused-button[aria-pressed="true"] { color: var(--piplay-accent); border-color: color-mix(in srgb, var(--piplay-accent) 65%, transparent); }
      .piplay-focused-button svg { width: 20px; height: 20px; fill: none; stroke: currentColor; stroke-width: 1.9; stroke-linecap: round; stroke-linejoin: round; }
      .piplay-focused-button svg .fill { fill: currentColor; stroke: none; }
      .piplay-focused-button.is-primary { width: 50px; height: 50px; border-radius: 16px; background: rgba(248,250,252,.94); color: #071018; border-color: transparent; }
      .piplay-focused-button.is-primary:hover { background: #fff; }
      .piplay-focused-button.is-danger:hover { background: rgba(180,30,44,.82); }
      .piplay-focused-cc { font-size: 11px; font-weight: 800; letter-spacing: -.4px; }
      .piplay-focused-time { min-width: 42px; font-variant-numeric: tabular-nums; text-shadow: 0 1px 3px #000; }
      .piplay-focused-time:last-child { text-align: right; }
      .piplay-focused-progress {
        appearance: none; -webkit-appearance: none; height: 18px; flex: 1;
        margin: 0; background: transparent; pointer-events: none; cursor: pointer;
      }
      .piplay-focused-overlay.is-visible .piplay-focused-button,
      .piplay-focused-overlay.is-visible .piplay-focused-progress { pointer-events: auto; }
      .piplay-focused-progress::-webkit-slider-runnable-track {
        height: 4px; border-radius: 999px;
        background: linear-gradient(to right,
          var(--piplay-accent) 0 var(--piplay-progress, 0%),
          rgba(255,255,255,.38) var(--piplay-progress, 0%) 100%);
      }
      .piplay-focused-progress::-webkit-slider-thumb {
        appearance: none; -webkit-appearance: none; width: 14px; height: 14px;
        margin-top: -5px; border: 0; border-radius: 50%; background: var(--piplay-accent);
        box-shadow: 0 0 0 3px rgba(0,0,0,.28);
      }
      .piplay-focused-progress:focus-visible { outline: 2px solid var(--piplay-accent); outline-offset: 2px; }
      .piplay-focused-button:disabled { opacity: .35; cursor: default; }
      /* Custom seeking/advance can alter ad playback. Leave YouTube's native ad UI untouched. */
      .piplay-focused-overlay.is-ad .piplay-focused-bottom { display: none; }
      .piplay-focused-overlay.is-ad .piplay-focused-center { display: none; }
      .piplay-focused-overlay.is-ad::after { opacity: 0; }
      @media (max-width: 520px), (max-height: 300px) {
        .piplay-focused-top { top: 9px; left: 9px; right: 9px; }
        .piplay-focused-bottom { left: 12px; right: 12px; bottom: 52px; }
        .piplay-focused-button { width: 34px; height: 34px; border-radius: 10px; }
        .piplay-focused-button.is-primary { width: 44px; height: 44px; border-radius: 14px; }
        .piplay-focused-time { display: none; }
      }
    `;
    document.head.appendChild(style);
  }

  function button(action, label, icon, extraClass = "") {
    return `<button type="button" class="piplay-focused-button ${extraClass}" data-action="${action}" data-piplay-no-drag="true" aria-label="${label}" title="${label}">${icon}</button>`;
  }

  function buildRoot() {
    const overlay = document.createElement("div");
    overlay.id = ROOT_ID;
    overlay.className = "piplay-focused-overlay is-visible";
    overlay.setAttribute("data-piplay-no-drag", "true");
    overlay.innerHTML = `
      <div class="piplay-focused-top">
        <div class="piplay-focused-group">
          ${button("mute", "Mute", icons.volume)}
          ${button("captions", "Captions", '<span class="piplay-focused-cc" aria-hidden="true">CC</span>')}
          ${button("settings", "PiPlay settings", icons.settings)}
          ${button("pinToggle", "Pin popout on top", icons.pin)}
        </div>
        <div class="piplay-focused-group">
          ${button("fullscreenToggle", "Expand or restore popout", icons.expand)}
          ${button("close", "Close popout and return video", icons.close, "is-danger")}
        </div>
      </div>
      <div class="piplay-focused-center">
        ${button("playPause", "Play", icons.play, "is-primary")}
        ${button("next", "Next video", icons.next)}
      </div>
      <div class="piplay-focused-bottom">
        <span class="piplay-focused-time" data-time="current">0:00</span>
        <input class="piplay-focused-progress" data-action="seek" data-piplay-no-drag="true"
               type="range" min="0" max="1000" value="0" step="1" aria-label="Video progress" title="Video progress" />
        <span class="piplay-focused-time" data-time="duration">0:00</span>
      </div>`;
    return overlay;
  }

  function isWatchPage() {
    const host = location.hostname.toLowerCase();
    return (host === "youtube.com" || host.endsWith(".youtube.com")) && location.pathname === "/watch";
  }

  function playerElement() {
    return document.querySelector("{{AdPlayerSelector}}");
  }

  function video() {
    return document.querySelector("#movie_player video.html5-main-video,video.html5-main-video,video");
  }

  function isAdActive() {
    const player = boundPlayer && boundPlayer.isConnected ? boundPlayer : playerElement();
    // A missing player element is unknown ad state and fails closed (YouTube_Compliance.md).
    if (!player) return true;
    return player.classList.contains({{adShowingJson}}) || player.classList.contains({{adInterruptingJson}});
  }

  function formatTime(value) {
    if (!Number.isFinite(value) || value < 0) return "0:00";
    const total = Math.floor(value);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = String(total % 60).padStart(2, "0");
    return hours ? `${hours}:${String(minutes).padStart(2, "0")}:${seconds}` : `${minutes}:${seconds}`;
  }

  function postWindowAction(action, trustedEvent) {
    if (!documentToken) return;
    if (!trustedEvent || !["close", "pinToggle", "fullscreenToggle", "settings"].includes(action)) return;
    try {
      const host = window.chrome && window.chrome.webview;
      if (host) host.postMessage(JSON.stringify({
        channel: "piplay.focused", v: 1, type: "request", nonce: nonce, documentToken: documentToken, action: action
      }));
    } catch (_) { /* native strip remains available */ }
  }

  function postSurfaceState(active) {
    if (!documentToken) return;
    if (surfaceActive === active) return;
    surfaceActive = active;
    try {
      const host = window.chrome && window.chrome.webview;
      if (host) host.postMessage(JSON.stringify({
        channel: "piplay.focused", v: 1, type: "state", nonce: nonce, documentToken: documentToken, active: active
      }));
    } catch (_) { /* native strip remains visible until a positive state reaches the host */ }
  }

  function cacheControls() {
    controls = root ? {
      play: root.querySelector('[data-action="playPause"]'),
      mute: root.querySelector('[data-action="mute"]'),
      captions: root.querySelector('[data-action="captions"]'),
      pin: root.querySelector('[data-action="pinToggle"]'),
      next: root.querySelector('[data-action="next"]'),
      progress: root.querySelector('[data-action="seek"]'),
      current: root.querySelector('[data-time="current"]'),
      duration: root.querySelector('[data-time="duration"]')
    } : null;
  }

  function setDisabled(element, disabled) {
    const next = !!disabled;
    if (element && element.disabled !== next) element.disabled = next;
  }

  function setAttribute(element, name, value) {
    const next = String(value);
    if (element && element.getAttribute(name) !== next) element.setAttribute(name, next);
  }

  function setText(element, value) {
    if (element && element.textContent !== value) element.textContent = value;
  }

  function setIcon(element, name, markup) {
    if (!element || element.dataset.piplayIcon === name) return;
    element.innerHTML = markup;
    element.dataset.piplayIcon = name;
  }

  function toggleClass(element, name, enabled) {
    if (element && element.classList.contains(name) !== enabled)
      element.classList.toggle(name, enabled);
  }

  function setAccent() {
    if (root && root.style.getPropertyValue("--piplay-accent") !== config.accent)
      root.style.setProperty("--piplay-accent", config.accent);
  }

  function updateProgress() {
    if (!root || root.hidden || !controls) return;
    const media = boundMedia;
    const adActive = isAdActive();
    toggleClass(root, "is-ad", adActive);
    const length = media && Number.isFinite(media.duration) ? media.duration : 0;
    setDisabled(controls.progress, adActive || length <= 0);
    if (media && !adActive && controls.progress &&
        !controls.progress.matches(":active") && document.activeElement !== controls.progress) {
      const ratio = length > 0 ? Math.max(0, Math.min(1, media.currentTime / length)) : 0;
      const value = String(Math.round(ratio * 1000));
      const fill = `${ratio * 100}%`;
      if (controls.progress.value !== value) controls.progress.value = value;
      if (controls.progress.style.getPropertyValue("--piplay-progress") !== fill)
        controls.progress.style.setProperty("--piplay-progress", fill);
    }
    setText(controls.current, formatTime(media ? media.currentTime : 0));
    setText(controls.duration, formatTime(length));
  }

  function onMediaStateChanged() {
    updateControls();
  }

  const mediaStateEvents = ["play", "pause", "ended", "volumechange", "durationchange", "loadedmetadata", "emptied", "seeking", "seeked"];

  function bindMedia(nextMedia) {
    if (boundMedia === nextMedia) return;
    if (boundMedia) {
      boundMedia.removeEventListener("timeupdate", updateProgress);
      for (const name of mediaStateEvents) boundMedia.removeEventListener(name, onMediaStateChanged);
    }
    boundMedia = nextMedia || null;
    if (boundMedia) {
      boundMedia.addEventListener("timeupdate", updateProgress);
      for (const name of mediaStateEvents) boundMedia.addEventListener(name, onMediaStateChanged);
    }
  }

  function bindPlayer(nextPlayer) {
    if (boundPlayer === nextPlayer) return;
    if (adObserver) adObserver.disconnect();
    adObserver = null;
    boundPlayer = nextPlayer || null;
    if (boundPlayer) {
      adObserver = new MutationObserver(() => updateControls());
      adObserver.observe(boundPlayer, { attributes: true, attributeFilter: ["class"] });
    }
  }

  function stopUpdateTimer() {
    if (updateTimer) window.clearInterval(updateTimer);
    updateTimer = 0;
  }

  function startUpdateTimer() {
    if (!updateTimer) updateTimer = window.setInterval(updateControls, 1000);
  }

  function clearHideTimer() {
    if (hideTimer) window.clearTimeout(hideTimer);
    hideTimer = 0;
  }

  function hasInteractiveAttention() {
    if (!root) return false;
    const focused = document.activeElement && root.contains(document.activeElement);
    const hovered = root.querySelector(".piplay-focused-button:hover,.piplay-focused-progress:hover");
    return !!focused || !!hovered;
  }

  function hideControlsWhenIdle() {
    hideTimer = 0;
    if (!root || root.hidden || !config.fadeEnabled) return;
    if (hasInteractiveAttention()) {
      hideTimer = window.setTimeout(hideControlsWhenIdle, 250);
      return;
    }
    root.classList.remove("is-visible");
  }

  function revealControls() {
    if (!root || root.hidden) return;
    root.classList.add("is-visible");
    clearHideTimer();
    const media = video();
    if (config.fadeEnabled && media && !media.paused)
      hideTimer = window.setTimeout(hideControlsWhenIdle, config.fadeDelayMs);
  }

  function updateControls() {
    if (!root || root.hidden) return;
    if (!controls) cacheControls();
    bindMedia(video());
    const media = boundMedia;
    const adActive = isAdActive();
    if (!media) {
      lastPaused = null;
      setDisabled(controls && controls.play, true);
      setDisabled(controls && controls.next, true);
      updateProgress();
      return;
    }

    if (controls.play) {
      const paused = media.paused;
      const label = paused ? "Play" : "Pause";
      setDisabled(controls.play, false);
      setIcon(controls.play, paused ? "play" : "pause", paused ? icons.play : icons.pause);
      setAttribute(controls.play, "aria-label", label);
      if (controls.play.title !== label) controls.play.title = label;
    }
    if (controls.mute) {
      const muted = media.muted || media.volume === 0;
      const label = muted ? "Unmute" : "Mute";
      setAttribute(controls.mute, "aria-pressed", muted);
      setAttribute(controls.mute, "aria-label", label);
      if (controls.mute.title !== label) controls.mute.title = label;
    }
    if (controls.pin) {
      const label = config.topmost ? "Unpin popout from top" : "Pin popout on top";
      setAttribute(controls.pin, "aria-pressed", config.topmost);
      setAttribute(controls.pin, "aria-label", label);
      if (controls.pin.title !== label) controls.pin.title = label;
    }
    const nativeCaptions = document.querySelector(".ytp-subtitles-button");
    if (controls.captions) {
      setDisabled(controls.captions, !nativeCaptions);
      setAttribute(controls.captions, "aria-pressed",
        !!nativeCaptions && nativeCaptions.getAttribute("aria-pressed") === "true");
    }
    const nativeNext = document.querySelector(".ytp-next-button");
    if (controls.next)
      setDisabled(controls.next, adActive || !nativeNext || nativeNext.getAttribute("aria-disabled") === "true");

    updateProgress();
    const pausedChangedToPlaying = lastPaused === true && !media.paused;
    lastPaused = media.paused;
    if (media.paused) { clearHideTimer(); toggleClass(root, "is-visible", true); }
    else if (pausedChangedToPlaying) revealControls();
  }

  function handleAction(event) {
    if (!event.isTrusted) return;
    const control = event.target && event.target.closest ? event.target.closest("[data-action]") : null;
    if (!control || !root || !root.contains(control)) return;
    revealControls();
    const action = control.dataset.action;
    const media = video();

    if (action === "playPause" && media) {
      if (media.paused) { const play = media.play(); if (play && play.catch) play.catch(() => {}); }
      else media.pause();
    } else if (action === "mute" && media) {
      media.muted = !media.muted;
    } else if (action === "captions") {
      const native = document.querySelector(".ytp-subtitles-button");
      if (native) native.click();
    } else if (action === "next") {
      if (isAdActive()) return;
      const native = document.querySelector(".ytp-next-button:not([aria-disabled='true'])");
      if (native) native.click();
    } else if (action === "pinToggle") {
      postWindowAction(action, event.isTrusted);
    } else if (action === "close" || action === "fullscreenToggle" || action === "settings") {
      postWindowAction(action, event.isTrusted);
    }
    updateControls();
  }

  function handleSeek(event) {
    if (!event.isTrusted) return;
    if (!event.target || event.target.dataset.action !== "seek") return;
    if (isAdActive()) return;
    const media = video();
    if (!media || !Number.isFinite(media.duration) || media.duration <= 0) return;
    media.currentTime = (Number(event.target.value) / 1000) * media.duration;
    revealControls();
  }

  function deactivateSurface() {
    document.documentElement.classList.remove(ACTIVE_CLASS);
    if (root) root.hidden = true;
    clearHideTimer();
    stopUpdateTimer();
    bindMedia(null);
    bindPlayer(null);
    lastPaused = null;
    postSurfaceState(false);
  }

  function install() {
    mutationQueued = false;
    if (!document.documentElement || !document.body) return;
    ensureStyle();
    const player = playerElement();
    if (!isWatchPage() || !player) {
      deactivateSurface();
      return;
    }

    const becameActive = !document.documentElement.classList.contains(ACTIVE_CLASS);
    document.documentElement.classList.add(ACTIVE_CLASS);
    bindPlayer(player);
    let created = false;
    if (!root || !document.contains(root)) {
      root = document.getElementById(ROOT_ID) || buildRoot();
      if (!root.parentNode) document.body.appendChild(root);
      root.addEventListener("click", handleAction);
      root.addEventListener("input", handleSeek);
      root.addEventListener("focusin", revealControls);
      created = true;
    }
    if (created || !controls || !controls.play || !controls.play.isConnected) cacheControls();
    const wasHidden = root.hidden;
    root.hidden = false;
    setAccent();
    if (becameActive || created || wasHidden) revealControls();
    updateControls();
    startUpdateTimer();
    postSurfaceState(true);
  }

  function queueInstall() {
    if (mutationQueued) return;
    mutationQueued = true;
    window.setTimeout(install, 0);
  }

  function revealControlsFromPointer(event) {
    if (surfaceActive !== true || !root || root.hidden || !event.isTrusted) return;
    const now = performance.now();
    if (now - lastPointerRevealAt < 125) return;
    lastPointerRevealAt = now;
    revealControls();
  }

  function revealControlsFromInput(event) {
    if (surfaceActive === true && root && !root.hidden && event.isTrusted) revealControls();
  }

  document.addEventListener("pointermove", revealControlsFromPointer, true);
  document.addEventListener("pointerdown", revealControlsFromInput, true);
  document.addEventListener("keydown", revealControlsFromInput, true);
  window.addEventListener("yt-navigate-finish", queueInstall);
  new MutationObserver(mutations => {
    // Ignore our own overlay construction. While active, only reconnect when the tracked
    // player/media/root is replaced; normal page churn is handled by media events + fallback tick.
    if (root && mutations.every(mutation => root.contains(mutation.target))) return;
    if (surfaceActive === true) {
      if (!isWatchPage() || !root || !root.isConnected || !boundPlayer || !boundPlayer.isConnected ||
          !boundMedia || !boundMedia.isConnected) queueInstall();
      return;
    }
    if (isWatchPage()) queueInstall();
  }).observe(document.documentElement || document, { childList: true, subtree: true });

  window.__piplayFocusedSurface = {
    authorizeDocument(nextToken) {
      documentToken = typeof nextToken === "string" && nextToken.length > 0 ? nextToken : null;
      surfaceActive = null;
    },
    reportState() {
      postSurfaceState(!!(isWatchPage() && root && !root.hidden &&
        document.documentElement.classList.contains(ACTIVE_CLASS)));
    },
    configure(next) {
      if (next && typeof next.accent === "string") config.accent = next.accent;
      if (next && typeof next.fadeEnabled === "boolean") config.fadeEnabled = next.fadeEnabled;
      if (next && Number.isFinite(next.fadeDelayMs)) config.fadeDelayMs = Math.max(500, Math.min(10000, next.fadeDelayMs));
      if (next && typeof next.topmost === "boolean") config.topmost = next.topmost;
      setAccent();
      revealControls();
      updateControls();
    }
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", install, { once: true });
  else install();
})();
""";
    }

    /// <summary>Authorize the Focused script in the matching completed document.</summary>
    internal static string BuildPlayerFirstDocumentTokenScript(string token)
    {
        var tokenJson = JsonSerializer.Serialize(token);
        return $$"""
(() => {
  const documentToken = {{tokenJson}};
  const surface = window.__piplayFocusedSurface;
  if (!surface || typeof surface.authorizeDocument !== "function") return false;
  surface.authorizeDocument(documentToken);
  return true;
})()
""";
    }

    /// <summary>Ask an authorized Focused script to repeat its current recovery-chrome state.</summary>
    internal static string BuildPlayerFirstStateRequestScript() =>
        """
(() => {
  const surface = window.__piplayFocusedSurface;
  if (!surface || typeof surface.reportState !== "function") return false;
  surface.reportState();
  return true;
})()
""";

    /// <summary>Build the small live-update command consumed by an installed Focused surface.</summary>
    internal static string BuildPlayerFirstConfigurationScript(
        string accentColor,
        bool fadeEnabled,
        int fadeDelayMs,
        bool topmost)
    {
        var accentJson = JsonSerializer.Serialize(accentColor);
        var delay = Math.Clamp(fadeDelayMs, 500, 10_000);
        var fade = fadeEnabled ? "true" : "false";
        var pin = topmost ? "true" : "false";
        return $$"""
(() => {
  const surface = window.__piplayFocusedSurface;
  if (surface && typeof surface.configure === "function")
    surface.configure({ accent: {{accentJson}}, fadeEnabled: {{fade}}, fadeDelayMs: {{delay}}, topmost: {{pin}} });
})()
""";
    }

    private static async Task<string?> ExecuteAsync(CoreWebView2 webView, string script, string operation)
    {
        var result = await ExecuteRawAsync(webView, script, operation);
        // WebView2 returns the literal "null" for undefined / thrown / null results.
        if (!result.Succeeded || string.IsNullOrEmpty(result.Value) || result.Value == "null") return null;
        return result.Value;
    }

    private static async Task ExecuteVoidAsync(CoreWebView2 webView, string script, string operation)
    {
        _ = await ExecuteRawAsync(webView, script, operation);
    }

    private static async Task<DomExecutionResult> ExecuteRawAsync(
        CoreWebView2 webView,
        string script,
        string operation)
    {
        var failureState = FailureStates.GetOrCreateValue(webView);
        try
        {
            var value = await AsyncOperationDeadline.RunSingleFlightAsync(
                failureState.ExecutionGate,
                () => webView.ExecuteScriptAsync(script),
                ExecutionTimeout);
            var suppressed = failureState.GateFor(operation).RecordSuccess();
            if (suppressed is int repeatCount)
                Log.Info($"YouTube DOM {operation} recovered; {repeatCount} repeated failure(s) were suppressed.");
            return new DomExecutionResult(true, value);
        }
        catch (Exception ex)
        {
            if (failureState.GateFor(operation).RecordFailure())
                Log.Error($"YouTube DOM {operation} failed; repeated failures are suppressed until recovery.", ex);
            return new DomExecutionResult(false, null);
        }
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private readonly record struct DomExecutionResult(bool Succeeded, string? Value);

    private sealed class DomFailureState
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ConsecutiveFailureGate> _gates =
            new(StringComparer.Ordinal);

        public SemaphoreSlim ExecutionGate { get; } = new(1, 1);

        public ConsecutiveFailureGate GateFor(string operation)
        {
            lock (_sync)
            {
                if (!_gates.TryGetValue(operation, out var gate))
                {
                    gate = new ConsecutiveFailureGate();
                    _gates.Add(operation, gate);
                }
                return gate;
            }
        }
    }
}
