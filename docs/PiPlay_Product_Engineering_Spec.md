# PiPlay product and engineering specification

Actual behavior is the source and tests named beside each contract. Architecture: [`DECISIONS.md`](DECISIONS.md). Theme values: [`Theme_Preset_Differences.md`](Theme_Preset_Differences.md). Page-script policy: [`YouTube_Compliance.md`](YouTube_Compliance.md). Product language: [`AGENTS.md`](AGENTS.md). Not a media host or replacement player.

## 3. Quality requirements

| ID | Contract | Evidence anchor |
|---|---|---|
| Q-1 | Source audio stays muted/paused while the Popout owns playback. | `YouTubeDomBridge.SuppressPlaybackAsync`; `MainWindow.xaml.cs` 1 s guard. |
| Q-2 | Return preserves current video, playlist/mix context, timestamp, play state, volume/mute/rate, and window state where available. | `PlayerReturnState`, `ReturnPolicy`; `ReturnPolicyTests`. |
| Q-3 | YouTube DOM work is centralized, best-effort, schema-safe, and non-essential to app survival. | `YouTubeDomBridge`; `YouTubeDomBehaviorTests`, `RuntimeFailurePolicyTests`. |
| Q-4 | Use WebView2 Evergreen. | `src/PiPlay/PiPlay.csproj`, `WebViewEnvironmentService`. |
| Q-5 | Do not inspect credentials, download media, alter ads/monetization, bypass restrictions, or remove required YouTube controls/branding. | `YouTube_Compliance.md`; `NavigationPolicy`, `PlayerFirstSurfaceProtocol`. |
| Q-6 | URL/settings/navigation/network/runtime/script failures recover without a crash or stuck duplicate work. | `SettingsService`, `ConsecutiveFailureGate`; `RuntimeFailurePolicyTests`, `SettingsServiceTests`. |
| Q-7 | Move, resize, Pin, close/return, monitor restore, and DPI behavior are native and recoverable. | `WindowPlacementService`, `BorderlessResizeHitTestPolicy`, `RoundedWindowRegionPolicy`; WPF tests. |
| Q-8 | A visible player remains directly interactable at every fade or opacity setting. | `WindowOpacityPolicy`, `PlayerSurfaceDragProtocol`; `WindowOpacityPolicyTests`, `PlayerSurfaceProtocolTests`. |

## 5. Visual and interaction identity

Source controls: navigation, URL/search, profiles, Pin, Auto, Settings, transfer, Show Popout. Popout controls: Settings, Fade, Pin, Expand/Restore, Close, move handle. Every opened popup, menu, dropdown, item container, and tooltip uses the dark theme; every icon-only control has an accessible name. (`MainWindow.xaml`, `PlayerWindow.xaml`, `ControlStyles.xaml`, UI tests.)

Settings opens with Appearance, followed by Popout behaviour, then Privacy. Appearance previews live; **Done** keeps changes, while **Cancel**, close, and Escape discard the preview. Keyboard focus is visible on chrome controls and settings choices; sliders retain native range and keyboard commands. Corner choices are Follow theme, Square, Small round, and Round, independently of opacity. Soft Glass retains its translucent preset defaults. (`SettingsWindow.xaml`, `ControlStyles.xaml`, `PresentationPolishTests`.)

## 6. Auto, Fade, and Pin

### 6.1 Auto

Off by default; eligible only for a playing `/watch` video. Shorts, embeds, home, search, settings, history, and login do not auto-pop. Launch/return uses one identity gate so the returned video is not immediately re-popped. (`AutoPopoutPolicy`, `AutoPopoutPolicyTests`.)

### 6.2 Fade

Popout chrome visibility only. Delay presets: Short `1500 ms`, Normal `2500 ms`, Long `4000 ms`. (`FadePolicy`, `PlayerAppearancePolicy`, `FadePolicyTests`.)

### 6.3 Pin

Source and Popout Pin values are independent. Source Pin is suspended while the Popout owns playback and restored on return. (`MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`, `PlayerPinAffordanceTests`.)

## 7. Opacity and transparency

### 7.1 Controls fade

Pointer, keyboard, focus, pause, and movement recovery apply to the Popout chrome; the strip never becomes a second input mode.

### 7.2 Chrome auto-hide

When enabled, the idle-hidden strip collapses so the video fills the window; top-edge reveal restores it. Fade off keeps it visible. (`PlayerWindow`, `XamlInvariantTests`.)

### 7.3 Whole-window opacity

Active and idle opacity apply to the whole Popout; the Source title backdrop may also use the active wash. UI floor `0.45`; hand-edited persisted values from `0.10` through below `0.45` remain honored. Idle never exceeds active. (`WindowOpacityPolicy`, `WindowOpacityPolicyTests`.)

### 7.4 Click-through

Never set `WS_EX_TRANSPARENT`, transparent hit testing, or a transparent WebView. (`WindowOpacityApplier`, ADR-0006.)

## 8. Non-goals

No media download/re-hosting, ad blocking, restriction bypass, multiple Popouts, browser-native PiP, click-through, transparent WebView, default crop, required global hotkeys, cross-platform build, public telemetry, installer/auto-update, or credential inspection.

## 9. Technical direction

`net10.0-windows` WPF, nullable, implicit usings; `PublishTrimmed=false`, `PublishSingleFile=false`, `SelfContained=false`. WebView2 package in `src/PiPlay/PiPlay.csproj`; SDK in `global.json`; `PerMonitorV2` in `src/PiPlay/app.manifest`. (ADR-0001–0003, ADR-0007.)

- **REQ-APP-01:** one instance per channel/session; a second launch hands its request to the existing instance and never contends for the same WebView2 root. The receiving instance revalidates the payload with `YouTubeUrlHelper.TryParse` and routes it to the playback owner (ADR-0009): queued before browser readiness, Source navigation with no Popout, retarget-and-focus of the existing Popout for a video link while the hidden Source stays put, and retention (latest wins, shown in the address bar and placeholder note) for playlist-only links during a Popout and for any link arriving during launch, return, or Clear browser data; no link only activates. The running instance answers `accepted`, `rejected` (unsupported payload), or `unavailable` (closing); the sender exits after `accepted`, reports a rejected link, and otherwise starts as a replacement only after winning the session mutex, else reports the unresponsive instance. (`IncomingLinkPolicy`, `SingleInstanceHandoffPolicy`, `SingleInstancePipeTransport`, `IncomingLinkPolicyTests`, `SingleInstanceHandoffTests`, `MainWindowLifecycleTests`.)
- **REQ-APP-02:** exact `--help`, `-h`, and `/?` startup arguments show native executable usage and exit successfully before logging, single-instance election or handoff, settings, WebView2, or window creation. Help wins over every other argument and creates no persistent application state. Outside help, the first argument accepted by `YouTubeUrlHelper.TryParse` is handed to normal startup verbatim; unsupported arguments are ignored.

## 10. Playback modes and presentation

### 10.1 Normal page mode

Default: real YouTube watch page. Valid `PL...` and `RD...` list IDs remain in Normal watch URLs and return context. (`YouTubeUrlHelper`, `YouTubeUrlHelperTests`.)

### 10.2 Compact mode

Dormant: `PlaybackModePolicy.CompactPlayerEnabled` is `false`. Compact minimum `480 x 270` DIP; Normal `320 x 180` DIP. Compact builders omit auto-generated `RD...` lists. (`PlaybackModePolicy`, `PlaybackModePolicyTests`.)

### 10.3 Local Compact shell

Served from `https://piplay.local/player.html`. Isolated from Source navigation. Shell failure falls back to Normal without creating another player. (`PlayerShell*`, `WebViewEnvironmentService`, `PlayerShellAssetTests`.) A shell web message is accepted only when its source origin — scheme, host, and port — is exactly the shell origin; any other source is dropped before the payload is parsed and is logged once per bridge with the origin redacted. (`PlayerShellProtocol.IsTrustedShellSource`, `PlayerShellBridge`, `PlayerShellProtocolTests`.)

### 10.4 Standard and Focused

Standard is default. Focused overlay: [`YouTube_Compliance.md`](YouTube_Compliance.md). (`YouTubeDomBridge`, `PlayerFirstSurfaceProtocol`, `PlayerSurfaceProtocolTests`.)

## 11. Runtime coordination

One WPF dispatcher owns native/window state. Launch, return, navigation, and page calls are generation- or single-flight-guarded. Normal Popout DOM sync `250 ms`; Source suppression `1 s`; normal-page DOM execution `5 s`; connected single-instance client pipe payload `2 s`; hand-off UI dispatch `5 s`, acknowledgement wait `3 s`, replacement mutex election `3 s`, pipe worker shutdown wait `2 s`. Shutdown stops accepting hand-offs, waits for the pipe worker, drains the log, then releases the session mutex. Timers stop on close/navigation. (`MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`, `YouTubeDomBridge`, `SingleInstancePipePolicy`, `SingleInstanceHandoffPolicy`, `RuntimeFailurePolicyTests`, `SingleInstanceHandoffTests`.)

## 12. Component contracts

### 12.1 MainWindow / Source Window

Owns Source browsing, navigation/profile commands, launch/return, placeholder, and shared Settings. Does not parse URLs, embed raw JavaScript, or write settings directly.

### 12.2 PlayerWindow / Popout Player

Owns Popout chrome/playback, Pin/Fade/appearance, placement, state polling, and the close report. Does not own Source navigation, profiles, or the global Settings transaction.

### 12.3 WebViewEnvironmentService

Owns one environment, data folder, missing-runtime recovery, and virtual-host mapping.

### 12.4 YouTubeUrlHelper

Parses supported YouTube video/share/Shorts/embed/watch-playlist URLs and builds normalized watch or shell targets. Valid list IDs are retained; malformed IDs produce a non-blocking fallback reason. (`YouTubeUrlHelperTests`.) `StartupArgumentPolicy` uses the same parser for command-line launch arguments; it accepts the first supported target verbatim and otherwise starts without one. (`AppStartupArgumentTests`, `StartupArgumentPolicyTests`.)

### 12.5 YouTubeDomBridge and host protocols

All normal-page JavaScript belongs in `YouTubeDomBridge`. Host requests are exact-schema, versioned, nonce/document-token checked, source checked, and trusted-input gated. Focused actions and drag-message rules: [`YouTube_Compliance.md`](YouTube_Compliance.md).

### 12.6 SettingsService

Schema `4`, sanitization/migration/recovery, atomic persistence. Corrupt `settings.json` is quarantined with a timestamp; quarantines older than 30 days are deleted. A read IO failure (lock, permissions, disk) is not corruption: SettingsService leaves the file untouched in place and refuses every save until a load succeeds or the user explicitly resets, so defaults never overwrite unread data. Only the read itself can mark a file unread; a file whose bytes were observed but did not parse is quarantined, not blocked, and the flag follows the file's full path. `Save` reports saved, refused-unread, or failed; the Source shows a one-time title-bar hint, **Settings not saved**, on the first refusal and never a modal for it. (`SettingsService`, `SettingsServiceTests`, `MainWindowLifecycleTests`.)

## 13. Video Popout lifecycle

### 13.1 Preconditions

Browser ready; no launch, return, clear, or shutdown in progress; no player exists; target is a supported video or playlist page. A playlist-only page may use its first rendered playable item. (`PopoutTargetResolverTests`.)

### 13.2 Launch

Capture Source state and target, acknowledge mute+pause, hide Source WebView, show the placeholder, and create/navigate the single Popout with the shared environment. A first item adopted from a playlist-only page starts at zero; an unrelated miniplayer/preview timestamp is never carried into it. Failure restores Source visibility and captured playback state. (`MainWindow.xaml.cs`, `PopoutTargetResolverTests`.)

### 13.3 Source Placeholder

While active, disable Source navigation, URL, profile, and profile-action commands; an incoming link does not navigate the hidden Source either (REQ-APP-01). Auto-off and both recovery actions remain available. **Show Popout** and **Bring video back** remain separate. Must not leave WebView content bleeding through. (`MainWindow.xaml.cs`, XAML tests, `MainWindowLifecycleTests`.)

### 13.4 Race gate

Returns when the browser is not ready, launch/return/clear/shutdown is active, or a Popout already exists. (`MainWindow.xaml.cs`, `MainWindowLifecycleTests`.)

### 13.5 Failure

Hide the placeholder, restore Source, restore captured state where possible, report a concise error, suppress repeated failure noise. (`ConsecutiveFailureGate`, `RuntimeFailurePolicyTests`.)

## 14. Return and close

Return state is nullable; zero is a valid timestamp. Known live Popout state wins over launch fallback. Same-video return seeks; a different video/list navigates with context before replaying timestamp, play state, volume/mute, and rate where YouTube permits. A same-video decision is re-checked against the video the Source actually shows: if the Source moved, a known returned video navigates and an unknown one drives nothing. The Popout pairs each playback sample with the navigation and return identity it was read for; a retarget or SPA identity change discards the earlier sample, so the new video returns with unknown time rather than the previous video's. Return restores Source placement/Pin and arms Auto de-dup before asynchronous replay. A return that lands while YouTube reports an ad applies volume/mute and the play/pause intent, then holds the seek and rate for a bounded wait (`12 × 500 ms`), writing them only if the page reports clear while the replay is still for the video the Source shows; after the bound, or if the Source moved, the page keeps its own position and native controls. One return transition is bounded by `20 s`: a navigation whose completion never arrives, or a replay that hangs, releases the Source commands without replaying state. (`PlayerReturnState`, `ReturnPolicy`, `ReturnReplayAdPolicy`, `ReturnPolicyTests`, `ReturnReplayAdPolicyTests`, `MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`, `WpfRuntimeTests`, `MainWindowRecoveryTests`.)

## 15. WebView and navigation

### 15.2 Navigation and new windows

Source and Popout allow YouTube plus the supported Google sign-in/account hosts. Unrelated HTTP(S) targets open externally or are blocked by surface policy. The allowlist is a navigation guardrail, not a malicious-page security boundary. (`NavigationPolicy`, `NavigationPolicyTests`.)

### 15.3 Navigation failure

Failure keeps a safe URL and exposes retry behavior. (`MainWindow.xaml.cs`.)

### 15.4 Runtime failure

Missing or failed WebView2 exposes install/retry recovery. (`WebViewEnvironmentService`, `RuntimeFailurePolicyTests`.)

WebView2 process failures are classified once for both surfaces (ADR-0010): a renderer exit reloads the page on the live core, a browser-process exit recreates the Source control in place on the shared environment and returns to the last known page, and helper exits or an unresponsive renderer are logged only. One recovery runs at a time (a recreate coalesces every duplicate; a reload coalesces only another renderer exit), and after three consecutive automatic recoveries within `60 s` the Source shows the failed state, **The browser keeps failing**, with Retry, which resets the budget and takes the same recreate path. The Source panel names each state (**YouTube stopped responding**, **The browser component stopped**, **Restarting the browser**) and leaves on the first completed navigation of the live core; the failed state stays until Retry. The Popout reloads once per renderer exit behind a one-line notice and closes on a browser-process exit, returning playback with its last polled sample; the return state carries the failure so the Source recreates before acting on it, whichever window heard of the exit first. Any return in flight when the browser fails ends immediately; a return that arrives while the browser is restarting or failed is queued as the page the recreated browser opens. (`WebViewProcessFailurePolicy`, `WebViewProcessFailurePolicyTests`, `MainWindowRecoveryTests`, `MainWindow.xaml.cs`, `PlayerWindow.xaml.cs`.)

An unhandled dispatcher exception is logged and recovered from behind at most one message box at a time; a repeat of the same fault signature (exception type plus throw site) within `10 s` of the last dialog is logged only. Out-of-memory, stack-overflow, access-violation, and SEH faults are logged, get one dialog, and are then left to terminate the process rather than marked handled. A fault raised once shutdown has started is logged without a dialog and does not block the exit. (`DispatcherFaultPolicy`, `App`, `RuntimeFailurePolicyTests`.)

## 16. Window behavior

### 16.1 Size and DPI

Keep `UseLayoutRounding=False` and `AllowsTransparency=False` on top-level windows. (`PlaybackModePolicy`, XAML tests.)

### 16.2 Dragging

Popout chrome strip is `44 DIP` (`PlayerWindow.xaml`). Trusted primary mouse/pen drag on passive surface pixels begins only after `SystemParameters.MinimumHorizontalDragDistance` / `MinimumVerticalDragDistance`; below it the event remains a click. Interactive controls, ads, menus, captions, timelines, links, and overlays are excluded. Drag messages contain no coordinates. (`PlayerSurfaceDragProtocol`, `PlayerSurfaceScriptTests`, `PlayerWindow.xaml.cs`.)

### 16.3 Resizing

Native `12 DIP` resize band and `96 DIP` diagonal reach; not a `96 x 96` content-stealing square. (`BorderlessResizeHitTestPolicy`, tests.)

### 16.4 Multi-monitor behavior

`PerMonitorV2` is required; restore the prior monitor when available, otherwise clamp to visible work area. Placement persists screen pixels (`CoordinateSpace = "screen"`); the `WINDOWPLACEMENT` workspace offset is converted at the Win32 boundary and unmarked saved values are read as the legacy workspace-relative capture. (`WindowPlacementService`, `PlacementMath`, `PlacementMathTests`, WPF tests.)

## 17. Profiles and appearance ownership

Profiles contain a name, URL, optional mode/presentation/accent/Pin/Fade values, and placement. Non-null profile fields override global values; unset fields inherit. Duplicate names prompt overwrite/rename, URLs validate, and broken values fail gracefully. (`ProfileService`, `ProfileServiceTests`.)

## 18. Logging

Queue `4096` entries, batches `64 KiB`, failed-batch retention `512 KiB`, rotation near `1,000,000` bytes, files `logs\piplay.log` and one `.1` backup under the data root. Never log cookies, authorization headers, credential-bearing URLs, secrets, or unsanitized search text. (`LoggingService`, `LoggingServiceTests`, `AppPaths`.)

## 19. Security and privacy

No telemetry, analytics, crash upload, or credential collection (`PrivacyService`, `LoggingService`). Under the ADR-0007 data root:

| Data | Location | Contents |
|---|---|---|
| App state | `settings.json` | Schema `4` |
| Diagnostics | `logs\piplay.log` | plus one `.1` backup |
| Browser profile | `WebView2UserData\` | cookies, cache, permissions, YouTube/Google session; shared by Source and Popout |

**Reset app state** replaces app settings with defaults, removes stale settings quarantines, and does not touch browser data or logs. **Clear browser data** is separate and confirmed: every playback gate closes and the Popout leaves before the clear starts, then `ClearBrowsingDataAsync(AllProfile)` runs single-flight with a `30 s` foreground status wait. The underlying browser clear determines when the session is actually gone: while it runs, the Pop out button reads **Clearing browser data...**, Source navigation and Auto stay gated, an incoming link is retained, and a second clear is refused; Settings stays usable after the foreground wait. Late completion returns to the dispatcher once: success shows the signed-out YouTube home and reopens the gates, failure reopens them and leaves Clear retryable, and neither shows a further prompt after the timeout notice. Closing does not wait for a running clear and drops its late completion. (`PrivacyService`, `BrowserDataClearCoordinator`, `MainWindow.xaml.cs`, `PrivacyServiceTests`, `MainWindowClearDataTests`.)

## 20. Accessibility and usability

Every icon-only control has an accessible name and keyboard focus. Pin/transfer names state the next action. `Ctrl+L` and `F6` focus the Source URL while Source commands are available. (`MainWindow.xaml`, `PlayerWindow.xaml`, `MainWindow.xaml.cs`, UI tests.)

## 21. Packaging and release

SND-HOST owns source, builds, and publication; SND-DESK requires no repository and accepts packages only from GitHub Releases. Exact-source Stable publishing requires committed `VERSION`, `BUILD_NUMBER`, a clean tree, a Stable deployment root supplied by `PIPLAY_STABLE_ROOT` or `-DeployRoot`, and an active, exclusion-free GitHub tag ruleset for `refs/tags/stable-v*` that restricts updates and deletions with no bypass actors. The `PIPLAY_RELEASE_POLICY_TOKEN` Actions secret holds a fine-grained token scoped only to `devseviq/PiPlay`, with Administration write access and no Contents permission. Write access is required to expose ruleset bypass actors; the workflow uses this token solely to inspect tag policy, while `github.token` performs publication. Pushing the resulting `stable-vX.Y.Z-bN` tag produces a permanent GitHub Release ZIP from that exact tag; the workflow checks the provider rule before building and again immediately before publication. Manual `Publish test download` dispatch creates a uniquely tagged GitHub prerelease ZIP and SHA256 file for the selected exact commit. It atomically creates and verifies the tag before uploading a draft, verifies it again before making the prerelease visible, and requires the same exclusion-free/no-bypass protections for `refs/tags/test-*`. Test prereleases are explicitly non-release evidence and are never automatically deleted. Both downloaded payloads contain `scripts/Test-DownloadedPackage.ps1`, which binds caller-supplied GitHub identity to their complete manifest inventory, hashes, binary identity including the baked Stable channel, clean-source metadata, and test-versus-release evidence before UI smoke. Each ZIP is extracted and reverified with the trusted checkout verifier before publication. Signing is optional through `-SignScript` and runs before hashes. (`Build-PiPlay.ps1`, `Publish-Stable.ps1`, `Verify-StableDeploy.ps1`, `.github/workflows/ci.yml`, `.github/workflows/test-release.yml`, `.github/workflows/release.yml`.)

## 22. Quality gates

### 22.1 Automated contract coverage

Automated gate: `scripts/Test-LocalCI.ps1`.

### 22.2 Deployed UI smoke

`scripts/Test-UiSmoke.ps1` checks `PopOutButton`, `UrlBox`, `CloseButton`, `ProfilesCombo`, and `SettingsButton` against the selected executable, uses an isolated data root, foregrounds the real HWND in a per-monitor-DPI-aware capture context, and rejects blank/uniform frames. Downloaded packages enter it only through the standalone package verifier, with data and evidence paths outside the immutable package root. Needs an interactive desktop and WebView2.

### 22.3 End-user acceptance

Listen for duplicate audio through Popout launch/return (Q-1). Exercise one real playlist/mix return when available. Record unavailable account/ad/profile states as not run.

## 23. Deferred scope

Compact revival (also needs shell timeout/timestamp behavior, RD-list policy, and deployed acceptance), alternate main-window layouts, chrome-only/video-opaque transparency, curve-following outer border/shadow styling, tray mode, optional global hotkeys, and import/export.

## 24. Unresolved work

- **Q-1 live audio proof:** Source suppression reasserts every `1 s` (`MainWindow.xaml.cs`). Tests and visual smoke cannot certify no brief acoustic overlap during ads, autoplay-next, SPA rerenders, or `start_radio=1`. On Stable v0.13.2 b39 a plain-video pop-out and return produced no overlap by ear; those other paths remain unobserved.
- **Runtime-scheme policy:** `NavigationPolicy.IsAllowed` permits top-level `about:`, `data:`, and `blob:` on both surfaces (`NavigationPolicyTests`). No recorded product need for top-level `data:`.
- **Profile-selector shadow clipping:** popup shadow inset in `Theme/ControlStyles.xaml` is provisional (`Margin="0,4,20,20"`) until opened in a verified Stable build across applicable themes.
- Exact playlist queue position is not part of `PlayerReturnState`; return preserves video/list IDs, not an index contract.

## 26. Implementation constraints

### 26.3 Video selector

`YouTubeDomBridge`:

```js
document.querySelector('#movie_player video.html5-main-video') ||
document.querySelector('video.html5-main-video') ||
document.querySelector('video')
```

### 26.4 Atomic settings save

Write and flush a temporary file, then `File.Replace` when the destination exists, else `File.Move`; never `File.Copy` onto live settings. (`SettingsService.AtomicWrite`.)
