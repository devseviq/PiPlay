using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class DomBridgeDegradedTrackerTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Popout = Guid.NewGuid();

    [Fact]
    public void Crossing_at_exactly_three_raises_once_until_recovery_then_can_raise_again()
    {
        var tracker = new DomBridgeDegradedTracker();

        Assert.Null(tracker.RecordFailure(Source, "player-state read", 1));
        Assert.Null(tracker.RecordFailure(Source, "player-state read", 2));
        Assert.Equal("player-state read", tracker.RecordFailure(Source, "player-state read", 3));
        Assert.Null(tracker.RecordFailure(Source, "player-state read", 4));

        Assert.True(tracker.RecordRecovery(Source, "player-state read"));

        Assert.Null(tracker.RecordFailure(Source, "player-state read", 1));
        Assert.Equal("player-state read", tracker.RecordFailure(Source, "player-state read", 3));
    }

    [Fact]
    public void Recovery_on_another_surface_does_not_clear_the_first_surface_key()
    {
        var tracker = new DomBridgeDegradedTracker();

        // The Source's read is failing persistently while the popout runs the SAME operation name
        // on its own WebView (the final-review finding: degraded keys collided by operation only).
        Assert.Equal("player-state read", tracker.RecordFailure(Source, "player-state read", 3));

        // A healthy read on the popout must not clear the Source's hint mid-episode...
        Assert.False(tracker.RecordRecovery(Popout, "player-state read"));

        // ...and the Source cannot re-raise this episode (the raise fires only at the crossing).
        Assert.Null(tracker.RecordFailure(Source, "player-state read", 4));

        // The all-clear arrives only when the degraded key itself recovers.
        Assert.True(tracker.RecordRecovery(Source, "player-state read"));
    }

    [Fact]
    public void All_clear_fires_only_when_the_last_degraded_key_recovers()
    {
        var tracker = new DomBridgeDegradedTracker();

        Assert.Equal("source suppression", tracker.RecordFailure(Source, "source suppression", 3));
        Assert.Equal("player-state read", tracker.RecordFailure(Popout, "player-state read", 3));

        Assert.False(tracker.RecordRecovery(Popout, "player-state read"));
        Assert.True(tracker.RecordRecovery(Source, "source suppression"));
    }

    [Fact]
    public void Success_while_healthy_raises_nothing()
    {
        var tracker = new DomBridgeDegradedTracker();

        Assert.False(tracker.RecordRecovery(Source, "player-state read"));
    }
}
