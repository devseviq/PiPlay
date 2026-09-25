# Review — PiPlay polish implementation (branch `polish/review-2026-09-10`)

**Date:** 2026-09-10 · **Objective:** Judge whether the uncommitted implementation of the 2026-09-10 polish review (its F-1..F-10 and A-1..A-7) is commit-ready in code quality and in how it looks, and name what must change first. · **Decision supported:** commit as-is / commit after named fixes / rework.
**Mode:** delegated (Wave 1: Functional, Risk & Edge-Case, Usability with an accessibility lens, Information Architecture; Wave 2: none — no Critical or High finding and no unresolved conflict).
**Evidence:**

- E-1 `git diff -- src/` (1054 lines) — session scratchpad `review-controller/polish-impl/evidence/E-1-src.patch`
- E-2 `git diff -- tests/` (342 lines; the four new files are untracked and were read from the tree) — `E-2-tests.patch`
- E-3 docs diff: `CHANGELOG.md`, spec §15.4 and §20, `Theme_Preset_Differences.md` — `E-3-docs.patch`
- E-4 `scripts/Test-LocalCI.ps1` output: `LOCAL CI: PASS`, 1443 passed, 0 failed (Debug) — `E-4-localci.txt`
- E-5 47 offscreen renders at 2x plus `manifest.txt`: three presets with the cyan and amber accents; the controls strip, Settings top/middle/bottom, the failure panel in three states, five dialogs, the compact toolbar at 800 and 1000 in four states — `evidence/captures/`. Rendered from unrooted visuals by a temporary xUnit test on the WPF STA thread that was deleted afterwards. No hover, focus, tooltip or ClearType. Not acceptance evidence.
- E-6 `docs/reviews/review-controller-2026-09-10-piplay-polish.md` (the review the branch implements; itself untracked on the branch)
- E-7 the working tree: `5f1b8e1` plus the uncommitted changes (23 modified, 5 new files)

The scratchpad evidence is disposable and not committed. Line numbers are the working tree at review time.

## Executive assessment

- Scope: the uncommitted polish implementation, 23 modified and 5 new files; the local gate passes with 1443 tests.
- Conclusion: **commit after named fixes.** Nothing is Critical or High. The paths reviewed in depth (failure panel, queued return, Back/Reload gating, token pipeline, dialog builders) behave as claimed, and the enabled surfaces read well on every preset: accent labels 7.2–7.6:1, danger labels 5.2–5.6:1, one dialog shell with consistent button order.
- Main risks: four written claims outrun the code — the checked-toggle wash is visually inert (F-1), a hidden Playback-mode row can rewrite a stored profile token (F-2), the "Settings not saved" hint clears on a failed write (F-3), and the Done tooltip promises a readability check that is a validity check (F-7).
- Two disabled-state gaps undercut the polish the branch added (F-4, F-6), and the failure panel's button order contradicts every dialog (F-5).
- Documentation: the release notes contradict themselves on Back/Reload (F-9), the prior review carries branch-status prose the repo rule excludes (F-8), and the Opacity rows name one surface three ways (F-10).
- Priority actions: the nine immediate items in the action plan are each a few lines; none needs a redesign. Visual acceptance still belongs to the SND-DESK lane; the renders here only narrow what to look at there.

## Scope & method

Boundaries: the uncommitted working tree only. `PlayerWindow.xaml.cs`, the WebView2 recovery policy itself and release tooling were outside scope except where a changed string or call touched them. Journeys walked: pop out → browser fails → close the Popout → Retry; Clear browser data with a Popout open; Settings → accent → Done; profile save, rename, replace, edit, delete; runtime missing at first start.

Evidence: E-1..E-7. The controller read `MainWindow.xaml.cs`, the theme styles, `Prompt.cs`, the new test classes and every capture. The captures were produced by applying `ThemeResourceApplier` per preset to a plain test `Application` and rendering the real controls, `MainWindow`, `SettingsWindow` and `Prompt` dialogs through `RenderTargetBitmap` at 192 dpi.

Specialist work: four read-only `review-specialist` agents, one role each, disjoint scope, frozen evidence. Functional took the MainWindow state machine and its tests; Risk & Edge-Case took Prompt, SettingsWindow, Services, Theme and their tests; Usability (with an accessibility lens) took the renders; Information Architecture took labels, grouping, docs and code comments. All four returns passed the gates; the IA return ran over the word budget and was accepted rather than redirected. Every Medium finding was then verified by a controller read of the cited lines or by pixel-sampling the capture; Low findings are accepted on agent evidence with the location recorded.

Limitations: offscreen renders miss hover, focus, tooltips, DPI scaling and ClearType. The toolbar renders show the Pop out label in the light body ink because the content was detached from its Window before rendering and the label's `ElementName` binding no longer resolved; in the app that label binds to the button's Foreground (`OnAccent`), and `PopOutButton_keeps_its_explicit_label_bindings` asserts the binding. No interactive run happened on this machine.

Prediction check: three findings were predicted before delegation. P1 (Back/Reload stuck disabled after start-up) was falsified by direct read before the wave (`MainWindow.xaml.cs:260-263`). P2 (the queued-work note goes stale after the work is dropped) was falsified twice: no drop site is reachable while the note shows, and the narrowed form — the 20-s return deadline abandoning a snapshot queued on a failed browser — is disarmed at `MainWindow.xaml.cs:2548`, a clause the controller had missed and the Functional agent found. P3 (hint cleared on a failed save) matched (F-3). One of three matched; the wave's strongest findings (F-1, F-2, F-4, F-7, F-8, F-9) were not predicted, so delegation supplied most of this report and removed one wrong controller finding.

## System assessment

Structure: the branch keeps the existing shape. Theme tokens are derived in `ThemeColors` and published by `ThemeResourceApplier` as frozen brush/colour pairs consumed only through `DynamicResource`, so a preset or accent change re-resolves an open Settings window and Popout. `MainWindow` owns the browser state machine and the failure panel; `Prompt` builds every dialog in code from one shell. New pieces: `RuntimeLinkMode` (the panel's download-link role), per-state Pop out copy in `ApplyPopoutActionState`, the `OnAccent`/`OnDanger`/`AccentCheckedWash` token pairs, a presenter-scoped label style inside the filled buttons, and three WPF test classes that mostly assert rendered results (measured foregrounds, hosted-window re-resolution) rather than property values.

Information architecture: Settings now groups Popout presentation with fade delay, opacity and the top bar under "Popout behaviour"; the dialogs share one shell (title bar with close, primary on the left, Cancel on the right); destructive confirms are danger-filled with Cancel as the default and bodies that say what is lost. The failure panel is the one surface whose action order differs (F-5).

Journeys and context preservation: a return that arrives on a failed browser is queued and survives until Retry (`MainWindow.xaml.cs:2539-2548`, `:316-321`); a return during a restart replays after the first successful same-video navigation; Clear browser data closes an open Popout inside the clear (`:1853`), as its confirm says. Every write to the browser-state flags is followed by both availability updates, so Back, Reload and Pop out never keep a stale enabled state. Dependencies that matter: the disabled visual (Opacity 0.4 on the whole control) is shared by the filled and dark button styles, so any label meant to be read while disabled inherits about 2:1 contrast (F-6); the checked-wash token replaces rather than overlays the chip surface (F-1).

## Findings

### F-1 — Checked-toggle wash is visually inert; the SemiBold cue never landed · Medium · High · Theme / toggles and chips · Verified

Observation: `IsChecked` sets the chip's `bd.Background` to `AccentCheckedWash` (16 % alpha accent) in place of the opaque surface fill, so the checked fill composites over the page to almost the unchecked colour. Pixel-sampled from the renders: checked vs unchecked fill 1.08:1 on sharp-dark and 1.07:1 on minimal (the Usability agent measured about 1.0:1 on soft-glass). The 1-DIP accent ring is the entire cue. `ThemeCatalogTests` models the wash layered over `SurfaceBase` and `SurfaceRaised` and asserts a step of at least 1.10:1; the template replaces the surface instead, so the render sits below the step the test believes it enforces. No `FontWeight` trigger exists, so the SemiBold checked label the prior review asked for under its F-7 is absent, and no reason is recorded.
Evidence: [E-7:src/PiPlay/Theme/ControlStyles.xaml:338-339] [E-7:src/PiPlay/SettingsWindow.xaml:76-77] [E-7:tests/PiPlay.Tests/ThemeCatalogTests.cs:210-219] [E-5:sharp-dark-controls.png, minimal-controls.png, *-settings-middle.png] [E-3:docs/CHANGELOG.md:+26]
User impact: a user scanning the Pin and Auto toggles or the preset and fade chips still reads the on state from the thin ring; the release note says the wash carries the state, which it does not visibly do.
System impact: `ThemePolishTests` assert the brush value and `ThemeCatalogTests` a layered model; neither measures the rendered step, so the claim is covered without being true on screen. Checked chips also lose hover feedback because the `IsChecked` trigger follows and overrides `IsMouseOver`.
Recommendation: layer the wash over the surface as the test already models it (an inner overlay, or a checked fill derived from the surface plus the accent) and make the step visible, at least 1.5:1 in luminance; add `FontWeight=SemiBold` to the `IsChecked` trigger; otherwise reword the CHANGELOG and spec claim to the ring.
Dependencies: `ThemeColors.CheckedWashAlpha`; `ThemePolishTests`.
Validation: re-render the controls strip and sample checked vs unchecked fills; or assert a minimum luminance step in the wash test.

### F-2 — Hidden Playback mode row rewrites a stored "compact" token to null · Medium · High · Prompt / profiles · Verified

Observation: with `PlaybackModePolicy.CompactPlayerEnabled` false, `BuildModePicker` omits the Compact item and maps every token except "normal" to "Use global default" (Tag null); the getter returns the selected Tag, so any Edit-profile save writes null over a stored "compact" token. The row is now collapsed, so nothing shows the change. The code comment, the new test (mode "normal" only) and the release note all say the stored value survives.
Evidence: [E-7:src/PiPlay/Prompt.cs:112-136, 274-284] [E-7:tests/PiPlay.Tests/Ui/PromptPolishTests.cs:49-61] [E-3:docs/CHANGELOG.md:+27]
User impact: latent while Compact is dormant; a profile that carries the token (from an earlier build or a hand edit) loses it on any rename. The written claim is false today.
System impact: profile JSON `Mode` normalised to null on save.
Recommendation: when the row is hidden, return the stored token unchanged from the getter; add `InlineData("compact")` to the test; keep the release-note sentence only once that passes.
Dependencies: `PlaybackModePolicy`.
Validation: `Prompt.BuildEditProfile(owner: null, "Saved", Url, mode: "compact")`, then assert the picker's selected `Tag` is "compact" (today it is null).

### F-3 — "Settings not saved" hint clears on a failed write · Medium · High · MainWindow / settings · Verified

Observation: `SaveSettings` returns early only for `RefusedUnread`; both `Saved` and `Failed` fall through to clearing `_settingsSaveRefusalShown`, collapsing the hint and logging "Settings saves resumed". `SettingsService.Save` returns `Failed` from its catch.
Evidence: [E-1:src/PiPlay/MainWindow.xaml.cs:1969-1978] [E-7:src/PiPlay/Services/SettingsService.cs:124-127] [E-3:docs/CHANGELOG.md:+33 "clears after a later save succeeds"]
User impact: once the unread block lifts (a successful load, or Reset app state), a later disk failure removes the warning; the user believes settings persist while nothing was written.
System impact: hint state desynchronised from durable state; only a log line records the loss. Introduced by this branch.
Recommendation: `if (result != SettingsSaveResult.Saved) return;` before the clear; add a test that feeds `Failed` after `Saved`.
Dependencies: none.
Validation: extend the RefusedUnread → Saved → RefusedUnread test in `MainWindowPolishTests` with a Failed step.

### F-4 — Disabled "Pop out video" tooltip gives no reason while the browser is down · Medium · High · MainWindow / toolbar · Verified

Observation: `ApplyPopoutActionState` chooses the tooltip by state; only `Open` and `Clearing` mention the browser. In the `Ready` state with the browser restarting, failed or missing, the button is disabled (`CanStartVideoPopout` false) and, with `ToolTipService.ShowOnDisabled` now on, shows "Pop out the current video". The XAML comment and spec §20 say the tooltip names why.
Evidence: [E-7:src/PiPlay/MainWindow.xaml.cs:2296-2311, 2015-2017] [E-1:src/PiPlay/MainWindow.xaml:203-206] [E-5:manifest.txt popout=False in the runtime-missing and failed states]
User impact: a user hovering the dead button in any browser-down state is told it pops out the video, with no wait signal.
System impact: copy only. Introduced by this branch (the tooltip is newly visible while disabled).
Recommendation: in the default arm return a browser-down variant when `browserDown || !_browserReady` (for example "Pop out video is available when the browser is back"); add a Ready plus `SetBrowserFailedForTests` case to the tooltip test.
Dependencies: none.
Validation: extend `Disabled_primary_actions_keep_their_tooltips` with the failed state.

### F-5 — The failure panel's primary action sits on the right; every dialog's sits on the left · Medium · High · Failure panel · Verified

Observation: keeps-failing renders [Get WebView2 Runtime (secondary)] [Retry (accent)]; runtime-missing renders [Get WebView2 Runtime (accent)] [Retry]; all five dialogs render [primary or destructive] [Cancel]. The recommended action swaps sides between the two panel states, and in keeps-failing the first button scanned is the secondary one.
Evidence: [E-5:sharp-dark-panel-failed-video-waiting.png, sharp-dark-panel-runtime-missing.png, *-confirm-*.png] [E-7:src/PiPlay/MainWindow.xaml:292-301]
User impact: a user who has learned "primary is on the left" from the dialogs reaches for the download link first in the failed state.
System impact: none.
Recommendation: order the panel buttons so the primary is always first — [Retry] [Get WebView2 Runtime] whenever the runtime is present — and leave runtime-missing as is. Keep the accent/secondary styling as implemented.
Dependencies: the panel-order assertions in `MainWindowPolishTests`.
Validation: re-render the three panel states; assert `RuntimeRetryButton` precedes the download button among the panel's children when the runtime is present.

### F-6 — Disabled filled buttons dim their own label to about 2:1, including the "Returning…" and "Clearing…" feedback · Medium for the transitional labels, Low for plain disabled buttons · High · Theme · Verified

Observation: the `IsEnabled=False` trigger sets `Opacity 0.4` on the whole button (AccentButton and DangerButton; DarkButton 0.45), so ink and fill fade together. The Usability agent measured Done disabled at 2.0–2.2:1, disabled danger buttons at 1.7–1.8:1 and the compact-toolbar labels that announce a return or a clear at 2.0–2.1:1; the controller's composite arithmetic with the `OnAccent` ink over `AppBackground` gives about 1.9:1 for the same labels.
Evidence: [E-7:src/PiPlay/Theme/ControlStyles.xaml:184-186, 225-227] [E-5:*-controls.png Done and Replace, *-panel-reloading.png, toolbar-800-returning.png, toolbar-1000-clearing.png — the toolbar renders show the light harness ink, see limitations]
User impact: WCAG 1.4.3 exempts inactive controls, but the transitional labels are the only feedback on a narrow window that a return or a clear (up to 30 s) is in progress; at about 2:1 they read as a dimmed button, not a status.
System impact: none.
Recommendation: for Returning and Clearing keep a readable label — dim the fill only (muted surface with `TextSecondary` ink), or render the transitional states as a non-interactive busy visual without the disabled opacity. Plain disabled buttons can stay as they are.
Dependencies: the prior review's F-6, which introduced these labels.
Validation: re-render `toolbar-800-returning` from an attached window and sample label against fill; target at least 4.5:1.

### F-7 — The "readable accent" gate only checks that the hex parses · Medium · Medium (the tooltip part High) · Settings / accent · Verified

Observation: `AccentReadabilityPolicy.Evaluate` fails only on `AccentGate.Invalid`; every parseable hex is "readable". The new Done tooltip "Done needs a readable accent. Choose another colour or use the default." therefore appears only for malformed text and names the wrong cause. Separately, the contrast floors the branch asserts are gated for the six catalog accents only; recomputed by the Risk agent with the branch's own `ContrastRatio` and `DeriveAccentSet`, a user-typed mid-grey (about #7D787D on minimal) gives `OnAccent` on the hover fill 2.92:1 and the accent glyph on its wash 2.84:1, and no `OnAccentHover` re-pick exists.
Evidence: [E-7:src/PiPlay/Theme/AccentReadabilityPolicy.cs:14-18] [E-1:src/PiPlay/SettingsWindow.xaml.cs:+203-210] [E-2:tests/PiPlay.Tests/ThemeCatalogTests.cs:205-219] [E-7:src/PiPlay/Theme/ThemeColors.cs:208, 235-238]
User impact: a user who types a low-contrast hex gets no warning and hover labels below the floor; a user who mistypes is told the accent is unreadable rather than invalid.
System impact: the assertion suite gives false coverage confidence for arbitrary accents. The tooltip copy is new (the prior review's F-6 asked for it); the catalog-only gate is pre-existing.
Recommendation: reword the tooltip to the real gate ("Done needs a valid accent such as #2BAED0"), or make `Evaluate` a contrast check and keep the copy; consider an `OnAccentHover` pick mirroring the pressed one.
Dependencies: the accent-intensity dial; `ThemeCatalog.AccentOptions`.
Validation: `AccentReadabilityPolicy.Evaluate("#7D787D")` returns readable today; the Done tooltip test expects the new wording.

### F-8 — The prior review document carries branch-status prose · Medium · High · Docs · Verified

Observation: E-6 line 169 opens "**Implementation status (2026-09-10, branch `polish/review-2026-09-10`, uncommitted).** Landed with red-first tests: …" and lists what landed and what did not. The repo rule excludes status prose, and the paragraph is false the moment the branch merges. The document must still be committed: about 45 source and test comments and three test headers resolve "F-x" through it, and its per-finding "Revision" blocks are the only recorded reasons for the F-1 and F-5 deviations.
Evidence: [E-6:169] [E-7: grep "polish review 2026-09-10" under `src/` and `tests/`]
User impact: a maintainer reads a stale claim about branch state in a committed document.
System impact: none.
Recommendation: replace the paragraph with an undated outcome line per finding (implemented, or landed differently with the reason) and drop branch and commit state; keep the Revision blocks.
Dependencies: none.
Validation: read E-6 around line 169 after the edit.

### F-9 — Release notes contradict themselves and mislabel the filled buttons · Medium · High · Docs / CHANGELOG · Verified

Observation: bullet 13 (existing) says "The Source controls come back as soon as the browser gives up or a restart runs long"; new bullet 29 says "Back and Reload wait for a live browser". New bullet 25 lists "Save, OK, Done, Delete, Clear, Replace, …" as filled-button labels: "Clear" is not a label (the button reads "Clear browser data"), and "Reset app state" and "Bring video back" are missing; bullets 25–27 use "ink", "wash", "body-text label" and "Settings chip". The CHANGELOG ships with releases.
Evidence: [E-7:docs/CHANGELOG.md:13, 25-27, 29] [E-7:src/PiPlay/Services/PrivacyService.cs ClearConfirmButton, ResetConfirmButton] [E-7:src/PiPlay/MainWindow.xaml:263]
User impact: a reader cannot tell whether Back works after a give-up, and meets implementation vocabulary and a button list that does not match the screen.
System impact: none.
Recommendation: qualify bullet 13 ("the Source controls except Back and Reload come back…"); replace the parenthetical with "every filled button" or the true labels; swap ink, wash and body-text for plain words.
Dependencies: none.
Validation: read bullets 13 and 25–29 together.

### F-10 — The Opacity group names the Source surface three ways · Medium · High · Settings / labels · Verified

Observation: the row label says "In use (Popout + Source bar)", the description "the Source title-bar background", the slider tooltip and automation name "Source top bar"; the automation name also says lowercase "popout". The vocabulary in `docs/AGENTS.md` defines Source Window and no term for its bar; the release notes say "title bar" for the Source and "top bar" for the Popout.
Evidence: [E-7:src/PiPlay/SettingsWindow.xaml:268, 279, 284, 285, 295] [E-5:sharp-dark-settings-middle.png] [E-7:docs/AGENTS.md:19] [E-7:docs/CHANGELOG.md:21, 32]
User impact: label, helper text and tooltip appear to describe three surfaces; the user has to work out they are one.
System impact: none.
Recommendation: one term in all four strings (either "Source title bar", matching the release notes, or "Source top bar", matching the Popout's term), and "Popout" capitalised in the automation names.
Dependencies: none.
Validation: grep the four strings in the opacity block.

## Cross-cutting themes

- Claims outrun the visible or tested result: F-1, F-2, F-3 and F-7 are each a written claim (comment, release note, spec sentence or tooltip) that the code does not deliver. The tests assert the mechanism (a brush value, a "normal" token, a Saved result) rather than the claim; the repo rule that behaviour claims stay tied to source and tests is what these findings enforce.
- Disabled states were outside the polish pass: the shared 0.4-opacity disabled visual dims everything it touches (F-6), the newly visible disabled tooltip lacks its reason (F-4), and the inert Retry is the only control in the reloading state (A-4).
- Documentation register: British and American spelling, "popout" capitalisation and three names for one surface (F-10, A-2), plus status prose (F-8) — quick edits, and the reason the doc rules exist.
- Consistency across surfaces is otherwise good: one dialog shell, danger confirms with Cancel as default, readable ink on every filled button and preset, availability recomputed after every state change.

## Action plan

Immediate (before commit):

1. F-3 — return unless `Saved`; test with `Failed`.
2. F-4 — browser-down tooltip in the default arm; test with the failed state.
3. F-2 — pass the stored token through when the row is hidden; `InlineData("compact")`.
4. F-1 — layer the wash over the surface (or raise the step) and add the SemiBold trigger, or reword the wash claim.
5. F-7 — reword the Done tooltip to the real gate.
6. F-5 — Retry first whenever the runtime is present.
7. F-8, F-9, F-10 — the document and label edits above.

Near-term:

8. F-6 — readable transitional labels on the compact toolbar.
9. A-1 — a seam test for the return-into-failed path (`BeginReturnForTests` before the failed-state queue; assert the deadline is disarmed and the snapshot retained).
10. A-2, A-3 — spelling register, "Popout" capitalisation in the Popout chrome, `MainWindowPolishTests` in the §15.4 citation.
11. A-5, A-6 — hoist the presenter style; fade the Danger background instead of the whole border on hover.

Structural:

12. Decide whether arbitrary accents get a real readability check (F-7) or the UI stops promising one.
13. A-4, A-7, A-8 — design suggestions for the reloading state, the lone toggle chips and the Edit-profile checkbox; product decisions, not defects.

Validation: re-run `pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1`; re-render the controls strip, the panel states and the toolbar states after F-1, F-5 and F-6 from an attached window; visual acceptance on the SND-DESK lane.

## Open questions

- Should the queued return snapshot be protected while the URL box and Home stay live in the failed state? The spec's drop rule lets a user navigate away and lose the snapshot silently; keeping those controls usable was the branch's explicit choice (prior review F-5). Product decision.
- A return that arrives during the transient reload state (Navigate action) appears to queue a snapshot while the panel is visible without refreshing the note, so no "waiting" line shows. Low confidence; not verified.
- Is there a legibility bar for plain disabled filled labels (Done, Retry while reloading) at all, given WCAG exempts them? Only the transitional labels (F-6) bear on the decision.

## Evidence & confidence

Verified (controller direct read or measurement): F-1 (styles plus pixel sampling), F-2, F-3, F-4, F-5, F-6 (styles plus composite arithmetic), F-7 (the gate), F-8, F-9, F-10. Negative results also verified: the return deadline is disarmed on a failed browser (`:2548`, `:316-321`); availability is updated after every flag write; no reachable stale queued-work note; token pairs frozen and re-resolved; the Popout is closed inside the clear path (`:1853`); Danger ink 5.2–5.6:1 on every preset; the `Theme_Preset_Differences` claim about the Danger ink is true.
Uncertain: F-7's out-of-catalog contrast figures (agent-computed, not re-run); A-1's regression consequence is hypothetical.
Assumed: offscreen renders match on-screen colour and layout apart from ClearType, hover, focus, tooltips and DPI; the light Pop out label in the toolbar renders is a harness artifact (binding pre-existing and asserted by `PopOutButton_keeps_its_explicit_label_bindings`).
Untested here: interactive behaviour (hover, keyboard, screen reader), DPI scaling, the deployed build. Visual acceptance remains with the SND-DESK lane.

## Appendix

### Remaining findings

- A-1 — Return-into-failed path untested · Low · High. The `|| _browserFailed` clause at `MainWindow.xaml.cs:2548` is the only thing disarming the deadline for a return queued on a failed browser; no test exercises `BeginReturnTransition` in that state (the failed-queue test at `MainWindowRecoveryTests.cs:344-375` calls `ApplyReturnActionAsync` directly, and the E-5 harness did the same). Pre-existing code the branch's notes describe; add the seam test.
- A-2 — Register split · Low · High. "colour" in `CHANGELOG.md` +26 and the Done tooltip (`SettingsWindow.xaml.cs:205`) beside "Profile color" and "Accent color" in the UI; lowercase "popout" in `PlayerWindow.xaml:52, 68, 73, 74, 78` beside the capitalised close tooltip at line 77. The Popout part is the prior review's A-7 drift, which its status paragraph records as not landed; the "colour" spellings are new.
- A-3 — §15.4 citations · Low · High. The new panel, Back and Reload sentences sit in a paragraph whose closing citation list does not name `MainWindowPolishTests` (§20 does). Source is cited, so the rule holds; naming the test is an improvement.
- A-4 — Reloading state · Low · Medium. Heading, one line and an inert Retry; no progress cue. The state is bounded (10 s) and named, and the disabled Retry with a tooltip is what the prior review asked for (its F-6). Suggestion: a short "this can take a few seconds" line.
- A-5 — Duplicated presenter style · Low · Medium. The same `ContentPresenter.Resources` TextBlock style is pasted into AccentButton and DangerButton with no `BasedOn`; AccentButton's presenter also sets Display/Fixed/Grayscale text options and DangerButton's does not. Hoist to one keyed style.
- A-6 — Danger hover fade · Low · Medium. `bd.Opacity 0.88` on hover fades ink with fill to 4.29–4.44:1 (`ControlStyles.xaml:222-224`). Pre-existing trigger; fade the Background instead.
- A-7 — Lone toggle chips · Low · Medium. "Focused overlay" and "Auto-hide top bar" are single chips whose off state looks like an unpressed button and is never named. Pre-existing design; the prior review's A-2 moved the option and its A-7 already named the missing off-state name, recorded as not landed.
- A-8 — Edit profile checkbox · Low · High. "Profile color" is an unstyled system checkbox among styled controls, and the dimmed colour editor stays fully laid out. Pre-existing; the prior review's A-8 lists it, recorded as not landed.
- Deviations from the prior review, for the record: F-1 no `BasedOn` (reason given); F-1 preset chip label stays `TextPrimary` (reason given); F-5 narrowed (reason given); F-7 SemiBold weight not applied (no reason; see F-1 here); F-4 panel note and tooltip as two sentences (no reason; Low).

### Registry snapshot

| ID | Area | Source | Severity | Confidence | Status | Report |
|---|---|---|---|---|---|---|
| R-1 | Return queued on failed browser: deadline drops the snapshot | controller prediction | — | — | rejected (falsified at `:2548`) | prediction check |
| R-2 | Hint cleared on failed save | controller + Functional | Medium | High | verified | F-3 |
| R-3 | Compact toolbar label shift | controller | — | — | rejected (no consequence) | — |
| R-4 | Panel button order | controller + Usability | Medium | High | verified | F-5 |
| R-6 | CHANGELOG contradiction | IA | Medium | High | verified | F-9 |
| R-7 | CHANGELOG filled-button bullet | IA | Medium | High | verified | F-9 |
| R-8 | §15.4 citations | IA | Low | High | verified | A-3 |
| R-9 | Opacity group naming | IA | Medium | High | verified | F-10 |
| R-10 | Status prose in the prior review | IA | Medium | High | verified | F-8 |
| R-11 | Register and capitalisation | IA + controller | Low | High | verified | A-2 |
| R-12 | Disabled Pop out tooltip | Functional | Medium | High | verified | F-4 |
| R-13 | Return-into-failed path untested | Functional | Low | High | accepted | A-1 |
| R-14 | Disabled filled-button labels | Usability | Medium / Low | High | verified | F-6 |
| R-15 | Reloading state cue | Usability | Low | Medium | accepted | A-4 |
| R-16 | Checked wash inert | Usability + IA | Medium | High | verified | F-1 |
| R-17 | Lone toggle chips | Usability | Low | Medium | accepted | A-7 |
| R-18 | Edit profile checkbox | Usability | Low | High | accepted | A-8 |
| R-19 | Accent readability gate | Risk | Medium | Medium | verified (tooltip part) | F-7 |
| R-20 | Hidden mode row rewrites token | Risk | Medium | High | verified | F-2 |
| R-21 | Duplicated presenter style | Risk | Low | Medium | accepted | A-5 |
| R-22 | Danger hover fade | Risk | Low | Medium | accepted | A-6 |
