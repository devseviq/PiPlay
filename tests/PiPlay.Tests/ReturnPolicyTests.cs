using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class ReturnPolicyTests
{
    [Theory]
    [InlineData(120, true, ReturnAction.SeekAndPlay)]
    [InlineData(120, false, ReturnAction.Seek)]
    [InlineData(0, true, ReturnAction.SeekAndPlay)]   // 0 is a valid timestamp
    [InlineData(0, false, ReturnAction.Seek)]
    [InlineData(null, true, ReturnAction.Play)]       // unknown timestamp, was playing
    [InlineData(null, false, ReturnAction.None)]      // unknown + paused: do nothing
    public void Decide_matches_REQ_RETURN_01(int? lastKnownSeconds, bool wasPlaying, ReturnAction expected)
    {
        Assert.Equal(expected, ReturnPolicy.Decide(lastKnownSeconds, wasPlaying));
    }

    [Theory]
    [InlineData(120, true, true, ReturnAction.Seek)]
    [InlineData(120, false, false, ReturnAction.SeekAndPlay)]
    [InlineData(null, true, true, ReturnAction.None)]
    [InlineData(null, false, false, ReturnAction.Play)]
    [InlineData(120, true, null, ReturnAction.SeekAndPlay)]
    [InlineData(0, true, true, ReturnAction.Seek)]
    public void Popout_paused_state_overrides_source_launch_state_when_known(
        int? lastKnownSeconds, bool sourceWasPlaying, bool? returnedPaused, ReturnAction expected)
    {
        Assert.Equal(expected, ReturnPolicy.Decide(lastKnownSeconds, sourceWasPlaying, returnedPaused));
    }

    // Video-aware overload (overhaul Task 3): a popout that ended on a DIFFERENT video must make
    // the source NAVIGATE there; seeking the original video to the new video's timestamp is the
    // corruption this fixes.
    [Theory]
    [InlineData(120, true)]
    [InlineData(120, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Differing_video_ids_decide_navigate_regardless_of_timestamp_and_intent(
        int? lastKnownSeconds, bool wasPlaying)
    {
        Assert.Equal(ReturnAction.Navigate,
            ReturnPolicy.Decide(lastKnownSeconds, wasPlaying, returnedPaused: true, "newVideo0001", "oldVideo0001"));
    }

    [Theory]
    [InlineData("sameVideo001", "sameVideo001", 120, true, ReturnAction.SeekAndPlay)]   // unchanged video
    [InlineData(null, "oldVideo0001", 120, false, ReturnAction.Seek)]                   // returned id unknown
    [InlineData("", "oldVideo0001", null, true, ReturnAction.Play)]                     // empty = unknown
    [InlineData("newVideo0001", null, null, false, ReturnAction.None)]                  // source id unknown
    public void Unknown_or_unchanged_ids_fall_back_to_the_timestamp_decision(
        string? returnedId, string? sourceId, int? lastKnownSeconds, bool wasPlaying, ReturnAction expected)
    {
        Assert.Equal(expected, ReturnPolicy.Decide(lastKnownSeconds, wasPlaying, returnedId, sourceId));
    }

    // PP-01: the decision is re-checked against the video the Source is actually showing.
    [Theory]
    [InlineData(ReturnAction.SeekAndPlay, "videoA00001", "videoA00001", "videoA00001", ReturnAction.SeekAndPlay)] // unchanged
    [InlineData(ReturnAction.Seek, "videoA00001", "videoA00001", null, ReturnAction.Seek)]                       // live unknown
    [InlineData(ReturnAction.Seek, "videoA00001", null, "videoB00001", ReturnAction.Seek)]                       // launch unknown
    [InlineData(ReturnAction.Seek, "videoA00001", "videoA00001", "videoB00001", ReturnAction.Navigate)]          // Source moved
    [InlineData(ReturnAction.Play, "", "videoA00001", "videoB00001", ReturnAction.None)]                         // moved, returned unknown
    [InlineData(ReturnAction.Navigate, "videoC00001", "videoA00001", "videoB00001", ReturnAction.Navigate)]      // already navigating
    public void Live_source_identity_overrides_a_same_video_decision_when_the_source_moved(
        ReturnAction action, string? returnedId, string? launchId, string? liveId, ReturnAction expected)
    {
        Assert.Equal(expected, ReturnPolicy.ReconcileWithLiveSource(action, returnedId, launchId, liveId));
    }

    [Fact]
    public void Same_video_with_returned_paused_state_uses_the_popout_state()
    {
        Assert.Equal(ReturnAction.Seek,
            ReturnPolicy.Decide(120, sourceWasPlaying: true, returnedPaused: true, "sameVideo001", "sameVideo001"));
        Assert.Equal(ReturnAction.SeekAndPlay,
            ReturnPolicy.Decide(120, sourceWasPlaying: false, returnedPaused: false, "sameVideo001", "sameVideo001"));
    }

    // Playlist-page launch (spec 13.1 / 22.1): the popout can start with NO source video id at all.
    // Any video the popout then reports is somewhere the source's playlist page is not, so the
    // source must NAVIGATE there — the timestamp fallback would strand the user on the playlist
    // page and lose their position in the queue.
    [Theory]
    [InlineData(42, true)]
    [InlineData(42, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Videoless_launch_navigates_to_any_reported_video(int? lastKnownSeconds, bool wasPlaying)
    {
        Assert.Equal(ReturnAction.Navigate,
            ReturnPolicy.Decide(lastKnownSeconds, wasPlaying, returnedPaused: null,
                returnedVideoId: "newVideo0001", sourceVideoIdAtPopout: null,
                popoutLaunchedWithoutVideo: true));
    }

    [Theory]
    [InlineData(null, ReturnAction.Play)]   // popout never reached a video: nothing to navigate to
    [InlineData("", ReturnAction.Play)]
    public void Videoless_launch_without_reported_video_falls_back_to_timestamp_decision(
        string? returnedId, ReturnAction expected)
    {
        Assert.Equal(expected,
            ReturnPolicy.Decide(null, sourceWasPlaying: true, returnedPaused: null,
                returnedVideoId: returnedId, sourceVideoIdAtPopout: null,
                popoutLaunchedWithoutVideo: true));
    }

    // Q-1 suppression mutes the source at popout launch; return must always undo that. The popout's
    // reported value wins; otherwise the pre-suppression launch value; otherwise mute is forced false
    // so a return with no captured popout state can never leave the source silent.
    [Fact]
    public void ResolveReturnSettings_prefers_popout_then_launch_and_forces_unmute()
    {
        var known = ReturnPolicy.ResolveReturnSettings(0.4, true, 1.5, 0.9, false, 1.0);
        Assert.Equal(0.4, known.Volume);
        Assert.True(known.Muted);
        Assert.Equal(1.5, known.PlaybackRate);

        var launchFallback = ReturnPolicy.ResolveReturnSettings(null, null, null, 0.9, true, 1.0);
        Assert.Equal(0.9, launchFallback.Volume);
        Assert.True(launchFallback.Muted);   // the user had the source muted before popout: keep it muted
        Assert.Equal(1.0, launchFallback.PlaybackRate);

        var bothUnknown = ReturnPolicy.ResolveReturnSettings(null, null, null, null, null, null);
        Assert.Null(bothUnknown.Volume);
        Assert.False(bothUnknown.Muted);     // forced un-mute: suppression is always undone
        Assert.Null(bothUnknown.PlaybackRate);
    }
}
