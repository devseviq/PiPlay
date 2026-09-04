# Review — PiPlay next-Stable readiness

**Date:** 2026-09-02 · **Objective:** Decide what must change in PiPlay (`main` at `3fa1613`; v0.13.2 build 39 deployed; `--help` unreleased) before the next Stable promotion. · **Decision supported:** which items to fix before the next `Publish-Stable.ps1` run, which to schedule for the release after, and which to leave as documented limitations.
**Mode:** delegated (Wave 1: Journey, Risk & Edge-Case ×2, Functional; Wave 2: none — every Critical/High finding was confirmed by a direct re-read of the cited lines)
**Evidence:** frozen at commit `3fa1613`, clean tree.

| E-id | Location |
|---|---|
| E-1 | `docs/PiPlay_Product_Engineering_Spec.md` |
| E-2 | `docs/DECISIONS.md` |
| E-3 | `docs/YouTube_Compliance.md` |
| E-4 | `docs/AGENTS.md`, `README.md`, `CLAUDE.md`, `docs/CHANGELOG.md` |
| E-5 | `src/PiPlay/MainWindow.xaml.cs`, `src/PiPlay/MainWindow.xaml` |
| E-6 | `src/PiPlay/PlayerWindow.xaml.cs`, `src/PiPlay/PlayerWindow.xaml` |
| E-7 | `src/PiPlay/App.xaml.cs`, `Services/StartupArgumentPolicy.cs`, `StartupDispatcher.cs`, `SingleInstancePipePolicy.cs`, `DispatcherFaultPolicy.cs` |
| E-8 | `src/PiPlay/Services/YouTubeDomBridge.cs` |
| E-9 | `Services/PlayerFirstSurface*.cs`, `PlayerSurfaceDrag*.cs`, `PlayerShell*.cs`, `NavigationPolicy.cs`, `PlayerShell/player-shell.js`, `player.html` |
| E-10 | `Services/ReturnPolicy.cs`, `PopoutTargetResolver.cs`, `PopoutLaunchPolicy.cs`, `AutoPopoutPolicy.cs`, `PlaybackModePolicy.cs`, `PopoutNavigationPolicy.cs`, `YouTubeUrlHelper.cs`, `Models/PlayerReturnState.cs`, `Models/YouTubeTarget.cs` |
| E-11 | `Services/SettingsService.cs`, `PrivacyService.cs`, `BrowserDataClearCoordinator.cs`, `WebViewEnvironmentService.cs`, `ProfileService.cs`, `AppPaths.cs`, `AppChannel.cs`, `ConsecutiveFailureGate.cs`, `LoggingService.cs` |
| E-12 | `scripts/Publish-Stable.ps1`, `DeploySwap.ps1`, `Verify-StableDeploy.ps1`, `Build-PiPlay.ps1`, `PublishLock.ps1`, `Test-PublishMetadata.ps1`, `Test-DeploySwap.ps1`, `Test-PublishLock.ps1`, `Test-LocalCI.ps1`, `Test-UiSmoke.ps1`, `NativeCommand.ps1` |
| E-13 | `.github/workflows/ci.yml` |
| E-14 | `tests/PiPlay.Tests/**` |
| E-15 | Local gate run of `scripts/Test-LocalCI.ps1` at `3fa1613`: `LOCAL CI: PASS`, 1146 tests passed, 0 failed (captured output kept in the review working directory, not committed) |

## Executive assessment

Scope: the Video Popout journey, failure and recovery paths, the YouTube compliance and host-protocol boundary, and the Stable release pipeline, reviewed from source, XAML, scripts, docs and tests at `3fa1613`. The core is sound: 1146 tests pass, lifecycle guards reset in `finally`, the staged swap preserves `PiPlayData` and rolls back, the host protocols validate exact schemas, and the Compact kill switch is a constant. The gaps sit at the edges. Main risks: (1) the deploy root is validated only as "rooted", and the swap deletes every other file in it, so one wrong `PIPLAY_STABLE_ROOT` destroys unrelated data or the repository; (2) the ad-state rule the project wrote in `docs/YouTube_Compliance.md` is not enforced by the host-side page writes it names, and the in-page probe fails open; (3) a transient settings read failure ends with defaults overwriting the user's profiles; (4) a WebView2 process crash has no recovery path. Priority: fix the four release-path items (F-1, F-6, F-7, F-8) before the next publish; they are script-only changes. Land the app-side fixes (F-2 to F-5, F-9, F-10) in the release after, or in this one if it is not time-critical.

## Scope & method

**Boundaries.** Whole-product readiness at `main` commit `3fa1613` (clean tree; `VERSION` 0.13.2, `BUILD_NUMBER` 39 deployed; `--help` work unreleased). In scope: the Video Popout journey (manual and Auto), failure and recovery paths, the page-script / host-protocol / navigation boundary, and the Stable release pipeline. Out of scope: Compact mode (dormant and kill-switched), theme and appearance visuals, Settings window usability, accessibility beyond names and focus on journey controls, and live UI capture (interactive testing is a desk-machine activity; this review ran on the automated-gate machine).

**Evidence.** Source, XAML, scripts, docs and tests as listed, plus one local gate run. No screenshots or runtime traces were produced; every behaviour claim is tied to a source or test location.

**Journeys reviewed.** J1 browse → Pop out video → use Popout → Bring video back or Close → continue in Source. J2 Auto popout on `/watch` → return → no re-pop. J3 settings, profiles, Reset app state, Clear browser data. J4 failure and recovery: WebView2 runtime missing, navigation failure, dispatcher faults, corrupt settings, second-instance hand-off, shutdown mid-popout. J5 release: local gate → PR → `Publish-Stable.ps1` → `Verify-StableDeploy.ps1` → `Test-UiSmoke.ps1` → manual acceptance.

**Specialist work.** Wave 1, four `review-specialist` agents with disjoint scopes: Journey (popout lifecycle), Risk & Edge-Case (failure, recovery, IPC, shutdown), Functional (compliance and protocol boundary), Risk & Edge-Case (release pipeline). Returns were gated on evidence pointers, named impact and confidence, then merged through a controller-owned registry. Every Critical/High finding was re-read at the cited lines before entering this report; several severities were adjusted down after that re-read (recorded per finding).

**Prediction check.** Three top findings were predicted before delegation: the unguarded deploy root (matched, F-1), a return transition that can latch (matched in substance, F-5, at lower severity than predicted), and publish-gate parity with CI (matched, F-6). Four of the top findings were not predicted: F-2, F-3, F-4 and F-7. Delegation added real value on the compliance and recovery lenses; the system map held.

**Limitations.** No live playback, so Q-1 (no double audio) and Q-2 (visible placeholder) remain desk-acceptance items, not review findings. Script behaviour was reviewed by reading, not by running publish or deploy against a real root. Spec §24 items (Q-1 proof, `about:`/`data:`/`blob:` top-level allowance, profile-selector shadow clipping, playlist queue index) are known and unchanged; they are not restated as findings.

## System assessment

**Structure.** One WPF process hosts two WebView2 surfaces over a shared environment and channel-resolved data root. The Source Window (`MainWindow`) owns browsing, the URL box, profiles, settings, and the lifecycle guards `_popoutInProgress`, `_returnInProgress`, `_player`, `_clearingBrowserData`, `_mainWindowClosing`. The Popout Player (`PlayerWindow`) owns the second WebView2, borderless chrome (move, resize, Pin, Fade, Expand, Close), placement persistence and return-state capture. Policies live in small pure classes (`ReturnPolicy`, `PopoutTargetResolver`, `PopoutLaunchPolicy`, `AutoPopoutPolicy`, `NavigationPolicy`, `DispatcherFaultPolicy`, `SingleInstancePipePolicy`) with focused tests, which is why 1146 tests run in seconds without a browser.

**Journey hand-offs.** Pop out reads Source page state through `YouTubeDomBridge`, resolves a target, shows the Source Placeholder, starts a 1 s suppression guard on the Source page, and creates the Popout. Return runs the other way: the Popout captures `PlayerReturnState` on close, the Source applies `ReturnPolicy.Decide` (Navigate, SeekAndPlay, Seek, or Play), and a deferred replay completes the transition after navigation. Context carried across: video id, time, playing state, playlist or mix list, volume, mute, rate. Known gap: playlist queue index (spec §24).

**Dependencies.** WebView2 Evergreen runtime (install/retry panel), YouTube DOM structure (bounded scripts, 5 s timeout, consecutive-failure gate), Windows single-instance mutex plus named pipe, JSON persistence under the data root. Release depends on a clean committed tree, the local test gate, `Build-PiPlay.ps1`, a staged swap that preserves `PiPlayData`, and manifest verification.

**Broad-impact actions.** Clear browser data (destroys the session), Reset app state, Stable publish (replaces the deployed root), tag creation. All are confirmed or guarded except the publish root itself (F-1).

## Findings

### F-1 — Deploy root is validated only as "rooted"; the swap deletes every other file in it · Critical · High · Release pipeline · Verified

**Observation.** `scripts/Publish-Stable.ps1:82-86` rejects only paths that fail `[IO.Path]::IsPathRooted`, which drive-relative (`D:foo`) and root-relative (`\foo`) paths pass. `Invoke-StagedDeploy` in `scripts/DeploySwap.ps1` creates the root if missing, moves every child except `PiPlayData` to `<root>.backup`, moves the staged payload in, and removes the backup on success (243, 268-271, 282). `Repair-InterruptedDeploy` (82-93) deletes every non-`PiPlayData` child when a `.backup` sibling exists and the root lacks a complete payload. No emptiness, marker-presence, drive-root, or repository-overlap check exists in any script. `scripts/Test-DeploySwap.ps1` has no scenario for a non-empty unrelated root (238-241), and spec §21 states no dedicated-root rule.
**Evidence.** [E-12 Publish-Stable.ps1:82-86; DeploySwap.ps1:36-38, 82-93, 243, 268-271, 282; Test-DeploySwap.ps1:238-241] [E-1 §21].
**User impact.** `PIPLAY_STABLE_ROOT` pointing at a directory holding other content loses that content irreversibly on the success path. Pointing at the repository or one of its parents destroys the worktree and `.git`, recoverable only from a remote. Drive roots are refused only as a side effect of the sibling-parent check. Likelihood is low (sole operator, explicit variable); consequence is unbounded.
**System impact.** Rollback cannot help: the backup is deleted after a successful swap.
**Recommendation.** Require `IsPathFullyQualified`; refuse a root equal to, inside, or containing the repository root; require the root to be missing, empty, or to contain `build-info.json` plus the publish marker before the swap loop; add the unrelated-content case to `Test-DeploySwap.ps1`; state the dedicated-root rule in spec §21 and the README.
**Dependencies.** None. **Validation.** New `Test-DeploySwap.ps1` scenario; `ReleaseScriptPolicyTests` shape check for the guard.

### F-2 — The ad-state rule in `YouTube_Compliance.md` is not enforced by the code it names · High · High · Compliance boundary · Verified

**Observation.** `docs/YouTube_Compliance.md:15` forbids writing `currentTime`, changing playback rate, or invoking Next while YouTube reports `ad-showing` or `ad-interrupting`, and says unknown ad state fails closed, anchoring the rule to `YouTubeDomBridge`. The host-side writers `SeekAsync`, `SeekAndPauseAsync`, `SeekAndPlayAsync` and `ApplyPlaybackSettingsAsync` (rate) write unconditionally (`YouTubeDomBridge.cs:154-193`); the strings `ad-showing` and `ad-interrupting` appear only inside the Focused overlay script. That script's `isAdActive` (573-577) returns `false` when the player element is missing or YouTube renames the classes, so unknown state fails open for the in-page seek and Next (consumers 657, 749, 813, 827). The behaviour harness constructs `#movie_player` in every case and covers only the two recognised classes.
**Evidence.** [E-3:15] [E-8:154-193, 573-577, 657, 749, 813, 827] [E-14 YouTubeDomBehaviorTests, harness ad scenario].
**User impact.** The return path (Navigate then replay, `MainWindow.xaml.cs:1940-1952` → `ReplayPendingReturnStateAsync`) seeks and sets rate as soon as a video element reports state; during a pre-roll that element is playing the ad. Whether YouTube honours the write was not observed; the policy the project set for itself is violated either way, silently.
**System impact.** Platform-policy exposure on the operator's signed-in account.
**Recommendation.** Add an ad-state probe to the shared `VideoSelector` prelude returning clear / ad / unknown; make the four writers and the Focused seek and Next no-ops unless clear; for the return replay, wait (bounded) for clear instead of seeking; add unknown-state and ad-state harness cases for every writer.
**Dependencies.** Product decision on what return does during an ad (Open question 1). **Validation.** Harness cases per writer; a search for `ad-showing` in `YouTubeDomBridge.cs` must hit the shared prelude.

### F-3 — Any settings read failure is treated as corruption, and the next Save overwrites the original · High · High · Failure & recovery · Verified

**Observation.** `SettingsService.Load` catches every exception, calls `Quarantine()`, and returns defaults (`SettingsService.cs:52-57`). A transient share violation (indexer, antivirus, backup or sync client) makes `File.ReadAllText` throw; `Quarantine` then fails on the same lock and only logs (128-142), so no copy is kept. The first `Save` of the session replaces the intact original with defaults through `File.Replace` (115-124), and `Save` itself swallows errors (60-72). Spec §12.6 promises quarantine for corrupt JSON; `SettingsServiceTests` cover malformed JSON only (266-276).
**Evidence.** [E-11 SettingsService.cs:39-57, 60-72, 115-142] [E-1 §12.6] [E-14 SettingsServiceTests.cs:266-276].
**User impact.** Profiles, theme, placement and last URL lost with no message and no quarantine file to recover from.
**System impact.** `settings.json` replaced by defaults.
**Recommendation.** Quarantine only on `JsonException` or a null document; on an IO failure keep a load-failed flag that makes `Save` refuse until a successful reload, log once, and tell the user once.
**Dependencies.** Reset and quarantine cleanup paths. **Validation.** Unit test that injects an `IOException` on read and asserts no quarantine and no overwrite.

### F-4 — A WebView2 process failure mid-session has no recovery path · High · High · Failure & recovery · Verified

**Observation.** No `ProcessFailed` handler exists anywhere under `src/PiPlay`; the runtime panel is reachable only from initial load and Retry (`MainWindow.xaml.cs:210-248`). The panel heading and primary button are fixed in XAML as "WebView2 Runtime is required" and "Get WebView2 Runtime" even when the message is the generic "couldn't start the browser component" (`MainWindow.xaml:269-276` vs `MainWindow.xaml.cs:232-237`). Spec §15.4 promises install/retry recovery for a missing or failed WebView2.
**Evidence.** [E-5 MainWindow.xaml.cs:183, 210-248; MainWindow.xaml:269-276] [E-6 PlayerWindow.xaml.cs:249-252] [E-1 §15.4].
**User impact.** A renderer or browser process crash leaves a blank Source with `_browserReady` still true, navigations silently failing, and no explanation; only a restart recovers. The same crash is the only restart-only trigger of F-5.
**Recommendation.** Handle `CoreWebView2.ProcessFailed` on both surfaces and route it to `ShowRuntimeError` with a crash-specific heading and Retry (Retry already re-creates the core and clears a pending return); make the heading and call-to-action conditional on the failure kind.
**Validation.** Extend `RuntimeFailurePolicyTests`; on the desk machine, kill the renderer process and confirm the panel and Retry.

### F-5 — The return transition has no terminal guarantee · Medium · High · Popout lifecycle · Verified

**Observation.** A Navigate-return stores `_pendingReturnReplay` and calls `NavigateInternal` (`MainWindow.xaml.cs:1940-1952`). `NavigateInternal` queues silently when the browser is not ready and swallows a `Navigate` throw (434-445), and `Player_OnClosed` then skips `CompleteReturnTransition` (1881). Every clearing path is a later navigation event or `ShowRuntimeError` (240-248, 262-298, 300-361). While latched, Pop out reads "Returning video..." and the URL box, Back, Home, Reload, Profiles and Settings are disabled (736-758, 1694-1699). Two specialists rated this High; the controller re-read lowers it: `_browserReady` is set false only by `ShowRuntimeError`, which itself completes the transition; a queued URL is consumed on Retry (215-219); and the Source page stays clickable during the transition, so any in-page link clears or completes it. The restart-only case is a dead browser process (F-4). The early `core is null` return in `ReplayPendingReturnStateAsync` (317-318) is the same class and practically unreachable.
**Evidence.** [E-5:215-219, 240-248, 262-298, 300-361, 434-445, 736-758, 1694-1699, 1761-1773, 1876-1881, 1940-1952].
**User impact.** With a dead browser, the whole Source is disabled including Settings; otherwise the latch is cleared by the next navigation.
**Recommendation.** Complete the transition when `NavigateInternal` queues or throws, and bound `_returnInProgress` with a dispatcher-timer deadline that also clears the pending replay.
**Validation.** With `SetBrowserReadyForTests(false)`, a different-video return must leave `ReturnInProgressForTests` false after the deadline.

### F-6 — `-SkipTests` still yields a tag and "RELEASE VERIFIED"; the publish gate is not the CI gate · Medium · High · Release pipeline · Verified

**Observation.** `-SkipTests` skips step 1 (`Publish-Stable.ps1:206-208`), but only `-AllowDirty` and `-AllowVersionBump` feed `NonReleaseReason` (274-283), so `Build-PiPlay.ps1` records release evidence, the tag is created, and `Verify-StableDeploy.ps1` prints "RELEASE VERIFIED" (285). `CLAUDE.md` names only the other two switches as diagnostic-only. Step 1 also runs `dotnet test PiPlay.sln --configuration Debug` directly instead of `scripts/Test-LocalCI.ps1`, so the Node version check and the Release-channel build stage that CI runs are not part of the publish gate.
**Evidence.** [E-12 Publish-Stable.ps1:50, 206-208, 274-283; Verify-StableDeploy.ps1:285; Test-LocalCI.ps1] [E-13 ci.yml] [E-4 CLAUDE.md release stamps].
**User impact.** An operator can publish an untested commit with full release evidence; CI on `main` is the only backstop. Rated Medium because of that backstop; the omission contradicts the script's own comment that diagnostic escape hatches are never release evidence.
**Recommendation.** Make `-SkipTests` a non-release reason; run `Test-LocalCI.ps1` as step 1; list `-SkipTests` with the diagnostic-only switches in `CLAUDE.md`.
**Validation.** `ReleaseScriptPolicyTests` assertion that `-SkipTests` appears in the non-release reasons.

### F-7 — The deployed UI smoke defaults to source output and binds its PASS to nothing about Stable · Medium · High · Release pipeline · Verified

**Observation.** `-ExePath` defaults to `bin\publish\latest\PiPlay.exe` and the first `.EXAMPLE` is the no-argument form (`Test-UiSmoke.ps1:13-18`); the exe check is a non-literal `Test-Path` (26); the PASS asserts no channel, marker or build-info; the screenshot is named by timestamp only (140). Spec §22.2 says the script checks "against the deployed executable"; `CLAUDE.md` forbids source or `bin` output as evidence; the README passes the deployed path explicitly (34).
**Evidence.** [E-12 Test-UiSmoke.ps1:13-18, 26, 140] [E-1 §22.2] [E-4 CLAUDE.md, README.md:34].
**User impact.** A stale development build can produce "SMOKE PASS" and a screenshot filed as deployed evidence. Rated Medium because the documented invocation is correct; the default and the spec wording invite the error.
**Recommendation.** Default to `PIPLAY_STABLE_ROOT\PiPlay.exe` and fail when unset; refuse an exe without the publish marker and Stable channel beside it; embed version, build and commit in the screenshot name; use `-LiteralPath`.
**Validation.** `ReleaseScriptPolicyTests` shape check; one desk run with the new default.

### F-8 — The swap and lock harnesses run in no gate · Medium · High · Release pipeline · Verified

**Observation.** `LocalCiPlanTests` pins exactly five steps (33); CI runs only `Test-LocalCI.ps1` (`ci.yml:50-52`); neither CI nor publish runs `Test-DeploySwap.ps1` or `Test-PublishLock.ps1`; `ReleaseScriptPolicyTests` checks text shape only (235-261).
**Evidence.** [E-14 LocalCiPlanTests.cs:33; ReleaseScriptPolicyTests.cs:235-261] [E-13 ci.yml:50-52] [E-12 Test-DeploySwap.ps1:12-14].
**System impact.** The harness that caught a real data-loss bug (its own header, 12-14) can regress silently; F-1's new scenario would inherit the same gap.
**Recommendation.** Add both harnesses as plan steps in `Test-LocalCI.ps1` (schema bump in `LocalCiPlanTests`).
**Validation.** CI log shows both harness summaries.

### F-9 — A timed-out Clear browser data re-enables Pop out and allows Dispose while the clear still runs · Medium · High · Failure & recovery · Verified

**Observation.** After `PrivacyService.ClearTimeout` the `finally` resets `_clearingBrowserData` while the profile clear continues under the observer task (`MainWindow.xaml.cs:1350-1359, 1377-1384`); `CanStartVideoPopout` checks only that flag (1443-1444); the post-clear navigation to youtube.com is skipped on the timeout branch. `MainWindow_Closing` disposes the browser without consulting `BrowserDataClearCoordinator.IsRunning` (2035-2065; coordinator 12-19). The Dispose part is an interpretation (Medium confidence): the effect of disposing mid-clear was not observed.
**Evidence.** [E-5:1343-1359, 1377-1384, 1443-1444, 2035-2065] [E-11 BrowserDataClearCoordinator.cs:12-19].
**User impact.** A Popout can launch into a session being wiped; the Source stays on a stale signed-in page; closing mid-clear may leave a partial clear where REQ-PRIVACY-02 promised sign-out.
**Recommendation.** Gate Pop out and the closing Dispose on `IsRunning` (bounded wait on close); navigate home when a late clear completes.
**Validation.** Assert the popout guard reads the coordinator; unit test the timeout branch.

### F-10 — A second launch that cannot reach the running instance exits silently · Medium · High · Failure & recovery · Verified

**Observation.** When the mutex is held, `TrySendToExistingInstance` catches every failure and only logs; `Shutdown(0)` follows regardless (`App.xaml.cs:76-84, 199-213`). Triggers: the first instance is exiting (mutex still held, pipe cancelled) or the pipe server is in a back-off window (`SingleInstancePipePolicy.cs:21-30`). The received payload is also not re-validated (A-9).
**Evidence.** [E-7 App.xaml.cs:76-84, 114-117, 192-213; SingleInstancePipePolicy.cs:21-30].
**User impact.** Clicking a YouTube link does nothing and the URL is lost.
**Recommendation.** On hand-off failure retry briefly, then fall back to a normal launch; re-validate the payload with `YouTubeUrlHelper.TryParse` in `OnSecondInstance`.
**Validation.** `AppStartupArgumentTests` case for hand-off failure.

## Cross-cutting themes

1. **Hardened core, unguarded inputs.** The swap, rollback, lifecycle guards and protocols are careful; the values fed into them (deploy root, `-SkipTests`, smoke exe path, pipe payload) are trusted without checks.
2. **Rules declared but not enforced by their anchors.** `YouTube_Compliance.md:15` versus the `YouTubeDomBridge` writers; spec §22.2 versus the smoke default; spec §12.6 versus `Load`; the `Publish-Stable.ps1` header versus step 7 tagging (A-2). Where a document names the code that enforces a rule, a test should pin it.
3. **State machines without deadlines.** The return transition, the clear timeout and the hand-off each depend on a later event that may never come.
4. **What is working well.** Guards reset in `finally`; `PiPlayData` survives swaps; protocols require exact schema, nonce and document token; the Compact switch is a constant; the runtime panel and Retry work for the startup case; settings writes are atomic.

## Action plan

**Immediate (script-only, before the next `Publish-Stable.ps1` run).** F-1 deploy-root guard plus harness case and §21/README rule; F-6 `-SkipTests` as non-release reason and `Test-LocalCI.ps1` as step 1; F-7 smoke default, marker/channel assertion and stamped filename; F-8 harnesses in the gate.
**Near-term (application code, next release).** F-2 shared ad-state probe with fail-closed writers and a bounded wait on return; F-3 quarantine only on JSON failure and refuse Save after an IO load failure; F-4 `ProcessFailed` handling with conditional panel copy; F-5 return-transition deadline; F-9 coordinator-gated popout and close; F-10 hand-off fallback and payload re-validation.
**Structural.** Write the E-3 carve-outs the code relies on (Source suppression while popped out, host-side writes on return); adopt a deadline pattern for cross-surface transitions; surface persistent DOM-bridge failure once in the UI (A-1).
**Validation.** The unit and harness tests named per finding; desk acceptance for Q-1 on ad and autoplay paths and for the Focused button row against YouTube's ad disclosure (Open question 4); a `Test-DeploySwap.ps1` run in CI.

## Open questions

1. **Return during an ad.** Should the return replay wait for the ad to end, apply only play/pause, or skip the seek? This decides the shape of F-2's fix.
2. **Suppression carve-out.** The 1 s Source suppression mutes and pauses whatever is playing on the hidden Source, including an ad. Is that the intended reading of `YouTube_Compliance.md:15`? Document the answer either way (A-7).
3. **`-SkipTests` intent.** If it is diagnostic-only, F-6 is a one-line reason plus a doc line; if it is meant for releases, the header comment and `CLAUDE.md` need to say so.
4. **Desk-only checks.** Q-1 no double audio on ad and autoplay paths, and whether the Focused top button row overlaps YouTube's ad disclosure, need a live page.

## Evidence & confidence

**Verified (direct reads at the cited lines).** F-1 to F-10 code paths; the 1146-test gate run; `_sourceMutedAtPopout` restore on return (refutes a "sticky mute" claim, A-7); `PlaybackModePolicy.CompactPlayerEnabled` constant; protocol exact-schema checks; no `ProcessFailed` handler in `src/PiPlay`.
**Uncertain.** Whether YouTube honours a `currentTime` write during a pre-roll (F-2 mechanism); the effect of `Browser.Dispose()` mid-clear (F-9); the frequency of transient settings-file locks (F-3); whether two sessions publish concurrently in practice (A-3).
**Assumed.** Evidence frozen at `3fa1613`; the deployed Stable matches build 39; the operator publishes from a PR-merged `main`.
**Untested.** Live playback, Q-1, Q-2, Focused overlay against ads, publish and deploy against a real root.

## Appendix — remaining findings

| ID | Finding | Severity | Confidence | Evidence | Recommendation |
|---|---|---|---|---|---|
| A-1 | DOM-bridge failure degrades silently: `ConsecutiveFailureGate` coalesces log lines and never surfaces; on timeout, state read returns null (return resumes at a stale position), suppression returns false, seeks no-op. | Medium | High | E-11 ConsecutiveFailureGate.cs:12-36; E-8:983-1019 | Show one degraded-state indicator on persistent failure. |
| A-2 | Step 6 creates `stable-vX.Y.Z-bN` before step 7's final verification; a step 7 failure leaves the tag, contradicting the header (25-26). Tag is local-only and no doc says so. The next publish's tag preflight blocks a re-publish, so it is not silent. | Low | High | E-12 Publish-Stable.ps1:25-26, 375-381; E-4 CLAUDE.md | Delete the just-created tag on step 7 failure; document local-only. |
| A-3 | Publish lock uses the `Local\` mutex namespace and an un-normalised key, so publishes from different sessions (or trailing-backslash variants) do not exclude each other. | Low | Medium | E-12 PublishLock.ps1:45 | `Global\` namespace and normalised full path. |
| A-4 | `CaptureReturnStateNowAsync` sets `_finalReturnPlaybackCaptured` and stops the sync timer before awaiting; only `RetargetTo` resets. The bridge swallows exceptions, so the failure path is practically unreachable. | Low | High | E-6:755-762, 382-389; E-8:87-116 | Set the flag after a successful capture. |
| A-5 | Auto's `_autoLastHandledVideoId` is set at launch and return but never cleared on Source navigation, so re-opening the same video does not auto-pop. | Low | High | E-5:1555, 1926-1927, 644, 678; E-10 AutoPopoutPolicy.cs:35-37 | Clear the latch when navigation leaves that video id. |
| A-6 | Placeholder button reads "Show popout"; product language is "Show Popout". | Low | High | E-5 MainWindow.xaml:248; E-4 AGENTS.md:25 | Align copy. |
| A-7 | Source suppression mutes and pauses every 1 s without ad gating; E-3 has no carve-out for it or for host-side writes. The "sticky mute" sub-claim is refuted: mute is restored from `_sourceMutedAtPopout`. | Low | Medium | E-8:68-79, 127-131; E-3:15; E-5:1465, 1935, 1949 | Write the carve-out into E-3 (Open question 2). |
| A-8 | Shell protocol `Parse` ignores its version key and accepts unknown fields, unlike the Focused and drag protocols. Compact is dormant and origin-gated, so latent only. | Low | High | E-9 PlayerShellProtocol.cs:98-127 vs PlayerFirstSurfaceProtocol.cs:103-115 | Enforce version and exact schema. |
| A-9 | The pipe server passes the raw payload to `NavigateTo`; the sender validated it, the receiver does not. Any same-session process can make PiPlay navigate or open an external URL. Same-user trust boundary, so Low; the asymmetry with REQ-APP-02 is the defect. | Low | High | E-7 App.xaml.cs:192-196; StartupArgumentPolicy.cs:52-58; E-5:424-463 | `TryParse` in `OnSecondInstance` (with F-10). |
| A-10 | `IsGoogleAuthHost` admits any `google.<2-3 alpha TLD>` and `http://`; tests cover only the extra-label look-alike. | Low | Medium | E-9 NavigationPolicy.cs:46, 71-97; E-14 NavigationPolicyTests.cs:60-73 | Pin an explicit TLD list or require HTTPS. |

**Registry snapshot.** 20 accepted findings (1 Critical, 3 High, 7 Medium, 9 Low); 0 rejected returns; 1 severity conflict (F-5) resolved by controller evidence; severity lowered from the specialist rating on F-5, F-6, F-7, A-2, A-3 and A-4 after the controller re-read; 1 sub-claim refuted (A-7); 0 unresolved conflicts. Known spec §24 items excluded by design.
