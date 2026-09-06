using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PiPlay;
using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Clear browser data after the foreground wait (spec 19, review 2026-09-05 PP-06): the
/// destructive operation is what gates playback, not the status prompt. A controllable task
/// stands in for the WebView2 clear; the window is headless, so the observable contract is the
/// gates, the button state, the queued navigation, and the prompts.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class MainWindowClearDataTests : IDisposable
{
    private const string SignedOutHome = "https://www.youtube.com/";

    private sealed class Harness
    {
        public MainWindow Window { get; }
        public TaskCompletionSource Clear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Title, string Body)> Prompts { get; } = new();
        public int FactoryCalls { get; private set; }
        public bool PlayerGoneWhenStarted { get; private set; }

        public Harness()
        {
            Window = Invoke(() =>
            {
                var window = new MainWindow();
                window.SetBrowserReadyForTests(true);
                return window;
            });
        }

        /// <summary>Starts the clear on the STA thread; the returned task is awaited from the test thread.</summary>
        public Task StartAsync(TimeSpan foregroundWait) =>
            Invoke(() => Window.PerformClearBrowserDataAsync(
                clearFactory: () =>
                {
                    FactoryCalls++;
                    PlayerGoneWhenStarted = Window.PlayerForTests is null;
                    return Clear.Task;
                },
                foregroundWait,
                (title, body) => Prompts.Add((title, body))));

        public string PopOutLabel => Invoke(() => ((TextBlock)Window.FindName("PopOutButtonText")!).Text);
        public bool PopOutEnabled => Invoke(() => ((Button)Window.FindName("PopOutButton")!).IsEnabled);
        public bool SettingsEnabled => Invoke(() => ((Button)Window.FindName("SettingsButton")!).IsEnabled);
    }

    private static T Invoke<T>(Func<T> func)
    {
        T result = default!;
        StaTestThread.Invoke(() => result = func());
        return result;
    }

    /// <summary>Pump the STA dispatcher until the condition holds; late continuations arrive from the thread pool.</summary>
    private static void PumpUntil(MainWindow window, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            if (Invoke(condition)) return;
            Thread.Sleep(10);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task The_popout_closes_before_the_clear_starts_and_its_return_does_not_script_the_profile()
    {
        var h = new Harness();
        StaTestThread.Invoke(() =>
        {
            var player = new PlayerWindow(
                environment: null!, url: "https://www.youtube.com/watch?v=AAAAAAAAAAA", topmost: false,
                placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);
            player.TrackReturnIdentity("https://www.youtube.com/watch?v=AAAAAAAAAAA");
            h.Window.AttachPlayerWithReturnForTests(player);
        });

        var run = h.StartAsync(TimeSpan.Zero);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, h.FactoryCalls);
        Assert.True(h.PlayerGoneWhenStarted, "the Popout must be gone before the profile clear starts");
        Assert.Null(Invoke(() => h.Window.PlayerForTests));
        Assert.Null(Invoke(() => h.Window.PendingReturnReplayForTests));
        Assert.False(Invoke(() => h.Window.ReturnInProgressForTests));
        Assert.Null(Invoke(() => h.Window.PendingUrlForTests));   // the return did not navigate the Source

        h.Clear.SetResult();
        PumpUntil(h.Window, () => !h.Window.BrowserDataClearActiveForTests, "the clear to release");
    }

    [Fact]
    public async Task After_the_foreground_wait_playback_stays_gated_while_settings_is_usable()
    {
        var h = new Harness();

        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { (PrivacyService.ClearResultTitle, PrivacyService.ClearTimedOut) }, h.Prompts);
        Assert.True(Invoke(() => h.Window.BrowserDataClearInProgressForTests));
        Assert.True(Invoke(() => h.Window.BrowserDataClearActiveForTests));
        Assert.False(Invoke(() => h.Window.CanStartVideoPopoutForTests));
        Assert.False(Invoke(() => h.Window.SourceCommandsAvailableForTests));
        Assert.True(Invoke(() => h.Window.IncomingLinkStateForTests.ClearingBrowserData));
        Assert.Equal("Clearing browser data...", h.PopOutLabel);
        Assert.False(h.PopOutEnabled);
        Assert.True(h.SettingsEnabled);   // status stays reachable; only the foreground phase locks it

        h.Clear.SetResult();
        PumpUntil(h.Window, () => !h.Window.BrowserDataClearActiveForTests, "the clear to release");
    }

    [Fact]
    public async Task A_second_clear_while_one_is_still_running_does_not_start_another()
    {
        var h = new Harness();
        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));

        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, h.FactoryCalls);
        Assert.Equal((PrivacyService.ClearResultTitle, PrivacyService.ClearAlreadyRunning), h.Prompts[^1]);

        h.Clear.SetResult();
        PumpUntil(h.Window, () => !h.Window.BrowserDataClearActiveForTests, "the clear to release");
    }

    [Fact]
    public async Task Late_success_shows_the_signed_out_source_once_and_reopens_the_gates_without_another_prompt()
    {
        var h = new Harness();
        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));
        var promptsAfterTimeout = h.Prompts.Count;

        h.Clear.SetResult();
        PumpUntil(h.Window, () => h.Window.PendingUrlForTests == SignedOutHome, "the signed-out navigation");

        Assert.False(Invoke(() => h.Window.BrowserDataClearActiveForTests));
        Assert.True(Invoke(() => h.Window.CanStartVideoPopoutForTests));
        Assert.True(Invoke(() => h.Window.SourceCommandsAvailableForTests));
        Assert.Equal("Pop out video", h.PopOutLabel);
        Assert.Equal(promptsAfterTimeout, h.Prompts.Count);   // the timeout prompt already said it would finish in the background

        // No second refresh from the same completion.
        h.Window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        Assert.Equal(SignedOutHome, Invoke(() => h.Window.PendingUrlForTests));
    }

    [Fact]
    public async Task Late_failure_reopens_the_gates_without_navigating_or_prompting()
    {
        var h = new Harness();
        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));
        var promptsAfterTimeout = h.Prompts.Count;

        h.Clear.SetException(new InvalidOperationException("profile locked"));
        PumpUntil(h.Window, () => !h.Window.BrowserDataClearActiveForTests, "the failed clear to release");

        Assert.Null(Invoke(() => h.Window.PendingUrlForTests));
        Assert.True(Invoke(() => h.Window.CanStartVideoPopoutForTests));
        Assert.Equal(promptsAfterTimeout, h.Prompts.Count);
    }

    [Fact]
    public async Task Closing_during_a_clear_does_not_wait_and_drops_the_late_completion()
    {
        var h = new Harness();
        await h.StartAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));

        StaTestThread.Invoke(() => h.Window.Close());   // bounded: returns with the clear still running
        Assert.True(Invoke(() => h.Window.BrowserDataClearInProgressForTests));

        h.Clear.SetResult();
        PumpUntil(h.Window, () => !h.Window.BrowserDataClearInProgressForTests, "the clear to release");
        h.Window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);

        Assert.Null(Invoke(() => h.Window.PendingUrlForTests));   // no refresh against a closed window
    }

    public void Dispose() => StaTestThread.Invoke(() =>
    {
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            try { window.Close(); }
            catch { /* Never-shown test windows can already be tearing down. */ }
        }
    });
}
