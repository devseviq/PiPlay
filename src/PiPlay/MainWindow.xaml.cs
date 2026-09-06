using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay;

/// <summary>
/// MainWindow / Source Window (spec 12.1). Hosts the YouTube browsing WebView, applies the
/// navigation allowlist, starts the Video Popout lifecycle, shows/hides the Source
/// Placeholder, and coordinates return.
/// </summary>
public partial class MainWindow : Window
{
    // Segoe MDL2 caption glyphs (kept as escapes so the source stays plain ASCII).
    private const string GlyphMaximize = "";
    private const string GlyphRestore = "";
    private const string GlyphPopOut = "\uE8A7";
    private const string GlyphBringBack = "\uE73F";
    private const double CompactToolbarThreshold = 940;

    private SettingsService _settingsService = new();   // replaced only by the test seam
    private AppSettings _settings;

    private bool _browserReady;
    // PP-02: one recovery at a time; the consecutive budget ends a crash loop in a visible state.
    private bool _browserRecoveryInProgress;
    private bool _browserFailed;
    private int _consecutiveBrowserRecoveries;
    private DateTimeOffset? _lastBrowserFailureUtc;
    private string? _lastKnownSourceUrl;
    private System.Windows.Threading.DispatcherTimer? _returnDeadlineTimer;
    private bool _placementRestored;
    private bool _loadingProfiles;
    private string? _pendingUrl;
    // Incoming-link route (REQ-APP-01, ADR-0009): the newest supported target that arrived while
    // playback ownership was moving (launch/return/clear) or the Popout could not host it. Applied
    // once ownership is stable; a newer link replaces it (latest wins, never a queue).
    private YouTubeTarget? _retainedIncomingTarget;

    // Video Popout lifecycle state (spec 13).
    private bool _popoutInProgress;
    private bool _returnInProgress;
    private PlayerWindow? _player;
    private bool _sourcePinSuspendedForPopout;
    private bool _sourceTopmostBeforePopout;
    private bool _sourceNavigationSuspended;
    private bool _sourceWasPlayingAtPopout;
    // The source's pre-suppression playback settings, captured at popout launch. Used as the return
    // fallback when the popout never reported live state (e.g. X-close before the first sync sample),
    // so suppression's mute is always undone and the source never returns silent (Q-1).
    private double? _sourceVolumeAtPopout;
    private bool? _sourceMutedAtPopout;
    private double? _sourcePlaybackRateAtPopout;
    // The video the source was on when the popout launched (overhaul Task 3): compared against the
    // popout's returned video id to decide navigate-vs-seek on close (REQ-RETURN-01).
    private string? _popoutSourceVideoId;
    // True when the popout launched from a playlist PAGE (spec 22.1): even when the launch
    // resolved the page's first item to start, that id is the popout's, not the Source's — any
    // video the popout then reports is somewhere the source is not, so return must navigate.
    private bool _popoutLaunchedWithoutVideo;
    private System.Windows.Threading.DispatcherTimer? _sourceSuppressionTimer;
    private bool _sourceSuppressionTickInProgress;
    private PlayerReturnState? _pendingReturnReplay;
    private bool _pendingReturnReplayInProgress;

    // Auto (spec §6.1): source-side playback detector + the de-dup key that blocks the return-resume
    // re-pop loop. The timer only runs while Auto is on and the browser is ready.
    private System.Windows.Threading.DispatcherTimer? _autoTimer;
    private bool _autoTickInProgress;
    private string? _autoLastHandledVideoId;
    // A failed Auto suppression attempt must not become a successful handled-video latch, but it
    // also must not reopen a prompt every 250 ms. Block only that failed video until Source moves;
    // manual Pop out remains available and clears the block on success.
    private string? _autoSuppressionFailedVideoId;

    // Color-wheel mouse moves and intensity-slider drags both arrive much faster than WPF can present a
    // frame. Keep only the latest requested (accent, intensity) PAIR and apply it at most once per ~33 ms
    // preview frame — one coalescing channel, because both feed the same accent derivation.
    private readonly System.Windows.Threading.DispatcherTimer _accentPreviewTimer;
    private string? _pendingAccentPreview;
    private int? _pendingAccentIntensityPreview;
    private string? _lastAppliedAccentPreview;
    private int? _lastAppliedAccentIntensityPreview;

    /// <summary>
    /// The accent intensity being previewed while Settings is open, or null to follow the persisted
    /// value. Deliberately VISUAL-ONLY and never written to <c>_settings</c>: the dismiss-without-apply
    /// path reverts by clearing this and re-reading persisted state, so a preview that mutated settings
    /// would make cancel un-revertable.
    /// </summary>
    private int? _previewAccentIntensity;

    // Shutdown owns the final settings save. A PlayerWindow close raised from this path still
    // contributes its placement/state, but must not drive the Source WebView or save a second time.
    private bool _mainWindowClosing;

    // Guards both privacy actions against re-entrancy (double-click, reopen mid-clear).
    private bool _privacyActionInProgress;
    private readonly BrowserDataClearCoordinator _browserDataClearCoordinator = new();
    // True only while Clear browser data is running, so the popout's return handler does not
    // drive source playback against a session that is being wiped.
    private bool _clearingBrowserData;
    private int _browserDataClearGeneration;

    /// <summary>
    /// The profile clear is destructive for as long as the underlying operation runs, not for as
    /// long as the foreground status wait lasts (PP-06). Commands that start or change playback
    /// gate on this; Settings gates only on the foreground phase so it stays usable afterwards.
    /// </summary>
    private bool BrowserDataClearActive => _clearingBrowserData || _browserDataClearCoordinator.IsRunning;
    // One shared Settings surface can be requested from either window. Keep the instance so a second
    // request activates it instead of opening a competing modal dialog with a different owner.
    private SettingsWindow? _settingsDialog;
    private bool _settingsSaveRefusalShown;
    // Full preset preview companion to _previewAccentIntensity. Accent-only preview must derive against
    // the pending preset, not snap back to the persisted one while Settings is open.
    private string? _previewThemeId;

    public MainWindow() : this(initialSettings: null)
    {
    }

    internal MainWindow(AppSettings? initialSettings)
    {
        InitializeComponent();
        BorderlessWindowHelper.EnableExpandedResizeZones(this);

        _settings = initialSettings ?? _settingsService.Load();
        _accentPreviewTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _accentPreviewTimer.Tick += AccentPreviewTimer_Tick;
        // Assembly-qualified pack URI (not the short form): resolves against the PiPlay assembly
        // regardless of Application.ResourceAssembly, so it loads in production and under tests.
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/PiPlay;component/Assets/piplay.ico"));

        BorderlessWindowHelper.EnableProperMaximize(this);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized ? GlyphRestore : GlyphMaximize;
        SourceInitialized += (_, _) =>
        {
            // Theme-driven native corner shape (docs/Theme_Preset_Differences.md): explicit theme/override data,
            // never an opacity side effect. Default mode leaves the window DWM-pristine.
            ApplyOwnCornerMode();
            if (_placementRestored) return;
            WindowPlacementService.Restore(this, NormalizeSourcePlacementForRestore(
                _settings.MainWindow.Placement, MinWidth, MinHeight));
            _placementRestored = true;
        };

        ApplyTopmost(_settings.MainWindow.Topmost);
        ApplyAuto(_settings.AutoPopout);
        ApplySourceAppearance();
        ApplySourceBarOpacity();
        LoadProfilesIntoCombo();
        ApplyChannelTitle();
        UpdatePopoutActionState();
        ApplySourceToolbarLayout(Width);
    }

    private static PlacementData? NormalizeSourcePlacementForRestore(
        PlacementData? placement,
        double minWidthDip,
        double minHeightDip) =>
        placement is null
            ? null
            : PlacementMath.EnsureMinSize(
                placement,
                (int)Math.Ceiling(minWidthDip),
                (int)Math.Ceiling(minHeightDip));

    internal static PlacementData? NormalizeSourcePlacementForRestoreForTests(
        PlacementData? placement,
        double minWidthDip = 760,
        double minHeightDip = 480) =>
        NormalizeSourcePlacementForRestore(placement, minWidthDip, minHeightDip);

    /// <summary>
    /// Surface a non-default channel (e.g. Stable) in the title bar and taskbar so a deployed copy is
    /// differentiable from the dev app at a glance. The Default channel keeps the plain "PiPlay" title.
    /// </summary>
    private void ApplyChannelTitle()
    {
        if (AppInfo.Channel == PiPlayChannel.Default) return;
        Title = AppInfo.WindowTitle;
        TitleText.Text = AppInfo.WindowTitle;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) => await InitializeBrowserAsync();

    // --- WebView2 initialization + recovery (spec 15, Q-6) ---

    private async Task InitializeBrowserAsync()
    {
        try
        {
            // The failure panel is not touched here: a recreate shows "Restarting the browser"
            // before calling in, and the panel leaves on the first completed navigation (or in the
            // recreate's finally). Collapsing it here would blank the letterbox during the restart.
            var env = await App.Current.WebViewEnvironment.EnsureCreatedAsync();
            if (_mainWindowClosing) return;

            await Browser.EnsureCoreWebView2Async(env);
            if (_mainWindowClosing)
            {
                // Closing can overtake either WebView await. Do not attach handlers or navigate a
                // browser whose owning window has already entered teardown.
                try { Browser.Dispose(); } catch { /* teardown is already in progress */ }
                return;
            }

            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // Each core is new, so each handler attaches exactly once per core (PP-02).
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.SourceChanged += Core_SourceChanged;
            core.ProcessFailed += Core_ProcessFailed;

            _browserReady = true;
            _browserFailed = false;
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();

            // A pending link wins; otherwise a recreated browser goes back to the page it was on,
            // and a first launch to the saved one.
            var startUrl = _pendingUrl ?? _lastKnownSourceUrl ?? _settings.LastUrl;
            _pendingUrl = null;
            NavigateInternal(startUrl);
            UpdateAutoDetector();   // start the Auto detector if Auto was left on
            Log.Info("Source browser initialized.");
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            if (_mainWindowClosing) return;
            Log.Error("WebView2 runtime not found.", ex);
            ShowRuntimeError(
                "WebView2 Runtime is required",
                "PiPlay needs the Microsoft Edge WebView2 Evergreen Runtime to display YouTube. " +
                "Install it, then click Retry.");
        }
        catch (Exception ex)
        {
            if (_mainWindowClosing) return;
            Log.Error("Failed to initialize the Source browser.", ex);
            ShowRuntimeError("The browser component could not start",
                "PiPlay couldn't start the browser component.\n\n" + ex.Message);
        }
    }

    /// <summary>Terminal failed state: the browser is unusable until Retry (spec 15.4).</summary>
    private void ShowRuntimeError(string heading, string message)
    {
        _browserReady = false;
        _browserFailed = true;
        UpdateAutoDetector();
        AbandonReturnTransition("the browser failed");
        ShowBrowserState(heading, message, retryEnabled: true);
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
    }

    /// <summary>
    /// The one panel for starting/retrying/failed (PP-02): heading names the failure kind, Retry
    /// is enabled only when nothing is already running. The panel collapses on the next successful
    /// navigation of a live core.
    /// </summary>
    private void ShowBrowserState(string heading, string message, bool retryEnabled)
    {
        RuntimeErrorHeading.Text = heading;
        RuntimeErrorText.Text = message;
        RuntimeRetryButton.IsEnabled = retryEnabled;
        RuntimeErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideBrowserState()
    {
        RuntimeErrorPanel.Visibility = Visibility.Collapsed;
        RuntimeRetryButton.IsEnabled = true;
    }

    private void DownloadRuntime_Click(object sender, RoutedEventArgs e) =>
        OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");

    /// <summary>Retry is a manual recreate: it resets the automatic budget and takes the same path.</summary>
    private async void RetryRuntime_Click(object sender, RoutedEventArgs e)
    {
        _consecutiveBrowserRecoveries = 0;
        _lastBrowserFailureUtc = null;
        await RecreateSourceBrowserAsync("Retry");
    }

    // --- WebView2 process failure recovery (spec 15.4, PP-02) ---

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = WebViewProcessFailurePolicy.Classify(e.ProcessFailedKind);
        Log.Warn($"Source WebView2 process failure: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode}, {e.ProcessDescription}).");
        HandleSourceProcessFailure(kind);
    }

    /// <summary>Internal for the WPF test lane (CoreWebView2 event args cannot be constructed).</summary>
    internal WebViewRecoveryAction HandleSourceProcessFailure(WebViewFailureKind kind, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var count = WebViewProcessFailurePolicy.ConsecutiveCountFor(_consecutiveBrowserRecoveries, _lastBrowserFailureUtc, at);
        var action = WebViewProcessFailurePolicy.Decide(
            kind, count, _browserRecoveryInProgress ? WebViewRecoveryAction.Recreate : null, _mainWindowClosing);
        Log.Info($"Source browser failure {kind}: {action} (consecutive recoveries: {count}).");
        if (action is WebViewRecoveryAction.Ignore or WebViewRecoveryAction.LogOnly) return action;

        _lastBrowserFailureUtc = at;
        _consecutiveBrowserRecoveries = count;
        var (heading, message) = WebViewProcessFailurePolicy.Describe(kind, action);
        switch (action)
        {
            case WebViewRecoveryAction.Reload:
                _consecutiveBrowserRecoveries++;
                // The core is alive; only the page died. A return in flight has no page to land
                // on, so it ends here; the reload restores the page itself.
                AbandonReturnTransition("the page renderer exited");
                ShowBrowserState(heading, message, retryEnabled: false);
                try { Browser.CoreWebView2?.Reload(); }
                catch (Exception ex)
                {
                    Log.Error("Reload after renderer exit failed; recreating the browser instead.", ex);
                    _ = RecreateSourceBrowserAsync("reload failed");
                }
                break;
            case WebViewRecoveryAction.Recreate:
                _consecutiveBrowserRecoveries++;
                ShowBrowserState(heading, message, retryEnabled: false);
                _ = RecreateSourceBrowserAsync(kind.ToString());
                break;
            case WebViewRecoveryAction.GiveUp:
                ShowRuntimeError(heading, message);
                break;
        }

        return action;
    }

    /// <summary>
    /// The single recreate path for Retry and automatic recovery: mark the browser unusable first
    /// (a dead core is non-null), end any return in flight, swap the control in place, and run the
    /// same initialization a launch does. The last-known page is the navigation target.
    /// </summary>
    private async Task RecreateSourceBrowserAsync(string reason)
    {
        if (_browserRecoveryInProgress || _mainWindowClosing) return;
        _browserRecoveryInProgress = true;
        try
        {
            Log.Info($"Recreating the Source browser ({reason}).");
            _browserReady = false;
            UpdateAutoDetector();
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();
            AbandonReturnTransition("the browser is restarting");
            var (heading, message) = WebViewProcessFailurePolicy.DescribeRestarting();
            ShowBrowserState(heading, message, retryEnabled: false);
            ReplaceBrowserControl();
            await InitializeBrowserAsync();
        }
        finally
        {
            _browserRecoveryInProgress = false;
            if (_browserReady) HideBrowserState();
            else RuntimeRetryButton.IsEnabled = true;
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();
        }
    }

    /// <summary>
    /// Swap the WebView2 control for a fresh one at the same child index of the letterbox (below
    /// the placeholder and the failure panel), keeping its style and visibility. Handlers on the
    /// old core are detached best-effort; disposing the control ends its renderer either way.
    /// </summary>
    private void ReplaceBrowserControl()
    {
        var old = Browser;
        var index = SourceLetterbox.Children.IndexOf(old);
        var replacement = new WebView2
        {
            Name = old.Name,
            Style = old.Style,
            Visibility = old.Visibility,
        };

        if (index >= 0) SourceLetterbox.Children.RemoveAt(index);
        SourceLetterbox.Children.Insert(Math.Max(index, 0), replacement);
        Browser = replacement;
        try { UnregisterName(old.Name); RegisterName(old.Name, replacement); } catch { /* name scope is a convenience */ }

        try
        {
            var core = old.CoreWebView2;
            if (core is not null)
            {
                core.NavigationStarting -= Core_NavigationStarting;
                core.NavigationCompleted -= Core_NavigationCompleted;
                core.NewWindowRequested -= Core_NewWindowRequested;
                core.SourceChanged -= Core_SourceChanged;
                core.ProcessFailed -= Core_ProcessFailed;
            }
        }
        catch { /* a dead core may refuse; the control is going away regardless */ }
        try { old.Dispose(); } catch (Exception ex) { Log.Warn($"Disposing the failed Source browser threw: {ex.Message}"); }
    }

    /// <summary>
    /// Terminal outcome for a return transition that cannot finish (spec 14, PP-02): no page to
    /// replay onto, or no completion event within the deadline. Releases the Source commands.
    /// </summary>
    private void AbandonReturnTransition(string reason)
    {
        if (_pendingReturnReplay is null && !_returnInProgress) return;
        _pendingReturnReplay = null;
        Log.Warn($"Return transition ended without replaying state: {reason}.");
        CompleteReturnTransition();
    }

    private void StartReturnDeadline()
    {
        _returnDeadlineTimer ??= CreateReturnDeadlineTimer();
        _returnDeadlineTimer.Stop();
        _returnDeadlineTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateReturnDeadlineTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = WebViewProcessFailurePolicy.ReturnTransitionDeadline,
        };
        timer.Tick += (_, _) => OnReturnDeadlineElapsed();
        return timer;
    }

    private void OnReturnDeadlineElapsed()
    {
        _returnDeadlineTimer?.Stop();
        if (!_returnInProgress) return;
        // A replay loop that is still running re-checks its pending state every step and stops.
        AbandonReturnTransition("no completion within the return deadline");
    }

    // --- Navigation policy (spec 15.2) - shared helper, two distinct handlers ---

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        ClearStalePendingReturnReplay(e.Uri);

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            NavigationPolicy.IsAllowed(uri, NavigationSurface.Source))
        {
            return;
        }

        e.Cancel = true;
        Log.Info($"Source navigation blocked, opening externally: {Log.RedactUrl(e.Uri)}");
        OpenExternal(e.Uri);
    }

    private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        ReleaseBrowserStateAfterNavigation();
        if (_pendingReturnReplay is null) return;
        if (!e.IsSuccess)
        {
            _pendingReturnReplay = null;
            CompleteReturnTransition();
            Log.Warn("Returned-video navigation failed; the Source Window is available without replaying state.");
            return;
        }

        await ReplayPendingReturnStateAsync();
    }

    /// <summary>
    /// Any completed navigation on a live core ends the transient reload/restart notice: a failed
    /// one shows WebView2's own error page, which the panel must not cover with a disabled Retry.
    /// The failed state (browser not ready) stays until Retry.
    /// </summary>
    private void ReleaseBrowserStateAfterNavigation()
    {
        if (_browserReady && !_browserRecoveryInProgress) HideBrowserState();
    }

    private void ClearStalePendingReturnReplay(string? uri)
    {
        if (_pendingReturnReplay?.VideoId is not { Length: > 0 } pendingId) return;
        if (YouTubeUrlHelper.TryParse(uri, out var target) &&
            string.Equals(pendingId, target.VideoId, StringComparison.Ordinal)) return;

        _pendingReturnReplay = null;
        CompleteReturnTransition();
    }

    private async Task ReplayPendingReturnStateAsync()
    {
        if (_pendingReturnReplayInProgress) return;
        var state = _pendingReturnReplay;
        if (state is null)
        {
            CompleteReturnTransition();
            return;
        }

        if (BrowserDataClearActive || _mainWindowClosing)
        {
            if (ReferenceEquals(_pendingReturnReplay, state)) _pendingReturnReplay = null;
            CompleteReturnTransition();
            return;
        }

        var core = Browser.CoreWebView2;
        if (core is null) return;

        _pendingReturnReplayInProgress = true;
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                // Re-validate every iteration: a navigation during the retry window can null/replace the
                // pending state (ClearStalePendingReturnReplay) or move the source off-target. The loop
                // holds `state` locally, so without this it would replay stale state onto the wrong page.
                if (!IsPendingReturnReplayCurrent(state)) return;
                if (!IsCurrentSourceReturnReplayTarget(core, state))
                {
                    if (ReferenceEquals(_pendingReturnReplay, state)) _pendingReturnReplay = null;
                    return;
                }

                var current = await YouTubeDomBridge.ReadPlayerStateAsync(core);
                if (!IsPendingReturnReplayCurrent(state)) return;
                if (current is not null)
                {
                    await ApplyReturnedPlaybackStateAsync(core, state);
                    if (ReferenceEquals(_pendingReturnReplay, state)) _pendingReturnReplay = null;
                    return;
                }

                await Task.Delay(250);
            }

            // Timed out waiting for the video element: clear the pending state so a later same-video
            // navigation cannot re-fire this replay.
            if (ReferenceEquals(_pendingReturnReplay, state)) _pendingReturnReplay = null;
            Log.Warn("Returned-video playback-state replay timed out waiting for the source video element.");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pendingReturnReplay, state)) _pendingReturnReplay = null;
            Log.Error("Returned-video playback-state replay failed.", ex);
        }
        finally
        {
            _pendingReturnReplayInProgress = false;
            if (_pendingReturnReplay is null) CompleteReturnTransition();
        }
    }

    private static bool IsCurrentSourceReturnReplayTarget(CoreWebView2 core, PlayerReturnState state) =>
        state.VideoId is not { Length: > 0 } expected ||
        (YouTubeUrlHelper.TryParse(core.Source, out var target) &&
         string.Equals(target.VideoId, expected, StringComparison.Ordinal));

    private bool IsPendingReturnReplayCurrent(PlayerReturnState state) =>
        ReferenceEquals(_pendingReturnReplay, state) && !BrowserDataClearActive && !_mainWindowClosing;

    private async Task ApplyReturnedPlaybackStateAsync(CoreWebView2 core, PlayerReturnState state)
    {
        if (!IsPendingReturnReplayCurrent(state)) return;
        await YouTubeDomBridge.ApplyPlaybackSettingsAsync(core, state.Volume, state.Muted, state.PlaybackRate);
        if (!IsPendingReturnReplayCurrent(state)) return;

        if (!ReturnReplayAdPolicy.WantsGuardedWrite(state.LastKnownSeconds, state.PlaybackRate))
        {
            await ApplyReturnedPlayPauseAsync(core, state.Paused);
            return;
        }

        // The writers re-check ad state atomically; this read decides whether the seek can go
        // now or must wait (PP-03). Clear is the common case and keeps the single atomic write.
        var adState = await YouTubeDomBridge.ReadAdStateAsync(core);
        if (!IsPendingReturnReplayCurrent(state)) return;
        if (adState == YouTubeAdState.Clear)
        {
            await ApplyReturnedSeekAndPlayPauseAsync(core, state);
            return;
        }

        // An ad owns the page. The play/pause intent may go now (user-requested, not an ad
        // action); the seek and rate are retained for a bounded wait and written only if the
        // page clears while this replay is still for the video the Source shows.
        Log.Info($"Return landed on an ad ({adState}); holding the seek for up to {ReturnReplayAdPolicy.WaitBound.TotalSeconds:0} s.");
        await ApplyReturnedPlayPauseAsync(core, state.Paused);
        var outcome = await ReturnReplayAdPolicy.WaitForClearAsync(
            () => YouTubeDomBridge.ReadAdStateAsync(core),
            () => IsPendingReturnReplayCurrent(state) && IsCurrentSourceReturnReplayTarget(core, state),
            Task.Delay);
        switch (outcome)
        {
            case DeferredSeekOutcome.Applied:
                if (state.LastKnownSeconds is { } seconds) await YouTubeDomBridge.SeekAsync(core, seconds);
                if (state.PlaybackRate is not null)
                    await YouTubeDomBridge.ApplyPlaybackSettingsAsync(core, null, null, state.PlaybackRate);
                Log.Info("Return seek applied after the ad cleared.");
                break;
            case DeferredSeekOutcome.Skipped:
                Log.Info("Return seek skipped: the ad outlasted the wait; the page keeps its own position and controls.");
                break;
            default:
                Log.Info("Return seek dropped: the Source left the returned video during the ad wait.");
                break;
        }
    }

    private static async Task ApplyReturnedSeekAndPlayPauseAsync(CoreWebView2 core, PlayerReturnState state)
    {
        switch (state.Paused)
        {
            case false when state.LastKnownSeconds is not null:
                await YouTubeDomBridge.SeekAndPlayAsync(core, state.LastKnownSeconds.Value);
                break;
            case true when state.LastKnownSeconds is not null:
                await YouTubeDomBridge.SeekAndPauseAsync(core, state.LastKnownSeconds.Value);
                break;
            case false:
                await YouTubeDomBridge.PlayAsync(core);
                break;
            case true:
                await YouTubeDomBridge.PauseAsync(core);
                break;
            default:
                if (state.LastKnownSeconds is not null)
                    await YouTubeDomBridge.SeekAsync(core, state.LastKnownSeconds.Value);
                break;
        }
    }

    private static Task ApplyReturnedPlayPauseAsync(CoreWebView2 core, bool? paused) => paused switch
    {
        false => YouTubeDomBridge.PlayAsync(core),
        true => YouTubeDomBridge.PauseAsync(core),
        _ => Task.CompletedTask,
    };

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            NavigationPolicy.IsAllowed(uri, NavigationSurface.Source))
        {
            // Keep allowed targets (YouTube / Google sign-in) inside the Source Window.
            Browser.CoreWebView2.Navigate(e.Uri);
        }
        else
        {
            Log.Info($"Source new-window blocked, opening externally: {Log.RedactUrl(e.Uri)}");
            OpenExternal(e.Uri);
        }
    }

    private void Core_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        // Display the real URL for usability; we never *log* the query (see Log.RedactUrl).
        var source = Browser.CoreWebView2.Source;
        _lastKnownSourceUrl = source;   // where a recreated browser goes back to (PP-02)
        UrlBox.Text = source;
    }

    // --- Navigation entry points ---

    /// <summary>
    /// Deliver a link from outside the Source page (startup argument, single-instance hand-off).
    /// Routes to whichever surface owns playback; see <see cref="AcceptIncomingLink"/>.
    /// </summary>
    public void NavigateTo(string url) => AcceptIncomingLink(url);

    private IncomingLinkState CurrentIncomingLinkState => new(
        BrowserReady: _browserReady,
        PopoutActive: _player is not null,
        PopoutInProgress: _popoutInProgress,
        ReturnInProgress: _returnInProgress,
        ClearingBrowserData: BrowserDataClearActive,
        Closing: _mainWindowClosing);

    /// <summary>
    /// The receiving boundary for every externally delivered link (REQ-APP-01, spec 13.3,
    /// ADR-0009). The decision is pure (<see cref="IncomingLinkPolicy"/>); this applies it: the
    /// hidden Source is never navigated while a Popout owns playback, a ready Popout retargets in
    /// place and takes focus, and anything arriving mid-transition is retained until the transition
    /// finishes. The result is what the sender is told (PP-05).
    /// </summary>
    internal IncomingLinkDecision AcceptIncomingLink(string? payload)
    {
        var decision = IncomingLinkPolicy.Decide(payload, CurrentIncomingLinkState);
        var target = decision.Target;
        switch (decision.Action)
        {
            case IncomingLinkAction.QueueUntilReady:
                _retainedIncomingTarget = null;
                _pendingUrl = YouTubeUrlHelper.BuildWatchUrl(target!);
                break;
            case IncomingLinkAction.NavigateSource:
                _retainedIncomingTarget = null;
                NavigateInternal(YouTubeUrlHelper.BuildWatchUrl(target!));
                break;
            case IncomingLinkAction.RetargetPopout:
                _retainedIncomingTarget = null;
                if (_player is not null && _player.TryRetargetFromIncomingLink(target!))
                {
                    Log.Info("Incoming link retargeted the Popout Player.");
                    ActivateExistingPlayer();
                }
                else
                {
                    // The player is closing: playback is on its way back to the Source, whose
                    // return transition applies the link.
                    RetainIncomingLink(target!);
                }
                break;
            case IncomingLinkAction.Retain:
                RetainIncomingLink(target!);
                break;
            case IncomingLinkAction.Reject:
                Log.Warn("Incoming link rejected: not a supported YouTube target.");
                break;
            case IncomingLinkAction.Unavailable:
                Log.Info("Incoming link not taken: the Source Window is closing.");
                break;
        }

        return decision;
    }

    private void RetainIncomingLink(YouTubeTarget target)
    {
        _retainedIncomingTarget = target;
        var url = YouTubeUrlHelper.BuildWatchUrl(target);
        Log.Info($"Incoming link retained until playback ownership is stable: {Log.RedactUrl(url)}");

        // Short pending status (Q-6): the address bar shows where the Source is headed, and the
        // placeholder (when it is up) says why nothing moved yet.
        UrlBox.Text = url;
        if (SourcePlaceholder.Visibility == Visibility.Visible)
        {
            PlaceholderNoteText.Text = PendingIncomingLinkNote;
            PlaceholderNoteText.Visibility = Visibility.Visible;
        }
    }

    internal const string PendingIncomingLinkNote =
        "A new link is waiting. It opens in this window after Bring video back.";

    /// <summary>
    /// Called at the end of every ownership transition (launch settled, return finished, clear
    /// finished): re-decides the retained link against the new state. It may retarget the player a
    /// launch just created, navigate the Source, queue behind a browser that is (re)starting, or
    /// stay retained if another transition is already under way.
    /// </summary>
    private void ApplyRetainedIncomingLink()
    {
        var target = _retainedIncomingTarget;
        if (target is null) return;
        _retainedIncomingTarget = null;
        AcceptIncomingLink(YouTubeUrlHelper.BuildWatchUrl(target));
    }

    internal YouTubeTarget? RetainedIncomingTargetForTests => _retainedIncomingTarget;
    internal IncomingLinkState IncomingLinkStateForTests => CurrentIncomingLinkState;
    internal void SetClearingBrowserDataForTests(bool clearing) => _clearingBrowserData = clearing;
    internal void SetMainWindowClosingForTests(bool closing) => _mainWindowClosing = closing;
    internal void SetPopoutInProgressForTests(bool inProgress) => _popoutInProgress = inProgress;
    internal Task BringVideoBackForTestsAsync() => BringVideoBackAsync();
    internal void ApplyRetainedIncomingLinkForTests() => ApplyRetainedIncomingLink();

    private void NavigateInternal(string? input)
    {
        if (!_browserReady || Browser.CoreWebView2 is null)
        {
            _pendingUrl = input;
            return;
        }

        var target = ResolveNavigationUrl(input);
        try { Browser.CoreWebView2.Navigate(target); }
        catch (Exception ex) { Log.Error("Navigate failed.", ex); }
    }

    private static string ResolveNavigationUrl(string? input)
    {
        input = (input ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(input)) return "https://www.youtube.com/";

        if (YouTubeUrlHelper.TryParse(input, out var target))
            return YouTubeUrlHelper.BuildWatchUrl(target);

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return input;
        }

        // Anything else: treat as a YouTube search.
        return "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(input);
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NavigateInternal(UrlBox.Text);
        e.Handled = true;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void HomeButton_Click(object sender, RoutedEventArgs e) => NavigateInternal("https://www.youtube.com/");

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focusAddress = e.Key == Key.F6 ||
                           (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        if (!focusAddress || !SourceCommandsAvailable) return;

        UrlBox.Focus();
        UrlBox.SelectAll();
        e.Handled = true;
    }

    private void SourceToolbar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplySourceToolbarLayout(e.NewSize.Width);

    internal void ApplySourceToolbarLayout(double width)
    {
        var compact = width > 0 && width < CompactToolbarThreshold;
        PopOutButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PopOutButtonIcon.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
    }

    private void ProfileActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SourceCommandsAvailable) return;
        UpdateProfileCommandState();
        ProfileActionsMenu.PlacementTarget = ProfileActionsButton;
        ProfileActionsMenu.Placement = PlacementMode.Bottom;
        ProfileActionsMenu.IsOpen = true;
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open an external link.", ex);
        }
    }

    // --- Pin / topmost (spec 6.3) ---

    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePinSuspendedForPopout) return;
        ApplyTopmost(PinToggle.IsChecked == true);
        _settings.MainWindow.Topmost = Topmost;
        SaveSettings();
    }

    private void ApplyTopmost(bool on)
    {
        if (_sourcePinSuspendedForPopout)
        {
            _sourceTopmostBeforePopout = on;
            Topmost = false;
            PinToggle.IsChecked = false;
            PinnedHint.Visibility = Visibility.Collapsed;
            UpdateSourcePinAffordance(false);
            return;
        }

        Topmost = on;
        PinToggle.IsChecked = on;
        PinnedHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourcePinAffordance(on);
    }

    private void SuspendSourcePinForPopout()
    {
        if (_sourcePinSuspendedForPopout) return;
        _sourceTopmostBeforePopout = Topmost;
        _sourcePinSuspendedForPopout = true;
        PinToggle.IsEnabled = false;
        Topmost = false;
        PinToggle.IsChecked = false;
        PinnedHint.Visibility = Visibility.Collapsed;
        UpdateSourcePinAffordance(false);
    }

    private void RestoreSourcePinAfterPopout()
    {
        if (!_sourcePinSuspendedForPopout)
        {
            PinToggle.IsEnabled = true;
            ApplyTopmost(Topmost);
            return;
        }

        var restoreTopmost = _sourceTopmostBeforePopout;
        _sourcePinSuspendedForPopout = false;
        PinToggle.IsEnabled = true;
        ApplyTopmost(restoreTopmost);
    }

    private void UpdateSourcePinAffordance(bool pinned)
    {
        var label = pinned
            ? "Unpin Source Window from top"
            : "Pin Source Window on top";
        PinToggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, label);
    }

    private void CaptureSourceTopmostPreferenceForClose()
    {
        _settings.MainWindow.Topmost = _sourcePinSuspendedForPopout
            ? _sourceTopmostBeforePopout
            : Topmost;
    }

    internal bool SavedSourceTopmostForTests => _settings.MainWindow.Topmost;
    internal void SuspendSourcePinForPopoutForTests() => SuspendSourcePinForPopout();
    internal void RestoreSourcePinAfterPopoutForTests() => RestoreSourcePinAfterPopout();
    internal void CaptureSourceTopmostPreferenceForCloseForTests() => CaptureSourceTopmostPreferenceForClose();

    private void ApplySourceAppearance()
    {
        ApplySourceAppearance(ResolvedAccentColor);
    }

    private void ApplySourceAppearance(string accentColor)
    {
        // One contrast-safe presentation accent drives the Source Pin and pinned hint. The persisted
        // Theme.AccentColor remains exact; SurfaceHover is the live floor used by the shared resources.
        var surface = Application.Current.TryFindResource("SurfaceHover") as SolidColorBrush;
        var pinBrush = surface is null
            ? ThemeColors.Brush(accentColor)
            : ThemeColors.ContrastBrush(accentColor, surface.Color);
        ToggleAccent.SetCheckedBrush(PinToggle, pinBrush);
        PinnedHint.Foreground = pinBrush;
    }

    // --- Auto: auto-popout on playback (spec §6.1) ---

    private void AutoToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoPopout = AutoToggle.IsChecked == true;
        SaveSettings();
        UpdateAutoDetector();
    }

    /// <summary>Reflect the Auto setting on the toolbar toggle (no side effects on the detector).</summary>
    private void ApplyAuto(bool on) => AutoToggle.IsChecked = on;

    /// <summary>Start/stop the playback detector to match the Auto setting and browser readiness.</summary>
    private void UpdateAutoDetector()
    {
        if (_browserReady && _settings.AutoPopout)
        {
            if (_autoTimer is null)
            {
                _autoTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),   // matches PlayerWindow._syncTimer
                };
                _autoTimer.Tick += AutoTimer_Tick;
            }
            // Clear the de-dup on (re)enable so Auto immediately pops the video already playing.
            _autoLastHandledVideoId = null;
            _autoSuppressionFailedVideoId = null;
            _autoTimer.Start();
        }
        else
        {
            _autoTimer?.Stop();
        }
    }

    private async void AutoTimer_Tick(object? sender, EventArgs e)
    {
        // One tick at a time (a slow DOM read must not stack); skip while a popout owns the source.
        if (_autoTickInProgress) return;
        if (!_browserReady || !_settings.AutoPopout) return;
        if (_popoutInProgress || _returnInProgress || _player is not null || BrowserDataClearActive) return;
        var core = Browser.CoreWebView2;
        if (core is null) return;

        _autoTickInProgress = true;
        try
        {
            var src = core.Source;
            YouTubeUrlHelper.TryParse(src, out var target);

            if (!string.IsNullOrEmpty(_autoSuppressionFailedVideoId))
            {
                if (string.Equals(target.VideoId, _autoSuppressionFailedVideoId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // A new Source video is a new Auto edge; the old failure must not suppress it.
                _autoSuppressionFailedVideoId = null;
            }

            var isWatchVideo = YouTubeUrlHelper.IsWatchUrl(src);
            if (!AutoPopoutPolicy.NeedsPlayerState(
                    autoEnabled: true,
                    isWatchVideo,
                    currentVideoId: target.VideoId,
                    lastHandledVideoId: _autoLastHandledVideoId,
                    popoutActive: _popoutInProgress || _returnInProgress || _player is not null))
            {
                return;
            }

            var state = await YouTubeDomBridge.ReadPlayerStateAsync(core);
            var decision = AutoPopoutPolicy.Decide(
                autoEnabled: true,
                isPlaying: state is { Paused: false },
                isWatchVideo,
                currentVideoId: target.VideoId,
                lastHandledVideoId: _autoLastHandledVideoId,
                popoutActive: _popoutInProgress || _returnInProgress || _player is not null);

            if (decision == AutoPopDecision.Pop)
            {
                Log.Info("Auto: playback detected on a new video; starting Video Popout.");
                // Carry the exact identity Auto just evaluated into launch. Re-resolving through a
                // stale YouTube SPA canonical can silently substitute another video and break the
                // once-per-video return latch.
                await StartVideoPopoutAsync(target);
            }
        }
        catch (Exception ex)
        {
            // Best-effort, per the DOM-bridge contract (Q-6): a hiccup means no auto-pop, never a crash.
            Log.Error("Auto-popout detector tick failed.", ex);
        }
        finally
        {
            _autoTickInProgress = false;
        }
    }

    // --- Profiles (spec 17, MVP basic save/load) ---

    private void LoadProfilesIntoCombo()
    {
        _loadingProfiles = true;
        var profiles = _settings.Profiles.ToList();
        ProfilesCombo.ItemsSource = profiles;
        ProfilesCombo.SelectedItem = _settings.ActiveProfileName is null
            ? null
            : profiles.FirstOrDefault(p => string.Equals(p.Name, _settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
        _loadingProfiles = false;
        UpdateProfileCommandState();
        ApplyResolvedAccent();
    }

    private bool SourceCommandsAvailable =>
        !_sourceNavigationSuspended && !_returnInProgress && !_popoutInProgress &&
        !BrowserDataClearActive && !_mainWindowClosing;

    /// <summary>Hidden Source content cannot keep accepting navigation or profile commands.</summary>
    private void UpdateSourceCommandAvailability()
    {
        var enabled = SourceCommandsAvailable;
        SourceNavigationGroup.IsEnabled = enabled;
        SourceProfileGroup.IsEnabled = enabled;
        BackButton.IsEnabled = enabled;
        ReloadButton.IsEnabled = enabled;
        HomeButton.IsEnabled = enabled;
        UrlBox.IsEnabled = enabled;
        ProfilesCombo.IsEnabled = enabled;
        ProfileActionsButton.IsEnabled = enabled;
        SaveProfileMenuItem.IsEnabled = enabled;

        var hasProfile = enabled && ProfilesCombo.SelectedItem is Profile;
        EditProfileMenuItem.IsEnabled = hasProfile;
        DeleteProfileMenuItem.IsEnabled = hasProfile;
        if (!enabled) ProfileActionsMenu.IsOpen = false;
        SettingsButton.IsEnabled = !_returnInProgress && !_privacyActionInProgress && !_mainWindowClosing;
    }

    /// <summary>Edit/Delete act on the selected profile, so they are enabled only when one is picked.</summary>
    private void UpdateProfileCommandState() => UpdateSourceCommandAvailability();

    private void ProfilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProfileCommandState();
        if (_loadingProfiles) return;
        if (ProfilesCombo.SelectedItem is not Profile profile)
        {
            ProfileAccentService.ClearActiveProfile(_settings);
            ApplyResolvedAccent();
            SaveSettings();
            return;
        }

        ProfileAccentService.SetActiveProfile(_settings, profile);
        ApplyResolvedAccent();
        SaveSettings();
        if (profile.Topmost is bool tm) ApplyTopmost(tm);
        NavigateInternal(profile.Url);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var currentUrl = Browser.CoreWebView2?.Source ?? _settings.LastUrl;
        var (ok, error) = ProfileService.ValidateUrl(currentUrl);
        if (!ok)
        {
            Prompt.ShowInfo(this, "Save profile", error!);
            return;
        }

        var name = Prompt.AskText(this, "Save profile", "Name this profile:");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var existing = ProfileService.Find(_settings, name);
        if (existing is not null)
        {
            if (!Prompt.AskConfirm(this, "Overwrite profile?",
                    $"A profile named \"{name}\" already exists. Overwrite it?", "Overwrite"))
            {
                return;
            }
        }

        ProfileService.Save(_settings, ProfileService.CreateQuickSaveProfile(existing, name, currentUrl, Topmost));
        SaveSettings();
        LoadProfilesIntoCombo();
        Log.Info("Profile saved.");
    }

    private void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesCombo.SelectedItem is not Profile original) return;

        var edited = Prompt.EditProfile(this, original.Name, original.Url, original.Mode, original.AccentColor,
            EffectiveAccentColor, presentation: original.Presentation);
        if (edited is null)
        {
            ApplyResolvedAccent();
            return;   // cancelled (and an invalid edit never gets here)
        }

        // Name, Url, and the Phase 3 playback Mode are editable; carry the other Phase 2 fields through.
        var updated = new Profile
        {
            Name = edited.Value.Name,
            Url = edited.Value.Url,
            Mode = PlaybackModePolicy.NormalizeProfileMode(edited.Value.Mode),
            Presentation = PopoutPresentationPolicy.NormalizeProfilePresentation(edited.Value.Presentation),
            AccentColor = ProfileService.NormalizeAccentForStorage(edited.Value.AccentColor),
            Topmost = original.Topmost,
            FadeEnabled = original.FadeEnabled,
            Bounds = original.Bounds,
        };

        var outcome = ProfileService.Update(_settings, original.Name, updated);
        if (outcome == ProfileUpdateOutcome.NameConflict)
        {
            // Same overwrite/rename prompt the Save path uses (spec 17: no silent clutter).
            if (!Prompt.AskConfirm(this, "Overwrite profile?",
                    $"A profile named \"{updated.Name}\" already exists. Overwrite it?", "Overwrite"))
            {
                return;
            }
            outcome = ProfileService.Update(_settings, original.Name, updated, overwrite: true);
        }

        if (outcome != ProfileUpdateOutcome.Updated) return;   // NotFound is not reachable from a live selection

        ProfileAccentService.RenameActiveProfileIfMatches(_settings, original.Name, updated.Name);

        SaveSettings();
        LoadProfilesIntoCombo();
        Log.Info("Profile edited.");
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesCombo.SelectedItem is not Profile profile) return;

        if (!Prompt.AskConfirm(this, "Delete profile?",
                $"Delete the profile \"{profile.Name}\"? This can't be undone.", "Delete", danger: true))
        {
            return;
        }

        ProfileService.Remove(_settings, profile.Name);
        ProfileAccentService.ClearActiveProfileIfMatches(_settings, profile.Name);
        SaveSettings();
        LoadProfilesIntoCombo();
        Log.Info("Profile deleted.");
    }

    // --- Privacy actions: Settings window (spec 19, Phase 2, REQ-PRIVACY-01/02) ---

    /// <summary>Whether Clear browser data can run right now (live core + no prior clear in flight).</summary>
    internal bool CanClearBrowserData =>
        _browserReady && Browser.CoreWebView2 is not null && !_browserDataClearCoordinator.IsRunning;

    internal bool BrowserDataClearInProgressForTests => _browserDataClearCoordinator.IsRunning;
    internal bool BrowserDataClearActiveForTests => BrowserDataClearActive;
    internal bool SourceCommandsAvailableForTests => SourceCommandsAvailable;
    internal PlayerWindow? PlayerForTests => _player;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings(this);

    private void Player_SettingsRequested(object? sender, EventArgs e) =>
        OpenSettings(sender as Window ?? this);

    private void OpenSettings(Window owner)
    {
        if (_privacyActionInProgress) return;

        if (_settingsDialog is not null)
        {
            PrepareExistingSettingsDialog(_settingsDialog, owner);
            _settingsDialog.Activate();
            return;
        }

        var browserReadyForClear = _browserReady && Browser.CoreWebView2 is not null;
        var clearUnavailableHint = _browserDataClearCoordinator.IsRunning
            ? PrivacyService.ClearAlreadyRunningHint
            : null;
        var dialog = new SettingsWindow(
            isBrowserReady: browserReadyForClear,
            clearBrowserDataUnavailableHint: clearUnavailableHint,
            themeId: _settings.Theme.ThemeId,
            accentColor: ResolvedAccentColor,
            fadeIdleDelayMs: EffectiveFadeIdleDelayMs,
            compactMode: _settings.Player.CompactMode,
            activeOpacityOverride: _settings.Theme.ActiveWindowOpacity,
            idleOpacityOverride: _settings.Theme.IdleWindowOpacity,
            stripAutoHideOverride: _settings.Theme.StripAutoHide,
            cornerStyle: EffectiveCornerStyle,
            accentEditContext: AccentEditContext,
            accentIntensity: EffectiveAccentIntensity,
            accentFollowsThemePreset: ProfileAccentService.AccentOverridingProfile(_settings) is null,
            focusedPresentation: _settings.Player.FocusedPresentation)
        {
            Owner = owner,
            // A Source-owned dialog still has to clear an already-pinned Popout, and vice versa.
            Topmost = owner.Topmost || Topmost || (_player?.Topmost ?? false),
        };
        _settingsDialog = dialog;
        // Live preview (spec 7.3 / docs/Theme_Preset_Differences.md): slider moves apply to the open popout immediately.
        // animate:false — the event fires per drag tick; restarting the 150ms fade each tick would
        // stair-step and churn animation timers. The drag itself is the animation.
        dialog.OpacityPreviewChanged += (constant, idle) =>
        {
            ApplySourceBarOpacity(constant);
            _player?.ApplyWindowOpacity(constant, idle, animate: false);
        };
        dialog.AccentPreviewChanged += QueueAccentPreview;
        dialog.AccentIntensityPreviewChanged += QueueAccentIntensityPreview;
        dialog.ThemePreviewChanged += () => PreviewTheme(dialog);

        bool accepted;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            _settingsDialog = null;
        }
        // A final pointer update can still be waiting for the 33 ms coalescing tick when the user
        // clicks Apply. Commit that latest visual value before clearing the preview session; the
        // persisted writer below remains the source of truth for accepted settings.
        if (accepted) ApplyPendingAccentPreview();
        CancelQueuedAccentPreview();
        if (!accepted)
        {
            RevertAppearancePreview();
            return;
        }

        if (dialog.RequestedAction == PrivacyAction.ResetAppState)
        {
            PerformResetAppState();
            return;
        }

        if (dialog.AppearanceChanged)
        {
            ApplyPlayerPreferences(dialog.ThemeId, dialog.AccentColor, dialog.FadeIdleDelayMs, dialog.CompactMode,
                dialog.FocusedPresentation,
                dialog.ActiveOpacityOverride, dialog.IdleOpacityOverride, dialog.StripAutoHideOverride,
                dialog.CornerStyle, dialog.AccentIntensity);
        }

        switch (dialog.RequestedAction)
        {
            case PrivacyAction.ClearBrowserData:
                _ = PerformClearBrowserDataAsync();
                break;
        }
    }

    private void PrepareExistingSettingsDialog(SettingsWindow dialog, Window requester)
    {
        if (dialog.WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(dialog);

        // ShowDialog keeps the first requester as Owner for the lifetime of the modal session.
        // Re-parenting a visible modal is unsafe; raising its topmost level is enough to keep a
        // repeat request from leaving Settings behind either pinned PiPlay window.
        dialog.Topmost = dialog.Topmost || requester.Topmost || Topmost || (_player?.Topmost ?? false);
    }

    private void PerformResetAppState()
    {
        if (_privacyActionInProgress) return;
        _privacyActionInProgress = true;
        try
        {
            ApplyResetState();
            Prompt.ShowInfo(this, PrivacyService.ResetDoneTitle, PrivacyService.ResetDoneBody);
        }
        finally
        {
            _privacyActionInProgress = false;
        }
    }

    /// <summary>
    /// Apply a reset to the live UI: defaults to settings.json, empty profiles combo, pin off.
    /// References no Browser/WebView2 member and queues no navigation, so the user stays signed in.
    /// Internal so a headless WPF test can prove it runs with a null CoreWebView2.
    /// </summary>
    internal void ApplyResetState()
    {
        _settings = _settingsService.Reset();
        ApplyTopmost(false);
        ApplyAuto(false);
        ApplyThemeResources();
        ApplySourceAppearance();
        ApplySourceBarOpacity();
        ApplyOwnCornerMode();
        ApplyOpenPlayerAppearance();
        UpdateAutoDetector();   // Auto is off after reset → stop the detector
        LoadProfilesIntoCombo();
    }

    private void ApplyPlayerPreferences(string themeId, string accentColor, int fadeIdleDelayMs, bool compactMode,
        bool focusedPresentation,
        double? activeOpacityOverride, double? idleOpacityOverride, bool? stripAutoHideOverride,
        string cornerStyle = ThemeCatalog.DefaultCornerStyle,
        int? accentIntensity = null)
    {
        // The profile-aware writer routes the effective accent to the named Settings target;
        // behavior values are NULLABLE overrides (theme code review P2) — it keeps
        // nulls null so an accent-only apply leaves the user following preset defaults, and
        // mirrors the EFFECTIVE values onto the legacy Player fields.
        ThemeSettingsWriter.Apply(_settings, themeId, accentColor, fadeIdleDelayMs, compactMode,
            activeOpacityOverride, idleOpacityOverride, stripAutoHideOverride, cornerStyle, accentIntensity);
        _settings.Player.FocusedPresentation = focusedPresentation;

        // Restyle the theme-driven shell resources live (DynamicResource consumers re-resolve), then
        // restore the RESOLVED profile/global accent over that palette. Full theme application derives
        // from Theme.AccentColor and cannot know an active profile override by itself.
        ApplyThemeResources();
        ApplyAccentResourcesAndSource(ResolvedAccentColor);
        ApplySourceBarOpacity();
        ApplyOwnCornerMode();
        // Apply the open player's complete appearance once. Calling ApplyResolvedAccent above would
        // also send an accent-only player update, duplicating this accepted-Settings repaint.
        ApplyOpenPlayerAppearance();
        SaveSettings();
    }

    private string EffectiveAccentColor =>
        ThemePreferenceResolver.AccentColor(_settings.Theme, _settings.Player);

    private Profile? ActiveProfile =>
        ProfileAccentService.ActiveProfile(_settings);

    internal string ResolvedAccentColor =>
        ProfileAccentService.ResolvedAccentColor(_settings, EffectiveAccentColor);

    /// <summary>
    /// Tells Settings which color its accent picker is about to edit (P2, spec decision 7). The picker
    /// always edits whatever is painting the app, so it must say which — otherwise a user editing "the
    /// app accent" while a colored profile is active would not know they were changing that profile.
    /// </summary>
    internal string AccentEditContext =>
        ProfileAccentService.AccentOverridingProfile(_settings) is { } profile
            ? $"Editing the color for profile “{profile.Name}” — it is driving the app accent. "
              + "Deselect the profile to edit the default accent instead."
            : "Editing the app accent.";

    internal void LivePreviewAccent(string hex)
    {
        if (!ThemeCatalog.IsValidHex(hex)) return;
        ApplyAccentEverywhere(ThemeCatalog.NormalizeAccentColor(hex));
    }

    private void QueueAccentPreview(string hex)
    {
        if (!ThemeCatalog.IsValidHex(hex)) return;

        var normalized = ThemeCatalog.NormalizeAccentColor(hex);
        if (string.Equals(normalized, _pendingAccentPreview, StringComparison.OrdinalIgnoreCase)) return;
        if (_pendingAccentPreview is null
            && string.Equals(normalized, _lastAppliedAccentPreview, StringComparison.OrdinalIgnoreCase)) return;

        _pendingAccentPreview = normalized;
        if (!_accentPreviewTimer.IsEnabled) _accentPreviewTimer.Start();
    }

    /// <summary>Queue a live preview of how far the accent reaches (Settings' intensity slider).</summary>
    private void QueueAccentIntensityPreview(int intensity)
    {
        var normalized = ThemeCatalog.NormalizeAccentIntensity(intensity);
        if (normalized == _pendingAccentIntensityPreview) return;
        if (_pendingAccentIntensityPreview is null && normalized == _lastAppliedAccentIntensityPreview) return;

        _pendingAccentIntensityPreview = normalized;
        if (!_accentPreviewTimer.IsEnabled) _accentPreviewTimer.Start();
    }

    private void AccentPreviewTimer_Tick(object? sender, EventArgs e) => ApplyPendingAccentPreview();

    private void ApplyPendingAccentPreview()
    {
        _accentPreviewTimer.Stop();
        var accent = _pendingAccentPreview;
        var intensity = _pendingAccentIntensityPreview;
        _pendingAccentPreview = null;
        _pendingAccentIntensityPreview = null;
        if (accent is null && intensity is null) return;

        // The two channels paint the SAME derivation, so they must be applied as a pair: an
        // intensity-only move still has to repaint with the accent the user is currently previewing
        // (otherwise it would dedupe away as "accent unchanged" and the slider would look dead), and an
        // accent-only move has to keep the previewed reach.
        if (intensity is int i) _previewAccentIntensity = i;
        var target = accent ?? _lastAppliedAccentPreview ?? ResolvedAccentColor;

        if (string.Equals(target, _lastAppliedAccentPreview, StringComparison.OrdinalIgnoreCase)
            && _previewAccentIntensity == _lastAppliedAccentIntensityPreview)
        {
            return;   // this frame changed nothing that is painted
        }

        LivePreviewAccent(target);
        _lastAppliedAccentPreview = target;
        _lastAppliedAccentIntensityPreview = _previewAccentIntensity;
    }

    private void CancelQueuedAccentPreview()
    {
        _accentPreviewTimer.Stop();
        _pendingAccentPreview = null;
        _pendingAccentIntensityPreview = null;
        _lastAppliedAccentPreview = null;
        _lastAppliedAccentIntensityPreview = null;
        _previewAccentIntensity = null;   // stop overriding: rendering follows persisted settings again
        _previewThemeId = null;
    }

    private void ApplyResolvedAccent() => ApplyAccentEverywhere(ResolvedAccentColor);

    private void ApplyAccentEverywhere(string accentColor)
    {
        ApplyAccentResourcesAndSource(accentColor);
        _player?.ApplyAccent(accentColor);
    }

    private void ApplyAccentResourcesAndSource(string accentColor)
    {
        // Pass the intensity EXPLICITLY. ApplyAccentOnly defaults it to null (= the catalog default), so
        // omitting it here still compiles and still looks wired — while silently snapping a user who
        // chose 0 or 100 back to 50 on every profile switch, dialog cancel, and reset.
        ThemeResourceApplier.ApplyAccentOnly(Application.Current.Resources, accentColor,
            ThemeCatalog.PresetFor(_previewThemeId ?? _settings.Theme.ThemeId), EffectiveAccentIntensity);
        ApplySourceAppearance(accentColor);
    }

    /// <summary>
    /// How far the accent currently reaches: the live preview while Settings is open, otherwise the
    /// persisted preference. Every accent repaint reads this, so preview and persisted state can never
    /// disagree about what is on screen.
    /// </summary>
    private int EffectiveAccentIntensity =>
        _previewAccentIntensity ?? ThemeCatalog.NormalizeAccentIntensity(_settings.Theme.AccentIntensity);

    private void ApplyThemeResources()
    {
        ThemeResourceApplier.Apply(Application.Current.Resources, _settings.Theme, _settings.Player);
    }

    private void PreviewTheme(SettingsWindow dialog)
    {
        _previewThemeId = dialog.ThemeId;
        _previewAccentIntensity = dialog.AccentIntensity;
        var previewTheme = new ThemeSettings
        {
            ThemeId = dialog.ThemeId,
            AccentColor = dialog.AccentColor,
            AccentIntensity = dialog.AccentIntensity,
            CornerStyle = dialog.CornerStyle,
        };
        ThemeResourceApplier.Apply(Application.Current.Resources, previewTheme, _settings.Player);
        ApplySourceAppearance(dialog.AccentColor);
        ApplySourceBarOpacity(dialog.ConstantWindowOpacity);

        var cornerMode = ThemeCatalog.DwmCornersFor(
            ThemeCatalog.PresetFor(dialog.ThemeId), dialog.CornerStyle);
        var popoutCornerRadius = ThemeCatalog.RadiiFor(
            ThemeCatalog.PresetFor(dialog.ThemeId), dialog.CornerStyle).PopoutFrame;
        ApplyOwnCornerMode(cornerMode);
        _player?.ApplyAppearance(dialog.AccentColor, dialog.FadeIdleDelayMs, dialog.StripAutoHide);
        _player?.ApplyWindowOpacity(dialog.ConstantWindowOpacity, dialog.IdleWindowOpacity, animate: false);
        _player?.ApplyCornerAppearance(cornerMode, popoutCornerRadius);
    }

    private void RevertAppearancePreview()
    {
        // Dismissed without applying: undo the complete preview transaction, not just its accent.
        CancelQueuedAccentPreview();
        ApplyThemeResources();
        ApplyResolvedAccent();
        ApplySourceBarOpacity();
        ApplyOwnCornerMode();
        ApplyOpenPlayerAppearance();
    }

    private void ApplySourceBarOpacity() => ApplySourceBarOpacity(EffectiveActiveWindowOpacity);

    private void ApplySourceBarOpacity(double opacity) =>
        MainBarBackdrop.Opacity = WindowOpacityPolicy.Normalize(opacity);

    private int EffectiveFadeIdleDelayMs =>
        ThemePreferenceResolver.FadeIdleDelayMs(_settings.Theme, _settings.Player);

    private bool EffectiveStripAutoHide =>
        ThemePreferenceResolver.StripAutoHide(_settings.Theme, _settings.Player);

    private double EffectiveActiveWindowOpacity =>
        ThemePreferenceResolver.ActiveWindowOpacity(_settings.Theme, _settings.Player);

    private double EffectiveIdleWindowOpacity =>
        ThemePreferenceResolver.IdleWindowOpacity(_settings.Theme, _settings.Player);

    private string EffectiveCornerStyle =>
        ThemePreferenceResolver.CornerStyle(_settings.Theme);

    private DwmCornerMode EffectiveDwmCornerMode =>
        ThemePreferenceResolver.DwmCorners(_settings.Theme);

    private double EffectivePopoutCornerRadiusDip =>
        ThemePreferenceResolver.Radii(_settings.Theme).PopoutFrame;

    /// <summary>Apply the theme/override native corner preference to this window's own HWND.
    /// No-op before SourceInitialized (which applies the initial mode).</summary>
    private void ApplyOwnCornerMode() => ApplyOwnCornerMode(EffectiveDwmCornerMode);

    private void ApplyOwnCornerMode(DwmCornerMode cornerMode)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        WindowOpacityApplier.SetCornerMode(hwnd, cornerMode);
        WindowOpacityApplier.SetBorderColor(hwnd, suppress: true);   // P1 borderless: no Win11 DWM frame hairline
    }

    private void ApplyOpenPlayerAppearance()
    {
        ApplyOpenPlayerAppearance(ResolvedAccentColor);
    }

    private void ApplyOpenPlayerAppearance(string accentColor)
    {
        _player?.ApplyAppearance(accentColor, EffectiveFadeIdleDelayMs, EffectiveStripAutoHide);
        _player?.ApplyWindowOpacity(EffectiveActiveWindowOpacity, EffectiveIdleWindowOpacity);
        _player?.ApplyCornerAppearance(EffectiveDwmCornerMode, EffectivePopoutCornerRadiusDip);
    }

    internal void ReplaceSettingsForTests(AppSettings settings)
    {
        _settings = settings;
        ApplyThemeResources();
        ApplySourceAppearance();   // refresh the imperative pin/hint accent from the injected settings
        ApplySourceBarOpacity();
        LoadProfilesIntoCombo();
    }

    internal string ResolvedAccentColorForTests => ResolvedAccentColor;

    // The dismiss-without-apply / edit-cancel revert path (ShowDialog() != true), exposed so the
    // revert can be asserted without driving a modal dialog.
    internal void RevertPreviewedAccentForTests() => ApplyResolvedAccent();

    internal void QueueAccentPreviewForTests(string hex) => QueueAccentPreview(hex);
    internal void FlushAccentPreviewForTests() => ApplyPendingAccentPreview();
    internal bool HasPendingAccentPreviewForTests => _pendingAccentPreview is not null;

    internal void QueueAccentIntensityPreviewForTests(int intensity) => QueueAccentIntensityPreview(intensity);
    internal void CancelQueuedAccentPreviewForTests() => CancelQueuedAccentPreview();
    internal int EffectiveAccentIntensityForTests => EffectiveAccentIntensity;
    internal void PreviewThemeForTests(SettingsWindow dialog) => PreviewTheme(dialog);
    internal void RevertAppearancePreviewForTests() => RevertAppearancePreview();
    /// Lets the cancel-transaction guard observe the half of the revert that only an OPEN popout can
    /// show. Without a player attached, dropping ApplyOpenPlayerAppearance from the revert is invisible.
    internal void AttachPlayerForTests(PlayerWindow? player) => _player = player;
    /// Attach AND wire the return path, so closing the headless player runs Player_OnClosed the
    /// way a real Bring video back does.
    internal void AttachPlayerWithReturnForTests(PlayerWindow player)
    {
        _player = player;
        player.PlayerClosed += Player_OnClosed;
    }
    internal void PrepareExistingSettingsDialogForTests(SettingsWindow dialog, Window requester) =>
        PrepareExistingSettingsDialog(dialog, requester);

    internal void SelectProfileForTests(string? name)
    {
        ProfileAccentService.SetActiveProfile(_settings, name is null ? null : ProfileService.Find(_settings, name));
        LoadProfilesIntoCombo();
    }

    internal (string AccentColor, int FadeIdleDelayMs, double ActiveWindowOpacity, double IdleWindowOpacity,
        bool StripAutoHide, string CornerStyle) EffectivePlayerPreferencesForTests =>
        (ResolvedAccentColor, EffectiveFadeIdleDelayMs, EffectiveActiveWindowOpacity, EffectiveIdleWindowOpacity,
            EffectiveStripAutoHide, EffectiveCornerStyle);

    private Task PerformClearBrowserDataAsync() =>
        PerformClearBrowserDataAsync(clearFactory: null, PrivacyService.ClearTimeout,
            (title, body) => Prompt.ShowInfo(this, title, body));

    /// <summary>
    /// The one destructive profile clear (spec 16.4, PP-06). <paramref name="clearFactory"/> is
    /// the real WebView2 clear on the Source core unless a test supplies a controllable task;
    /// <paramref name="foregroundWait"/> bounds the status wait, never the operation; and
    /// <paramref name="notify"/> is the modal result prompt.
    /// </summary>
    internal async Task PerformClearBrowserDataAsync(
        Func<Task>? clearFactory, TimeSpan foregroundWait, Action<string, string> notify)
    {
        if (_privacyActionInProgress) return;

        Task? clearTask = null;
        Stopwatch? stopwatch = null;

        try
        {
            // Re-check readiness at execution time (the cached enabled state can be stale). This
            // lives INSIDE the try so a throw here (e.g. CoreWebView2 access during a WebView2
            // teardown) can never escape this fire-and-forget task unobserved.
            if (_browserDataClearCoordinator.IsRunning)
            {
                notify(PrivacyService.ClearResultTitle, PrivacyService.ClearAlreadyRunning);
                return;
            }

            var core = Browser.CoreWebView2;
            if (!_browserReady || (core is null && clearFactory is null))
            {
                notify(PrivacyService.ClearResultTitle, PrivacyService.ClearBrowserNotReady);
                return;
            }

            // Order matters (PP-06): every playback gate closes and the Popout leaves BEFORE the
            // clear starts, so nothing scripts a profile that is being wiped. The Popout's return
            // handler sees BrowserDataClearActive and skips driving Source playback.
            _privacyActionInProgress = true;
            _clearingBrowserData = true;
            _pendingReturnReplay = null;
            if (_returnInProgress) CompleteReturnTransition();
            SettingsButton.IsEnabled = false;   // no second Settings window mid-await
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();

            // Single shared profile: closing the popout avoids it showing a logged-out surface.
            if (_player is not null) { try { _player.Close(); } catch { /* ignore */ } }

            var factory = clearFactory ?? (() => PrivacyService.ClearBrowserDataAsync(core!));
            if (!_browserDataClearCoordinator.TryStart(factory, out clearTask))
            {
                notify(PrivacyService.ClearResultTitle, PrivacyService.ClearAlreadyRunning);
                return;
            }
            var generation = ++_browserDataClearGeneration;

            // Bound the wait (PrivacyService.ClearTimeout) so a hung clear can never wedge the
            // gear/privacy actions for the rest of the session. The clear runs on the SOURCE core,
            // which we keep alive, so its completion handler always fires (a closed WebView would
            // release it un-invoked). Time it so the bound can be retuned from real durations.
            stopwatch = Stopwatch.StartNew();
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(foregroundWait, timeoutCancellation.Token);
            var completedTask = await Task.WhenAny(clearTask, timeoutTask);
            if (BrowserDataClearCoordinator.DidForegroundWaitExpire(clearTask, completedTask))
            {
                // The status wait elapsed, not the clear. The coordinator keeps the underlying
                // task, the playback gates stay closed through BrowserDataClearActive, and one
                // observer brings its terminal state back to this window.
                Log.Warn("Clear browser data exceeded the timeout; it may still complete in the background.");
                _ = ObserveTimedOutBrowserDataClearAsync(clearTask, stopwatch, generation);
                notify(PrivacyService.ClearResultTitle, PrivacyService.ClearTimedOut);
                return;
            }

            timeoutCancellation.Cancel();
            await clearTask;   // propagate the operation's own terminal exception, including TimeoutException
            stopwatch.Stop();

            ReflectClearedBrowserData();
            Log.Info($"Browser data cleared in {stopwatch.ElapsedMilliseconds} ms (user signed out).");
            notify(PrivacyService.ClearDoneTitle, PrivacyService.ClearDoneBody);
        }
        catch (Exception ex)
        {
            Log.Error("Clear browser data failed.", ex);
            notify(PrivacyService.ClearResultTitle, PrivacyService.ClearFailed);
        }
        finally
        {
            _privacyActionInProgress = false;
            _clearingBrowserData = false;
            SettingsButton.IsEnabled = true;
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();
            ApplyRetainedIncomingLink();
        }
    }

    /// <summary>Reflect the signed-out state; a nav hiccup must not mask a successful clear.</summary>
    private void ReflectClearedBrowserData()
    {
        try { NavigateInternal("https://www.youtube.com/"); }
        catch (Exception navEx) { Log.Error("Post-clear navigation failed.", navEx); }
    }

    private async Task ObserveTimedOutBrowserDataClearAsync(Task clearTask, Stopwatch? stopwatch, int generation)
    {
        bool succeeded;
        try
        {
            await clearTask.ConfigureAwait(false);
            stopwatch?.Stop();
            succeeded = true;
            Log.Info($"Timed-out browser data clear completed in the background after " +
                     $"{stopwatch?.ElapsedMilliseconds ?? 0} ms (user signed out).");
        }
        catch (Exception ex)
        {
            stopwatch?.Stop();
            succeeded = false;
            Log.Error($"Timed-out browser data clear failed after " +
                      $"{stopwatch?.ElapsedMilliseconds ?? 0} ms.", ex);
        }

        // Terminal state belongs on the dispatcher (PP-06): the playback gates reopen there, and
        // Settings may be open in its nested modal loop, which still pumps this.
        try { _ = Dispatcher.BeginInvoke(new Action(() => OnLateBrowserDataClearCompleted(succeeded, generation))); }
        catch { /* app shutdown owns the remaining lifetime */ }
    }

    /// <summary>
    /// A clear that outlived its foreground wait has ended. The timeout prompt already told the
    /// user it would finish in the background, so this reopens the gates and, on success, shows
    /// the signed-out Source once; a failure is logged and leaves Clear retryable. Nothing runs
    /// against a closing window or a clear that is no longer the current one.
    /// </summary>
    private void OnLateBrowserDataClearCompleted(bool succeeded, int generation)
    {
        RefreshClearBrowserDataAvailability();
        if (_mainWindowClosing || generation != _browserDataClearGeneration) return;

        if (succeeded) ReflectClearedBrowserData();
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
        ApplyRetainedIncomingLink();
    }

    /// <summary>
    /// Every settings write goes through here. A refusal after a read failure (spec 12.6) is safe
    /// but was silent; the title-bar hint makes it visible once, without a modal in the middle of a
    /// return or close, and Reset app state in Settings is the way out.
    /// </summary>
    private void SaveSettings()
    {
        var result = _settingsService.Save(_settings);
        if (result != SettingsSaveResult.RefusedUnread || _settingsSaveRefusalShown) return;
        _settingsSaveRefusalShown = true;
        SettingsUnsavedHint.Visibility = Visibility.Visible;
        Log.Warn("Settings changes are not being saved this session; the title-bar hint is shown once.");
    }

    internal bool IsSettingsUnsavedHintVisibleForTests => SettingsUnsavedHint.Visibility == Visibility.Visible;
    internal void ReplaceSettingsServiceForTests(SettingsService service) => _settingsService = service;
    internal void SaveSettingsForTests() => SaveSettings();

    private void RefreshClearBrowserDataAvailability()
    {
        if (_settingsDialog is null) return;

        bool browserReady;
        try { browserReady = _browserReady && Browser.CoreWebView2 is not null; }
        catch { browserReady = false; }
        var unavailableHint = _browserDataClearCoordinator.IsRunning
            ? PrivacyService.ClearAlreadyRunningHint
            : null;
        _settingsDialog.SetClearBrowserDataAvailability(browserReady, unavailableHint);
    }

    /// <summary>Test-only: the navigation queued while the browser was not ready (null = none).</summary>
    internal string? PendingUrlForTests => _pendingUrl;

    // --- Video Popout lifecycle (spec 13) ---

    private async void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null) await BringVideoBackAsync();
        else await StartVideoPopoutAsync();
    }

    private void ShowPopoutButton_Click(object sender, RoutedEventArgs e) => ActivateExistingPlayer();

    private void PlaceholderShowPopoutButton_Click(object sender, RoutedEventArgs e) => ActivateExistingPlayer();

    private async void PlaceholderBringBackButton_Click(object sender, RoutedEventArgs e) =>
        await BringVideoBackAsync();

    private bool CanStartVideoPopout =>
        _browserReady && !_popoutInProgress && !_returnInProgress && _player is null &&
        !BrowserDataClearActive && !_mainWindowClosing;

    private async Task StartVideoPopoutAsync(YouTubeTarget? resolvedTarget = null)
    {
        // Guards (spec 13.4): browser ready, no popout in flight, single player (ADR-0005).
        if (!CanStartVideoPopout) return;

        _popoutInProgress = true;
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
        var core = Browser.CoreWebView2;
        PlayerState? launchState = null;

        try
        {
            // 1) Read source state; capture timestamp + was-playing + volume/mute/rate BEFORE
            // suppressing (REQ-RETURN-01). The settings are the return fallback if the popout never
            // reports live state, so suppression's mute is always undone on return (Q-1).
            launchState = await YouTubeDomBridge.ReadPlayerStateAsync(core);
            _sourceWasPlayingAtPopout = launchState is { Paused: false };
            _sourceVolumeAtPopout = launchState?.Volume;
            _sourceMutedAtPopout = launchState?.Muted;
            _sourcePlaybackRateAtPopout = launchState?.PlaybackRate;
            var seconds = launchState?.CurrentTime;

            // 2) Resolve the currently visible Source video first; canonical is only a fallback for
            // pages whose address does not expose a playable target.
            //
            // A caller-supplied identity (Auto) is carried, not re-resolved, so a stale canonical cannot
            // substitute another video. But Auto captured it BEFORE its own DOM read, and the Source can
            // have SPA-advanced during that await. Abandon rather than pop a video the Source has already
            // left: the next tick re-evaluates whatever is actually on screen.
            if (resolvedTarget is not null
                && !PopoutTargetResolver.CapturedTargetStillMatchesSource(resolvedTarget, core.Source))
            {
                Log.Info("Auto: the Source moved to another video while detecting playback; abandoning this launch.");
                return;
            }

            var target = resolvedTarget ?? await ResolvePopoutTargetAsync(core);
            if (target is null || !PopoutLaunchPolicy.IsLaunchableTarget(target))
            {
                Prompt.ShowInfo(this, "Pop out video", "Open a YouTube video or playlist first, then press Pop out video.");
                return;
            }

            // 2a) Spec 13.1 / 22.1: a playlist-only launch starts the page's first playable item.
            // YouTube has no videoless "start playlist" watch URL (playlist?list= is a static browse
            // page and playnext=1 is dead), so resolve the first item off the page the Source is
            // already showing. Best-effort (Q-3): when the read fails, the popout degrades to the
            // playlist page itself — spec 22.1's "may launch" covers that.
            if (target.IsPlaylistOnly && string.IsNullOrEmpty(target.VideoId))
            {
                var firstItemUrl = await YouTubeDomBridge.ReadFirstPlaylistItemUrlAsync(core);
                if (!PopoutTargetResolver.CapturedTargetStillMatchesSource(target, core.Source))
                {
                    Log.Info("Video Popout: the Source moved away from the captured playlist while resolving its first item; abandoning this launch.");
                    return;
                }

                target = PopoutTargetResolver.WithFirstPlaylistItem(target, firstItemUrl);
            }

            // A playlist page shows no video the popout took over: a first-item id resolved above is
            // the popout's launch plan, not the Source's video — never same-video-seek the Source
            // with it, and treat any video the popout returns as new context to navigate to.
            _popoutSourceVideoId = target.IsPlaylistOnly ? null : target.VideoId;
            _popoutLaunchedWithoutVideo = target.IsPlaylistOnly || string.IsNullOrEmpty(target.VideoId);

            // 2b) Resolve the effective playback mode (spec 10). Profile/global compact settings
            // remain reserved data, but ResolveEffectivePopoutMode honors the compact-player
            // kill-switch, so new popouts force Normal while compact is disabled.
            var mode = PlaybackModePolicy.ResolveEffectivePopoutMode(
                ResolveActiveProfileMode(target.VideoId), _settings.Player.CompactMode);
            var presentation = PopoutPresentationPolicy.ResolveEffectivePresentation(
                ResolveActiveProfilePresentation(target.VideoId), _settings.Player.FocusedPresentation);
            var launchSeconds = PopoutTargetResolver.ResolveLaunchSeconds(target, seconds);
            target.StartSeconds = launchSeconds;
            var popoutUrl = PlaybackModePolicy.BuildPopoutUrl(
                mode, target, launchSeconds, WebViewEnvironmentService.ShellPlayerUrl);

            // 3) Pause the source and show the placeholder (Q-1: no duplicate audio). A non-null
            // FallbackReason (mix/radio drop) rides along as the placeholder note (Q-6) — it was
            // previously log-only, invisible to the user.
            try
            {
                if (PopoutLaunchPolicy.RequiresAcknowledgedSuppression(target))
                {
                    await PopoutLaunchPolicy.RequireAcknowledgedSourceSuppressionAsync(
                        () => YouTubeDomBridge.SuppressPlaybackAsync(core));
                }
                else if (!await YouTubeDomBridge.SuppressPlaybackAsync(core))
                {
                    // Playlist page: no video element is guaranteed, so "no video found" is a
                    // legitimate outcome here, not a failed ownership transfer — a launch that owns
                    // no playback must not abort. A playing miniplayer, when present, was still
                    // found and suppressed by the same script above.
                    Log.Info("Playlist-only popout: no source playback to suppress.");
                }
            }
            catch
            {
                // Keep Auto from reopening this one failure every 250 ms, but do not claim the
                // video was successfully handled. A manual retry remains available; Source
                // navigation clears this failure latch for the next video.
                _autoSuppressionFailedVideoId = target.VideoId;
                throw;
            }

            // Auto de-dup is committed only after playback ownership really transferred.
            _autoSuppressionFailedVideoId = null;
            _autoLastHandledVideoId = target.VideoId;
            StartSourceSuppressionGuard();
            ShowSourcePlaceholder(true, target.FallbackReason);

            // 4) Create the single Popout Player on the shared environment, in the resolved mode.
            var env = App.Current.WebViewEnvironment.Environment
                      ?? await App.Current.WebViewEnvironment.EnsureCreatedAsync();

            // The target doubles as the compact error bar's fallback handle (spec 10.3 / Q-6,
            // Stage 4). Its StartSeconds was normalized before URL construction: ordinary videos
            // keep the live/fallback timestamp, while a playlist-only first item starts at zero.
            _player = new PlayerWindow(env, popoutUrl, _settings.Player.Topmost,
                _settings.Player.Placement, _settings.Player.LastWidth, _settings.Player.LastHeight,
                _settings.Player.FadeEnabled, ResolvedAccentColor,
                EffectiveFadeIdleDelayMs, mode, target,
                EffectiveActiveWindowOpacity, EffectiveIdleWindowOpacity,
                EffectiveStripAutoHide, EffectiveDwmCornerMode, EffectivePopoutCornerRadiusDip,
                nudgePlayOnInitialPause: _sourceWasPlayingAtPopout || _popoutLaunchedWithoutVideo,
                presentation: presentation);
            _player.PlayerClosed += Player_OnClosed;
            _player.SettingsRequested += Player_SettingsRequested;
            SuspendSourcePinForPopout();
            _player.Show();

            if (target.FallbackReason is not null) Log.Info($"Popout fallback: {target.FallbackReason}");
            Log.Info($"Video Popout started at t={launchSeconds?.ToString() ?? "0"}s, " +
                      $"wasPlaying={_sourceWasPlayingAtPopout}, mode={mode}, presentation={presentation}.");
        }
        catch (Exception ex)
        {
            // Failure after pause (spec 13.5): restore the source and resume if it had been playing.
            Log.Error("Video Popout failed; restoring source.", ex);
            StopSourceSuppressionGuard();
            ShowSourcePlaceholder(false);
            if (_player is not null)
            {
                var failedPlayer = _player;
                _player = null;
                failedPlayer.PlayerClosed -= Player_OnClosed;
                failedPlayer.SettingsRequested -= Player_SettingsRequested;
                try { failedPlayer.Close(); } catch { /* ignore */ }
            }
            RestoreSourcePinAfterPopout();
            RestoreSourceAfterReturn();
            if (core is not null)
                await YouTubeDomBridge.ApplyPlaybackSettingsAsync(
                    core, launchState?.Volume, launchState?.Muted, launchState?.PlaybackRate);
            if (_sourceWasPlayingAtPopout && core is not null) await YouTubeDomBridge.PlayAsync(core);
            Prompt.ShowInfo(this, "Pop out video", "PiPlay couldn't pop out this video. It stayed in the main window.");
        }
        finally
        {
            _popoutInProgress = false;
            UpdatePopoutActionState();   // covers both outcomes: player created or rolled back
            UpdateSourceCommandAvailability();
            ApplyRetainedIncomingLink();
        }
    }

    /// <summary>
    /// Show/focus the existing popout (ADR-0005 activate-existing rule). RestoreWindow first:
    /// Activate() alone does not un-minimize, and restoring (not forcing Normal) keeps a
    /// maximized popout maximized.
    /// </summary>
    private void ActivateExistingPlayer()
    {
        if (_player is null) return;
        if (_player.WindowState == WindowState.Minimized)
            System.Windows.SystemCommands.RestoreWindow(_player);
        _player.Activate();

        // Activate can be denied by Windows foreground rules (a hand-off from another process
        // is the usual case). A brief topmost pulse raises the Popout without changing its Pin.
        var pinned = _player.Topmost;
        _player.Topmost = true;
        _player.Topmost = pinned;
    }

    private async Task BringVideoBackAsync()
    {
        if (_player is null || _popoutInProgress) return;

        _popoutInProgress = true;
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
        try
        {
            var player = _player;
            await player.CaptureReturnStateNowAsync();
            player.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Bring video back failed; focusing the existing popout instead.", ex);
            ActivateExistingPlayer();
        }
        finally
        {
            _popoutInProgress = false;
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();
            // A link retained while the return was starting: when the close completed synchronously
            // the return transition already ran with _popoutInProgress set and re-retained it, and
            // nothing else fires afterwards. Idempotent when the transition applied it (PP-01).
            ApplyRetainedIncomingLink();
        }
    }

    /// <summary>
    /// Reflect the single-player lifecycle on the primary action (Q-6): while a popout is open the
    /// button returns playback instead of implying a second popout will open. Label, tooltip, and
    /// UIA name flip together so the accessible name never lies (REQ-UI-02).
    /// </summary>
    private enum PopoutActionState
    {
        Ready,
        Open,
        Returning,
        /// <summary>A browser-data clear is still running (PP-06): nothing may start playback.</summary>
        Clearing,
    }

    private void UpdatePopoutActionState()
    {
        var state = _returnInProgress
            ? PopoutActionState.Returning
            : _player is not null
                ? PopoutActionState.Open
                : BrowserDataClearActive
                    ? PopoutActionState.Clearing
                    : PopoutActionState.Ready;
        ApplyPopoutActionState(state);
    }

    internal void ApplyPopoutActionState(bool hasPlayer)
        => ApplyPopoutActionState(hasPlayer ? PopoutActionState.Open : PopoutActionState.Ready);

    private void ApplyPopoutActionState(PopoutActionState state)
    {
        var label = state switch
        {
            PopoutActionState.Open => "Bring video back",
            PopoutActionState.Returning => "Returning video...",
            PopoutActionState.Clearing => "Clearing browser data...",
            _ => "Pop out video",
        };
        PopOutButtonIcon.Text = state is PopoutActionState.Ready or PopoutActionState.Clearing ? GlyphPopOut : GlyphBringBack;
        PopOutButtonText.Text = label;
        System.Windows.Automation.AutomationProperties.SetName(PopOutButton, label);
        PopOutButton.ToolTip = state switch
        {
            PopoutActionState.Open => "Return playback to the Source Window",
            PopoutActionState.Returning => "Returning playback to the Source Window",
            PopoutActionState.Clearing => "Pop out video is available after the browser-data clear finishes",
            _ => "Pop out the current video",
        };
        PopOutButton.IsEnabled = state switch
        {
            PopoutActionState.Open => !_popoutInProgress && !BrowserDataClearActive && !_mainWindowClosing,
            PopoutActionState.Returning => false,
            PopoutActionState.Clearing => false,
            _ => CanStartVideoPopout,
        };

        var playerCanActivate = state == PopoutActionState.Open && !_popoutInProgress &&
                                !BrowserDataClearActive && _player is not null;
        ShowPopoutButton.Visibility = state == PopoutActionState.Open
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShowPopoutButton.IsEnabled = playerCanActivate;
        PlaceholderShowPopoutButton.IsEnabled = playerCanActivate;
        PlaceholderBringBackButton.IsEnabled = playerCanActivate;
    }

    /// <summary>
    /// The selected profile's playback-mode override for this popout. REQ-PROFILE-01 governs the
    /// per-field precedence (a profile field overrides the global default); the pure
    /// <see cref="PlaybackModePolicy.ResolveProfileOverride"/> additionally scopes the override to
    /// the profile's own video, so a stale combo selection plus manual navigation can't apply a
    /// profile's compact preference to an unrelated video. Null when no profile is selected, the
    /// profile carries no mode, or the popout target is not that profile's video.
    /// </summary>
    private string? ResolveActiveProfileMode(string? targetVideoId)
    {
        if (ProfilesCombo.SelectedItem is not Profile profile) return null;
        var profileVideoId = YouTubeUrlHelper.TryParse(profile.Url, out var profileTarget)
            ? profileTarget.VideoId
            : null;
        return PlaybackModePolicy.ResolveProfileOverride(profile.Mode, profileVideoId, targetVideoId);
    }

    /// <summary>Target-scoped profile override for the independent Popout presentation field.</summary>
    private string? ResolveActiveProfilePresentation(string? targetVideoId)
    {
        if (ProfilesCombo.SelectedItem is not Profile profile) return null;
        var profileVideoId = YouTubeUrlHelper.TryParse(profile.Url, out var profileTarget)
            ? profileTarget.VideoId
            : null;
        return PopoutPresentationPolicy.ResolveProfileOverride(
            profile.Presentation, profileVideoId, targetVideoId);
    }

    private static async Task<YouTubeTarget?> ResolvePopoutTargetAsync(CoreWebView2 core)
    {
        var canonical = await YouTubeDomBridge.ReadCanonicalUrlAsync(core);
        return PopoutTargetResolver.Resolve(core.Source, canonical);
    }

    internal void ShowSourcePlaceholder(bool visible, string? note = null)
    {
        // Tier-1 placeholder (spec 13.3): hide the source WebView, show the accent-derived
        // near-black letterbox panel.
        Browser.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
        SourcePlaceholder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _sourceNavigationSuspended = visible;
        UpdateSourceCommandAvailability();

        // Optional non-blocking note (Q-6), e.g. the mix/radio fallback reason. Cleared with the
        // placeholder so a stale note can't survive into the next popout.
        PlaceholderNoteText.Text = note ?? string.Empty;
        PlaceholderNoteText.Visibility =
            visible && !string.IsNullOrEmpty(note) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginReturnTransition()
    {
        _returnInProgress = true;
        StartReturnDeadline();
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
    }

    private void CompleteReturnTransition()
    {
        _returnDeadlineTimer?.Stop();
        _returnInProgress = false;
        UpdatePopoutActionState();
        UpdateSourceCommandAvailability();
        ApplyRetainedIncomingLink();
    }

    private void RestoreSourceAfterReturn()
    {
        if (_mainWindowClosing) return;

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(this);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }

        Activate();

        // Activate can be denied by Windows foreground rules. A brief topmost pulse raises the
        // Source without changing its persisted Pin preference or its final z-order contract.
        var pinned = Topmost;
        Topmost = true;
        Topmost = pinned;
    }

    internal bool ReturnInProgressForTests => _returnInProgress;
    internal bool CanStartVideoPopoutForTests => CanStartVideoPopout;
    internal void BeginReturnForTests() => BeginReturnTransition();
    internal void CompleteReturnForTests() => CompleteReturnTransition();
    internal void RestoreSourceAfterReturnForTests() => RestoreSourceAfterReturn();

    internal void SetBrowserReadyForTests(bool ready)
    {
        _browserReady = ready;
        UpdatePopoutActionState();
    }

    internal bool BrowserReadyForTests => _browserReady;
    internal bool BrowserRecoveryInProgressForTests => _browserRecoveryInProgress;
    internal bool BrowserFailedForTests => _browserFailed;
    internal int ConsecutiveBrowserRecoveriesForTests => _consecutiveBrowserRecoveries;
    internal void SetBrowserRecoveryInProgressForTests(bool inProgress) => _browserRecoveryInProgress = inProgress;
    internal WebView2 BrowserForTests => Browser;
    internal void ReplaceBrowserControlForTests() => ReplaceBrowserControl();
    internal Task RecreateSourceBrowserForTestsAsync() => RecreateSourceBrowserAsync("test");
    internal void SetPendingReturnReplayForTests(PlayerReturnState? state) => _pendingReturnReplay = state;
    internal bool IsReturnDeadlineArmedForTests => _returnDeadlineTimer?.IsEnabled == true;
    internal void ElapseReturnDeadlineForTests() => OnReturnDeadlineElapsed();
    internal string? LastKnownSourceUrlForTests { get => _lastKnownSourceUrl; set => _lastKnownSourceUrl = value; }
    internal bool IsRuntimeErrorPanelVisibleForTests => RuntimeErrorPanel.Visibility == Visibility.Visible;
    internal string RuntimeErrorHeadingForTests => RuntimeErrorHeading.Text;
    internal bool IsRuntimeRetryEnabledForTests => RuntimeRetryButton.IsEnabled;
    internal void ReleaseBrowserStateAfterNavigationForTests() => ReleaseBrowserStateAfterNavigation();
    internal void ShowBrowserStateForTests(string heading, string message, bool retryEnabled) =>
        ShowBrowserState(heading, message, retryEnabled);

    private void StartSourceSuppressionGuard()
    {
        _sourceSuppressionTimer ??= CreateSourceSuppressionTimer();
        if (!_sourceSuppressionTimer.IsEnabled) _sourceSuppressionTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateSourceSuppressionTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += SourceSuppressionTimer_Tick;
        return timer;
    }

    private void StopSourceSuppressionGuard()
    {
        _sourceSuppressionTimer?.Stop();
        _sourceSuppressionTickInProgress = false;
    }

    private async void SourceSuppressionTimer_Tick(object? sender, EventArgs e)
    {
        if (_sourceSuppressionTickInProgress) return;
        if (_player is null || BrowserDataClearActive || _mainWindowClosing) return;
        var core = Browser.CoreWebView2;
        if (core is null) return;

        _sourceSuppressionTickInProgress = true;
        try
        {
            await YouTubeDomBridge.SuppressPlaybackAsync(core);
        }
        finally
        {
            _sourceSuppressionTickInProgress = false;
        }
    }

    private async void Player_OnClosed(object? sender, PlayerReturnState state)
    {
        try
        {
            _player = null;

            // Persist Popout Player window state.
            _settings.Player.Topmost = state.Topmost;
            _settings.Player.FadeEnabled = state.FadeEnabled;
            if (state.Placement is not null)
            {
                _settings.Player.Placement = state.Placement;
                if (state.Placement.Width >= 320) _settings.Player.LastWidth = state.Placement.Width;
                if (state.Placement.Height >= 180) _settings.Player.LastHeight = state.Placement.Height;
            }

            // Placement/settings must survive even if source scripting fails.
            SaveSettings();

            // The popout no longer owns playback, so stop re-asserting the source suppression —
            // including on shutdown, which returns below.
            StopSourceSuppressionGuard();

            if (_mainWindowClosing)
            {
                Log.Info("Captured Video Popout state during app shutdown.");
                return;
            }

            // Both windows share one browser process (PP-02). When the Popout saw it die first,
            // the Source's own notification may still be queued: start the recreate now so the
            // return below is queued for the new browser instead of scripting the dead core.
            if (state.BrowserProcessFailed) HandleSourceProcessFailure(WebViewFailureKind.BrowserProcessExited);

            // Return to the source (spec 14). LastKnownSeconds is nullable; 0 is a valid timestamp.
            // ApplyReturnActionAsync self-skips while Clear browser data is wiping the session.
            BeginReturnTransition();
            ShowSourcePlaceholder(false);
            RestoreSourcePinAfterPopout();
            RestoreSourceAfterReturn();
            await ApplyReturnActionAsync(state);

            if (_pendingReturnReplay is null) CompleteReturnTransition();

            SaveSettings();
            Log.Info(_mainWindowClosing ? "Popout Player closed during app shutdown." : "Returned from Video Popout.");
        }
        catch (Exception ex)
        {
            StopSourceSuppressionGuard();
            _pendingReturnReplay = null;
            if (!_mainWindowClosing)
            {
                ShowSourcePlaceholder(false);
                RestoreSourcePinAfterPopout();
                RestoreSourceAfterReturn();
                CompleteReturnTransition();
            }
            Log.Error("Error returning from Video Popout.", ex);
        }
    }

    /// <summary>
    /// The return decision of <see cref="Player_OnClosed"/> (REQ-RETURN-01), separate from the
    /// persistence half so the WPF lane can drive it without writing real settings. Skipped
    /// entirely while Clear browser data is wiping the session — the page is about to be
    /// cleared/navigated, so driving it would be wasted or race. Navigate needs no live core
    /// (<see cref="NavigateInternal"/> queues until the browser is ready); the script-driven
    /// cases do, and fall through silently without one.
    /// </summary>
    internal async Task ApplyReturnActionAsync(PlayerReturnState state)
    {
        if (BrowserDataClearActive) return;
        if (_browserRecoveryInProgress || _browserFailed)
        {
            // The browser is restarting or failed (PP-02): its core is dead even when non-null.
            // Keep the returned video as the page the recreated browser opens; never script it.
            if (!string.IsNullOrEmpty(state.VideoId))
            {
                var target = new YouTubeTarget
                {
                    VideoId = state.VideoId,
                    PlaylistId = state.PlaylistId,
                    StartSeconds = state.LastKnownSeconds,
                };
                _autoLastHandledVideoId = state.VideoId;
                NavigateInternal(YouTubeUrlHelper.BuildWatchUrl(target));
                Log.Info("Return arrived while the Source browser was unavailable; the video is queued for the restarted browser.");
            }
            return;
        }

        var core = Browser.CoreWebView2;

        // REQ-RETURN-01/P4: resume from the popout's current paused state when known; otherwise
        // fall back to the older source-was-playing snapshot. 0 is a valid timestamp distinct from
        // unknown. Decision lives in ReturnPolicy.
        var action = ReturnPolicy.Decide(state.LastKnownSeconds, _sourceWasPlayingAtPopout,
            state.Paused, state.VideoId, _popoutSourceVideoId, _popoutLaunchedWithoutVideo);

        // PP-01: the launch identity is only trustworthy if the Source is still on that page. The
        // incoming-link route keeps it there, but a same-video seek against another page would be
        // the worst outcome, so the live page has the last word.
        action = ReturnPolicy.ReconcileWithLiveSource(
            action, state.VideoId, _popoutSourceVideoId, LiveSourceVideoId(core));

        // Arm before any WebView script await: the Auto timer can run as soon as Player_OnClosed
        // clears _player. A seek/play return leaves the original Source video visible; a Navigate
        // return exposes the player's final video. Either way, return-resume is not a new Auto edge.
        var returnedSourceVideoId = action == ReturnAction.Navigate
            ? state.VideoId
            : _popoutSourceVideoId ?? state.VideoId;
        if (!string.IsNullOrEmpty(returnedSourceVideoId))
            _autoLastHandledVideoId = returnedSourceVideoId;

        if (action != ReturnAction.Navigate && core is not null)
        {
            // Popout live value wins; else the pre-suppression launch value; else forced un-mute so
            // the source can never return silent after launch-time mute suppression (Q-1).
            var (volume, muted, rate) = ReturnPolicy.ResolveReturnSettings(
                state.Volume, state.Muted, state.PlaybackRate,
                _sourceVolumeAtPopout, _sourceMutedAtPopout, _sourcePlaybackRateAtPopout);
            await YouTubeDomBridge.ApplyPlaybackSettingsAsync(core, volume, muted, rate);
            if (BrowserDataClearActive || _mainWindowClosing) return;
        }

        switch (action)
        {
            case ReturnAction.Navigate:
                // The popout ended on a DIFFERENT video (recommendation click, playlist
                // auto-advance, SPA navigation): bring the source to where the user
                // actually is. The timestamp rides the watch URL; Auto's de-dup key
                // updates FIRST so the returned video is not instantly re-popped.
                _pendingReturnReplay = CloneForReturnReplay(
                    state, _sourceWasPlayingAtPopout,
                    _sourceVolumeAtPopout, _sourceMutedAtPopout, _sourcePlaybackRateAtPopout);
                NavigateInternal(YouTubeUrlHelper.BuildWatchUrl(
                    new YouTubeTarget { VideoId = state.VideoId, PlaylistId = state.PlaylistId },
                    state.LastKnownSeconds));
                break;
            case ReturnAction.SeekAndPlay when core is not null:
                await YouTubeDomBridge.SeekAndPlayAsync(core, state.LastKnownSeconds!.Value);
                break;
            case ReturnAction.Seek when core is not null:
                await YouTubeDomBridge.SeekAndPauseAsync(core, state.LastKnownSeconds!.Value);
                break;
            case ReturnAction.Play when core is not null:
                await YouTubeDomBridge.PlayAsync(core);
                break;
        }
    }

    private static string? LiveSourceVideoId(CoreWebView2? core)
    {
        string? source;
        try { source = core?.Source; }
        catch { return null; }
        return YouTubeUrlHelper.TryParse(source, out var live) ? live.VideoId : null;
    }

    // Return seams (overhaul Task 3, WPF lane): drive the navigate-vs-seek return decision
    // headlessly — the queued pending URL is the observable for the Navigate case.
    internal void SeedPopoutReturnForTests(
        string? sourceVideoId,
        bool sourceWasPlayingAtPopout = false,
        double? sourceVolumeAtPopout = null,
        bool? sourceMutedAtPopout = null,
        double? sourcePlaybackRateAtPopout = null,
        bool popoutLaunchedWithoutVideo = false)
    {
        _popoutSourceVideoId = sourceVideoId;
        _popoutLaunchedWithoutVideo = popoutLaunchedWithoutVideo;
        _sourceWasPlayingAtPopout = sourceWasPlayingAtPopout;
        _sourceVolumeAtPopout = sourceVolumeAtPopout;
        _sourceMutedAtPopout = sourceMutedAtPopout;
        _sourcePlaybackRateAtPopout = sourcePlaybackRateAtPopout;
    }

    internal string? AutoLastHandledVideoIdForTests => _autoLastHandledVideoId;
    internal PlayerReturnState? PendingReturnReplayForTests => _pendingReturnReplay;

    private static PlayerReturnState CloneForReturnReplay(
        PlayerReturnState state, bool sourceWasPlayingAtPopout,
        double? sourceVolumeAtPopout, bool? sourceMutedAtPopout, double? sourcePlaybackRateAtPopout)
    {
        var (volume, muted, rate) = ReturnPolicy.ResolveReturnSettings(
            state.Volume, state.Muted, state.PlaybackRate,
            sourceVolumeAtPopout, sourceMutedAtPopout, sourcePlaybackRateAtPopout);

        return new()
        {
            VideoId = state.VideoId,
            PlaylistId = state.PlaylistId,
            LastKnownSeconds = state.LastKnownSeconds,
            Paused = state.Paused ?? !sourceWasPlayingAtPopout,
            Volume = volume,
            Muted = muted,
            PlaybackRate = rate,
        };
    }

    // --- Single-instance activation (REQ-APP-01) ---

    /// <summary>
    /// A second launch reached the running instance. The link (if any) goes to the playback owner
    /// first, and the surface that took it comes forward: the Popout when it retargeted, otherwise
    /// the Source. A closing Source rejects the request so the sender can say so (PP-05).
    /// </summary>
    public IncomingLinkDecision ActivateFromSecondInstance(string? url)
    {
        var decision = AcceptIncomingLink(url);
        if (_mainWindowClosing) return decision;
        if (decision.Action == IncomingLinkAction.RetargetPopout && _player is not null) return decision;

        // Un-minimize to the window's prior state (Normal or Maximized) rather than forcing Normal,
        // which would silently drop a maximized layout the user left (REQ-WINDOW-01 / spec 16.4).
        // RestoreWindow remembers the pre-minimize state; the guard keeps it from un-maximizing.
        if (WindowState == WindowState.Minimized) System.Windows.SystemCommands.RestoreWindow(this);
        Show();
        Activate();

        // Briefly assert topmost to pull to the foreground, then restore the real Pin state.
        var pinned = Topmost;
        Topmost = true;
        Topmost = pinned;
        return decision;
    }

    // --- Window chrome buttons ---

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _mainWindowClosing = true;
            UpdatePopoutActionState();
            UpdateSourceCommandAvailability();
            _autoTimer?.Stop();
            _returnDeadlineTimer?.Stop();
            // Close is bounded (PP-06): a clear still running is not waited for and its late
            // completion is dropped by the closing check; the profile may be partially cleared.
            if (_browserDataClearCoordinator.IsRunning)
                Log.Warn("Closing while a browser-data clear is still running; it is not waited for.");
            CancelQueuedAccentPreview();
            StopSourceSuppressionGuard();
            // Close the Popout Player too (its handler captures/persists player state).
            if (_player is not null) { try { _player.Close(); } catch { /* ignore */ } }

            var placement = WindowPlacementService.TryCapture(this);
            if (placement is not null) _settings.MainWindow.Placement = placement;
            if (Browser.CoreWebView2 is not null) _settings.LastUrl = Browser.CoreWebView2.Source;
            // While a popout owns the foreground, Source Topmost is temporarily false. Preserve the
            // user's saved Source Pin preference instead of persisting that transition detail.
            CaptureSourceTopmostPreferenceForClose();
            SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Error saving settings on close.", ex);
        }
        finally
        {
            // WebView2 is IDisposable; explicit teardown makes the Source controller/renderer
            // ownership deterministic instead of waiting for process exit or finalization.
            try { Browser.Dispose(); } catch { /* best effort during window teardown */ }
        }
    }
}
