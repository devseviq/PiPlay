namespace PiPlay.Models;

/// <summary>
/// State reported by the Popout Player back to the Source Window when it closes (spec 14).
/// The Source Window owns <c>sourceWasPlayingAtPopout</c> (captured before pausing) and
/// uses it together with <see cref="LastKnownSeconds"/> to honor REQ-RETURN-01.
/// </summary>
public sealed class PlayerReturnState
{
    /// <summary>Last known player timestamp. Nullable; 0 is valid and distinct from unknown.</summary>
    public int? LastKnownSeconds { get; set; }

    /// <summary>Last known paused state from the popout. Null means unknown, so the source fallback applies.</summary>
    public bool? Paused { get; set; }

    /// <summary>Last known media volume from the popout video element, in the 0..1 range.</summary>
    public double? Volume { get; set; }

    /// <summary>Last known muted state from the popout video element.</summary>
    public bool? Muted { get; set; }

    /// <summary>Last known playback rate from the popout video element.</summary>
    public double? PlaybackRate { get; set; }

    /// <summary>
    /// The video the player was LAST on (overhaul Task 3). The popout can move off its launch
    /// video — compact recommendations/playlist auto-advance, normal-page SPA navigation — and the
    /// source must return to where the user actually is, not blind-seek the original video to a
    /// foreign timestamp. Null = unknown; the source then keeps the pre-Task-3 seek behavior.
    /// </summary>
    public string? VideoId { get; set; }

    /// <summary>
    /// The playlist the player was last inside, if any (spec 22.1: return preserves the playlist
    /// context). Follows <see cref="VideoId"/>: updated on every tracked navigation, and null when
    /// the current video is not part of a playlist — a stale list must not ride a video that left it.
    /// </summary>
    public string? PlaylistId { get; set; }

    /// <summary>
    /// The Popout closed because the shared browser process died (PP-02). Both windows share one
    /// environment, so the Source's core is dead too; the Source must recover before it acts on
    /// this state, whichever window's failure notification arrived first.
    /// </summary>
    public bool BrowserProcessFailed { get; set; }

    public bool Topmost { get; set; }

    /// <summary>Whether controls fade was enabled when the player closed (persisted for next popout).</summary>
    public bool FadeEnabled { get; set; }

    public PlacementData? Placement { get; set; }
}
