# About the pictures

These are captures of the running 0.5 Windows app using locally installed games
in a disposable demo workspace. They are not mockups. Demo environment names are
invented for the screenshots. No personal game library, host folder mapping,
connection key, game files or saved game is part of the release.

## Current gallery

- SkyRoads, Titus the Fox, Hexxagon and Indiana Jones and the Fate of Atlantis.
- Ten combinations per game, compared on the same frozen source for that game.
- Individual settings, one pass; final UI captures are from live sessions.
- Before/after pairs use one new frozen frame with the selected settings.
- [Pictures and selected settings](GAME_GALLERY.md) · [Presets](GAME_PRESETS.json) · [Counters, candidate checks and hashes](GALLERY_EVIDENCE.json).
- The older Doom II / SkyRoads images below record the earlier maximum-strength demonstration.

## Earlier maximum-strength captures

- Intensity **2.0**, tone **2.0**, structure **2.0**.
- **One Phoenix DLSS5 pass**. NVIDIA DLSS5 uses one neural evaluation.
- Native-resolution NR, followed by 4× NVIDIA Super Resolution; Frame Generation enabled.
- No preliminary resize. A 320×200 source produces 1280×800 before display aspect correction.
- Default color style; the warm/cool choices are looks rather than strength sliders.
- RTX 5080, driver 616.64; pinned DOSBox Pure 1.0-preview7 and NVIDIA Streamline 2.14.1.

Doom II and SkyRoads are real game captures. The UI images are
rendered from the live WPF controls with the game's real processed framebuffer.
The native presentation window is temporarily hidden for the UI capture so it
can be composed with those controls. These are screenshots of rendered software,
not photographs of a display or captures of generated intermediate frames.

The before/after figure repeatedly processes **one frozen DOS source frame**.
The original is enlarged with nearest-neighbor sampling and the enhanced image
is fitted to the same display size. It illustrates the effect at the stated
settings, not a controlled claim of higher artistic quality.

## Observed graphics counters

| Game | DOS source | SR frames | Generated presentations | NVIDIA FG status bits |
| --- | --- | ---: | ---: | ---: |
| Doom II | 320 × 200 | 163 | 161 | 0 |
| SkyRoads | 320 × 200 | 1054 | 409 | 0 |

Counters are snapshots from one app process and are cumulative across the game
runs, not per-game FPS or a benchmark. They come from the real graphics runtime
and NVIDIA presenter. FG can suspend while the app is in the background or its
menu/settings are open. A still image cannot demonstrate interpolation quality.
These observations establish active execution, not a promised visual improvement
or a frame-rate multiplier in every game.

## MCP demonstration

`mcp-session.png` is a diagnostic view of a real authenticated loopback HTTP
session: initialize/discover, launch, press Enter, set effects, change volume,
read status and retrieve an enhanced PNG. The displayed image is that tool's
actual PNG result. The labels summarize completed calls. There is no generated
AI chat transcript and no claim that a model played the game autonomously.
The capture key was temporary and is not present in these files.

## The deliberately overcooked one

`four-passes.png` uses **four Phoenix DLSS5 passes**, with intensity/tone/structure at
2.0 and SR/FG enabled. It was captured with the earlier 640×384 processing
canvas and 1280×768 output, and is kept only in the collapsible joke/example section.
The current and earlier main galleries use one pass. Repeated neural processing can invent details
and distort the original art; that example is not a recommended preset.

## Credits and reproducibility

Doom II: id Software and the credited creators.
SkyRoads: Bluemoon and the credited creators.
Game artwork and trademarks remain with their respective rights holders.
No game download is included or required by the build/tests.

The developer-only `--showcase <manifest.json>` workflow in `src/Showcase.cs`
requires a disposable app folder and your own games. It refuses an existing
non-demo library. Captures, raw game copies, host paths and temporary server
settings are excluded from release packaging. `CAPTURE_EVIDENCE.json` records
only public settings, counters, tool summaries and screenshot hashes.

The live showcase also verified both processing orders and SR-off mode with both renderers. `upscale-first.png` shows the alternative order. [Pipeline dimensions, GPU checks and repeated resolution changes](PIPELINE_EVIDENCE.json) are recorded separately.
