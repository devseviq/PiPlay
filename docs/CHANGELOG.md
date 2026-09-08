# Changelog

## Unreleased

- A second launch now waits for the running instance to answer before it exits, takes over as the running instance when the one that was there is closing or already gone, and says the running window did not respond when it cannot take over.
- A link handed to the running instance goes to whichever window owns playback: a video link retargets an open Video Popout and brings it forward instead of changing the Source page behind it, a playlist link waits with a note on the Playing in Video Popout panel until Bring video back, and a link that arrives while a Popout is opening, playback is returning, or browser data is being cleared shows in the address box and opens when that finishes.
- When a Source page's renderer crashes PiPlay reloads it behind a short notice, when the browser component stops PiPlay restarts it and returns to the page you were on, and after three automatic recoveries within a minute PiPlay stops restarting it and leaves a notice with Retry.
- A Popout whose page crashes reloads it in place and shows a notice until the page comes back, and a Popout whose browser component stops closes and hands playback back to the Source, which restarts its own browser before it takes the return.
- A Popout that advances to another video on its own or is retargeted by a new link returns that video with its own position, pause, mute, volume, and rate instead of the previous video's.
- Pop out video now reads as clearing browser data and stays unavailable until the clear itself finishes, and a clear that outlasts the status wait shows the signed-out page once when it completes in the background, without a second prompt.
- Same-video return now waits for a clear page before seeking or changing rate, and a Source identity change during that wait drops the stale write.
- A return that lands while the Source browser is restarting or has given up keeps the video, its position, pause, mute, volume, and rate for the replacement core, including the one Retry starts; a same-video replay the failure interrupts keeps its snapshot for the page the replacement core reopens.
- The Source controls come back as soon as the browser gives up or a restart runs long, and the waiting return still replays once the replacement browser is ready.
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
