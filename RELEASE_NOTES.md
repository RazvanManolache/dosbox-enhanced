# DOSBox Enhanced 0.5

- Saved environments with mapped drives, startup programs and separate DOSBox/graphics settings.
- Searchable program tree, friendly names, environment copies and direct EXE/COM/BAT launch.
- Per-environment DOSBox settings editor, volume controls and configurable mouse-release shortcut.
- Optional Phoenix / NVIDIA DLSS 5 neural rendering, NVIDIA Super Resolution and Frame Generation.
- Native input by default; optional 0.25×–4× fractional resize slider with a pixel preview, plus independent NVIDIA SR before or after NR.
- CPU Auto checkbox, live manual cycle slider and exact value, with per-environment manual-speed memory.
- Built-in MCP: 23 tools; disabled by default, authenticated and local-only. Remote access is opt-in.
- Four-game capture gallery, per-game starting presets and 40 same-source settings comparisons.

## Downloads

- `DOSBox-Enhanced-0.5-win-x64.zip`: portable Windows app, DOSBox Pure, model-free Phoenix library/kernels, documentation and setup.
- `DOSBox-Enhanced-0.5-source.zip`: source, pinned submodules, Phoenix API patch, build scripts and CI.
- `SHA256SUMS.txt`: archive checksums.

## Requirements and limits

- Windows 10 build 19041+ or Windows 11; no separate .NET, Python or Node installation needed to run.
- Standard DOSBox use works without neural graphics.
- Optional graphics setup downloads verified NVIDIA runtimes and requires a separately supplied compatible model DLL.
- Graphics tested on RTX 5080; bundled Phoenix kernels target sm_120.
- Image-derived motion and flat depth can produce artifacts.
- Save states require compatible core, settings and game files.
- Main screenshots use individual one-pass presets for four games, with SR and FG enabled. Earlier maximum-strength and four-pass examples are separately labeled.
- No games, personal profiles, host mappings, saves, connection keys, caches, model DLLs or extracted weights included.
- Component attribution and terms: `THIRD_PARTY_NOTICES.md`.
