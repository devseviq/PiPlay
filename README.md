<p align="center"><img src="docs/assets/app-icon/piplay-256.png" alt="PiPlay icon" width="96" height="96"></p>

# PiPlay

**Watch YouTube in a floating window that stays out of your way.** Browse YouTube as usual in PiPlay's main window, signed in to your own account, then press **Pop out video**. The video moves into a small borderless player that you can place anywhere, resize, pin above other windows, and fade while you work. **Bring video back** returns it to the main window at the same point in the video. Your playlist, volume, mute, and speed come back with it.

PiPlay shows the real YouTube pages and keeps YouTube's own controls, captions, quality settings, and ads. It is a Windows app (WPF with Microsoft WebView2) and runs from a folder with no installer.

## Highlights

- **One Video Popout.** A borderless window you can move, resize, and pin, running the real YouTube player. Drag it by its top bar or by the video itself.
- **Seamless hand-off.** Pop out and bring back keep the video, playlist or mix, position, play/pause state, volume, mute, and speed. The main window's audio stays muted and paused while the Popout plays.
- **Park it anywhere.** Right-click the Popout's top bar to move it to a screen corner or to size it for Small (480 × 270), Medium (640 × 360), or Large (960 × 540) 16:9 video. Double-click the top bar, or press F11, to fill the screen.
- **Get the chrome out of the way.** Fade dims the top bar when you are idle, and Auto-hide collapses it so the video fills the window. Opacity settings make the whole Popout translucent, separately for in use and idle. The Popout always accepts clicks; it never turns click-through.
- **Auto.** Optionally pop out every video you start playing on a `/watch` page. It is off by default.
- **Profiles.** Save a page as a profile with its own accent colour, Pin setting, and Popout presentation: the standard watch page, or the Focused overlay that puts compact controls over the video.
- **Themes.** Sharp Dark, Minimal, and Soft Glass, each with an accent colour and intensity. Corners can follow the theme or be Square, Small round, or Round.
- **Links go to the right place.** Open a YouTube link with `PiPlay.exe <link>`, or launch PiPlay again with one. A running PiPlay receives it: a video link goes straight to an open Popout, and anything else goes to the main window.
- **Recovers by itself.** If the browser component crashes, PiPlay reloads or restarts it and keeps the video you were watching.

## Keyboard and mouse

These shortcuts work while the video has keyboard focus. YouTube's own keys also keep working: Space/K, J/L, M, F, C, and the arrow keys.

| Where | Shortcut | Action |
|---|---|---|
| Main window | `Ctrl+L` or `F6` | Go to the address box. Enter opens a link or video ID, or searches YouTube. |
| Main window | `Ctrl+Shift+P` | **Pop out video**, or **Bring video back** if the video is already popped out. |
| Main window | `Ctrl+T` | Pin the main window on top. |
| Popout | `F11`, or double-click the top bar | Expand to fill the screen, or restore. |
| Popout | `Esc` | Restore an expanded Popout. |
| Popout | `Ctrl+T` | Pin the Popout on top. |
| Popout | `Ctrl+W` or `Ctrl+Shift+P` | **Bring video back**. |
| Popout | Right-click the top bar | Corners, 16:9 sizes, and the actions above. |

## Try the beta

Download the ZIP and matching `.sha256` file for the newest prerelease (currently **0.14.0-beta.1**) from [GitHub Releases](https://github.com/devseviq/PiPlay/releases). The build is unsigned.

1. Install what the release needs: Windows x64, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), the [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), and [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows), which the package check uses.
2. Follow the download and verification commands in the release notes. They confirm the ZIP is the one GitHub published.
3. Extract everything into a new folder that you can write to, and keep the files together.
4. Close any other PiPlay first. Downloaded packages share one single-instance identity.
5. Run `PiPlay.exe`.

Things to try: **Pop out video** on a playing video, then **Bring video back** or close the Popout. Listen for exactly one audio stream, and check that playback picks up where it was. Try a playlist or mix, then move, resize, pin, and park the Popout. Restart PiPlay and check that your settings stuck.

**Known limitations.** Brief audio overlap during ads, autoplay-next, or playlist/mix transitions has not been ruled out. Return keeps the playlist but not your exact position in its queue. The profile-menu shadow and mixed-DPI appearance still need visual testing. Changes to YouTube's pages can affect playback transfer.

## Your data and privacy

PiPlay has no telemetry, analytics, or crash upload, and it never reads your Google or YouTube credentials. Everything it stores lives in one data folder. For a downloaded package this is `PiPlayData` beside `PiPlay.exe`. A build you run from source uses `%LOCALAPPDATA%\PiPlay`. The `PIPLAY_DATA_ROOT` environment variable overrides both.

| What | Where | Contents |
|---|---|---|
| Settings | `settings.json` | Appearance, Popout behaviour, window positions, profiles |
| Diagnostics | `logs\piplay.log` (plus one `.1` backup) | Events with links redacted. No cookies, credentials, or search text. |
| Browser profile | `WebView2UserData\` | Your YouTube/Google session, cookies, and cache, shared by the main window and the Popout |

**Settings → Privacy** has two separate resets. *Reset app state* restores default settings and removes profiles, but keeps you signed in. *Clear browser data* signs you out and clears cookies and cache. To uninstall, close PiPlay and delete its folder, including `PiPlayData`.

## What PiPlay will never do

It will never download, re-host, or proxy media. It will never block, skip, or speed up ads. It will never bypass age, region, sign-in, or DRM restrictions, or hide YouTube's required controls and branding. While an ad plays, PiPlay does not seek or change the playback speed. See [`docs/YouTube_Compliance.md`](docs/YouTube_Compliance.md).

## FAQ

**Why not the browser's built-in picture-in-picture?** The Popout is a real window running the real YouTube page. You keep YouTube's controls, captions, quality menu, playlists, and your signed-in account, and the window can be moved, resized, pinned, faded, and parked where you want it.

**Can I have more than one Popout?** No. PiPlay keeps exactly one Popout so that only one video is ever playing. **Show Popout** brings the existing one forward.

**The window is blank or says "WebView2 Runtime is required".** Install the WebView2 Evergreen Runtime and click **Retry**. You don't need to restart PiPlay.

**How do I report a problem?** Use [Report beta feedback](https://github.com/devseviq/PiPlay/issues/new/choose). Include the release tag, your Windows version, display scaling, the steps to reproduce, what you expected and what happened, and whether ads or a playlist were involved. The log is `PiPlayData\logs\piplay.log`. Check screenshots and logs for personal information before you share them. Never attach browser-profile folders, cookies, or account credentials.

## Development

Prerequisites: the .NET SDK from [`global.json`](global.json) (10.0.300 or a later feature band), Node 24 or newer for the page-script tests, and PowerShell 7.

```powershell
pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1   # restore, Debug tests, Release build; -Plan prints the steps
dotnet run --project src\PiPlay                     # Default channel, data in %LOCALAPPDATA%\PiPlay
```

`PiPlay.exe --help` (or `-h`, `/?`) shows command-line usage in a dialog and exits. To contribute, branch from `main`, run the local gate, and open a pull request. The required check is `Build and test (Windows)`.

| Document | Owns |
|---|---|
| [`docs/PiPlay_Product_Engineering_Spec.md`](docs/PiPlay_Product_Engineering_Spec.md) | Product behavior and quality requirements |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Architecture decisions in force |
| [`docs/AGENTS.md`](docs/AGENTS.md) | Working rules and product vocabulary (Video Popout, Source Window, Pin, Fade, Auto) |
| [`docs/YouTube_Compliance.md`](docs/YouTube_Compliance.md) | Page-script and platform-safety policy |
| [`docs/Theme_Preset_Differences.md`](docs/Theme_Preset_Differences.md) | Theme values |
| [`docs/RELEASING.md`](docs/RELEASING.md) | Test and Stable publication and acceptance |
| [`docs/CHANGELOG.md`](docs/CHANGELOG.md) | User-visible changes |
