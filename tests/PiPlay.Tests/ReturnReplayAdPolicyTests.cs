using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Deferred return seek under an ad (spec 14, YouTube_Compliance, review 2026-09-05 PP-03): the
/// retained seek is written only when the page reports clear while the request is still for the
/// video the Source shows; otherwise the page keeps its own position.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class ReturnReplayAdPolicyTests
{
    private sealed class Probe
    {
        private readonly Queue<YouTubeAdState> _states;
        public int Reads { get; private set; }
        public List<TimeSpan> Delays { get; } = new();
        public Func<bool> IsCurrent { get; set; } = () => true;

        public Probe(params YouTubeAdState[] states) => _states = new Queue<YouTubeAdState>(states);

        public Task<YouTubeAdState> ReadAsync()
        {
            Reads++;
            return Task.FromResult(_states.Count > 0 ? _states.Dequeue() : YouTubeAdState.Ad);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(42, null, true)]
    [InlineData(null, 1.5, true)]
    [InlineData(0, null, true)]      // zero is a valid timestamp
    [InlineData(null, null, false)]  // play/pause and volume/mute are not ad-gated
    public void Only_a_seek_or_a_rate_needs_the_ad_gate(int? seconds, double? rate, bool expected)
    {
        Assert.Equal(expected, ReturnReplayAdPolicy.WantsGuardedWrite(seconds, rate));
    }

    [Fact]
    public async Task A_clear_page_applies_at_once_without_waiting()
    {
        var probe = new Probe(YouTubeAdState.Clear);

        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(probe.ReadAsync, probe.IsCurrent, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Applied, outcome);
        Assert.Equal(1, probe.Reads);
        Assert.Empty(probe.Delays);
    }

    [Fact]
    public async Task An_ad_that_clears_applies_after_the_wait_with_one_poll_interval_per_read()
    {
        var probe = new Probe(YouTubeAdState.Ad, YouTubeAdState.Unknown, YouTubeAdState.Clear);

        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(probe.ReadAsync, probe.IsCurrent, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Applied, outcome);
        Assert.Equal(3, probe.Reads);
        Assert.Equal(new[] { ReturnReplayAdPolicy.PollInterval, ReturnReplayAdPolicy.PollInterval }, probe.Delays);
    }

    [Fact]
    public async Task An_ad_that_outlasts_the_bound_skips_the_seek_and_stops_polling()
    {
        var probe = new Probe();   // always Ad

        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(probe.ReadAsync, probe.IsCurrent, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Skipped, outcome);
        Assert.Equal(ReturnReplayAdPolicy.MaxPolls, probe.Reads);
        Assert.Equal(ReturnReplayAdPolicy.MaxPolls - 1, probe.Delays.Count);
    }

    [Fact]
    public async Task A_video_change_during_the_wait_abandons_the_sample_before_the_next_read()
    {
        var probe = new Probe(YouTubeAdState.Ad, YouTubeAdState.Clear);
        probe.IsCurrent = () => probe.Reads < 1;   // the Source moves on right after the first read

        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(probe.ReadAsync, probe.IsCurrent, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Abandoned, outcome);
        Assert.Equal(1, probe.Reads);   // the Clear that followed was never consulted
    }

    [Fact]
    public async Task A_clear_read_for_a_request_that_just_went_stale_is_not_applied()
    {
        var probe = new Probe(YouTubeAdState.Clear);
        var current = true;
        probe.IsCurrent = () => current;
        Task<YouTubeAdState> ReadThenMove()
        {
            current = false;   // navigation lands while the probe is in flight
            return probe.ReadAsync();
        }

        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(ReadThenMove, probe.IsCurrent, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Abandoned, outcome);
    }

    [Fact]
    public void The_wait_is_bounded_and_shorter_than_a_typical_pre_roll_budget()
    {
        Assert.True(ReturnReplayAdPolicy.WaitBound >= TimeSpan.FromSeconds(3));
        Assert.True(ReturnReplayAdPolicy.WaitBound <= TimeSpan.FromSeconds(10));
    }
}
