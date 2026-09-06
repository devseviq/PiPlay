# PiPlay decisions in force

Supersede an ADR here; do not silently contradict it. New decisions use the next unused `ADR-NNNN` ID.

## ADR-0001 — WPF shell (accepted)

PiPlay is Windows-only WPF hosting YouTube with WebView2. Native chrome owns move/resize, topmost, placement, DPI, focus, and UI Automation names. (`src/PiPlay/PiPlay.csproj`, `MainWindow`, `PlayerWindow`.)

## ADR-0002 — .NET 10 without aggressive packaging (accepted)

Target `net10.0-windows`; keep `Nullable` and `ImplicitUsings` enabled, `SelfContained=false`, `PublishTrimmed=false`, `PublishSingleFile=false`, and no NativeAOT. (`src/PiPlay/PiPlay.csproj`.)

## ADR-0003 — WebView2 Evergreen (accepted)

Use Microsoft WebView2 Evergreen through the package version in `src/PiPlay/PiPlay.csproj`, with no fixed browser executable. Source and Popout share one environment and channel-resolved data root. Missing runtime must surface install/retry recovery. (`WebViewEnvironmentService`.)

## ADR-0004 — Native Popout (accepted)

The borderless Popout owns a second WebView2. Source playback is muted/paused, Source WebView is hidden behind the Source Placeholder, and the shared environment/session is retained. Browser-native PiP is not used. (`MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`.)

## ADR-0005 — One Popout Player (accepted)

Exactly one Popout exists. `_popoutInProgress`, `_returnInProgress`, and `_player` are the lifecycle ownership guards. **Show Popout** activates the existing player; **Bring video back** captures state, closes it, and returns playback. A playable Popout navigation retargets that player. (`MainWindow.xaml.cs`, `ReturnPolicy`.)

## ADR-0006 — No click-through (accepted)

Fade and opacity are visual only. Do not set `WS_EX_TRANSPARENT`, pass through mouse input, or use a transparent WebView. Settings stops at `0.45`; hand-edited persisted values from `0.10` through below `0.45` remain honored. (`WindowOpacityPolicy`, `WindowOpacityPolicyTests`.)

## ADR-0007 — Stable channel and portable data (accepted)

`PiPlayChannel` is baked into assembly metadata; `PIPLAY_CHANNEL` is a test/diagnostic override. `PIPLAY_DATA_ROOT` overrides data location. Otherwise Stable uses `<exeDir>\PiPlayData`, Default uses `%LOCALAPPDATA%\PiPlay`. Each channel has its own per-session mutex; Default and Stable may run side by side. Stable deployment uses `PIPLAY_STABLE_ROOT`, `Publish-Stable.ps1`, and `Verify-StableDeploy.ps1`; `PiPlayData` stays in place during staged replacement. (`AppChannel`, `AppPaths`, `DeploySwap.ps1`.)

## ADR-0008 — Rounded Popout region (accepted)

Only a floating Popout with effective `Round` corners receives the DPI-scaled native region: `22 DIP` for Soft Glass/explicit Round. Resize/DPI refreshes it; maximize/snap clears it and floating restore reapplies it. Keep standard WebView2, `AllowsTransparency=False`, native opacity, and the resize subclass. The region does not promise a curve-following DWM border/shadow; composition hosting remains deferred. (`RoundedWindowRegionPolicy`, `RoundedWindowRegionApplier`, WPF tests.)

## ADR-0009 — Incoming links go to the playback owner (accepted)

A link delivered from outside (startup argument, single-instance hand-off) is revalidated and routed by one receiving decision: queue before browser readiness, navigate the Source when it owns playback, retarget and focus the single Popout for a video link, and retain the newest target through launch/return/clear and for playlist-only links during a Popout. The hidden Source never navigates while a Popout owns playback, so the return identity comparison stays honest; the live Source page still has the last word before a same-video seek. Delivery is acknowledged; a sender without an owner starts only after winning the session mutex. Extends ADR-0005, which covers navigation inside the Popout. (`IncomingLinkPolicy`, `SingleInstanceHandoffPolicy`, `MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`, `App.xaml.cs`.)

## ADR-0010 — WebView2 process failures recover per surface behind one policy (accepted)

Both windows share one `CoreWebView2Environment`, so a browser-process exit kills every core at once and a renderer exit kills one page. One pure policy classifies the failure and decides for both surfaces: reload a dead renderer on the live core, recreate the Source control in place for a dead browser process, log self-recovering helpers, coalesce duplicates of a recovery already running, and stop after a bounded number of consecutive recoveries so a crash loop ends in a visible failed state with Retry rather than a flicker. The Popout takes the scoped cut: it reloads its page after a renderer exit and closes after a browser-process exit, handing playback back to the Source with its last polled sample and a flag that makes the Source recover before acting on the return. Recreating the Popout in place was rejected for this pass because its shell bridge, placement, and fade state would all need re-establishing on a new core while a return is the already-tested path. Any return in flight ends when the browser fails, and one return transition is bounded in time. (`WebViewProcessFailurePolicy`, `MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`, `PlayerReturnState`.)
