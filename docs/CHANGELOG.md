# Changelog

## Unreleased

- Same-video return now waits for a clear page before seeking or changing rate, and a Source identity change during that wait drops the stale write.
- A return that lands while the Source browser is restarting or has given up keeps the video, its position, pause, mute, volume, and rate for the replacement core, including the one Retry starts; a same-video replay the failure interrupts keeps its snapshot for the page the replacement core reopens.
- The Source controls come back as soon as the browser gives up or a restart runs long, and the waiting return still replays once the replacement browser is ready.
- Duplicate Source renderer crashes coalesce into one reload, and a reload that never finishes counts as another failure so a crash loop still ends in the failed state with Retry.
- A second-launch hand-off that timed out before it started applying no longer takes effect afterwards, and one the running instance cannot apply answers unavailable instead of delaying later hand-offs.
- `PiPlay.exe --help`, `-h`, and `/?` show native usage and exit before normal startup; command-line launch targets remain limited to values accepted by the shared YouTube parser.

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
