# Review — PiPlay polish, UI and quality enhancements

**Date:** 2026-09-10 · **Objective:** Find the polish, UI and quality gaps in PiPlay's three windows and its dialogs that are worth fixing now, ranked by user impact, with the evidence needed to fix them. · **Decision supported:** which items to implement in the next polish pass (verified by WPF-lane tests on this machine), which to hand to the SND-DESK visual-acceptance lane, and which to record as accepted limitations.
**Mode:** delegated (Wave 1: Usability, Information Architecture, Accessibility, Risk & Edge-Case; Wave 2: Verification ×2, Adversarial)
**Evidence:** frozen at `fix/guarded-return-recovery-handoff` commit `5f1b8e1` (clean tree; `VERSION` 0.13.2, `BUILD_NUMBER` 39). Rendered captures and the manifest live in the review working directory (not committed).

| E-id | Location |
|---|---|
| E-1 … E-8 | 152 rendered captures: Source Window (ready, Popout open, placeholder, profiles, Pinned/unsaved/Auto, Returning, Clearing, runtime-missing, restarting, keeps-failing) in the default 1180×760 and compact 760×480 layouts; Popout Player shell at 960×540 / 480×270 / 320×180 with both error bars; Settings top and bottom; seven Prompt dialogs; all three presets (sharp-dark, minimal, soft-glass) at 96 and 144 DPI; accent-intensity 0 (Round) and 100 (Square) variants. Plus `index.txt` with DIP sizes. |
| E-9 … E-12 | `src/PiPlay/MainWindow.xaml`, `MainWindow.xaml.cs`, `PlayerWindow.xaml`, `PlayerWindow.xaml.cs` |
| E-13 … E-16 | `src/PiPlay/SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `Prompt.cs`, `Controls/AccentColorPicker.xaml(.cs)` |
| E-17 … E-19 | `src/PiPlay/Theme/Colors.xaml`, `ControlStyles.xaml`, `ThemeCatalog.cs`, `ThemeColors.cs`, `ColorMath.cs`, `AccentReadabilityPolicy.cs`, `ContrastBrushConverter.cs`, `ThemeResourceApplier.cs`, `ToggleAccent.cs` |
| E-20 … E-22 | `src/PiPlay/Services/PrivacyService.cs`, `PlayerShellErrorPolicy.cs`, `WebViewProcessFailurePolicy.cs` |
| E-23 … E-27 | `docs/AGENTS.md`, `docs/PiPlay_Product_Engineering_Spec.md` (§5, 6, 7, 10, 13–15, 17, 19, 20, 23, 24), `docs/Theme_Preset_Differences.md`, `docs/DECISIONS.md`, `docs/reviews/code-review-and-polish-plan-2026-09-05.md` (2026-09-06 record) and `review-controller-2026-09-02-piplay-readiness.md` |
| E-28 | `tests/PiPlay.Tests/Infrastructure/Wcag.cs`, `ContrastReportTests.cs`, `Ui/XamlInvariantTests.cs` (plus `ThemeCatalogTests.cs`, `Ui/WpfRuntimeTests.cs`, `MainWindowRecoveryTests.cs` read for behaviour claims) |
| E-29 | Temporary capture harness (a WPF-lane test that rendered each window state with `RenderTargetBitmap`; deleted after the captures were frozen) |

**How to read the captures.** They are the real WPF visual trees of the four surfaces, rendered from this source at synthetic DPI with the windows invisible. They contain no WebView2 content, no hover, focus, fade or DWM frame, and the browser-failure states were entered through test hooks that touch only the panel. They are not deployed-Stable evidence and close no SND-DESK acceptance item.

## Executive assessment

Scope: the Source Window, Popout Player, Settings window and the code-built prompts, across three presets and two DPIs, judged for clarity, feedback, naming, failure handling and accessibility. The shell is in good shape: every icon-only XAML control has an accessible name and a keyboard focus ring, secondary text clears 6.7:1 on every surface, the 144 DPI renders show no clipping, and the failure and recovery logic hardened on 2026-09-06 holds under re-read. The gaps are concentrated in four places. (1) Every accent-filled button whose content is a plain string — every dialog's primary action, Settings **Done**, the placeholder's **Bring video back**, the Popout's **Open normal page** — paints its label in near-white on the accent fill at about 2.4:1 instead of the intended dark-on-accent 7:1, because the app-wide implicit `TextBlock` style outranks the inherited foreground; the toolbar's **Pop out video** is the only site that escapes. (2) The code-built dialogs give their text fields and combos no accessible name, contradicting spec §20. (3) The one browser-failure panel offers the runtime download as its accent action in every state and leaves the toolbar, the placeholder buttons and the return action live behind it. (4) Disabled controls cannot explain themselves anywhere except Settings' Clear button, which matters most in the compact toolbar, where the Returning and Clearing states are otherwise invisible. Priority: fix (1) and (2) first — one style change plus names in `Prompt.cs` — then the failure-panel action set and the disabled-tooltip/compact-state pair, then the copy and confirm-prompt items. All of these are verifiable with WPF-lane tests; the visual acceptance of the result stays with the SND-DESK lane.

## Scope & method

**Boundaries.** In scope: the four UI surfaces as rendered from source, their XAML and code-behind, the theme styles and palette, the user-facing strings, and the docs that fix the vocabulary and contracts. Out of scope: YouTube page content and the compact shell page (no WebView2 in the evidence), live fade/hover/focus behaviour, deployed-Stable checks, and every feature the spec defers in §23 (tray, hotkeys, alternate layouts, import/export). Spec §24 items (Q-1 audio proof, runtime-scheme policy, profile-popup shadow inset, playlist queue index) are known and unchanged; they are not restated.

**Evidence.** 152 captures rendered from the current source plus the files listed above. Every finding cites a capture file or a source line. Where a capture and the code could disagree (the failure-state captures were entered through a panel-only test hook), the code was treated as authoritative and the capture cited only for the panel itself.

**Journeys reviewed.** J1 browse → Pop out video → Popout chrome (Pin, Fade, Expand, Settings, Close) → Bring video back. J2 the same loop in the compact toolbar (toolbar narrower than 940 DIP; the window's `MinWidth` is 760). J3 Settings: theme, corners, accent, intensity, presentation, fade, opacity, top bar, Reset app state, Clear browser data. J4 profiles: save, select, edit, overwrite, delete. J5 failure and recovery: runtime missing, could not start, restarting, keeps failing, Popout shell error, clear in progress, settings not saved.

**Specialist work.** Wave 1: four `review-specialist` agents with disjoint scopes — Usability (Source toolbar, placeholder, Popout chrome), Information Architecture (Settings, profiles, dialog copy versus `docs/AGENTS.md`), Accessibility (names, keyboard, disabled tooltips, colour-only state, contrast per preset), Risk & Edge-Case (failure and recovery action sets). Returns were gated on evidence pointers, named impact and confidence, then merged through a controller-owned registry. Wave 2: two Verification agents re-checked every High and every claim that will drive a code change (the accent-label mechanism and its fix options; the failure-state action sets, including whether Reset app state restarts the process), and one Adversarial agent tried to falsify the top three findings and swept the toolbar and Popout chrome for misses. Every High in this report was additionally confirmed by the controller at the cited lines, and the accent-label finding by sampling label pixels in the captures.

**Prediction check.** Three top findings were predicted before delegation: the runtime download button staying primary in every failure state (matched, F-3, at Medium after the adversarial pass), disabled controls never showing their tooltips (matched, F-6), and no first-launch feedback (not raised by any specialist; the 2026-09-06 decision stands, and only its tooltip consequence surfaced inside F-6). The two findings that survive as High — the accent-label contrast regression (F-1) and the unnamed dialog inputs (F-2) — were not predicted. Delegation added real value on the accessibility lens; the system map held.

**Limitations.** No live playback, no live keyboard pass, no hover or fade states, no DWM frame, no deployed build. Two accessibility questions (keyboard focus visibility inside the profile combo and the profile menu; whether a 1-px accent ring is a sufficient checked cue) need a live pass and are listed as open questions. The captures are disposable working evidence and are not committed.

## System assessment

**Structure.** One WPF process; the Source Window owns browsing, the URL box, profiles, Settings and the lifecycle guards; the Popout Player owns the second WebView2 and its borderless chrome; Settings previews appearance live and commits on Done; `Prompt.cs` builds the seven dialogs in code from the same styles. The theme system replaces `DynamicResource` palette, accent, radius, density and elevation entries per preset; accent readability is computed by `AccentReadabilityPolicy` and exposed as the `OnAccent` brush.

**Information architecture.** The vocabulary in `docs/AGENTS.md` is followed almost everywhere; one string still says "main window". Settings is ordered Appearance → Popout behaviour → Privacy, but the Popout presentation toggle sits under Appearance above the Popout header, and the opacity slider that also tints the Source title bar sits under a Popout-only header with the exception buried in helper text. Destructive confirmations invert the hierarchy: the recoverable Clear browser data is styled as danger while the unrecoverable Reset app state and the silent Overwrite profile are not.

**Journeys and hand-offs.** The pop-out/return loop carries video, time, playing state, list, volume, mute and rate, and the 2026-09-06 work made the return survive a Source failure by queuing the snapshot for the recreated core. What the UI does not yet do is *say* so: while the Source is failed or restarting, the toolbar, the placeholder buttons and **Bring video back** stay enabled as if they would act now, and the panel copy never mentions the waiting video.

**Context preservation.** The Popout's own Close is the same return path as the toolbar's, so the outcome of a return during failure is intended and tested; the gap is affordance, not state. Clear browser data closes the Popout before the clear by design (spec §19), but the confirm body does not say so, and a failed or timed-out clear leaves the user without the video and without a reopen path.

**Broad-impact actions.** Reset app state (unrecoverable profiles), Clear browser data (signs out), Overwrite profile (silent replacement), Delete profile. Only Delete and Clear are styled and worded as destructive.

## Findings

### F-1 — Accent-button string labels render TextPrimary on the accent fill (≈2.4:1) instead of OnAccent (≈7:1) · High · High · Theme / every dialog, Settings, placeholder, Popout error bar · Verified

**Observation.** `AccentButton` sets `Foreground` to `OnAccent` and passes `TextElement.Foreground` to its `ContentPresenter` (`Theme/ControlStyles.xaml:141-180`). For string content the presenter generates a `TextBlock` through its default data template, and that TextBlock takes the app-wide implicit `TextBlock` style (`ControlStyles.xaml:96-100`), whose `Foreground` setter outranks the inherited value (WPF precedence: style setter above inheritance). Only `PopOutButton`, whose inner TextBlocks bind `Foreground` explicitly (`MainWindow.xaml:203,209`), renders the intended colour. `DangerButton` (`ControlStyles.xaml:182-206`) has the same defect and its presenter carries no `TextElement.Foreground` at all, so its `White` never ships either. `PresetToggle` in Settings (`SettingsWindow.xaml:47-84`) loses its checked foreground the same way.
**Evidence.** Pixel samples: the dialog **OK** label, Settings **Done** and the Popout **Open normal page** label are RGB 244,247,250 (TextPrimary) on 43,174,208 (accent); the toolbar **Pop out video** label is 6,20,26 (OnAccent) [E-7 `dialog-sharp-dark-info-no-video@96.png`; E-6 `settings-sharp-dark-ready@96.png`; E-5 `popout-sharp-dark-default-error@96.png`; E-2 `main-sharp-dark-default-ready@96.png`]. Affected sites: `Prompt.cs:189,285,373,398`; `SettingsWindow.xaml:341`; `MainWindow.xaml:255,279`; `PlayerWindow.xaml:98`. Ratios with the `Wcag.cs` formula: TextPrimary on the six accents 2.19–3.70:1 across the three presets (worst: minimal's TextPrimary on amber); OnAccent on the same accents 4.70–7.59:1. `AccentPrimary` is not intensity-scaled (`ThemeColors.cs:196`), so intensity 0 does not change this.
**User impact.** Low-vision users cannot read the primary action of every dialog, Settings Done, Bring video back on the placeholder, Get WebView2 Runtime, and Open normal page, under all three presets and all six accents. The irreversible **Delete** confirm ships at 3.19:1 (3.18 minimal, 3.23 soft-glass) as 13-px SemiBold, which is normal-size text and needs 4.5:1.
**System impact.** `AccentReadabilityPolicy` and the dark-on-accent gates in `ContrastReportTests.cs:33-34` and `ThemeCatalogTests.cs:199-206` validate a foreground the app never paints; `ContrastReportTests.cs:31-32` and `ThemeCatalogTests.cs:194-197` assert white-on-Danger at a 3.0 floor, i.e. a pair that never ships and the large-text floor (the Delete label is 13-px SemiBold, not large text). No test asserts a rendered label colour (`WpfRuntimeTests.cs:524-542` asserts `Button.Foreground`, which is correct and irrelevant).
**Recommendation.** Give the `ContentPresenter` in `AccentButton`, `DangerButton` and `PresetToggle` a resources-scoped implicit `TextBlock` style, `BasedOn` the app style, whose `Foreground` binds to the templated control's `Foreground` (`RelativeSource AncestorType=Control`). This keeps Segoe UI and Ideal text formatting, preserves the `IsPressed` foreground trigger, and does not touch `PopOutButton`'s StackPanel content (a template-level `TextBlock` bound to `Content`, as `IconButton` does, would print the panel's type name there). Then add a rendered-label test. For Danger, apply the same readability policy as the accent: an `OnDanger` brush chosen by contrast (the dark ink on every preset's Danger fill, 5.2–5.5:1) and a 4.5 floor in both test files. This changes Delete and Clear from white-on-rose to dark-on-rose; the alternative, darkening the Danger fills until white clears 4.5, touches the pinned palette tables in `docs/Theme_Preset_Differences.md` and is the SND-DESK lane's call if the dark ink is rejected.
**Dependencies.** F-7 (chips' checked foreground returns once the fix lands, which changes the chip cue).
**Validation.** Render an `AccentButton` and a `DangerButton` with string content on the STA test thread, walk the visual tree to the generated TextBlock, and assert its `Foreground` equals the control's `Foreground`; sample the label pixel in a re-captured dialog.
**Revision (implementation, 2026-09-10).** `AccentButton` and `DangerButton` took the presenter-scoped style (without `BasedOn`, which the StaticResource XML invariant forbids); `OnDanger` ships per preset. `PresetToggle` did not: its checked label now stays `TextPrimary` on purpose, because the accent-coloured label fell under 4.5:1 on a dim accent and the F-7 wash plus border already carry the state (`ThemePolishTests.Preset_chip_label_stays_primary_text_when_checked`).

### F-2 — Code-built dialog inputs have no accessible name · High · High · Prompt dialogs · Verified

**Observation.** `Prompt.EditProfile` creates the Name and URL `TextBox`es (`Prompt.cs:227,236`), the playback-mode and presentation `ComboBox`es (`Prompt.cs:117,154`) and `AskText` its box (`Prompt.cs:180`) with neither `AutomationProperties.Name` nor `LabeledBy`; the captions are plain `TextBlock`s, not `Label`s with `Target`. The only automation name in the file is the close X (`Prompt.cs:78`). `DarkTextBox` and `DarkComboBox` add no header, and the WPF peers have no adjacent-text fallback. Every XAML surface does name its controls, so this is a code-built-dialog gap. Spec §20 states that icon-only controls are named; it says nothing about inputs, and the edit dialog is the one place a screen-reader user types.
**Evidence.** [E-15 `Prompt.cs:78,117,154,180,227,236`] [E-7 `dialog-sharp-dark-edit-profile@96.png`, `dialog-sharp-dark-ask-text@96.png`] [E-24 §20].
**User impact.** A screen-reader user tabbing through Edit profile hears unnamed edit and combo fields and cannot tell Name from URL; the same in the name prompt when saving a profile.
**System impact.** None.
**Recommendation.** Set `AutomationProperties.SetLabeledBy(box, caption)` (or `SetName`) in the builders for every input; keep the captions as they are.
**Dependencies.** None.
**Validation.** Build each dialog on the STA thread and assert every `TextBox`/`ComboBox` has a non-empty `AutomationProperties.Name` or a `LabeledBy` element.

### F-3 — "Get WebView2 Runtime" is the accent action in every browser-failure state, and the only enabled one while restarting · Medium · High · Source browser-failure panel · Verified

**Observation.** The panel has one un-named accent button wired to the runtime download and one `Retry` (`MainWindow.xaml:279-283`). `ShowBrowserState` changes only the heading, the body and `Retry.IsEnabled` (`MainWindow.xaml.cs:303-309`). Callers: runtime not found and could-not-start pass `retryEnabled: true` (lines 259, 268); reload, recreate and restarting pass `false` (360, 372, 448); keeps-failing passes `true` (377). In every one of these a live core existed except the first two, so the download link is never the fix there, yet it is the accent action, and during **Restarting the browser** it is the only enabled control.
**Evidence.** [E-9 `MainWindow.xaml:271-283`] [E-10 `MainWindow.xaml.cs:259,268,303-309,360,372,377,448`] [E-22 `WebViewProcessFailurePolicy.cs:115-135`] [E-4 `main-sharp-dark-default-restarting@96.png`].
**User impact.** A user with a working runtime is steered to an external download page mid-restart or after repeated crashes. Severity is Medium, not High: the recreate self-heals and Retry is one click away, so the cost is a wasted download and confusion, not a blocked task.
**System impact.** None; recovery proceeds unattended while the user leaves the app.
**Recommendation.** Name the button; keep it as the accent action only for runtime-not-found; show it as a secondary `DarkButton` for could-not-start and keeps-failing (a broken runtime is a real cause there); hide it while a reload, recreate or restart is in progress. Make `Retry` the accent action whenever the link is secondary or hidden.
**Dependencies.** F-4 (same panel copy).
**Validation.** Drive each state through the existing test hook and assert the download button's visibility and style per state.

### F-4 — While the Source browser is failed or restarting, "Bring video back" acts as if it would play now · Medium · High · Source toolbar, placeholder, failure panel · Verified

**Observation.** The Open-state `PopOutButton` and the placeholder buttons are enabled by `_popoutInProgress`, `BrowserDataClearActive`, `_mainWindowClosing` and `_player` alone (`MainWindow.xaml.cs:2193-2208`); `BringVideoBackAsync` has no browser guard (2115-2127). The return itself is handled: the snapshot is queued for the replacement core (2448-2467), the URL is queued (850-856), and a recreate replays it without user action (435-460), as spec §15.4 and ADR-0010 require. The Popout's own Close takes the same path (`PlayerWindow.xaml.cs:954→1491→1528`; `MainWindow.xaml.cs:2409`), so gating the toolbar would not change the outcome and would block a self-healing path. What is missing is the message: neither the panel copy (`WebViewProcessFailurePolicy.cs:118-134`) nor the return action's tooltip says that the video is waiting and returns when the browser is back.
**Evidence.** [E-10 lines above] [E-12 `PlayerWindow.xaml.cs:954,1491,1528`] [E-22 `118-134`] [E-26 ADR-0010] [E-24 §14, §15.4]; `MainWindowRecoveryTests.cs:344` proves the queued replay.
**User impact.** Clicking the promising primary action closes the one window that was playing; the video reappears only after the restart or Retry, with nothing on screen having said so.
**System impact.** None; the queued snapshot is correct.
**Recommendation.** Do not gate the return. Add one sentence to the panel body when a Popout or a queued return exists ("Your video is waiting and returns here when the browser is back"), and switch the return action's tooltip to the same message while failed or restarting.
**Dependencies.** F-3.
**Validation.** Attach a player, enter the failed state through the test hook, assert the panel body and the tooltip.

### F-5 — Source navigation, URL box and profiles stay live behind the opaque failure panel · Medium · High · Source toolbar · Verified

**Observation.** `SourceCommandsAvailable` (`MainWindow.xaml.cs:1152-1154`) names `_sourceNavigationSuspended`, `_returnInProgress`, `_popoutInProgress`, `BrowserDataClearActive` and `_mainWindowClosing`, not `_browserFailed` or `_browserRecoveryInProgress`, while `CanStartVideoPopout` (1924-1926) does check `_browserReady`. So Back, Reload and Home are dead no-ops on a null core (888-893), Enter in the URL box (and Ctrl+L / F6 per spec §20) queues silently (850-856), and picking a profile changes the accent and Pin but not the page (1180-1197) and overrides the queued return, a drop the spec allows but the UI never announces.
**Evidence.** [E-10 lines above] [E-4 `main-sharp-dark-default-restarting@96.png`] [E-24 §15.4, §20].
**User impact.** Half of each action lands: the user sees an accent change or nothing, and a queued return can be lost by a profile click that looked harmless.
**System impact.** `_pendingUrl` overwritten; the queued snapshot dropped at the next navigation.
**Recommendation.** Fold the failed and recovering states into `SourceCommandsAvailable` so the nav, URL and profile groups disable with the panel, and re-enable them where `_browserFailed` clears; Settings stays available.
**Dependencies.** F-3, F-4 (same panel).
**Validation.** Enter the failed state through the test hook; assert the three groups are disabled and re-enable after the simulated Retry.
**Revision (implementation, 2026-09-10).** The recommendation conflicted with spec §14 and §15.4 and the changelog, which pin that the failed state releases the Source commands so the user can steer the replacement core (the queued return becomes "the page the replacement browser opens"). The landed scope is narrower: Back and Reload, which call straight into the core, disable while no live core exists; the address box, Home and profiles stay enabled; and the failure panel now names the queued video or page (F-4), so a profile click that replaces the queued return is announced as "The page you chose opens when the browser is back" instead of dropping it silently (`MainWindowPolishTests.Back_and_reload_wait_for_a_live_core_while_the_address_box_stays_open`, `A_newer_link_outranks_the_queued_video_in_the_note`).

### F-6 — Disabled controls cannot explain themselves, and the compact toolbar hides the only in-progress state text · Medium · High · Source toolbar, Settings · Verified

**Observation.** `ToolTipService.SetShowOnDisabled` is set once in the app, on Settings' Clear button (`SettingsWindow.xaml.cs:174`). `ApplyPopoutActionState` writes state-explaining tooltips for Returning and Clearing and then disables the button (`MainWindow.xaml.cs:2186-2199`), so those tooltips can never display; the same swallow hides the disabled Ready tooltip before the browser is ready, Settings **Done** when the accent is unreadable (`SettingsWindow.xaml:343`), and `Retry` while restarting. Edit/Delete selected profile carry no tooltip at all (`MainWindow.xaml:163,170`). In the compact toolbar (`CompactToolbarThreshold` 940, measured against the toolbar width; window `MinWidth` 760) `PopOutButtonText` is collapsed (911-916), Clearing uses the Ready glyph and Returning the Open glyph (2183), so the only visible difference between Ready and Clearing is the disabled opacity, for a clear that can run past the 30-s foreground wait and a return bounded at 20 s.
**Evidence.** [E-14 `SettingsWindow.xaml.cs:174`] [E-10 `911-916,2183,2186-2199`] [E-13 `343`] [E-9 `163,170`] [E-2 `main-sharp-dark-min-ready@96.png`] vs [E-3 `main-sharp-dark-min-clearing@96.png`, `main-sharp-dark-min-returning@96.png`] [E-24 §14, §19]. The adversarial pixel diff of ready versus clearing at 760 wide is confined to the button and is opacity only.
**User impact.** In a small window the user sees a dead icon with no reason, reads it as a hang, and may force-close mid-clear. Spec §19 says the button "reads Clearing browser data…"; compact never renders it.
**System impact.** None.
**Recommendation.** Set `ShowOnDisabled` on `PopOutButton`, `RuntimeRetryButton`, Settings `Done` (and let Done's disabled tooltip name the unreadable accent), and give Edit/Delete profile a tooltip. In compact mode keep a short label for the two transition states ("Returning…", "Clearing…") and use a distinct in-progress glyph so the states differ from their neighbours by shape, not only opacity.
**Dependencies.** None.
**Validation.** `ApplySourceToolbarLayout(760)` then each state; assert glyph, label visibility and `ToolTipService.GetShowOnDisabled`.

### F-7 — Toggle "on" state is colour-only: glyph hue plus a 1-px accent ring · Medium · High · Source Pin/Auto, Popout Fade/Pin, Settings chips · Verified

**Observation.** `ToggleAccent.Apply` swaps only `Foreground` and `BorderBrush` (`ToggleAccent.cs:63-75`); `PinToggle` keeps `BorderThickness` 1 always on with a transparent brush, so checked shows a 1-px accent ring and an accent glyph (`ControlStyles.xaml:280-313`), no fill, weight or shape change. `PresetToggle` chips change only their border colour today because their checked foreground never renders (F-1). Luminance contrast between the unchecked and checked glyph colours: 1.01:1 sharp-dark, 1.71 minimal, 1.88 soft-glass. The Source Pin has a text cue ("• Pinned", `MainWindow.xaml:56`); Auto, Popout Fade and Popout Pin have none.
**Evidence.** [E-19 `ToggleAccent.cs:63-75`] [E-18 `ControlStyles.xaml:280-313`] [E-13 `SettingsWindow.xaml:47-84`] [E-2 `main-sharp-dark-default-pinned-unsaved-auto@96.png`] [E-6 `settings-sharp-dark-not-ready-focused-autohide@96.png`]. UIA `ToggleState` is correct, so screen readers are unaffected.
**User impact.** Users with colour-vision deficiency cannot tell Auto, Fade or Popout Pin on from off, nor which theme, corner or fade chip is selected.
**System impact.** None.
**Recommendation.** Give checked toggles a filled background (an accent wash) and chips a SemiBold weight, so the cue is luminance and weight as well as hue. The unused `SwatchToggle` style already fills.
**Dependencies.** F-1 (chip foreground).
**Validation.** XAML invariant: the `PinToggle` and `PresetToggle` templates carry an `IsChecked` trigger that changes `Background` or `FontWeight`.

### F-8 — The unrecoverable Reset app state and the silent Overwrite profile are the least-warned destructive prompts · Medium · High · Settings, profiles · Verified

**Observation.** Reset app state confirms with `danger: false` and a body that names no loss (`SettingsWindow.xaml.cs:344-345`; `PrivacyService.cs:20-23`), while the recoverable Clear browser data uses `danger: true` (359-360) and Delete profile says "This can't be undone." (`MainWindow.xaml.cs:1279-1280`). Overwrite profile (`MainWindow.xaml.cs:1216-1217,1258-1259`) is not styled as danger and never says the stored URL, mode and colour are replaced.
**Evidence.** [E-14 `344-345,359-360`] [E-20 `20-23`] [E-10 `1216-1217,1258-1259,1279-1280`] [E-7 `dialog-sharp-dark-confirm-danger@96.png`, `confirm-plain@96.png`].
**User impact.** Permanent loss of every saved profile behind the weakest warning; silent replacement of a tuned profile during a quick save.
**System impact.** None.
**Recommendation.** `danger: true` and "Saved profiles can't be recovered." for Reset; for Overwrite name what is replaced, style as danger and label the button **Replace**. `PrivacyServiceTests` pins the constants and needs the same edit.
**Dependencies.** None.
**Validation.** Assert the constants and the `danger` argument at both call sites.

### F-9 — "Settings not saved" never clears, and its explanation cannot be reached · Medium · High · Source title bar · Verified

**Observation.** The hint is set visible once (`MainWindow.xaml.cs:1885`) and nothing collapses it; `_settingsSaveRefusalShown` is never reset. `PerformResetAppState` (1409-1422) resets in-process, and `SettingsService.Reset` clears the unread block (`SettingsService.cs:156`), so the next save succeeds and `SaveSettings` returns early (1883) without touching the hint. The only explanation is a tooltip on a non-focusable `TextBlock` that sits in the caption area without `WindowChrome.IsHitTestVisibleInChrome` (`MainWindow.xaml:19,52-63`), so the caption hit-test takes the mouse and the tooltip cannot open.
**Evidence.** [E-10 `1409-1422,1880-1889`] [E-9 `19,52-63,67-68`] [E-2 `main-sharp-dark-default-pinned-unsaved-auto@96.png`].
**User impact.** After a successful Reset the user is still told saving fails and may run the destructive reset again; before that, the marker is cryptic.
**System impact.** None; settings are saved correctly, only the marker is stale.
**Recommendation.** Hide the hint and reset the flag on the next `Saved` result; opt the hint `TextBlock` into chrome hit-testing (the title text and icon stay draggable).
**Dependencies.** None.
**Validation.** `SaveSettingsForTests` after replacing the service with one that saves; assert the hint collapses.

### F-10 — The failure panel overpaints the live placeholder; a failed Clear keeps its side effects unannounced · Medium · High · Source placeholder, Clear browser data · Verified

**Observation.** `RuntimeErrorPanel` and `SourcePlaceholder` are siblings in one Grid, the panel last and opaque (`MainWindow.xaml:241,271`); `ShowBrowserState` never collapses the placeholder, whose Show Popout / Bring video back stay enabled and Tab-reachable behind the panel (`MainWindow.xaml.cs:2207-2208`). Separately, Clear browser data closes the Popout and drops the pending return before the clear starts (1766, 1773), by design (spec §19); on exception the user gets `ClearFailed` and `finally` restores nothing (1810-1823); `ClearConfirmBody` (`PrivacyService.cs:33-35`) does not mention the Popout, and nothing reopens it.
**Evidence.** [E-9 `241-285`] [E-10 `1748-1759,1766-1773,1810-1823,2207-2208,2245-2250`] [E-20 `33-35,48-50`] [E-24 §19].
**User impact.** Blind Tab onto an invisible button returns the video; a clear that did nothing still cost the user the popped-out video and their place.
**System impact.** None; the return queues correctly (F-4) and the clear's ordering is the spec's.
**Recommendation.** Disable the placeholder while the panel is shown and re-enable on hide. Add "The Popout closes first." to the clear confirm body.
**Dependencies.** F-3, F-4 (same panel).
**Validation.** Show both surfaces; assert placeholder `IsEnabled`; assert the constant.

## Cross-cutting themes

- **One implicit style, four symptoms.** The app-wide `TextBlock` style is the root of F-1, the Danger label, the chips' lost checked foreground (F-7) and, harmlessly, the combo's content site. Fix it once at the presenter and add the rendered-label test that would have caught it.
- **Failure states are decided correctly and communicated poorly.** F-3, F-4, F-5 and F-10 are all "the code queues, gates or heals, and the UI still offers the action as if it acted now". The fixes are copy and enablement, not lifecycle changes.
- **Disabled means silent.** F-6 and the Done/Retry/Edit/Delete cases share one missing property.
- **Destructive hierarchy inverted.** F-8: the recoverable action is styled as dangerous; the unrecoverable ones are not.
- **Vocabulary drift at the edges.** One "main window", lowercase "popout" in the Popout tooltips, "Popout behaviour" beside "Accent color", "Video Popout" unused in Settings (appendix A-1, A-7).

## Action plan

**Immediate (this pass, WPF-lane verifiable).** F-1 presenter style plus rendered-label test and Danger-row correction; F-2 dialog input names; F-3 panel action set; F-4 waiting-video copy; F-5 command gating on failed/recovering; F-6 `ShowOnDisabled` set, compact transition labels and glyph; F-8 prompt wording and danger styling; F-9 hint clears and reachable; F-10 placeholder disabled under the panel and clear-body sentence; F-7 checked-state fill and weight (the structure is testable here; the look wants the visual pass); appendix A-1 to A-4 copy and section order.
**Near-term.** Appendix A-5/A-6 (Popout tooltips lead with the return verb; distinct Show Popout glyph).
**Structural.** None required. A framework move (WPF to WinUI 3 or similar) was raised during the review. None of the ten findings is WPF-specific (style precedence, copy, enablement, prompt naming), so a migration would not close them, and it would rewrite the borderless chrome, the theme system and both WebView2 hosts. If pursued it is an ADR, not a polish item.
**Validation.** `scripts/Test-LocalCI.ps1` on this machine for every item above; the SND-DESK lane for the look of the accent labels, the checked-toggle fill, the compact transition labels, the failure panel in each state, and the two open keyboard questions.

## Open questions

1. Keyboard focus inside the profile combo and the profile actions menu: `DarkComboBoxItem` and `DarkMenuItem` set no `FocusVisualStyle`; the highlight is a `SurfaceHover` background at 1.27:1 against `SurfaceBase`. Needs a live keyboard pass (SND-DESK).
2. Is a 1-px accent ring plus glyph hue an acceptable checked cue for the icon toggles once F-7's fill lands, or should Auto and the Popout toggles gain a text cue like the Source Pin?
3. Confirming Reset or Clear from Settings closes the dialog with `DialogResult = true`, which commits any appearance preview the user had not pressed Done on (`MainWindow.xaml.cs:1368-1388`). Intended?

## Evidence & confidence

**Verified.** F-1 (pixels and precedence rule), F-2, F-3, F-4, F-5, F-6, F-8, F-9, F-10 at the cited lines; F-7's structure; all appendix items marked Verified.
**Uncertain.** F-9's tooltip suppression is an interpretation of `WindowChrome` caption hit-testing; F-7's severity depends on whether a 1-px ring counts as a cue.
**Assumed.** Captures rendered from source match what the deployed build paints for the same tree at the same DPI; no hover/fade state changes the cited pixels.
**Untested.** Live keyboard focus, hover, fade, DWM frame, deployed build, YouTube content.

## Appendix

### A — Remaining findings

- **A-1 · Medium · Verified** — Popout-failure info says "It stayed in the main window." (`MainWindow.xaml.cs:2085`); the product term is Source Window (`docs/AGENTS.md:19,25`). Replace with "Playback stayed in the Source Window."
- **A-2 · Medium · Verified** — Popout presentation (Focused overlay) sits under Appearance above the "Popout behaviour" header (`SettingsWindow.xaml:221-241`). Move it below the header as the first control.
- **A-3 · Medium · Verified** — Edit profile offers a "Playback mode" combo with two labels and one outcome while Compact is dormant (`Prompt.cs:114-127`; spec §10.2). Hide it while `CompactPlayerEnabled` is false.
- **A-4 · Medium · Medium** — The "In use" opacity slider also tints the Source title bar but sits under the Popout-only header with the exception in helper text (`SettingsWindow.xaml:240,263-280`). Name both windows in the row label.
- **A-5 · Medium · Interpretation** — The Popout chrome never says "Bring video back"; return is only the Close X with tooltip "Close popout (return video)" (`PlayerWindow.xaml:76`). Lead the tooltip with the verb.
- **A-6 · Low · Verified** — Glyph E8A7 is "Pop out video" in Ready and "Show Popout" in Open (`MainWindow.xaml:193,201`); in compact with the placeholder up two unlabelled icons duplicate the labelled placeholder pair. Consider a distinct glyph for Show Popout.
- **A-7 · Low · Verified** — Copy drift: "Focused overlay" never names its off-state Standard; intensity reads "Off" at 0 while the tooltip says 0 still colours the main action (`SettingsWindow.xaml.cs:319-320`; `SettingsWindow.xaml:212`); "behaviour" beside "color"; window `Title` "PiPlay settings" versus visible "Settings"; Popout tooltips say lowercase "popout"; `ContrastReportTests.cs:33` labels #00D4FF the install default while `ThemeCatalog.DefaultAccentColor` is #2BAED0; the XAML default panel body (`MainWindow.xaml:277`) says "reopen PiPlay" where the code path says "click Retry" (never shown).
- **A-8 · Low · Verified** — The profile combo truncates long names at 140 px with only a static "Saved profiles" tooltip; the Source Pin silently unpins and disables while popped out (`MainWindow.xaml.cs:969-979`); the Edit-profile "Profile color" checkbox is the unstyled system control; `SwatchToggle` is defined and unused; the accent picker's disc is an `Image` (mouse-only; RGB and hex boxes give a keyboard path).
- **Passed, no finding.** TextSecondary on every surface 6.72–11.99:1; the placeholder note's amber 8.31:1; the URL-box focus border 4.77–7.95:1 at every accent and intensity; `KeyboardFocusRing` on every button, toggle, combo and slider style; every icon-only XAML control named; 144 DPI renders without clipping; the Popout chrome at 320×180 and 480×270 and both error bars raised nothing at Medium or above.

### B — Registry snapshot

| ID | Finding | Severity | Confidence | Status |
|---|---|---|---|---|
| F-1 (AC-1, AC-2) | Accent/Danger string labels render TextPrimary | High | High | verified (pixels + Wave 2) |
| F-2 (AC-4) | Dialog inputs unnamed | High | High | verified (Wave 2) |
| F-3 (RK-1) | Runtime link primary in every failure state | Medium (from High) | High | verified; adversarial weakened |
| F-4 (RK-2) | Return offered as immediate while failed | Medium (from High) | High | verified; gating fix falsified |
| F-5 (AD-1) | Source commands live behind the panel | Medium | High | verified |
| F-6 (US-1, US-2, AC-3, RK-5) | Disabled tooltips; compact transition states | Medium | High | verified (merged) |
| F-7 (AC-5) | Checked state colour-only | Medium | High | verified; "hue-only" partially refuted (1-px ring) |
| F-8 (IA-3, IA-4) | Reset / Overwrite under-warned | Medium | High | verified |
| F-9 (RK-6, AD-2) | Unsaved hint never clears / unreachable | Medium | High / Medium | verified (merged) |
| F-10 (RK-3, RK-4) | Panel over placeholder; failed clear side effects | Medium | High | verified (merged) |
| A-1 … A-8 | Copy, order, dead combo, tooltips, minor | Medium–Low | High–Medium | accepted |
| O-1 … O-3 | Combo/menu focus; ring sufficiency; preview commit on Reset/Clear | — | — | open questions |

Prediction check, gate log and the full registry are in the review working directory (`review-controller/piplay-polish/`, disposable).
