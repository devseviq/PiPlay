# Changelog

## Unreleased

- Keyboard shortcuts work while the video has focus. In the main window, Ctrl+Shift+P pops the video out or brings it back, and Ctrl+T pins the window. In the Popout, F11 expands or restores, Ctrl+T pins, Ctrl+W or Ctrl+Shift+P brings the video back, and Esc restores an expanded Popout. Holding a key acts once, and tooltips name the shortcut.
- Double-click the Popout's top bar to expand or restore it. Right-click the top bar to park the Popout in a screen corner, to size it for Small, Medium, or Large 16:9 video, or to reach Expand, Pin, and Bring video back.
- The Popout's window title shows the video's name, so the taskbar and Alt+Tab say what is playing.
- After you click Pin, Fade, or Expand in the Popout, Space goes back to YouTube's play/pause instead of pressing the button again. The top bar stays up while a keyboard user has focus in it.
- Pausing a video in the Popout, or reaching its end, no longer starts it playing again a moment later.
- The top edge of the Popout's buttons clicks the button instead of starting a window resize.
- Dragging the Popout by the video works with swapped (left-handed) mouse buttons.
- The browser recovery panel, including "WebView2 Runtime is required" with its Retry and download buttons, is now visible. The browser surface used to cover it.
- After the browser restarts, Auto no longer pops out the video you just brought back.
- When a pop out fails, the main window's video is no longer left muted.
- A Popout whose page keeps crashing closes and returns the video without also restarting the main window's healthy browser.
- Reset app state clears the Settings not saved note.
- Typing an 11-letter word such as "programming" in the address box searches YouTube instead of opening a missing video. Playlist and live-stream embed links open the playlist or are ignored, instead of opening a video that does not exist.
- Only Google's own sign-in domains open inside PiPlay. Look-alike domains open in your default browser.
- PiPlay's ad check now reads the player that holds the video it writes to. A second, hidden YouTube player on the page can no longer make an ad look finished and let a seek or speed change through.
- Only your own Windows account can hand a link to a running PiPlay or answer a second launch. A program that connects and stays silent no longer delays later links. An elevated and a non-elevated PiPlay no longer hand links to each other.
- A settings file PiPlay cannot read keeps its quarantined copy for 30 days from when it was set aside. If it cannot be moved it is copied instead. Profile names ignore capitals, so a settings file holding two profiles whose names differ only in capitals keeps the first as it is and loads the other as "Name (2)", instead of hiding it behind the first.
- The log keeps its backup when a second launch writes to it, and its timestamps read the same in every Windows language.
- A PiPlay that cannot open its single-instance lock shows the "already running" message instead of crashing at start.
- Focused overlay: the controls fade again after you click one, and the progress bar keeps moving after a mouse seek. Next and Captions act only on the playing video's player. Unmute at volume 0 turns the sound back on. The first drag after clicking a control moves the Popout instead of pausing the video.

## 0.14.0-beta.1 — 2026-09-23 (build 40)

Test prerelease for feedback. Live playback/audio and display-scaling acceptance remain pending for this package. Requires Windows x64, the .NET 10 Desktop Runtime, and WebView2 Evergreen; PowerShell 7 is needed for package verification. See the README for setup and known limitations.

- Checked toolbar icons keep readable contrast over their tinted hover background.
- Downloaded-package verification accepts prerelease versions while checking the numeric file version and full product version independently.
- Filled buttons keep their label contrast on hover: accent buttons select ink for the hover fill, and destructive buttons brighten their background without fading the text.
- A second launch now waits for the running instance to answer before it exits, takes over as the running instance when the one that was there is closing or already gone, and says the running window did not respond when it cannot take over.
- A link handed to the running instance goes to whichever window owns playback: a video link retargets an open Video Popout and brings it forward instead of changing the Source page behind it, a playlist link waits with a note on the Playing in Video Popout panel until Bring video back, and a link that arrives while a Popout is opening, playback is returning, or browser data is being cleared shows in the address box and opens when that finishes.
- When a Source page's renderer crashes PiPlay reloads it behind a short notice, when the browser component stops PiPlay restarts it and returns to the page you were on, and after three automatic recoveries within a minute PiPlay stops restarting it and leaves a notice with Retry.
- A Popout whose page crashes reloads it in place and shows a notice until the page comes back, and a Popout whose browser component stops closes and hands playback back to the Source, which restarts its own browser before it takes the return.
- A Popout that advances to another video on its own or is retargeted by a new link returns that video with its own position, pause, mute, volume, and rate instead of the previous video's.
- Pop out video now reads as clearing browser data and stays unavailable until the clear itself finishes, and a clear that outlasts the status wait shows the signed-out page once when it completes in the background, without a second prompt.
- Same-video return now waits for a clear page before seeking or changing rate, and a Source identity change during that wait drops the stale write.
- A return that lands while the Source browser is restarting or has given up keeps the video, its position, pause, mute, volume, and rate for the replacement core, including the one Retry starts; a same-video replay the failure interrupts keeps its snapshot for the page the replacement core reopens.
- The address box, Home, and profile controls come back as soon as the browser gives up or a restart runs long; Back and Reload wait for a live browser, and the waiting return still replays once the replacement browser is ready.
- Duplicate Source renderer crashes coalesce into one reload, and a reload that never finishes counts as another failure so a crash loop still ends in the failed state with Retry.
- A second-launch hand-off that timed out before it started applying no longer takes effect afterwards, and one the running instance cannot apply answers unavailable instead of delaying later hand-offs.
- `PiPlay.exe --help`, `-h`, and `/?` show native usage and exit before normal startup; command-line launch targets remain limited to values accepted by the shared YouTube parser.
- A settings file PiPlay cannot read when it starts is left exactly as it was, and no save overwrites it until a later start reads it or app state is reset.
- The first change PiPlay cannot save after failing to read its settings file adds a title bar note that settings are not saved this session, and the note points at Reset app state as the way to start over.
- Saved window positions come back where they were when the taskbar sits along the top or the left edge of the screen, and positions saved by earlier versions are still read correctly.
- The Popout with rounded corners keeps the last pixel of its right and bottom edges instead of clipping a sliver off them.
- Settings gains a Cancel button that discards the preview and restores the look you had before, the corner choices sit directly under the theme presets, the fade delay choices name their durations, the fade, opacity, and top bar section is called Popout behaviour with Privacy last on the page, and the sliders and chips show a clear focus ring when reached by keyboard.
- A repeating unexpected problem shows one dialog instead of a new one for every repeat, the same problem stays silent for ten seconds after you dismiss it, and a problem PiPlay cannot recover from says the app will close instead of claiming it will keep running.
- PiPlay now waits at most five seconds for a YouTube page to answer a request, so a page that stops responding no longer leaves a pop out stuck waiting.
- Test builds now arrive as GitHub prereleases and Stable builds as GitHub releases, each carrying a ZIP and its checksum; the downloaded package verifies itself against the published release before it runs the UI smoke, and publication stops unless the release is new and its tag is covered by a rule that keeps it from moving or being deleted.
- Filled buttons use contrasting text colours, including destructive confirmations, under every theme preset.
- Checked Pin, Auto, Fade, and Settings choices add a background tint and a heavier label to their outline; checked theme names keep their normal text colour.
- The profile name prompt and the Edit profile dialog announce their inputs by their captions to a screen reader, and Edit profile no longer shows the Playback mode row while the Compact player is off; a stored value survives the edit.
- The Source failure panel leads with Retry and keeps Get WebView2 Runtime as a secondary way out, except when the runtime is missing, where the download leads; a passing notice hides the download and says why Retry waits, the panel says when a returned video or a chosen page is waiting for the browser, and the placeholder beneath it is disabled.
- Bring video back says the video waits in the Source Window while the browser is down, Back and Reload wait for a live browser while the address box, Home, and profiles stay usable, and the compact toolbar keeps a readable Returning... or Clearing... label while the action is unavailable.
- Pop out video, Retry, Done, Edit, and Delete keep a tooltip that says why they are disabled: Edit and Delete ask for a profile first and Done asks for a valid accent colour.
- Reset app state confirms with a destructive button and says saved profiles can't be recovered, and saving or renaming onto an existing profile asks to Replace it with a body that names the profile and what the replacement discards, with Cancel as the default.
- The Settings not saved note clears after a later save succeeds, returns on a new refusal, and shows its explanation when hovered in the title bar.
- Clear browser data says an open Video Popout closes first, and a failed pop out says playback stayed in the Source Window.
- Settings moves Popout presentation under Popout behaviour, the opacity rows say which windows each value reaches, and the Popout close tooltip leads with Bring video back.

## 0.13.2 — 2026-08-23 (build 39)

- The deployed UI smoke now isolates browser/app data, captures the foreground PiPlay HWND in the correct mixed-DPI coordinate space, and fails closed on blank or uniform frames.

## 0.13.1 — 2026-08-23 (build 38)

- Playlist-only launches start the adopted first playable item at the beginning instead of inheriting an unrelated miniplayer, preview, or playlist-URL timestamp.
- Project guidance and the Stable release path are slimmer: one local CI command, an explicit machine-local deployment root, and no single-file/self-contained packaging.

## 0.13.0 — 2026-08-20 (build 37)

- Playlist-only pages can launch the first rendered playable item; return preserves the current video and playlist/mix context.
- Normal Popouts retain `RD...` mix/radio queues. Compact builders omit auto-generated lists; malformed list IDs fall back to one video with a non-blocking reason.
- Profile accents reach the Source/Popout letterbox, Source background wash, profile-row wash, and 1 px Popout identity edge. Intensity `0` removes shared background/edge reach while retaining primary-action identity.
- Source title wash extends through the washed background.
