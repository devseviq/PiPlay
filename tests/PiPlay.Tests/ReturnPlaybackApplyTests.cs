using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// One return-apply path for Navigate and same-video (spec 14): volume/mute and play/pause
/// go immediately; seek and rate wait for a clear page and are dropped if the Source moves
/// during an await.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class ReturnPlaybackApplyTests
{
    private sealed class Probe
    {
        private readonly Queue<YouTubeAdState> _ads;
        public List<(double? Volume, bool? Muted, double? Rate)> Settings { get; } = new();
        public List<bool?> PlayPause { get; } = new();
        public List<int> Seeks { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public int AdReads { get; private set; }
        public Func<bool> IsCurrent { get; set; } = () => true;
        public Func<int, Task>? SeekHook { get; set; }

        public Probe(params YouTubeAdState[] ads) => _ads = new Queue<YouTubeAdState>(ads);

        public Task<YouTubeAdState> ReadAdAsync()
        {
            AdReads++;
            return Task.FromResult(_ads.Count > 0 ? _ads.Dequeue() : YouTubeAdState.Ad);
        }

        public Task ApplySettingsAsync(double? volume, bool? muted, double? rate)
        {
            Settings.Add((volume, muted, rate));
            return Task.CompletedTask;
        }

        public Task ApplyPlayPauseAsync(bool? paused)
        {
            PlayPause.Add(paused);
            return Task.CompletedTask;
        }

        public Task SeekAsync(int seconds)
        {
            Seeks.Add(seconds);
            return SeekHook is null ? Task.CompletedTask : SeekHook(seconds);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }

        public ReturnPlaybackWriters Writers => new(ApplySettingsAsync, ApplyPlayPauseAsync, SeekAsync);
    }

    private static PlayerReturnState Snapshot(
        int? seconds = 42, bool? paused = false, double? volume = 0.25, bool? muted = true, double? rate = 1.5) =>
        new()
        {
            VideoId = "AAAAAAAAAAA",
            LastKnownSeconds = seconds,
            Paused = paused,
            Volume = volume,
            Muted = muted,
            PlaybackRate = rate,
        };

    [Fact]
    public async Task A_clear_page_applies_volume_play_then_seek_and_rate_without_waiting()
    {
        var probe = new Probe(YouTubeAdState.Clear);

        var outcome = await ReturnPlaybackApply.ApplyAsync(
            Snapshot(), probe.ReadAdAsync, probe.Writers, () => probe.IsCurrent(), probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Applied, outcome);
        Assert.Equal((0.25, (bool?)true, (double?)null), probe.Settings[0]);
        Assert.Equal(new bool?[] { false }, probe.PlayPause);
        Assert.Equal(new[] { 42 }, probe.Seeks);
        Assert.Contains(probe.Settings, s => s.Rate == 1.5);
        Assert.Empty(probe.Delays);
    }

    [Fact]
    public async Task An_ad_gets_volume_and_play_immediately_and_the_seek_after_it_clears()
    {
        var probe = new Probe(YouTubeAdState.Ad, YouTubeAdState.Clear);

        var outcome = await ReturnPlaybackApply.ApplyAsync(
            Snapshot(), probe.ReadAdAsync, probe.Writers, () => probe.IsCurrent(), probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Applied, outcome);
        Assert.Equal(new bool?[] { false }, probe.PlayPause);
        Assert.Equal(new[] { 42 }, probe.Seeks);
        Assert.Equal(new[] { ReturnReplayAdPolicy.PollInterval }, probe.Delays);
        Assert.Equal(2, probe.AdReads);
    }

    [Fact]
    public async Task An_ad_that_outlasts_the_bound_never_writes_seek_or_rate()
    {
        var probe = new Probe();

        var outcome = await ReturnPlaybackApply.ApplyAsync(
            Snapshot(paused: true), probe.ReadAdAsync, probe.Writers, () => probe.IsCurrent(), probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Skipped, outcome);
        Assert.Equal(new bool?[] { true }, probe.PlayPause);
        Assert.Empty(probe.Seeks);
        Assert.DoesNotContain(probe.Settings, s => s.Rate is not null);
    }

    [Fact]
    public async Task A_stale_request_after_the_ad_clears_does_not_seek()
    {
        var probe = new Probe(YouTubeAdState.Clear);
        var current = true;
        Task<YouTubeAdState> ReadThenMove()
        {
            current = false;
            return probe.ReadAdAsync();
        }

        var outcome = await ReturnPlaybackApply.ApplyAsync(
            Snapshot(), ReadThenMove, probe.Writers, () => current, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Abandoned, outcome);
        Assert.Empty(probe.Seeks);
        Assert.DoesNotContain(probe.Settings, s => s.Rate is not null);
    }

    [Fact]
    public async Task A_source_change_during_the_seek_await_does_not_write_rate()
    {
        var probe = new Probe(YouTubeAdState.Clear);
        var current = true;
        var seekStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seekContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.SeekHook = async _ =>
        {
            seekStarted.SetResult();
            await seekContinue.Task;
        };

        var apply = ReturnPlaybackApply.ApplyAsync(
            Snapshot(), probe.ReadAdAsync, probe.Writers, () => current, probe.DelayAsync);
        await seekStarted.Task;
        current = false;
        seekContinue.SetResult();

        var outcome = await apply;

        Assert.Equal(DeferredSeekOutcome.Abandoned, outcome);
        Assert.Equal(new[] { 42 }, probe.Seeks);
        Assert.DoesNotContain(probe.Settings, s => s.Rate is not null);
    }

    [Fact]
    public async Task Play_pause_and_volume_do_not_need_a_clear_page()
    {
        var probe = new Probe(YouTubeAdState.Ad);
        var state = Snapshot(seconds: null, rate: null, paused: true, volume: 0.4, muted: false);

        var outcome = await ReturnPlaybackApply.ApplyAsync(
            state, probe.ReadAdAsync, probe.Writers, () => true, probe.DelayAsync);

        Assert.Equal(DeferredSeekOutcome.Applied, outcome);
        Assert.Single(probe.Settings);
        Assert.Equal((0.4, (bool?)false, (double?)null), probe.Settings[0]);
        Assert.Equal(new bool?[] { true }, probe.PlayPause);
        Assert.Empty(probe.Seeks);
        Assert.Equal(0, probe.AdReads);
    }
}
