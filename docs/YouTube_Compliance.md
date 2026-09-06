# YouTube usage and compliance policy

Engineering boundary, not legal advice. Anchors: `YouTubeDomBridge`, `NavigationPolicy`, `PlayerFirstSurfaceProtocol`, `PlayerSurfaceDragProtocol`, `PlayerShellProtocol`.

## Allowed surface

PiPlay displays real YouTube pages through WebView2 and adds native window behavior. YouTube owns login/session, playlists, mixes, captions, settings, quality, branding, and ads. PiPlay may read/apply local playback state, transfer playback to its native Popout, and add the optional Focused layout over the real `/watch` page. It does not proxy, extract, replace, or re-host media.

Focused is best-effort and reversible. It uses `object-fit: contain`; nonmatching ratios letterbox rather than crop. Empty overlay pixels pass input. Native YouTube controls, branding, settings, quality, captions, fullscreen, ad UI, disclosures, links, and Skip controls remain reachable. Captions/Next convenience actions delegate to native controls and do nothing when unavailable (`YouTubeDomBridge`). Selector or injection failure withdraws the overlay and leaves the native recovery strip usable.

## Prohibited behavior

Do not download, rip, copy offline, proxy, extract, or re-host media; block, skip, accelerate, or hide ads; change monetization; bypass DRM, age, region, login, or playback restrictions; remove required controls/branding; or inspect/store Google/YouTube credentials.

While YouTube reports `ad-showing` or `ad-interrupting`, custom code must not write `currentTime`, change playback rate, invoke Next, or issue another skip-capable action. Unknown ad state fails closed. User play/pause/mute may delegate to native controls but PiPlay must not automate them to affect ad delivery. Host writes enforce this gate: while the player element carries `ad-showing` or `ad-interrupting`, or ad state is unknown (missing player element or failed probe), host scripts write no `currentTime` and change no `playbackRate`, failing closed; volume/mute and user-requested pause/play remain allowed, and Next remains gated as before. (`YouTubeDomBridge`.)

## Host-action boundary

Focused may request only `close`, `pinToggle`, `fullscreenToggle`, and `settings`. Messages require the exact schema/version, window nonce, current document token, trusted top-level HTTPS YouTube source, and trusted input. Reject unknown fields/actions, malformed payloads, stale documents, foreign sources, and synthetic events. No message may expose arbitrary URLs, scripts, shell commands, credentials, filesystem access, or pointer coordinates. (`PlayerFirstSurfaceProtocol`, `PlayerSurfaceDragProtocol`.)

Before public distribution, check the current [YouTube Terms of Service](https://www.youtube.com/static?template=terms) and [IFrame API documentation](https://developers.google.com/youtube/iframe_api_reference) against the requested behavior.
