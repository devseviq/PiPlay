using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay;

/// <summary>
/// PlayerWindow / Popout Player (spec 12.2): a borderless media window hosting the
/// popped-out YouTube playback on the shared WebView2 environment. Provides native-quality
/// move/resize, a Pin toggle, polls the last-known timestamp, and reports return state to
/// the Source Window when it closes.
/// </summary>
public partial class PlayerWindow : Window
{
    // Segoe MDL2 caption glyphs (kept as escapes so the source stays plain ASCII).
    private const string GlyphMaximize = "";
    private const string GlyphRestore = "";

    private readonly CoreWebView2Environment _environment;
    private readonly PlacementData? _placement;
    private readonly DispatcherTimer _syncTimer;
    private readonly PlayerReturnState _returnState = new();

    // Expand/restore (overhaul Task 4): true only while a compact fullscreen ELEMENT (the shell's
    // YouTube fullscreen button) caused the expansion, so exiting element fullscreen restores the
    // window without un-expanding one the user expanded deliberately.
    private bool _maximizedForFullScreenElement;

    // Mutable: the compact fallback (spec 10.3 / Q-6) flips the window to normal-mode behavior in
    // place, so the mode-dependent seams (timestamp source, minimum size) follow the live surface.
    private PlaybackMode _mode;

    // Controls fade (spec 7.1): idle/hover state machine over the chrome strip only.
    private readonly DispatcherTimer _idleTimer;
    private bool _fadeEnabled;
    private bool _isDragging;
    private bool _controlsVisible = true;
    private string _accentColor = ThemeCatalog.DefaultAccentColor;

    // Strip auto-hide (spec 7.2 chrome fade, Phase 4): when on, an idle-hidden strip also
    // height-collapses so the video fills the window; the top-edge hover band (via the activity
    // probe — WPF sees no mouse over the WebView2 child) reveals it. Same idle source as the
    // fade, so fade-off means no auto-hide either.
    private bool _stripAutoHide;

    // Whole-window opacity (spec 7.3, Phase 4): two persisted levels applied as layered alpha by
    // WindowOpacityApplier. Idle ONSET shares _idleTimer with the controls fade (one idleness
    // definition); the hover-restore poll exists because WPF receives no mouse events over the
    // WebView2 child HWND (Stage 0 spike finding), so restoring on movement over the video needs
    // a cursor probe. The poll runs while anything needs it (idle dip configured, or strip
    // auto-hide armed) — see UpdateActivityProbe; off at defaults.
    private double _constantWindowOpacity;
    private double _idleWindowOpacity;
    private bool _windowOpacityIdle;
    private DwmCornerMode _dwmCornerMode;            // theme/override native corner shape (docs/Theme_Preset_Differences.md)
    private double _popoutCornerRadiusDip;           // large Round silhouette; resolved ThemeRadii.PopoutFrame
    private bool _customWindowRegionApplied;
    private int _probeCursorX, _probeCursorY;        // last cursor position the activity probe saw
    private long _lastInWindowCursorMoveMs = -1;     // Environment.TickCount64 of the last in-window move
    private readonly DispatcherTimer _opacityHoverPoll;

    private bool _navCompleted;
    private int _navigationGeneration;
    // PP-04: bumped whenever the RETURN identity changes (retarget, SPA advance) so a playback
    // sample read for one video can never be paired with the next one's id. Navigation
    // generations alone miss SPA identity changes, which raise no NavigationStarting.
    private int _returnIdentityGeneration;
    private int _playerInitializationGeneration;
    // PP-02: one page reload per renderer exit, bounded; a browser-process exit closes the Popout.
    private bool _processRecoveryInProgress;
    private int _consecutiveProcessRecoveries;
    private DateTimeOffset? _lastProcessFailureUtc;
    private ulong? _activeNavigationId;
    private bool _syncTickInProgress;
    private bool _closing;
    private bool _capturedReturn;
    private bool _nudgedPlay;
    private bool _nudgePlayOnInitialPause;
    private bool _finalReturnPlaybackCaptured;

    // Compact (shell) mode only: the host side of the IFrame-API bridge (spec 10.3), the one-shot
    // "the IFrame API never came up" watchdog, and the one-way fallback latch.
    private PlayerShellBridge? _shellBridge;
    private PlayerSurfaceDragBridge? _surfaceDragBridge;
    private PlayerFirstSurfaceBridge? _playerFirstSurfaceBridge;
    private bool _surfaceDragQueued;
    private bool _surfaceDragAvailable;
    private bool _focusedOverlayReady;
    private bool _focusedSurfaceActive;
    private readonly DispatcherTimer _shellReadyTimer;
    private bool _fellBack;

    private readonly PopoutPresentation _presentation;

    // Mutable navigation state (overhaul Task 3): an in-place retarget (recommendation click via
    // NewWindowRequested) moves the player off its launch video, so the current URL and the target
    // the error bar's fallback reopens (Stage 4 / Q-6) must follow the LIVE video, not the launch one.
    private string _currentUrl;
    private YouTubeTarget? _currentTarget;

    /// <summary>Raised once when the player has closed, carrying the state needed to return (spec 14).</summary>
    public event EventHandler<PlayerReturnState>? PlayerClosed;

    /// <summary>
    /// Requests the Source-owned Settings workflow. The Popout exposes the affordance but deliberately
    /// owns no global settings or persistence policy.
    /// </summary>
    internal event EventHandler? SettingsRequested;

    internal TimeSpan FadeIdleDelayForTests => _idleTimer.Interval;

    public PlayerWindow(
        CoreWebView2Environment environment,
        string url,
        bool topmost,
        PlacementData? placement,
        int defaultWidth,
        int defaultHeight,
        bool fadeEnabled,
        string? accentColor = ThemeCatalog.DefaultAccentColor,
        int fadeIdleDelayMs = PlayerAppearancePolicy.DefaultFadeIdleDelayMs,
        PlaybackMode mode = PlaybackMode.Normal,
        YouTubeTarget? fallbackTarget = null,
        double constantWindowOpacity = WindowOpacityPolicy.Default,
        double idleWindowOpacity = WindowOpacityPolicy.Default,
        bool stripAutoHide = false,
        DwmCornerMode dwmCornerMode = DwmCornerMode.Default,
        double popoutCornerRadiusDip = 0,
        bool nudgePlayOnInitialPause = true,
        PopoutPresentation presentation = PopoutPresentation.Standard)
    {
        InitializeComponent();
        BorderlessWindowHelper.EnableExpandedResizeZones(this, HandleNativeMoveSizeStateChanged);

        _environment = environment;
        _currentUrl = url;
        _mode = mode;
        _presentation = presentation;
        _nudgePlayOnInitialPause = nudgePlayOnInitialPause;
        _currentTarget = fallbackTarget;
        _returnState.VideoId = fallbackTarget?.VideoId;   // the launch video until navigation says otherwise
        _returnState.PlaylistId = fallbackTarget?.PlaylistId;

        // Mode-specific minimum (spec 10.2 / 16.1): compact embed mode needs a larger floor than the
        // 320x180 normal minimum so the embedded player controls stay usable. MinWidth/MinHeight set
        // the floor and clamp the launch size up (the Math.Max below). A saved sub-minimum placement
        // is raised to the same floor via PlacementMath.EnsureMinSize (the saved bounds are physical
        // pixels; EnsureMinSize converts the DIP minimum with the saved DPI scale), so a window
        // restored across modes never opens below the mode minimum.
        var minWidth = PlaybackModePolicy.MinWidthFor(mode);
        var minHeight = PlaybackModePolicy.MinHeightFor(mode);
        MinWidth = minWidth;
        MinHeight = minHeight;
        // ForNextLaunch on the way IN as well as on capture (Task 4): a popout never LAUNCHES
        // expanded, including from pre-fix settings files that saved Maximized.
        _placement = placement is null
            ? null
            : PlacementMath.EnsureMinSize(PlacementMath.ForNextLaunch(placement)!, minWidth, minHeight);

        Width = Math.Max(MinWidth, defaultWidth);
        Height = Math.Max(MinHeight, defaultHeight);
        if (placement is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Topmost = topmost;
        PinToggle.IsChecked = topmost;
        UpdatePinAffordance(topmost);

        _fadeEnabled = fadeEnabled;
        FadeToggle.IsChecked = fadeEnabled;
        _returnState.FadeEnabled = fadeEnabled;

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncTimer.Tick += SyncTimer_Tick;

        // Compact only: if the shell never sends ANY bridge message (ready/state/error) within the
        // policy window, the IFrame API is dead (blocked script, offline) — surface the error bar
        // (spec 10.3 / Q-6). Any inbound message stops it.
        _shellReadyTimer = new DispatcherTimer { Interval = PlayerShellErrorPolicy.ReadyTimeout };
        _shellReadyTimer.Tick += ShellReadyTimer_Tick;

        _constantWindowOpacity = WindowOpacityPolicy.Normalize(constantWindowOpacity);
        _idleWindowOpacity = WindowOpacityPolicy.Normalize(idleWindowOpacity);
        _dwmCornerMode = dwmCornerMode;
        _popoutCornerRadiusDip = NormalizeCornerRadius(popoutCornerRadiusDip);
        _opacityHoverPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _opacityHoverPoll.Tick += OpacityHoverPoll_Tick;

        // Idle timer drives the fade-out; any mouse move restarts it (spec 7.1–7.2).
        // Both timers exist before ApplyAppearance, which touches the interval AND the probe.
        _idleTimer = new DispatcherTimer();
        _idleTimer.Tick += IdleTimer_Tick;
        ApplyAppearance(accentColor, fadeIdleDelayMs, stripAutoHide);
        MouseMove += (_, _) => OnUserActivity();
        MouseEnter += (_, _) => OnUserActivity();

        Loaded += (_, _) => ApplyFadeState();
        Loaded += async (_, _) => await InitializePlayerAsync();
        // OS-driven state changes (Win+Up/Down, aero snap) must keep the affordance honest too —
        // the direct calls in ToggleExpandedState cover the test lane, where unshown windows
        // never receive StateChanged.
        StateChanged += (_, _) =>
        {
            HandleWindowStateChanged();
            ApplyFocusedResizeInset();
            ApplyCornerModeToHwnd(forceRegionRefresh: true, applyDwmAttributes: false);
        };
        SizeChanged += (_, _) => ApplyCornerModeToHwnd(
            forceRegionRefresh: true, applyDwmAttributes: false);
        // Moving only matters when the Popout crosses into/out of a snap-like work-area corner.
        // Do not rebuild the HRGN (or rewrite DWM attributes) on every drag message.
        LocationChanged += (_, _) =>
        {
            // Native move/resize can emit many locations. Classify the settled snap geometry once
            // on WM_EXITSIZEMOVE; programmatic moves still classify immediately.
            if (!_isDragging) ApplyCornerModeToHwnd(applyDwmAttributes: false);
        };
        DpiChanged += (_, _) => ApplyCornerModeToHwnd(
            forceRegionRefresh: true, applyDwmAttributes: false);
        SourceInitialized += (_, _) =>
        {
            if (_placement is not null) WindowPlacementService.Restore(this, _placement);
            // Theme-driven native corners (docs/Theme_Preset_Differences.md): explicit theme/override data, no
            // longer an opacity side effect. Default mode leaves the window DWM-pristine.
            ApplyCornerModeToHwnd();
            ApplyWindowOpacityToHwnd(animate: false);   // appear at the configured level, no flash
        };
        Closing += PlayerWindow_Closing;
        Closed += PlayerWindow_Closed;
    }

    private int BeginPlayerInitialization() => ++_playerInitializationGeneration;

    private bool IsPlayerInitializationCurrent(int generation) =>
        !_closing && generation == _playerInitializationGeneration;

    private async Task InitializePlayerAsync()
    {
        var initializationGeneration = BeginPlayerInitialization();
        try
        {
            await Player.EnsureCoreWebView2Async(_environment);
            if (!IsPlayerInitializationCurrent(initializationGeneration)) return;

            var core = Player.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.SourceChanged += Core_SourceChanged;
            core.ProcessFailed += Core_ProcessFailed;
            // Secondary expand route (overhaul Task 4): the compact shell's YouTube fullscreen
            // button raises a fullscreen ELEMENT that today fills only the WebView bounds the
            // player already fills — honor it as window expansion. The handler gates on the LIVE
            // mode, so the normal watch page gains no new fullscreen invariant.
            core.ContainsFullScreenElementChanged += (_, _) =>
                ApplyFullScreenElementState(core.ContainsFullScreenElement);

            if (_mode == PlaybackMode.Compact)
            {
                // Compact mode hosts the local shell (spec 10.3): map its virtual host before
                // navigating, then let the shell bridge (IFrame API state) drive the return timestamp.
                WebViewEnvironmentService.MapShellVirtualHost(core);
                _shellBridge = new PlayerShellBridge(core);
                _shellBridge.Ready += ShellBridge_Ready;
                _shellBridge.StateReceived += ShellBridge_StateReceived;
                _shellBridge.ErrorReceived += ShellBridge_ErrorReceived;
                _shellBridge.RequestReceived += ShellBridge_RequestReceived;
            }

            // Register document-created scripts before Navigate. Each setup fails independently to
            // the native strip/ordinary watch page so a selector or WebView feature failure cannot
            // make the Popout unusable.
            try
            {
                var surfaceDragBridge = await PlayerSurfaceDragBridge.CreateAsync(
                    core, SystemParameters.MinimumHorizontalDragDistance,
                    SystemParameters.MinimumVerticalDragDistance);
                if (!IsPlayerInitializationCurrent(initializationGeneration))
                {
                    surfaceDragBridge.Dispose();
                    return;
                }

                surfaceDragBridge.DragRequested += SurfaceDragBridge_DragRequested;
                surfaceDragBridge.AvailabilityChanged += SurfaceDragBridge_AvailabilityChanged;
                _surfaceDragBridge = surfaceDragBridge;
            }
            catch (Exception ex)
            {
                if (!IsPlayerInitializationCurrent(initializationGeneration)) return;
                Log.Error("Passive player-surface dragging could not be initialized.", ex);
            }

            if (_presentation == PopoutPresentation.Focused)
            {
                try
                {
                    var playerFirstSurfaceBridge = await PlayerFirstSurfaceBridge.CreateAsync(
                        core, _accentColor, _fadeEnabled, (int)_idleTimer.Interval.TotalMilliseconds,
                        Topmost);
                    if (!IsPlayerInitializationCurrent(initializationGeneration))
                    {
                        playerFirstSurfaceBridge.Dispose();
                        return;
                    }

                    playerFirstSurfaceBridge.ActionRequested += PlayerFirstSurfaceBridge_ActionRequested;
                    playerFirstSurfaceBridge.ActiveChanged += PlayerFirstSurfaceBridge_ActiveChanged;
                    _playerFirstSurfaceBridge = playerFirstSurfaceBridge;
                }
                catch (Exception ex)
                {
                    if (!IsPlayerInitializationCurrent(initializationGeneration)) return;
                    Log.Error("Focused popout controls could not be initialized; using the watch page.", ex);
                }
            }

            // Registration awaits above may pump a close or a replacement initialization. Never
            // navigate from work that no longer owns this window's initialization generation.
            if (!IsPlayerInitializationCurrent(initializationGeneration)) return;
            core.Navigate(_currentUrl);
            if (_mode == PlaybackMode.Compact) _shellReadyTimer.Start();
            Log.Info($"Popout Player initialized (mode={_mode}, presentation={_presentation}).");
        }
        catch (Exception ex)
        {
            // EnsureCoreWebView2Async can finish by throwing after an intentional close. Closing is
            // already the user's answer; do not resurrect the window with a failure prompt.
            if (!IsPlayerInitializationCurrent(initializationGeneration)) return;
            Log.Error("Failed to initialize the Popout Player.", ex);
            Prompt.ShowInfo(this, "Video Popout", "PiPlay couldn't start the popout player.\n\n" + ex.Message);
            Close();
        }
    }

    // --- WebView2 process failure (spec 15.4, PP-02) ---

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"Popout WebView2 process failure: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode}, {e.ProcessDescription}).");
        HandleProcessFailure(WebViewProcessFailurePolicy.Classify(e.ProcessFailedKind));
    }

    /// <summary>
    /// Scoped cut of PP-02 for the Popout: a renderer exit reloads the page in place (bounded, one
    /// at a time); a browser-process exit has no core to recreate against, so the Popout closes and
    /// returns with its last polled sample while the Source recreates its own control. Internal for
    /// the WPF test lane.
    /// </summary>
    internal WebViewRecoveryAction HandleProcessFailure(WebViewFailureKind kind, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var count = WebViewProcessFailurePolicy.ConsecutiveCountFor(_consecutiveProcessRecoveries, _lastProcessFailureUtc, at);
        var action = WebViewProcessFailurePolicy.Decide(
            kind, count, _processRecoveryInProgress ? WebViewRecoveryAction.Reload : null, _closing);
        Log.Info($"Popout browser failure {kind}: {action} (consecutive recoveries: {count}).");
        if (action is WebViewRecoveryAction.Ignore or WebViewRecoveryAction.LogOnly) return action;

        _lastProcessFailureUtc = at;
        _consecutiveProcessRecoveries = count;
        switch (action)
        {
            case WebViewRecoveryAction.Reload:
                _consecutiveProcessRecoveries++;
                _processRecoveryInProgress = true;
                ShowShellError(WebViewProcessFailurePolicy.PopoutReloadMessage);
                try { Player.CoreWebView2?.Reload(); }
                catch (Exception ex)
                {
                    _processRecoveryInProgress = false;
                    Log.Error("Popout reload after renderer exit failed; closing the Popout.", ex);
                    Close();
                }
                break;
            case WebViewRecoveryAction.Recreate:
            case WebViewRecoveryAction.GiveUp:
                // Playback goes back to the Source with the last polled sample (the final capture
                // cannot run against a dead core; the PP-04 gate keeps the last poll intact). The
                // stamp tells the Source its own core is dead before it acts on the return.
                _processRecoveryInProgress = false;
                _returnState.BrowserProcessFailed = true;
                Log.Info("Popout Player closing after a browser-process failure; playback returns to the Source Window.");
                Close();
                break;
        }

        return action;
    }

    // --- Navigation policy: YouTube only (REQ-NAV-02) ---

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            NavigationPolicy.IsAllowed(uri, NavigationSurface.Player))
        {
            _activeNavigationId = e.NavigationId;
            BeginNavigation();
            ResetPlaybackSamplingForNavigation();
            return;
        }

        e.Cancel = true;
        Log.Info($"Player navigation blocked, opening externally: {Log.RedactUrl(e.Uri)}");
        OpenExternal(e.Uri);
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // URL-shape proxy (overhaul Task 3): WebView2 exposes no window-open disposition, so a
        // left-click on a recommendation and an explicit "open in new window" look identical. A
        // playable YouTube target stays in THIS player (ADR-0005: never a second window); anything
        // else opens in the system browser.
        e.Handled = true;
        if (!TryRetargetForNewWindow(e.Uri)) OpenExternal(e.Uri);
    }

    /// <summary>Internal for the WPF test lane (CoreWebView2 event args cannot be constructed).</summary>
    internal bool TryRetargetForNewWindow(string uri)
    {
        if (PopoutNavigationPolicy.DecideNewWindow(uri, out var target) != PopoutNewWindowAction.RetargetInPlace)
            return false;
        RetargetTo(target!);
        return true;
    }

    /// <summary>
    /// The incoming-link route (REQ-APP-01, ADR-0009): a supported VIDEO target delivered from
    /// outside retargets the Popout that owns playback. Narrow by design — playlist-only targets
    /// have no playable item here, and a closing player is already handing playback back, so the
    /// caller retains the link for the Source instead.
    /// </summary>
    internal bool TryRetargetFromIncomingLink(YouTubeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_closing || string.IsNullOrEmpty(target.VideoId)) return false;
        RetargetTo(target);
        return true;
    }

    /// <summary>
    /// Move this player to a new target in place, in its CURRENT mode (compact shell rebuild or
    /// normal watch URL). All launch-time state that the navigation invalidates follows: the
    /// fallback target (the error bar must reopen the NEW video), the return video id, the
    /// timestamp (unknown until the new page/shell reports), the one-shot play nudge, and the
    /// compact ready-watchdog (a dead NEW shell must still surface the error bar).
    /// </summary>
    private void RetargetTo(YouTubeTarget target)
    {
        _currentTarget = target;
        _returnState.VideoId = target.VideoId;
        _returnState.PlaylistId = target.PlaylistId;
        _returnIdentityGeneration++;
        ResetReturnMediaStateForNewTarget();
        _nudgedPlay = false;
        _finalReturnPlaybackCaptured = false;
        _navCompleted = false;
        _syncTimer.Stop();
        // The navigation tears down any fullscreen element with no exit event reaching a handler
        // that still believes the OLD page's element is up — drop the latch, keep the window state
        // (yanking the window around mid-retarget would be worse than staying expanded).
        _maximizedForFullScreenElement = false;
        _currentUrl = PlaybackModePolicy.BuildPopoutUrl(
            _mode, target, target.StartSeconds, WebViewEnvironmentService.ShellPlayerUrl);

        if (_mode == PlaybackMode.Compact)
        {
            HideShellError();
            // Re-arm the watchdog. A final state message from the dying old shell can stop it in
            // the brief window before the navigation commits — accepted residual: the failed-load
            // path in Core_NavigationCompleted still covers a shell that never comes up at all.
            _shellReadyTimer.Stop();
            _shellReadyTimer.Start();
        }

        Log.Info($"Popout retarget ({_mode}): {Log.RedactUrl(_currentUrl)}");
        Player.CoreWebView2?.Navigate(_currentUrl);
    }

    /// <summary>
    /// Track the CURRENT video for return (REQ-RETURN-01): YouTube SPA navigations and
    /// autoplay-advance never raise NavigationStarting, but Source follows them. The compact
    /// shell's piplay.local URL fails the YouTube host check, so this is naturally
    /// normal-page-only — compact tracking comes from shell state messages instead.
    /// </summary>
    private void Core_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => TrackReturnIdentity(Player.CoreWebView2?.Source);

    /// <summary>Internal for the WPF test lane (CoreWebView2 event args cannot be constructed).</summary>
    internal void TrackReturnIdentity(string? source)
    {
        if (YouTubeUrlHelper.TryParse(source, out var t) && t.VideoId is not null)
        {
            if (!string.Equals(_returnState.VideoId, t.VideoId, StringComparison.Ordinal))
            {
                // A different video is playing now: the last sample belonged to the previous one.
                // Unknown time for the new video is correct until its own poll lands (PP-04).
                _returnIdentityGeneration++;
                ResetReturnMediaStateForNewTarget();
            }

            _returnState.VideoId = t.VideoId;
            // Follows the video, including to null: a video OUTSIDE the list (recommendation
            // click) must not return wearing the stale playlist context.
            _returnState.PlaylistId = t.PlaylistId;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A superseded navigation may complete after a newer one has started. It must not mark the
        // new document ready or restart polling against a page that is still loading.
        // CompleteNavigation sets _navCompleted for the current generation.
        if (_activeNavigationId != e.NavigationId ||
            !CompleteNavigation(_navigationGeneration, e.IsSuccess)) return;

        OnNavigationSettled(e.IsSuccess);
    }

    /// <summary>What a completed, current navigation means for the recovery notice and the shell.</summary>
    private void OnNavigationSettled(bool isSuccess)
    {
        if (_processRecoveryInProgress)
        {
            // The reload after a renderer exit landed (or failed visibly through the error page).
            _processRecoveryInProgress = false;
            if (isSuccess) HideShellError();
        }

        // A compact shell that failed to load outright can never message the host — surface the
        // error bar now instead of waiting out the watchdog (spec 10.3 / Q-6, Stage 4).
        if (_mode == PlaybackMode.Compact && !isSuccess)
        {
            _shellReadyTimer.Stop();
            ShowShellError(PlayerShellErrorPolicy.ShellLoadFailedMessage);
        }
    }

    private int BeginNavigation()
    {
        if (_closing) return _navigationGeneration;
        // Any full navigation destroys the injected overlay. Restore native recovery chrome now;
        // only the new document's positive, source-gated handshake may hide it again.
        if (_focusedOverlayReady || _focusedSurfaceActive) ApplyFocusedSurfaceActive(false);
        _navigationGeneration++;
        _navCompleted = false;
        _syncTimer.Stop();
        return _navigationGeneration;
    }

    private bool CompleteNavigation(int generation, bool succeeded)
    {
        if (_closing || generation != _navigationGeneration) return false;

        _navCompleted = succeeded;
        if (!succeeded)
        {
            _syncTimer.Stop();
            return true;
        }

        // Normal mode polls the YouTube page DOM for the timestamp; compact mode reads it from the
        // shell bridge (IFrame API). A failed navigation never starts either source.
        if (PlaybackModePolicy.UsesDomSyncTimer(_mode) && !_syncTimer.IsEnabled) _syncTimer.Start();
        return true;
    }

    private void ResetPlaybackSamplingForNavigation()
    {
        _navCompleted = false;
        if (PlaybackModePolicy.UsesDomSyncTimer(_mode)) _syncTimer.Stop();
        ResetReturnMediaStateForNewTarget();
    }

    private void ResetReturnMediaStateForNewTarget()
    {
        _returnState.LastKnownSeconds = null;
        _returnState.Paused = null;
        _returnState.Volume = null;
        _returnState.Muted = null;
        _returnState.PlaybackRate = null;
    }

    // --- Compact shell bridge + error/fallback path (spec 10.3 / Q-6, Stage 4) ---

    private void ShellBridge_Ready(object? sender, EventArgs e) => _shellReadyTimer.Stop();

    private void ShellBridge_StateReceived(object? sender, InboundShellMessage state)
    {
        if (_mode != PlaybackMode.Compact) return;
        _shellReadyTimer.Stop();
        // Protocol v3: the shell reports the CURRENT video (playlist auto-advance and in-iframe
        // clicks are invisible to the host). PlayerShellProtocol.Parse already rejected malformed
        // ids at the wire (the parse IS the trust boundary), so a non-empty value here is a
        // well-formed id; absent/invalid keeps the last-known id. An id change is an identity
        // change like a Source-navigation one: the previous sample and any in-flight capture
        // belong to the old video (PP-04), so the generation moves before this sample lands.
        if (!string.IsNullOrEmpty(state.VideoId) &&
            !string.Equals(_returnState.VideoId, state.VideoId, StringComparison.Ordinal))
        {
            _returnIdentityGeneration++;
            ResetReturnMediaStateForNewTarget();
            _returnState.VideoId = state.VideoId;
        }

        // Compact mode's source of truth for the return timestamp is the IFrame API, not the DOM.
        // The time rides with the id in the same message, so it pairs with the video just set.
        _returnState.LastKnownSeconds = state.CurrentTime;
        // Compact shell protocol does not currently report paused/volume/mute/rate; normal mode
        // captures those from the DOM bridge.
        // A playing state proves recovery (e.g. a playlist auto-advanced past a dead entry) —
        // clear a showing error so the bar can't outlive the problem it reported.
        if (PlayerShellErrorPolicy.ShouldAutoDismiss(state.PlayerState)) HideShellError();
    }

    private void ShellBridge_ErrorReceived(object? sender, InboundShellMessage message)
    {
        if (_mode != PlaybackMode.Compact) return;
        _shellReadyTimer.Stop();
        ShowShellError(PlayerShellErrorPolicy.Describe(message.ErrorCode));
    }

    /// <summary>
    /// Map an allowlisted shell window-action request (spec 12.5 / docs/YouTube_Compliance.md) onto the EXISTING
    /// native handlers — the shell can ask for exactly what the chrome strip already does, never
    /// more. Off-allowlist actions were already dropped at the parse layer (they arrive as
    /// Unknown, which the bridge never surfaces), so this switch is the second of two gates.
    /// </summary>
    private void ShellBridge_RequestReceived(object? sender, InboundShellMessage message)
    {
        if (_mode != PlaybackMode.Compact) return;
        HandleWindowAction(message.Action);
    }

    private void PlayerFirstSurfaceBridge_ActionRequested(object? sender, string action)
    {
        if (_presentation != PopoutPresentation.Focused || !_focusedOverlayReady) return;
        var generation = _navigationGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_closing && _focusedOverlayReady && generation == _navigationGeneration)
                HandleWindowAction(action);
        }));
    }

    private void PlayerFirstSurfaceBridge_ActiveChanged(bool active)
    {
        // Changing the WebView row while inside WebMessageReceived risks reentrancy. Until this
        // deferred positive handshake runs, the native strip remains the recovery surface.
        var generation = _navigationGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation == _navigationGeneration) ApplyFocusedSurfaceActive(active);
        }));
    }

    private void ApplyFocusedSurfaceActive(bool active)
    {
        if (_closing || _presentation != PopoutPresentation.Focused) return;
        _focusedOverlayReady = active;
        // Full-client Focused removes the native drag strip, so it is allowed only after both the
        // overlay and whole-surface drag bridges are live. Either failure keeps recovery chrome.
        _focusedSurfaceActive = active && _surfaceDragAvailable;
        if (_focusedSurfaceActive)
        {
            ChromeStrip.BeginAnimation(OpacityProperty, null);
            ChromeStrip.Visibility = Visibility.Collapsed;
            ChromeStrip.IsHitTestVisible = false;
            _controlsVisible = false;
        }
        else
        {
            ChromeStrip.BeginAnimation(OpacityProperty, null);
            ChromeStrip.Opacity = 1;
            ChromeStrip.Visibility = Visibility.Visible;
            ChromeStrip.IsHitTestVisible = true;
            _controlsVisible = true;
            ApplyFadeState();
        }
        ApplyFocusedResizeInset();
    }

    /// <summary>Second host-side gate shared by both closed page protocols.</summary>
    private void HandleWindowAction(string? action)
    {
        switch (action)
        {
            case PlayerShellProtocol.ActionClose:
                // Deferred: Close() disposes the WebView2 in PlayerWindow_Closed, and this handler
                // runs inside CoreWebView2.WebMessageReceived — disposing the control inside its
                // own event callback is a documented WebView2 reentrancy hazard.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing) Close();
                });
                break;
            case PlayerShellProtocol.ActionPinToggle:
                PinToggle.IsChecked = PinToggle.IsChecked != true;
                ApplyTopmostFromToggle();
                break;
            case PlayerShellProtocol.ActionFullscreenToggle:
                ToggleExpandedState();
                break;
            case PlayerFirstSurfaceProtocol.ActionSettings:
                // The Source owns the single Settings dialog; defer for the same WebView2
                // reentrancy reason as Close.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing) SettingsRequested?.Invoke(this, EventArgs.Empty);
                });
                break;
        }
    }

    private void ShellReadyTimer_Tick(object? sender, EventArgs e)
    {
        _shellReadyTimer.Stop();
        if (_mode != PlaybackMode.Compact) return;
        ShowShellError(PlayerShellErrorPolicy.ReadyTimeoutMessage);
    }

    private void ShowShellError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
        // Redacted target only (spec 17): never the full query string. The bar always carries
        // "Open normal page"; in normal mode that reopens the current video as the normal page.
        Log.Info($"Popout notice shown (\"{message}\") for {Log.RedactUrl(_currentUrl)} in {_mode} mode; " +
                 "Open normal page offered.");
    }

    private void HideShellError()
    {
        if (ErrorBar.Visibility == Visibility.Collapsed) return;
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    private void FallbackButton_Click(object sender, RoutedEventArgs e) => FallBackToNormalPage();

    private void ErrorDismissButton_Click(object sender, RoutedEventArgs e) => HideShellError();

    /// <summary>
    /// The error bar's fallback action (Q-6): re-navigate this same window to the normal watch
    /// page for the same target at the best-known timestamp. In-place — closing instead would fire
    /// the return lifecycle (source resume / auto re-popout). From here the window IS a
    /// normal-mode player: the DOM sync timer becomes the one timestamp source, the shell bridge
    /// is disposed, and the minimum relaxes to the normal floor. One-way by design; saved mode
    /// preferences are untouched.
    /// </summary>
    private void FallBackToNormalPage()
    {
        if (_fellBack || _currentTarget is null || Player.CoreWebView2 is null) return;
        _fellBack = true;

        _shellReadyTimer.Stop();
        try { _shellBridge?.Dispose(); } catch { /* ignore */ }
        _shellBridge = null;

        _mode = PlaybackMode.Normal;
        MinWidth = PlaybackModePolicy.MinWidthFor(PlaybackMode.Normal);
        MinHeight = PlaybackModePolicy.MinHeightFor(PlaybackMode.Normal);
        // The mode gate makes further fullscreen-element events no-ops, so a latch set by the dying
        // shell could never clear — drop it (window state stays; the expand button is the exit).
        _maximizedForFullScreenElement = false;
        HideShellError();

        // A zero timestamp here means the shell never actually played (the usual fallback cause,
        // e.g. embedding disabled), so treat it as unknown and let BuildWatchUrl fall through to
        // the target's launch StartSeconds rather than restarting the video at 0:00.
        var seconds = _returnState.LastKnownSeconds is > 0 ? _returnState.LastKnownSeconds : null;
        _currentUrl = YouTubeUrlHelper.BuildWatchUrl(_currentTarget, seconds);
        Log.Info($"Compact fallback: reopening in normal page mode: {Log.RedactUrl(_currentUrl)}");
        Player.CoreWebView2.Navigate(_currentUrl);
    }

    // Test seams (WPF lane): drive the error/fallback path without a live WebView2 or shell.
    internal void HandleShellStateForTests(InboundShellMessage state) => ShellBridge_StateReceived(this, state);
    internal void HandleShellErrorForTests(InboundShellMessage message) => ShellBridge_ErrorReceived(this, message);
    internal void HandleShellLoadFailureForTests() => ShowShellError(PlayerShellErrorPolicy.ShellLoadFailedMessage);
    internal void RequestFallbackForTests() => FallBackToNormalPage();
    internal void HandleShellRequestForTests(InboundShellMessage message) => ShellBridge_RequestReceived(this, message);
    internal void HandleFocusedActionForTests(string action) => HandleWindowAction(action);
    internal bool IsErrorBarVisibleForTests => ErrorBar.Visibility == Visibility.Visible;
    internal bool IsProcessRecoveryInProgressForTests => _processRecoveryInProgress;
    internal void SettleNavigationForTests(bool succeeded) => OnNavigationSettled(succeeded);
    internal bool ReturnBrowserProcessFailedForTests => _returnState.BrowserProcessFailed;
    internal int ConsecutiveProcessRecoveriesForTests => _consecutiveProcessRecoveries;
    internal string ErrorTextForTests => ErrorText.Text;

    // Retarget / return-state seams (overhaul Task 3, WPF lane).
    internal string CurrentUrlForTests => _currentUrl;
    internal string? CurrentFallbackVideoIdForTests => _currentTarget?.VideoId;
    internal string? ReturnVideoIdForTests => _returnState.VideoId;
    internal string? ReturnPlaylistIdForTests => _returnState.PlaylistId;
    internal int? ReturnSecondsForTests => _returnState.LastKnownSeconds;
    internal bool? ReturnPausedForTests => _returnState.Paused;
    internal double? ReturnVolumeForTests => _returnState.Volume;
    internal bool? ReturnMutedForTests => _returnState.Muted;
    internal double? ReturnPlaybackRateForTests => _returnState.PlaybackRate;
    internal PlacementData? LaunchPlacementForTests => _placement;

    // Strip auto-hide seams (Wpf lane): drive the collapse/reveal state machine headlessly.
    internal bool StripAutoHideForTests => _stripAutoHide;
    internal bool IsChromeStripCollapsedForTests => ChromeStrip.Visibility == Visibility.Collapsed;
    internal bool IsChromeStripHitTestVisibleForTests => ChromeStrip.IsHitTestVisible;
    internal void HideControlsForTests() => HideControls();
    internal void CompleteHideFadeForTests() => OnHideFadeCompleted();

    // Navigation/polling seams: WebView2 navigation event args cannot be constructed in the WPF
    // lane, so drive the same generation state machine directly without initializing WebView2.
    internal int BeginNavigationForTests() => BeginNavigation();
    internal bool CompleteNavigationForTests(int generation, bool succeeded) =>
        CompleteNavigation(generation, succeeded);
    internal bool IsSyncTimerRunningForTests => _syncTimer.IsEnabled;
    internal bool IsClosingForTests => _closing;
    internal int BeginPlayerInitializationForTests() => BeginPlayerInitialization();
    internal bool IsPlayerInitializationCurrentForTests(int generation) =>
        IsPlayerInitializationCurrent(generation);
    internal bool TryBeginSyncPollForTests(out int generation) => TryBeginSyncPoll(out generation);
    internal bool IsSyncPollCurrentForTests(int generation) => IsSyncPollCurrent(generation);
    internal void EndSyncPollForTests() => EndSyncPoll();

    private async void SyncTimer_Tick(object? sender, EventArgs e)
    {
        // Two independent guards, both required: the popout must not sample while it is closing or
        // once the final return state is frozen (bring-back/close), and never before the active
        // navigation completed.
        if (_closing || _finalReturnPlaybackCaptured || !_navCompleted) return;
        var core = Player.CoreWebView2;
        // Authentication/error surfaces can complete inside the normal player during redirects,
        // but they have no video state to read. Keep the cheap URL-shape check outside WebView IPC.
        if (core is null || !YouTubeUrlHelper.IsWatchUrl(core.Source)
            || !TryBeginSyncPoll(out var generation)) return;
        var identityGeneration = _returnIdentityGeneration;

        try
        {
            var state = await YouTubeDomBridge.ReadPlayerStateAsync(core);
            if (state is null || !IsSyncPollCurrent(generation) || _finalReturnPlaybackCaptured) return;

            // The popout is the active surface now: if it came up paused, nudge play once
            // (play() is an allowed control per spec 19). Best-effort; never forced again.
            // REQ-RETURN-07: a popout launched from a paused source is never auto-nudged.
            if (_nudgePlayOnInitialPause && !_nudgedPlay && state.Paused)
            {
                _nudgedPlay = true;
                await YouTubeDomBridge.PlayAsync(core);
                if (!IsSyncPollCurrent(generation) || _finalReturnPlaybackCaptured) return;
            }

            TryApplyReturnPlaybackSample(state, generation, identityGeneration, isFinalCapture: false);
        }
        finally
        {
            EndSyncPoll();
        }
    }

    internal async Task<PlayerReturnState> CaptureReturnStateNowAsync()
    {
        _finalReturnPlaybackCaptured = true;
        _syncTimer.Stop();
        await CaptureCurrentPlaybackStateAsync();
        CaptureReturnWindowState();
        return _returnState;
    }

    private async Task CaptureCurrentPlaybackStateAsync()
    {
        if (!PlaybackModePolicy.UsesDomSyncTimer(_mode) || Player.CoreWebView2 is null) return;
        var navigationGeneration = _navigationGeneration;
        var identityGeneration = _returnIdentityGeneration;
        var state = await YouTubeDomBridge.ReadPlayerStateAsync(Player.CoreWebView2);
        // A retarget or SPA advance during the read already reset the media fields for the new
        // video; a stale sample must not refill them (PP-04). Unknown time is the honest answer.
        TryApplyReturnPlaybackSample(state, navigationGeneration, identityGeneration, isFinalCapture: true);
    }

    /// <summary>
    /// The one gate between a DOM sample and the return snapshot: the sample is applied only if it
    /// was read for the navigation AND the return identity that are still current. Polled samples
    /// additionally stop once the final capture froze the snapshot.
    /// </summary>
    private bool TryApplyReturnPlaybackSample(
        PlayerState? state, int navigationGeneration, int identityGeneration, bool isFinalCapture)
    {
        if (state is null || _closing) return false;
        if (!isFinalCapture && _finalReturnPlaybackCaptured) return false;
        if (navigationGeneration != _navigationGeneration
            || identityGeneration != _returnIdentityGeneration) return false;

        ApplyReturnPlaybackState(state);
        return true;
    }

    internal int ReturnIdentityGenerationForTests => _returnIdentityGeneration;
    internal int NavigationGenerationForTests => _navigationGeneration;
    internal bool TryApplyReturnPlaybackSampleForTests(
        PlayerState? state, int navigationGeneration, int identityGeneration, bool isFinalCapture) =>
        TryApplyReturnPlaybackSample(state, navigationGeneration, identityGeneration, isFinalCapture);

    private void ApplyReturnPlaybackState(PlayerState state)
    {
        _returnState.LastKnownSeconds = state.CurrentTime;
        _returnState.Paused = state.Paused;
        _returnState.Volume = state.Volume;
        _returnState.Muted = state.Muted;
        _returnState.PlaybackRate = state.PlaybackRate;
    }

    private bool TryBeginSyncPoll(out int generation)
    {
        generation = _navigationGeneration;
        if (_closing || _syncTickInProgress || !_navCompleted) return false;
        _syncTickInProgress = true;
        return true;
    }

    private bool IsSyncPollCurrent(int generation) =>
        !_closing && _navCompleted && generation == _navigationGeneration;

    private void EndSyncPoll() => _syncTickInProgress = false;

    // --- Chrome ---

    private void PinToggle_Click(object sender, RoutedEventArgs e) => ApplyTopmostFromToggle();

    private void ApplyTopmostFromToggle()
    {
        var pinned = PinToggle.IsChecked == true;
        Topmost = pinned;
        UpdatePinAffordance(pinned);
        RefreshFocusedSurfaceAppearance();
    }

    private void UpdatePinAffordance(bool pinned)
    {
        var action = pinned ? "Unpin popout from top" : "Pin popout on top";
        PinToggle.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, action);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ChromeStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // Expanded covers the monitor: there is nowhere to drag to, and DragMove on a maximized
        // borderless window misbehaves (the frame moves without un-maximizing). Restore is a
        // deliberate act (expand button / Esc), never a drag side effect.
        if (WindowState == WindowState.Maximized) return;
        try { DragMove(); }
        catch { /* DragMove throws if the button was already released */ }
        finally { OnUserActivity(); } // keep controls up briefly after a drag, then resume idle countdown
    }

    // --- Expand / restore (overhaul Task 4) ---

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleExpandedState();

    /// <summary>
    /// The ONE expand path (Q-2): the native strip button and the shell's fullscreenToggle request
    /// land here. Maximize semantics are the decided full-monitor cover (owner decision 2026-06-10,
    /// no work-area hook). A user toggle clears the fullscreen-element latch: from here on the
    /// expansion is theirs, and an element exit must not undo it.
    /// </summary>
    private void ToggleExpandedState()
    {
        _maximizedForFullScreenElement = false;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateExpandAffordance();
        // Any expand path counts as activity (adopted from the parallel b35c0dd landing): an
        // auto-hidden strip un-collapses, so the restore affordance is immediately reachable in
        // the new state instead of waiting for the top-edge reveal.
        OnUserActivity();
    }

    /// <summary>
    /// Runs on every StateChanged: any exit from Maximized — ours, or an OS path our toggles never
    /// see (Win+Down, aero snap) — invalidates the element-caused latch, because the expansion it
    /// described no longer exists. Without this, a stale latch survives an OS restore and the
    /// state machine lies until the next element event (review finding 2026-06-10).
    /// </summary>
    private void HandleWindowStateChanged()
    {
        if (WindowState != WindowState.Maximized) _maximizedForFullScreenElement = false;
        UpdateExpandAffordance();
    }

    /// <summary>Keep glyph and tooltip truthful in both states; the UIA name stays state-neutral
    /// ("Expand or restore popout", XamlInvariantTests pins it).</summary>
    private void UpdateExpandAffordance()
    {
        var maximized = WindowState == WindowState.Maximized;
        ExpandButton.Content = maximized ? GlyphRestore : GlyphMaximize;
        ExpandButton.ToolTip = maximized ? "Restore popout" : "Expand popout";
    }

    /// <summary>
    /// Secondary expand route (spec settled decision 14): a compact fullscreen ELEMENT (the
    /// shell's YouTube fullscreen button) expands the window; exiting restores it ONLY if the
    /// element caused the expansion. Gated on the LIVE mode — the compact→normal fallback flips
    /// <see cref="_mode"/> in place, and the normal watch page must not gain a fullscreen
    /// invariant (Popout Standard / Fullview Faded stay one path).
    /// </summary>
    private void ApplyFullScreenElementState(bool containsFullScreenElement)
    {
        if (_mode != PlaybackMode.Compact) return;
        if (containsFullScreenElement)
        {
            if (WindowState == WindowState.Maximized) return;   // already the user's posture
            _maximizedForFullScreenElement = true;
            WindowState = WindowState.Maximized;
            UpdateExpandAffordance();
            OnUserActivity();   // reveal the strip in the new state (see ToggleExpandedState)
        }
        else if (_maximizedForFullScreenElement)
        {
            _maximizedForFullScreenElement = false;
            if (WindowState != WindowState.Maximized) return;   // user already restored it
            WindowState = WindowState.Normal;
            UpdateExpandAffordance();
            OnUserActivity();
        }
    }

    /// <summary>Esc restores an expanded popout (Task 4 reversibility). Only reachable while WPF
    /// owns focus — keys pressed inside the WebView2 child stay in the browser.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && TryRestoreFromEscape()) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    private bool TryRestoreFromEscape()
    {
        if (WindowState != WindowState.Maximized) return false;
        _maximizedForFullScreenElement = false;
        WindowState = WindowState.Normal;
        UpdateExpandAffordance();
        OnUserActivity();
        return true;
    }

    // Expand/restore seams (WPF lane): KeyEventArgs needs a PresentationSource and fullscreen
    // element events need a live CoreWebView2, neither of which exists for an unshown window —
    // and unshown windows receive no StateChanged, so the OS path is driven directly.
    internal void ApplyFullScreenElementStateForTests(bool contains) => ApplyFullScreenElementState(contains);
    internal bool HandleEscapeForTests() => TryRestoreFromEscape();
    internal void HandleWindowStateChangedForTests() => HandleWindowStateChanged();
    internal bool IsMaximizedForFullScreenElementForTests => _maximizedForFullScreenElement;
    internal string ExpandGlyphForTests => (string)ExpandButton.Content;
    internal string ExpandToolTipForTests => (string)ExpandButton.ToolTip;

    // --- Controls fade (spec 7.1) ---

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void FadeToggle_Click(object sender, RoutedEventArgs e)
    {
        _fadeEnabled = FadeToggle.IsChecked == true;
        _returnState.FadeEnabled = _fadeEnabled;
        ApplyFadeState();
        RefreshFocusedSurfaceAppearance();
    }

    /// <summary>
    /// With the native strip collapsed, Focused needs its own four-sided WPF edge band so the
    /// WebView2 child HWND does not swallow top-edge resize. Expanded stays truly full-bleed.
    /// </summary>
    private void ApplyFocusedResizeInset()
    {
        if (_presentation != PopoutPresentation.Focused) return;
        if (!_focusedSurfaceActive)
        {
            Player.ClearValue(FrameworkElement.MarginProperty);
            return;
        }
        Player.Margin = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(BorderlessResizeHitTestPolicy.ResizeBorderDip);
    }

    internal void ApplyAppearance(string? accentColor, int fadeIdleDelayMs, bool stripAutoHide = false)
    {
        ApplyAccentVisuals(accentColor);
        _idleTimer.Interval = TimeSpan.FromMilliseconds(
            PlayerAppearancePolicy.NormalizeFadeIdleDelayMs(fadeIdleDelayMs));

        var autoHideTurnedOff = _stripAutoHide && !stripAutoHide;
        _stripAutoHide = stripAutoHide;
        if (autoHideTurnedOff && ChromeStrip.Visibility != Visibility.Visible) ShowControls();
        UpdateActivityProbe();

        if (_fadeEnabled && _idleTimer.IsEnabled) RestartIdleTimer();
        RefreshFocusedSurfaceAppearance();
    }

    /// <summary>Apply only the live accent surface; behavior and native-window settings stay put.</summary>
    internal void ApplyAccent(string? accentColor)
    {
        ApplyAccentVisuals(accentColor);
        RefreshFocusedSurfaceAppearance();
    }

    private void ApplyAccentVisuals(string? accentColor)
    {
        // One theme accent drives both Popout Pin and Popout Fade (overhaul Task 10); the brush is
        // shared (frozen) across the two toggles.
        _accentColor = ThemeCatalog.NormalizeAccentColor(accentColor);
        var accentBrush = ResolveAccentBrush(_accentColor);
        ToggleAccent.SetCheckedBrush(PinToggle, accentBrush);
        ToggleAccent.SetCheckedBrush(FadeToggle, accentBrush);
    }

    private void RefreshFocusedSurfaceAppearance() =>
        _playerFirstSurfaceBridge?.ApplyAppearance(
            _accentColor, _fadeEnabled, (int)_idleTimer.Interval.TotalMilliseconds, Topmost);

    private static Brush ResolveAccentBrush(string? accentColor)
    {
        var surface = Application.Current.TryFindResource("SurfaceHover") as SolidColorBrush;
        return surface is null
            ? ThemeColors.Brush(accentColor)
            : ThemeColors.ContrastBrush(accentColor, surface.Color);
    }

    /// <summary>Reset the fade lifecycle to match <see cref="_fadeEnabled"/>: either show-and-arm or pin visible.</summary>
    private void ApplyFadeState()
    {
        if (_fadeEnabled)
        {
            ShowControls();
            RestartIdleTimer();
        }
        else
        {
            _idleTimer.Stop();
            ShowControls(); // disabling fade restores the MVP "always visible" behavior immediately
            ExitWindowOpacityIdle(); // no idle source while fade is off, so no idle opacity either
        }
        UpdateActivityProbe();   // the fade state is a probe input (strip auto-hide gates on it)
    }

    private void OnUserActivity()
    {
        ExitWindowOpacityIdle();
        if (!_fadeEnabled) return;
        ShowControls();
        RestartIdleTimer();
    }

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        // Activity WPF couldn't see: the probe watches the WebView2 area (no WPF mouse events
        // there — Stage 0 spike finding). Recent in-window movement means not idle — re-arm.
        // This feeds the ONE idleness definition, holding up both the strip and the window
        // opacity. The probe only runs while idle opacity is configured, so default-look fade
        // behavior is untouched.
        if (_opacityHoverPoll.IsEnabled && _lastInWindowCursorMoveMs >= 0 &&
            Environment.TickCount64 - _lastInWindowCursorMoveMs < _idleTimer.Interval.TotalMilliseconds)
        {
            RestartIdleTimer();
            return;
        }
        if (FadePolicy.ShouldHide(_fadeEnabled, ChromeStrip.IsMouseOver, _isDragging, idleElapsed: true))
        {
            HideControls();
            // Window opacity idles on the SAME tick with the SAME inputs (one idleness definition,
            // spec 7.1–7.3).
            EnterWindowOpacityIdle();
        }
        else if (_fadeEnabled)
        {
            // Something is still holding controls up (pointer over the strip / mid-drag); re-arm.
            RestartIdleTimer();
        }
    }

    // --- Whole-window opacity (spec 7.3, Phase 4) ---

    internal (double Constant, double Idle) WindowOpacityLevelsForTests => (_constantWindowOpacity, _idleWindowOpacity);
    internal bool IsWindowOpacityIdleForTests => _windowOpacityIdle;
    internal bool IsOpacityHoverPollRunningForTests => _opacityHoverPoll.IsEnabled;
    internal void EnterWindowOpacityIdleForTests() => EnterWindowOpacityIdle();
    internal void OnUserActivityForTests() => OnUserActivity();

    /// <summary>Live re-apply seam, called by MainWindow for settings changes and the dialog's
    /// live preview (mirrors <see cref="ApplyAppearance"/>). The preview passes
    /// <paramref name="animate"/> = false: a slider drag fires per tick, and restarting the fade
    /// animation on every tick would stair-step — the drag itself is the animation.</summary>
    internal void ApplyWindowOpacity(double constantOpacity, double idleOpacity, bool animate = true)
    {
        _constantWindowOpacity = WindowOpacityPolicy.Normalize(constantOpacity);
        _idleWindowOpacity = WindowOpacityPolicy.Normalize(idleOpacity);
        ApplyWindowOpacityToHwnd(animate);
    }

    private void ApplyWindowOpacityToHwnd(bool animate)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // SourceInitialized applies the initial state

        var active = WindowOpacityPolicy.Effective(isIdle: false, _constantWindowOpacity, _idleWindowOpacity);
        var idle = WindowOpacityPolicy.Effective(isIdle: true, _constantWindowOpacity, _idleWindowOpacity);
        // Corner shape is theme/override data applied separately (ApplyCornerMode) — opacity no
        // longer drives DWM rounding (docs/Theme_Preset_Differences.md).
        WindowOpacityApplier.Apply(hwnd, _windowOpacityIdle ? idle : active, animate);
        UpdateActivityProbe();
    }

    /// <summary>Live re-apply seam for the theme/override native corner shape, called by
    /// MainWindow on settings changes (mirrors <see cref="ApplyWindowOpacity"/>).</summary>
    internal void ApplyCornerMode(DwmCornerMode mode)
    {
        ApplyCornerAppearance(mode, _popoutCornerRadiusDip);
    }

    private void SurfaceDragBridge_DragRequested(object? sender, EventArgs e)
    {
        if (_surfaceDragQueued || _closing) return;
        _surfaceDragQueued = true;
        // The final native handoff is PostMessage, so this stays non-blocking while avoiding a
        // second dispatcher hop that could miss a short drag after the physical button is released.
        ExecuteSurfaceDragRequest(
            BorderlessWindowHelper.IsLeftButtonDown,
            () => BorderlessWindowHelper.TryBeginWindowMove(this));
    }

    private void SurfaceDragBridge_AvailabilityChanged(bool available)
    {
        if (_closing) return;
        _surfaceDragAvailable = available;
        // Focused may hide its native recovery strip only when both page capabilities are live for
        // this exact document. A failed/rotating drag token immediately restores the strip.
        if (_presentation == PopoutPresentation.Focused)
            ApplyFocusedSurfaceActive(_focusedOverlayReady);
    }

    private void ExecuteSurfaceDragRequest(Func<bool> leftButtonDown, Func<bool> beginWindowMove)
    {
        try
        {
            if (!PlayerSurfaceDragPolicy.CanBegin(
                    _closing, WindowState == WindowState.Normal, leftButtonDown())) return;
            // TryBeginWindowMove posts SC_MOVE and returns before Windows enters its modal loop.
            // WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE drive _isDragging and idle timing truthfully.
            beginWindowMove();
        }
        finally
        {
            _surfaceDragQueued = false;
        }
    }

    private void HandleNativeMoveSizeStateChanged(bool active)
    {
        if (_closing)
        {
            _isDragging = false;
            return;
        }
        if (_isDragging == active) return;
        _isDragging = active;
        if (active)
        {
            _idleTimer.Stop();
            // Moving the window is activity, but do not reveal an intentionally hidden strip.
            ExitWindowOpacityIdle();
            return;
        }

        ExitWindowOpacityIdle();
        // Movement has settled: classify snap/work-area geometry once and clear/apply the custom
        // region as needed. SizeChanged kept an existing floating region fitted during resize.
        ApplyCornerModeToHwnd(applyDwmAttributes: false);
        if (_fadeEnabled) RestartIdleTimer();
    }

    // Headless WPF seam: exercise the guarded queued handoff without entering a native modal loop.
    internal void ExecuteSurfaceDragRequestForTests(bool leftButtonDown, Action beginWindowMove)
    {
        _surfaceDragQueued = true;
        ExecuteSurfaceDragRequest(() => leftButtonDown, () =>
        {
            beginWindowMove();
            return true;
        });
    }

    internal bool IsSurfaceDragQueuedForTests => _surfaceDragQueued;
    internal bool IsDraggingForTests => _isDragging;
    internal bool IsIdleTimerRunningForTests => _idleTimer.IsEnabled;
    internal void HandleNativeMoveSizeStateChangedForTests(bool active) =>
        HandleNativeMoveSizeStateChanged(active);
    internal PopoutPresentation PresentationForTests => _presentation;
    internal bool IsFocusedSurfaceActiveForTests => _focusedSurfaceActive;
    internal void ApplyFocusedSurfaceActiveForTests(bool active)
    {
        _surfaceDragAvailable = true;
        ApplyFocusedSurfaceActive(active);
    }

    /// <summary>Live re-apply seam for both the DWM fallback and the large Popout frame radius.</summary>
    internal void ApplyCornerAppearance(DwmCornerMode mode, double popoutCornerRadiusDip)
    {
        _dwmCornerMode = mode;
        _popoutCornerRadiusDip = NormalizeCornerRadius(popoutCornerRadiusDip);
        ApplyCornerModeToHwnd(forceRegionRefresh: true);
    }

    private void ApplyCornerModeToHwnd(bool forceRegionRefresh = false, bool applyDwmAttributes = true)
    {
        if (_closing) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // SourceInitialized applies the initial state
        if (applyDwmAttributes)
        {
            WindowOpacityApplier.SetCornerMode(hwnd, _dwmCornerMode);
            WindowOpacityApplier.SetBorderColor(hwnd, suppress: true); // P1 borderless: no Win11 frame hairline
        }

        var canApplyRegion = RoundedWindowRegionPolicy.CanApply(
            _dwmCornerMode,
            _popoutCornerRadiusDip,
            WindowState == WindowState.Normal);
        // Snap classification crosses several native APIs. Ineligible modes skip it entirely;
        // during a live move/resize, preserve the prior decision and classify once on native exit.
        var shouldApplyRegion = canApplyRegion && (_isDragging
            ? _customWindowRegionApplied
            : !RoundedWindowRegionApplier.IsSnapLike(hwnd));

        if (shouldApplyRegion)
        {
            if (forceRegionRefresh || !_customWindowRegionApplied)
            {
                var regionWasApplied = _customWindowRegionApplied;
                _customWindowRegionApplied = RoundedWindowRegionApplier.Apply(hwnd, _popoutCornerRadiusDip);
                // A stale region is worse than falling back to DWM: after a failed resize/DPI refresh
                // it could crop the live window to the previous bounds. If both replacement and
                // cleanup fail, retain ownership bookkeeping so the next lifecycle event retries.
                if (!_customWindowRegionApplied && regionWasApplied)
                    _customWindowRegionApplied = !RoundedWindowRegionApplier.Clear(hwnd);
            }
        }
        else if (_customWindowRegionApplied)
        {
            if (RoundedWindowRegionApplier.Clear(hwnd)) _customWindowRegionApplied = false;
        }
    }

    private static double NormalizeCornerRadius(double radiusDip) =>
        double.IsFinite(radiusDip) && radiusDip > 0 ? radiusDip : 0;

    /// <summary>
    /// The activity probe covers the WebView2 area WPF can't see. It runs for as long as
    /// something needs it: an idle opacity dip (prevent onset during movement, restore after) or
    /// the auto-hiding strip (top-edge reveal). Off at defaults, so default-look behavior never
    /// pays for it.
    /// </summary>
    private void UpdateActivityProbe()
    {
        var active = WindowOpacityPolicy.Effective(isIdle: false, _constantWindowOpacity, _idleWindowOpacity);
        var idle = WindowOpacityPolicy.Effective(isIdle: true, _constantWindowOpacity, _idleWindowOpacity);
        var wanted = (idle < active || (_stripAutoHide && _fadeEnabled)) &&
                     new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero;
        if (wanted && !_opacityHoverPoll.IsEnabled) _opacityHoverPoll.Start();
        else if (!wanted) _opacityHoverPoll.Stop();
    }

    private void EnterWindowOpacityIdle()
    {
        if (_windowOpacityIdle) return;
        _windowOpacityIdle = true;
        ApplyWindowOpacityToHwnd(animate: true);
    }

    private void ExitWindowOpacityIdle()
    {
        if (!_windowOpacityIdle) return;
        _windowOpacityIdle = false;
        ApplyWindowOpacityToHwnd(animate: true);
    }

    /// <summary>
    /// Activity sensor for the area WPF can't see (the WebView2 child swallows all mouse input):
    /// records the last cursor MOVE over this window so idle onset is prevented during sustained
    /// movement over the video, and restores immediately when movement happens while opacity-idle
    /// — spec 7.3 "hover restores full opacity". A cursor parked on the video still goes (and
    /// stays) idle.
    /// </summary>
    private void OpacityHoverPoll_Tick(object? sender, EventArgs e)
    {
        if (!WindowOpacityApplier.TryGetCursorPos(out var x, out var y)) return;
        var moved = x != _probeCursorX || y != _probeCursorY;
        _probeCursorX = x;
        _probeCursorY = y;
        if (!moved || PresentationSource.FromVisual(this) is null) return;

        var p = PointFromScreen(new Point(x, y));
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) return;
        // Inside our rectangle is not enough: an unpinned popout can sit BEHIND another app, and
        // movement over that app must not count as activity (the idle fade would never engage).
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !WindowOpacityApplier.IsPointOverWindow(hwnd, x, y)) return;
        _lastInWindowCursorMoveMs = Environment.TickCount64;

        // Top-edge band reveals a collapsed strip (spec 7.2: hover restores full chrome).
        if (!_focusedSurfaceActive && _stripAutoHide &&
            p.Y <= FadePolicy.TopEdgeRevealBandDip && ChromeStrip.Visibility != Visibility.Visible)
        {
            OnUserActivity();
            return;
        }

        // Movement elsewhere over the video restores window opacity (spec 7.3) and re-arms
        // idleness WITHOUT popping the strip up — the strip reveals only via its own hover paths.
        if (_windowOpacityIdle)
        {
            ExitWindowOpacityIdle();
            RestartIdleTimer();
        }
    }

    private void ShowControls()
    {
        if (_focusedSurfaceActive)
        {
            _controlsVisible = false;
            ChromeStrip.IsHitTestVisible = false;
            ChromeStrip.Visibility = Visibility.Collapsed;
            return;
        }
        // Un-collapse first (strip auto-hide): the reveal must restore the layout row before the
        // opacity fade-in has anything to fade in.
        if (ChromeStrip.Visibility != Visibility.Visible) ChromeStrip.Visibility = Visibility.Visible;
        if (_controlsVisible && ChromeStrip.IsHitTestVisible) return;
        _controlsVisible = true;
        ChromeStrip.IsHitTestVisible = true;
        AnimateStripOpacity(1.0);
    }

    private void HideControls()
    {
        if (_focusedSurfaceActive) return;
        if (!_controlsVisible) return;
        _controlsVisible = false;
        // Drop hit-testing only once fully faded so a hidden strip can't swallow clicks (Q-8).
        AnimateStripOpacity(0.0, onCompleted: OnHideFadeCompleted);
    }

    /// <summary>Runs when the hide fade finishes (and from the test seam: animation clocks only
    /// tick once a window has rendered, so the Wpf lane drives this directly).</summary>
    private void OnHideFadeCompleted()
    {
        if (_controlsVisible) return;   // re-shown mid-fade: leave it interactive
        ChromeStrip.IsHitTestVisible = false;
        // Strip auto-hide (Phase 4): once fully faded, also height-collapse so the video fills
        // the window; the top-edge hover band or any reveal path restores the row.
        if (_stripAutoHide) ChromeStrip.Visibility = Visibility.Collapsed;
    }

    private void AnimateStripOpacity(double to, Action? onCompleted = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(FadePolicy.FadeDurationMs));
        if (onCompleted is not null) animation.Completed += (_, _) => onCompleted();
        ChromeStrip.BeginAnimation(OpacityProperty, animation);
    }

    // --- Close / return (spec 14) ---

    private void PlayerWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Set this before stopping timers/disposing state so every outstanding await can recognize
        // an intentional shutdown and decline to publish stale state or show failure UI.
        _closing = true;
        _playerInitializationGeneration++;
        _finalReturnPlaybackCaptured = true;
        CaptureReturnWindowState();
    }

    /// <summary>
    /// Also driven by <see cref="CaptureReturnStateNowAsync"/> when the source window brings
    /// playback back without closing the popout first.
    /// </summary>
    private void CaptureReturnWindowState()
    {
        if (_capturedReturn) return;
        _capturedReturn = true;

        _syncTimer.Stop();
        _idleTimer.Stop();
        _shellReadyTimer.Stop();
        _opacityHoverPoll.Stop();
        _returnState.Topmost = Topmost;
        _returnState.FadeEnabled = _fadeEnabled;
        // ForNextLaunch (overhaul Task 4): closing expanded must not make the NEXT popout launch
        // expanded — the capture keeps the prior normal bounds and drops only the maximized flag.
        _returnState.Placement = PlacementMath.ForNextLaunch(WindowPlacementService.TryCapture(this));
    }

    private void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        try { _surfaceDragBridge?.Dispose(); } catch { /* ignore */ }
        try { _playerFirstSurfaceBridge?.Dispose(); } catch { /* ignore */ }
        try { _shellBridge?.Dispose(); } catch { /* ignore */ }
        try { Player.Dispose(); } catch { /* ignore */ }

        PlayerClosed?.Invoke(this, _returnState);
        Log.Info($"Popout Player closed; lastKnownSeconds=" +
                 $"{(_returnState.LastKnownSeconds?.ToString() ?? "unknown")}.");
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Failed to open an external link.", ex); }
    }
}
