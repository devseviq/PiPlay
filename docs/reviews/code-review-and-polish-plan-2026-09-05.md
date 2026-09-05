# PiPlay code review and polishing plan

Reviewed 2026-09-05. Replaces `review-controller-2026-09-05-grok-cli-eval.md` following a fresh comparison with source, tests, the preserved Grok diff, and Microsoft API documentation.

The historical fix bundle should not be applied as written. The useful findings are the second-launch routing gap, missing browser-process recovery, unguarded return-time page writes, and several smaller lifecycle defects. A fresh native probe also confirms an endpoint issue in the rounded region. The next polishing pass should make playback ownership and recovery reliable, then finish the visible states and native edges.

## Baseline and verification

| Surface | Verified state for this review |
|---|---|
| Initial review baseline | Local `main` at `9f514c8f843309a7d6f8cf07d15f26d4450f58c5`. Tracked files were clean; the replaced review was untracked. |
| Consolidated application source | In the authorized follow-up on the same date, local `main` fast-forwarded to freshly fetched `origin/main`, `ba520ebeb856ac9a09db562968b3bd6a64630b69`. The redundant worktree was removed after verification; one primary worktree and one local branch, `main`, remain. Installed state was not changed or verified. |
| Existing visual work | The consolidated checkout includes the Settings hierarchy, explicit Cancel button, RGB labels, dark sliders, keyboard focus rings, theme-sized Popout controls, and **Show Popout** wording. These were absent from the initial review baseline. |
| Functional comparison | `git diff 9f514c8..ba520eb -- src/PiPlay/MainWindow.xaml.cs src/PiPlay/PlayerWindow.xaml.cs src/PiPlay/Services` is empty. The application findings below therefore also apply to those files at the later commit. |
| Initial automated gate | `pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1` at `9f514c8`: **PASS**, 1,146 tests passed, zero failed/skipped; Release build zero warnings/errors. SDK 10.0.400, runtime 10.0.11, Node 26.7.0. Version/build stamps stayed 0.13.2/39. |
| Consolidation gate | The same full command on the primary checkout after fast-forwarding to `ba520eb`: **PASS**, 1,155 tests passed, zero failed/skipped; Release build zero warnings/errors. Same SDK/runtime/Node and unchanged 0.13.2/39 stamps. |
| Additional probes | Fifteen malformed-input cases against freshly compiled checked-in parser sources; return-policy decisions; native GDI region membership at three sizes. Results below. |
| Limits | No attended playback, audible-overlap measurement, live renderer crash, monitor/taskbar manipulation, or new visual acceptance. The two gates verify their named source baselines; neither verifies an installed package. |

Product authority remains the [specification](../PiPlay_Product_Engineering_Spec.md), [accepted decisions](../DECISIONS.md), [theme values](../Theme_Preset_Differences.md), and [page-script policy](../YouTube_Compliance.md). This document proposes work; it does not change those contracts. The [September 2 readiness review](review-controller-2026-09-02-piplay-readiness.md) remains a dated source of related findings.

## What survives the old review

| Grok item | Current conclusion | Action |
|---|---|---|
| 1. Malformed `%` crashes startup | The alleged crash is refuted for the fifteen tested inputs on .NET 10.0.11. Valid video links can retain the video while ignoring malformed offsets; malformed IDs are rejected. This is not a proof that arbitrary execution can never throw. | Add parser regression cases with PP-01; do not adopt Fix A. |
| 2. WebView2 process failure | Confirmed missing application handling: no `ProcessFailed` or `BrowserProcessExited` subscriptions. Recovery depends on the failure kind; a permanent blank screen is not the outcome of every process failure. | PP-02; overlaps readiness F-4. |
| 3. Mutex released before teardown finishes | Release-before-log-drain ordering is confirmed. Actual profile contention and pipe collision were not reproduced. Moving the release does not by itself prove browser processes have exited. | PP-05, low-severity shutdown hardening. |
| 4. Handoff during close | There is no receiving-side shutdown rejection, and the pipe has no delivery acknowledgement. The narrow dispatcher/teardown race is plausible, not a reproduced crash. | PP-05; overlaps readiness F-10. |
| 5. Handoff bypasses Source freeze | Confirmed routing and identity defect in code. With A popped out, incoming B reaches Source navigation while return still compares against launch identity A. Live wrong-page playback remains unobserved. | **PP-01, High.** |
| 6. Capture races retarget | Confirmed missing generation/identity check around the final capture await. Retarget or SPA identity changes can pair a new ID with an old playback sample. | PP-04; overlaps readiness A-4. |
| 7. Return seeks without an ad gate | Confirmed: host seek and playback-rate writers do not enforce the project's declared clear/ad/unknown rule. No claim is made that YouTube accepts the writes. | **PP-03, High**; overlaps readiness F-2. |
| 8. Placement coordinate spaces | Confirmed mixed coordinate inputs for ordinary top-level windows. Monitor selection during capture also uses the saved rectangle without conversion. The visible shift depends on taskbar position, window style, and clamping. | PP-07; Low pending a live reproduction. |
| 9. Clear-data timeout | Confirmed: the UI wait resets `_clearingBrowserData` while the coordinator can remain running; the late observer logs completion without refreshing the Source. | PP-06; Medium, overlaps readiness F-9. |
| 10. Double-click Retry duplicates handlers | Not demonstrated. The panel containing Retry collapses before the first await, so the alleged ordinary double-click route is not established. Absence of a reproduced route does not prove re-entry impossible. | Design and test initialization ownership with PP-02; do not import Fix C. |
| Extra: top-level `data:`/`blob:` | Allowed deliberately by `NavigationPolicy` and documented in spec section 24. The preserved material does not demonstrate an exploit. | Leave as a separately scoped policy question; do not silently change navigation policy during polish. |
| Extra: rounded Popout region | Native probes confirm the final right/bottom pixels are excluded with the current arguments. Different bounds sources for snapping and clipping are not independently proof of a defect. | PP-08; Low. Visual impact still needs capture. |

The preserved `piplay-mainwindow.diff` contains Fixes B and C, with no tests. Fix A survives only as a description in `piplay-fixes-verified.md`. The GrokHQ evidence README confirms that the original working copies and raw build/test transcripts were discarded; their historical pass/failure claims cannot be rerun or attributed conclusively to an environment issue.

| Proposed historical fix | Disposition |
|---|---|
| A: catch `UriFormatException` per query pair | No reproduced need for the tested malformed escapes, and the actual hunk is unavailable. Pin the intended parser outputs instead. |
| B: return immediately while Source is suspended | Prevents the hidden navigation by silently discarding the requested URL. Replace it with explicit routing/queueing at the incoming-request boundary. Its interaction with future recovery depends on that recovery design. |
| C: in-flight flag plus permanent handlers-attached flag | Single-flight initialization is a reasonable design tool. A window-lifetime boolean is insufficient if a new core/control is created in the same window. Attach/detach by actual core lifetime and test the chosen recovery path. |

Further corrections to the replaced document:

- `MainWindow.xaml.cs:1976` is `SeedPopoutReturnForTests`, not production retarget logic. Production `_popoutSourceVideoId` is set at launch; Popout retarget updates the player's return identity.
- A one-second suppression timer is a scheduling interval, not a guaranteed one-second upper bound on duplicate audio. Dispatch can be delayed and the DOM operation can fail or time out.
- Cancelling the pipe and ignoring a queued delegate does not recover an already-sent URL. Delivery acknowledgement and safe single-instance election need to be considered together.
- Region tests are feasible: the repository already has geometry tests and native `PtInRegion` seams. Existing assertions cover corners/interior, not the last right/bottom pixel.

## Actionable application backlog

Each item below is proposed and unimplemented. P1 belongs in the next reliability candidate; P2 can follow as a small focused change. Severity describes the user consequence; priority describes suggested sequencing.

### PP-01 — Route incoming links to the playback owner · P1 / High

Evidence: [MainWindow.xaml.cs](../../src/PiPlay/MainWindow.xaml.cs), `ActivateFromSecondInstance` at 2009, `NavigateTo` at 424, `NavigateInternal` at 434, `SourceCommandsAvailable` at 736, and `ApplyReturnActionAsync` at 1909; [ReturnPolicy.cs](../../src/PiPlay/Services/ReturnPolicy.cs), `Decide` at 44. No test currently references `ActivateFromSecondInstance` or `NavigateTo`.

Recommended product behavior: a supported video link retargets and focuses the existing, ready Popout. During launch/return, retain the latest accepted target until ownership is stable and show a short pending status. With no Popout, navigate the Source; before browser readiness, queue the target. Specify playlist-only requests and a launch with no URL explicitly. Record this incoming-link behavior in spec section 9/13; ADR-0005 currently describes navigation inside the Popout, not command-line delivery.

Implement the decision at the receiving boundary, revalidate payloads with `YouTubeUrlHelper.TryParse`, and expose a narrow guarded player-retarget method. Keep the hidden Source on its original page. Before applying a same-video return sample, check the actual Source identity and discard stale work after an ownership/navigation change.

Acceptance: cover not-ready, ready, Popout-active, launching, returning, clearing, closing, invalid payload, and no-URL activation. For A → incoming B → return, assert one player, the chosen visible owner, Source/return identity B when appropriate, and Auto de-dup for B. Include a newer request arriving during an awaited replay. Add the fifteen malformed-input cases from the evidence table to parser/startup coverage. Use `Ui/WpfRuntimeTests.cs`, `Ui/MainWindowLifecycleTests.cs`, `AppStartupArgumentTests.cs`, and the existing URL-policy tests; live playback remains a separate check.

### PP-02 — Recover browser failures and finish every transition · P1 / High

Evidence: [MainWindow.xaml.cs](../../src/PiPlay/MainWindow.xaml.cs):187–257, 300–361, 1942–1952; [PlayerWindow.xaml.cs](../../src/PiPlay/PlayerWindow.xaml.cs):236–252; [WebViewEnvironmentService.cs](../../src/PiPlay/Services/WebViewEnvironmentService.cs), `EnsureCreatedAsync`. The current Retry repeats initialization; it does not implement mid-session crash recovery.

Handle failures on both surfaces through one coordinated policy. A main-renderer exit can reload; a browser-process exit requires recreating affected WebView2 controls. Coalesce duplicate notifications from the shared environment, invalidate stale callbacks, and restore only the current intended target. Avoid treating self-recovering GPU/utility failures as missing-runtime errors. These distinctions follow [Microsoft's process-event guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-related-events).

Add visible starting/retrying/failed states, a failure-specific heading, and one active recovery attempt. Attach navigation and bridge handlers to each new core exactly once. Give return navigation a terminal outcome even when `Navigate` throws or no completion event arrives; use an overall deadline/cancellation path, not only the existing replay-loop retry count.

Acceptance: injected renderer and browser failure kinds choose the intended recovery; duplicate callbacks start one recovery; replacement cores receive handlers; stale callbacks cannot navigate or reopen a closing window; failed/no-event returns release `_returnInProgress`. Exercise startup failure, repeated Retry, shutdown during recovery, and a live failure against an isolated test package.

### PP-03 — Enforce the existing ad-state rule in every page writer · P1 / High

Evidence: [YouTubeDomBridge.cs](../../src/PiPlay/Services/YouTubeDomBridge.cs):154–192 and 573–577, compared with [YouTube_Compliance.md](../YouTube_Compliance.md). Host writes lack an ad gate; the overlay helper treats a missing player element as false.

Centralize a clear/ad/unknown probe and allow seek/rate/Next only when state is clear. Keep volume/mute and any Source-suppression behavior consistent with the explicitly documented policy. For deferred return, retain the current replay request within a bounded wait, then leave usable native controls with a concise status if a safe seek cannot be made. Never replay an old sample onto a changed video after waiting.

Acceptance: extend `YouTubeDomBehaviorTests.cs` and its Node harness with clear, both known ad classes, missing player, and stale document cases for every writer. Assert the attempted side effects, including playback rate, rather than just checking for selector strings. Live ad behavior and audio suppression must be recorded separately when available.

### PP-04 — Keep final return identity and playback sample together · P2 / Low

Evidence: [PlayerWindow.xaml.cs](../../src/PiPlay/PlayerWindow.xaml.cs), `RetargetTo` at 382, `TrackReturnIdentity` at 423, `CaptureReturnStateNowAsync` at 755, and `CaptureCurrentPlaybackStateAsync` at 764.

Capture a navigation/target generation before the final DOM await, validate it afterward, and produce a stable return snapshot. SPA identity changes must invalidate the sample too; the ordinary navigation counter alone may not cover them. If the target changed, recapture within the return budget or return unknown time. A stale A timestamp must not be assigned to B.

Acceptance: complete an old read after both a normal retarget and a SPA identity change; assert that B receives neither A's time nor A's playback settings. Cover null/failed capture and the final-capture flag. Include this item in the same candidate as PP-01's new external retarget route.

### PP-05 — Make handoff delivery and shutdown ownership explicit · P2 / Low incremental risk

Evidence: [App.xaml.cs](../../src/PiPlay/App.xaml.cs):76–83, 112–120, 168–218; [MainWindow.xaml.cs](../../src/PiPlay/MainWindow.xaml.cs):2009–2064. The existing broader silent-send-failure issue is readiness F-10, rated Medium there.

Stop accepting work as Source shutdown begins, check shutdown inside dispatched callbacks, and track the pipe worker's completion. Return a bounded accepted/rejected result to the sender; pipe-write success alone does not prove application acceptance. On a failed handoff, retry briefly and start a replacement only after winning the channel/session mutex. Never bypass an owned mutex as a fallback. Release owned resources and the mutex in a deliberate, bounded order after log drain; avoid synchronously joining a worker that is waiting on the UI dispatcher.

Acceptance: a close/handoff race either accepts the request or gives the sender a visible failure/retry outcome; it never reports silent success or launches two owners. Cover no acknowledgement, timeout, shutdown while dispatch is pending, and rapid relaunch. A mutex-order test is not evidence that WebView2 has released its profile.

### PP-06 — Keep privacy-operation state accurate after the UI timeout · P2 / Medium

Evidence: [MainWindow.xaml.cs](../../src/PiPlay/MainWindow.xaml.cs):1299–1404, 1442–1444, 2035–2064; [BrowserDataClearCoordinator.cs](../../src/PiPlay/Services/BrowserDataClearCoordinator.cs), `IsRunning` and `TryStart`.

Use the coordinator's underlying-operation state for commands that can start or change playback. Keep Settings/status usable, display that clearing is still running after the foreground wait expires, and marshal terminal completion back to the dispatcher. Refresh the Source after late success only if its window/operation is still current. Define bounded close behavior without promising completion of a clear that was interrupted. Check the ordering too: `TryStart` invokes the clear before the current code closes the Popout.

Acceptance: use a controllable task to exceed the UI wait; Popout/navigation remain appropriately gated, duplicate clears do not start, late success refreshes once, late failure reports accurately, and close causes no stale callback. Reuse `RuntimeFailurePolicyTests.cs` coordinator tests plus WPF command-state coverage.

### PP-07 — Convert placement coordinates at the API boundary · P2 / Low

Evidence: [WindowPlacementService.cs](../../src/PiPlay/Services/WindowPlacementService.cs):28–43, 63–78, 86–113. `rcNormalPosition` is used for both screen-based monitor selection and work-area clamping without conversion.

Choose and document a coordinate representation for persisted placement, then convert for monitor lookup, clamping, and `SetWindowPlacement`. Account for the actual window extended style: ordinary top-level placement uses workspace coordinates, while tool windows use screen coordinates. This distinction is specified by [Microsoft's WINDOWPLACEMENT contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement). Preserve existing saved placements when introducing the conversion.

Acceptance: synthetic top/left taskbar offsets, negative monitor origins, disconnected saved monitor, repeated save/restore, tool-window versus ordinary-window semantics, and DPI changes. Extend `PlacementMathTests.cs` with conversion coverage and verify Source and Popout placement in the packaged app.

### PP-08 — Correct rounded-region endpoints before changing frame sizing · P2 / Low

Evidence: [RoundedWindowRegionApplier.cs](../../src/PiPlay/Services/RoundedWindowRegionApplier.cs):15–38; [RoundedWindowRegionPolicy.cs](../../src/PiPlay/Services/RoundedWindowRegionPolicy.cs), `CreateGeometry`; fresh GDI probe below. Existing native tests are in `Ui/WpfRuntimeTests.cs:1790`.

Add native edge-membership assertions, then evaluate passing `WidthPx + 1` and `HeightPx + 1` to `CreateRoundRectRgn`. That endpoint adjustment retained the final interior pixels and excluded outside pixels in all three fresh probe sizes. Keep the policy's actual window dimensions unchanged; verify overflow/input boundaries when implementing the adapter change.

Do not replace the region dimensions with DWM frame dimensions solely because `IsSnapLike` uses them. `SetWindowRgn` takes window-relative coordinates, while DWM exposes visible bounds without the same invisible borders/DPI adjustment. Any visible-frame clipping change needs explicit origin/scale conversion. See [SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn) and [GetWindowRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect).

Acceptance: corner exclusion, all four straight edges, just-outside rejection, and square/maximize/snap clearing at representative DPI scales. Capture floating, snapped, and restored Popout edges over light and dark backgrounds; distinguish native membership from visible border/shadow appearance.

## Suggested implementation order and next polish

| Batch | Concrete scope | Completion evidence |
|---|---|---|
| 0. Select the source baseline — complete | The primary `main` now includes `ba520eb` and the existing presentation changes. Continue future implementation from this consolidated checkout. | Fast-forward completed; 1,155 tests passed; redundant worktree and local branches removed with branch history archived. |
| 1. Link ownership and return | PP-01 plus PP-04 and PP-05. Write the incoming-link contract and test receiving-side behavior before changing routing. | A → external B → return works with Auto on/off, including transition and close races. |
| 2. Browser and replay reliability | PP-02 and PP-03 as separate focused changes. Also carry forward readiness F-3: distinguish settings read IO failures from corrupt JSON and prevent defaults overwriting unread data. `SettingsService.Load/Save` still has the broad catch/unconditional-save behavior on both compared commits. | Recovery and DOM harness cases pass; an injected settings read failure preserves original bytes through a later save attempt. |
| 3. Privacy and native geometry | PP-06, PP-07, PP-08. The small PP-08 endpoint change can be taken earlier once its native regression assertions exist. | Late-clear state tests, coordinate round trips, and edge-membership tests; rendered placement/corner checks. |
| 4. Visible polish and acceptance | Use the five-point pass below on the final packaged candidate. | Screenshots and keyboard/playback observations tied to that package's commit. Unavailable states remain explicitly not run. |

Before any future Stable publish, carry forward readiness F-1/F-6/F-7/F-8: dedicated deploy-root validation, diagnostic treatment of skipped tests, smoke identity tied to the intended package, and swap/lock harnesses in the gate. The relevant `Publish-Stable.ps1`, `DeploySwap.ps1`, `Test-UiSmoke.ps1`, and `Test-LocalCI.ps1` files are unchanged between the two compared commits. These release-script defects are separate from visual polishing and were not fixed by the later test-package work.

The recommended five-point polishing pass is:

| Focus | Next useful improvement/check |
|---|---|
| 1. Understandable waiting and recovery | Pair PP-01/02/06 with visible pending, retrying, still-clearing, and failed states. Keep **Show Popout** and **Bring video back** distinct. Give each failure a usable next action and avoid a blank Retry interval. |
| 2. Settings clarity | Keep the later Appearance → colour controls → Popout behaviour → Privacy hierarchy. Verify profile overrides and inherited theme values are understandable, and that Done, Cancel, Escape, and title-bar close behave consistently across both open windows. Preserve Soft Glass 82%/72% opacity and independent corner choice. |
| 3. Source toolbar and keyboard use | At the declared minimum Source width, check URL text, profile names, selected/disabled controls, tooltip placement, and focus visibility. Walk Tab/Shift+Tab, Ctrl+L/F6, profiles, sliders, and transfer actions. Check the newly themed controls in the rendered candidate rather than relying only on XAML assertions. |
| 4. Popout edges and recovery controls | Validate PP-08, minimum-size chrome, all resize edges/corners, top-edge reveal, Pin, and expand/restore across themes and DPI scales. Open a populated profile menu and inspect its provisional shadow inset (`ControlStyles.xaml` popup margin) before changing that styling. |
| 5. Playback continuity on the final package | Exercise paused/playing return, different-video return, playlist/mix, Auto de-dup, external links, account redirects, and recovery. Listen for overlap on launch/return and available ad/autoplay paths. Record expected versus observed results and unavailable states. |

Use SND-HOST for source/build verification and the existing SND-DESK review lane for attended package checks. Source inspection and the fresh gate above do not establish that those visual or audio checks passed. Further theme redesign, Compact revival, tray mode, or transparency architecture changes are outside this polishing pass.

## Retained diagnostic evidence

The full local gate outputs, parser/policy output, native probes, and a copy of the replaced input are retained on the reviewing machine under `$env:TEMP\PiPlayReview-20260905-93063ffdd98c4320aa5943d2c3e2f448`. Files: `local-ci.log`, `consolidated-local-ci.log`, `parser-policy-probe.log`, `region-probe.log`, `region-endpoint-probe.log`, and `retired-review.md`. This temporary evidence directory is not a deployment or release artifact; the durable results are summarized here.

The parser probe compiled the current `YouTubeTarget`, `NavigationPolicy`, `YouTubeUrlHelper`, and `ReturnPolicy` sources with PowerShell `Add-Type`, supplying their implicit imports and adapting namespace wrappers. Method bodies were unchanged. It used PowerShell 7.6.5 / .NET 10.0.11, not an unidentified pre-existing PiPlay DLL.

| Input group | Count | Observed result |
|---|---:|---|
| Bare `%`, `%zz`, `abc%`, `%E2%82`, `%E2%82%` | 5 | Rejected, no throw. |
| Valid watch/share video with `t=%zz`, `t=%`, `t=%E2%82%s` | 3 | Video accepted, timestamp null, no throw. |
| Valid video with `t=99999999999999999999s`, `t=9999999999h`, `t=-5`, `t=1e9` | 4 | Video accepted, timestamp null, no throw. |
| `watch?v=%`, or a valid video ID followed by `%` | 2 | Rejected, no throw. |
| Valid video with `list=%E2%82` | 1 | Video accepted, list omitted, fallback reason populated, no throw. |

For a known playing timestamp of 42, `ReturnPolicy.Decide` returned `SeekAndPlay` for returned A / launch A, and `Navigate` for returned B / launch A. The policy is not passed the live Source page identity. This corroborates PP-01's decision-path defect without claiming a live hidden-WebView reproduction.

Native probe: `CreateRoundRectRgn(0, 0, width, height, diameter, diameter)` followed by `PtInRegion` at straight-edge midpoints. Every created GDI object was released with `DeleteObject`.

| Size / ellipse diameter in pixels | Current final right/bottom pixel | With right/bottom endpoints increased by 1 | Outside right/bottom after adjustment |
|---|---|---|---|
| 320 × 180 / 44 | Both excluded | Both included | Both excluded |
| 480 × 270 / 66 | Both excluded | Both included | Both excluded |
| 640 × 360 / 88 | Both excluded | Both included | Both excluded |

The current left/top midpoint and penultimate right pixel were included. A rectangular-region control included its final right pixel; adjusted rounded regions still excluded `(0,0)`. These are native geometry results, not screenshots of PiPlay or proof of a particular visible shadow defect.
