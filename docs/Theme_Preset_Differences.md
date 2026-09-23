# Current theme values

Source: `ThemeCatalog.cs`, `ThemeColors.cs`, `ThemeResourceApplier.cs`. Tests: `ThemeCatalogTests`, `ThemeColorsTests`, `ContrastReportTests`, `Ui/XamlInvariantTests`.

## Presets

| | Sharp Dark | Minimal | Soft Glass |
|---|---:|---:|---:|
| ID / card cue | `sharp-dark` / `Crisp · 100%` | `minimal` / `Quiet · 94%` | `soft-glass` / `Glass · 82%` |
| Default accent | `#2BAED0` | `#3F84C0` | `#9E84F0` |
| Fade / ms | normal / `2500` | long / `4000` | short / `1500` |
| Strip auto-hide | true | true | true |
| Active / idle opacity | `1.00 / 1.00` | `0.94 / 0.86` | `0.82 / 0.72` |
| DWM corners | Default | SmallRound | Round |
| Density / elevation | compact / none | normal / subtle | airy / soft |

Theme changes do not select playback presentation/mode, placement, Pin, profile, browser data, or input transparency.

## Palette

| Token | Sharp Dark | Minimal | Soft Glass |
|---|---|---|---|
| `AppBackground` | `#050609` | `#14120F` | `#0B1018` |
| `SurfaceBase` | `#0B0E12` | `#1C1A16` | `#121A26` |
| `SurfaceRaised` | `#131820` | `#26231E` | `#1B2738` |
| `SurfaceHover` | `#1E2630` | `#312D27` | `#26354B` |
| `BorderSubtle` / `BorderStrong` | `#181F29` / `#262F3D` | `#2E2A23` / `#3C362D` | `#2A3A52` / `#3A4D6A` |
| `TextPrimary` / `TextSecondary` | `#F4F7FA` / `#9AA2AD` | `#F4F1EC` / `#B0A99E` | `#F6F8FC` / `#C4CEDC` |
| `Danger` | `#E45D75` | `#E8564C` | `#E45D75` |

## Geometry and density

Radii are `(main, popout, title; button, icon, input; panel, popup, thumbnail; swatch, scrollbar, tooltip)` in DIP:

| | Sharp | Minimal | Soft Glass |
|---|---|---|---|
| Radii | `2,2,2; 3,3,3; 2,4,2; 4,3,4` | `8,12,8; 8,8,8; 10,10,6; 8,5,8` | `14,22,14; 12,12,12; 16,16,10; 12,6,10` |
| Control / icon / scrollbar | `30 / 30 / 8` | `34 / 32 / 10` | `38 / 36 / 10` |
| Button padding H,V | `10,5` | `12,6` | `16,9` |
| Input padding H,V | `8,0` | `10,0` | `14,2` |
| Menu / preset padding H,V | `8,5 / 8,0` | `10,6 / 10,0` | `14,9 / 14,0` |
| Tooltip padding H,V | `7,4` | `8,5` | `10,7` |
| Default border | `1` | `1` | `1` |

Inner elevation is Sharp `none`; Minimal popup/panel `8 / 1 / 0.22` and `6 / 1 / 0.16`; Soft Glass `16 / 2 / 0.34` and `12 / 2 / 0.26` (blur / depth / opacity). `ElevationPopup` is consumed by the ComboBox popup (`Theme/ControlStyles.xaml`); `ElevationPanel` has no current HWND-airspace consumer.

Corner override `theme` follows the preset; `square` is zero radii plus `Square`; `small` uses Sharp radii plus `SmallRound`; `round` uses Soft Glass radii plus `Round`; legacy `soft` normalizes to `round`. Only a floating effective-`Round` Popout receives the `22 DIP` native region; maximize/snap clears it.

## Accent and persistence

Hover uses `OnAccentHover`, selected for the lightened accent fill. Pressed continues to use `OnAccentPressed` and takes precedence over hover. `DangerHover` mixes the Danger fill 12% toward white while retaining opaque `OnDanger` text. String labels share the `FilledButtonLabel` style so each state and live theme preview reaches the rendered label (`ControlStyles.xaml`, `ThemeResourceApplier`, `ThemePolishTests`).

Offered accents are cyan `#2BAED0`, steel-blue `#3F84C0`, steel `#4A8FAB`, violet `#9E84F0`, green `#2DB57F`, and amber `#D69A2E`. Any valid `#RRGGBB` may be stored exactly; presentation tokens are contrast-corrected. `OnAccent` and `OnDanger` are the label inks picked by contrast for the accent and Danger fills (the dark ink on every preset's Danger); `AccentCheckedWash` is an overlay derived from the accent at alpha `0x2A`, layered over the toggle surface; checked labels use SemiBold. `AccentCheckedGlyph` corrects checked icon contrast against that wash over `SurfaceHover`. Each ships as a frozen brush with a `...Color` twin (`ThemeResourceApplier`, `ThemePolishTests`).

`theme.accentIntensity` is integer `0–100`, default `50`. At `0`, shared background/title reach and the Popout edge are off; primary controls remain accented and profile-row identity keeps preset `SubtleAlpha` via `ProfileRowWashAlpha`. Toolbar glyphs reach full accent at `50`; the wash continues to `100`. Derivation ceilings are `0.06` for `AccentLetterbox` toward black and `0.04` for `AppBackgroundWash` toward the preset background. `PopoutAccentEdge` is a 1 px inset edge with alpha proportional to intensity; Settings uses `{DynamicResource AppBackground}` (`SettingsWindow.xaml`). `ThemeResourceApplier` does not publish `Muted` or `Glow` as resource keys.

Schema `4` persists theme ID, accent, intensity, fade/corner/opacity overrides, active profile, profile accents, and presentation. Null overrides mean “use the preset.” Preset preview applies to open surfaces; **Done** commits, and any other dismissal restores the prior state. (`AppSettings`, `ThemeSettingsWriter`, `ThemePreferenceResolver`, `SettingsWindow`.)
