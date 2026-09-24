using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Locks the URL-redaction contract (spec §18): logs must never carry the query string, which
/// can hold tokens, timestamps, or playlist context. Surfaced by the spec-conformance review
/// as untested security-relevant logic.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class LoggingServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactUrl_handles_empty(string? url)
    {
        Assert.Equal("(none)", Log.RedactUrl(url));
    }

    [Fact]
    public void RedactUrl_drops_the_query_string_for_valid_urls()
    {
        var redacted = Log.RedactUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s");
        Assert.Equal("https://www.youtube.com/watch", redacted);
        Assert.DoesNotContain("dQw4w9WgXcQ", redacted); // the video id lives in the query
    }

    [Fact]
    public void RedactUrl_never_leaks_an_auth_token()
    {
        var redacted = Log.RedactUrl("https://accounts.google.com/signin?token=SUPERSECRET");
        Assert.Equal("https://accounts.google.com/signin", redacted);
        Assert.DoesNotContain("SUPERSECRET", redacted);
    }

    [Fact]
    public void RedactUrl_redacts_query_even_when_url_is_unparseable()
    {
        var redacted = Log.RedactUrl("not a url?token=SUPERSECRET");
        Assert.DoesNotContain("SUPERSECRET", redacted);
        Assert.Contains("<redacted>", redacted);
    }
}

/// <summary>
/// The delivery contract, now that callers hand entries to a background writer instead of appending
/// synchronously on the UI thread (a burst of DOM failures used to mean a disk write per poll tick).
/// Async delivery is only acceptable if nothing is lost: entries must still land, an exit must drain
/// what is still queued, and rotation must work off the in-memory byte count - there is no longer a
/// stat per entry to notice an oversized file for us.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class LoggingServiceDeliveryTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("PIPLAY_DATA_ROOT");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PiPlayLogTests-" + Guid.NewGuid().ToString("N"));

    private string LogFile => Path.Combine(_root, "logs", "piplay.log");

    /// <summary>Point the logger at a private data root and start its writer there.</summary>
    private void InitInTempRoot()
    {
        Environment.SetEnvironmentVariable("PIPLAY_DATA_ROOT", _root);
        Log.Init();
    }

    public void Dispose()
    {
        Log.Shutdown();
        Environment.SetEnvironmentVariable("PIPLAY_DATA_ROOT", _previousRoot);
        Log.Init();   // hand the rest of the (serial) suite a working logger on the shared temp root
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Queued_entries_reach_the_file()
    {
        InitInTempRoot();

        Log.Info("hello from the queue");
        Log.Warn("a warning");
        Log.Error("a failure");
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)), "the writer did not drain in time");

        var text = File.ReadAllText(LogFile);
        Assert.Contains("[INFO] hello from the queue", text);
        Assert.Contains("[WARN] a warning", text);
        Assert.Contains("[ERROR] a failure", text);
    }

    [Fact]
    public void Shutdown_drains_entries_still_in_flight()
    {
        // The reason App.OnExit calls Log.Shutdown: entries logged just before exit must not be lost
        // merely because the write is no longer synchronous.
        InitInTempRoot();

        for (var i = 0; i < 200; i++) Log.Info($"entry {i}");
        Log.Shutdown();   // no explicit flush - Shutdown itself has to drain

        var lines = File.ReadAllLines(LogFile);
        Assert.Equal(200, lines.Count(l => l.Contains("[INFO] entry ")));
        Assert.Contains(lines, l => l.Contains("[INFO] entry 199"));
    }

    [Fact]
    public void Exception_overload_records_the_type_and_message()
    {
        InitInTempRoot();

        Log.Error("while doing the thing", new InvalidOperationException("boom"));
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)));

        Assert.Contains(
            "[ERROR] while doing the thing :: InvalidOperationException: boom",
            File.ReadAllText(LogFile));
    }

    [Fact]
    public void Rotation_seeds_its_byte_count_from_the_file_on_disk()
    {
        // Init reads the existing length once. Without that seed an already-oversized log would never
        // roll, because nothing stats the file per entry any more.
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(LogFile, new string('x', 1_000_001), Encoding.UTF8);

        InitInTempRoot();
        Log.Info("first entry after the oversized file");
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)));

        Assert.True(File.Exists(LogFile + ".1"), "the oversized log was not rotated out");
        Assert.True(new FileInfo(LogFile + ".1").Length > 1_000_000, "the rotated file is not the oversized one");

        var text = File.ReadAllText(LogFile);
        Assert.Contains("first entry after the oversized file", text);
        Assert.DoesNotContain("xxxx", text);   // the live file is the fresh one
    }

    [Fact]
    public void Rotation_tracks_bytes_written_since_init()
    {
        InitInTempRoot();

        // Cross the 1 MB cap purely through logged volume: the in-memory counter is the only thing
        // that can notice, since the writer never stats the file.
        var chunk = new string('y', 10_000);
        for (var i = 0; i < 120; i++) Log.Info(chunk);
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(30)));

        Assert.True(File.Exists(LogFile + ".1"), "1.2 MB of entries did not trigger a rotation");
        Assert.True(new FileInfo(LogFile).Length < 1_000_000, "the live log kept growing past the cap");
    }

    [Fact]
    public void Rotation_rereads_the_file_after_another_process_already_rolled_it()
    {
        // A second launch logs to the same file before it hands off, and may roll it. The primary's
        // count is then stale; rotating on it again would replace the fresh .1 (the primary's own
        // history) with the second launch's few lines.
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(LogFile, new string('x', 999_000), Encoding.UTF8);
        var history = new FileInfo(LogFile).Length;
        InitInTempRoot();   // seeds the count just under the cap

        File.Move(LogFile, LogFile + ".1");                                        // the other process rolls
        File.WriteAllText(LogFile, "second launch handing off" + Environment.NewLine, Encoding.UTF8);

        Log.Info(new string('y', 2_000));   // pushes this process's count past the cap
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)));
        Log.Info("next entry");             // the rotation check runs before this append
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)));

        Assert.Equal(history, new FileInfo(LogFile + ".1").Length);                // the history survived
        var text = File.ReadAllText(LogFile);
        Assert.Contains("second launch handing off", text);
        Assert.Contains(new string('y', 2_000), text);
        Assert.Contains("[INFO] next entry", text);
    }

    [Fact]
    public void Timestamps_use_the_gregorian_invariant_shape_under_any_culture()
    {
        // th-TH formats yyyy in the Buddhist calendar (Gregorian + 543), and a culture's own time
        // separator replaces ':' in a custom format; neither may reach the log line.
        var culture = new CultureInfo("th-TH");
        culture.DateTimeFormat.TimeSeparator = ".";
        var previous = CultureInfo.CurrentCulture;
        InitInTempRoot();
        try
        {
            CultureInfo.CurrentCulture = culture;
            Log.Info("culture probe");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.True(Log.FlushForTests(TimeSpan.FromSeconds(10)));

        var line = Assert.Single(File.ReadAllLines(LogFile), l => l.EndsWith("[INFO] culture probe", StringComparison.Ordinal));
        var match = Regex.Match(line, @"^(\d{4})-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] culture probe$");
        Assert.True(match.Success, line);
        Assert.InRange(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), DateTime.Now.Year - 1, DateTime.Now.Year);
    }

    [Fact]
    public void Queue_overflow_drops_entries_instead_of_blocking_the_caller()
    {
        // This is the invariant the whole change exists for. If a full queue ever BLOCKS the caller,
        // the UI thread stalls on the disk - strictly worse than the synchronous append this replaced.
        // Park the writer so the bounded queue genuinely fills, then prove the caller sailed through.
        var gate = new ManualResetEventSlim(false);
        Log.WriterGateForTests = gate;
        try
        {
            InitInTempRoot();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 6000; i++) Log.Info($"burst {i}");   // > MaxQueuedEntries (4096)
            sw.Stop();

            Assert.True(Log.DroppedCountForTests > 0,
                "a 6000-entry burst against a parked writer dropped nothing - the queue is not bounded");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"enqueueing blocked the caller for {sw.Elapsed} - a full queue must drop, not block");

            gate.Set();       // release the writer
            Log.Shutdown();   // drain + join

            // The loss is reported rather than silent: a gap you cannot see is worse than a slow log.
            Assert.Contains("Log queue overflowed; dropped", File.ReadAllText(LogFile));
        }
        finally
        {
            Log.WriterGateForTests = null;
            gate.Set();
            gate.Dispose();
        }
    }

    [Fact]
    public void Logging_after_shutdown_is_a_silent_no_op()
    {
        InitInTempRoot();
        Log.Info("before");
        Log.Shutdown();

        // Q-6: logging must never throw, including on the far side of shutdown.
        var ex = Record.Exception(() =>
        {
            Log.Info("after");
            Log.Warn("after");
            Log.Error("after", new InvalidOperationException("boom"));
            Log.Shutdown();   // double shutdown
        });

        Assert.Null(ex);
        var text = File.ReadAllText(LogFile);
        Assert.Contains("before", text);
        Assert.DoesNotContain("[INFO] after", text);
    }

    [Fact]
    public void Failed_init_disables_logging_instead_of_throwing()
    {
        // An unusable data root must degrade to "no logging", never take the app down at startup.
        Environment.SetEnvironmentVariable("PIPLAY_DATA_ROOT", "\0:\\not-a-path");

        var ex = Record.Exception(() =>
        {
            Log.Init();
            Log.Info("swallowed");
        });

        Assert.Null(ex);
    }
}
