# Changelog

## Unreleased

- Pin Phoenix 0.8.1-beta (`4c9b75ff42b6`) and build its published native C API
  directly; remove the former compatibility patch and generated-source export.
- Reuse a CMake build directory per Phoenix revision.
- Verify identical native machine code and production-kernel inputs against v0.5;
  its tested portable download and historical release tag remain unchanged.

## 0.5

First prepared public release of the DOS workspace.

- Embedded DOSBox Pure, saved environments, multiple drives, mapped subfolders,
  quick runs and independent environment copies.
- Icon toolbar, searchable program tree across environments, friendly names,
  visibility/order management and optional automatic executable discovery.
- Per-environment DOSBox configuration editor, core options and Advanced INI.
- Optional native Phoenix / NVIDIA neural rendering, NVIDIA Super Resolution and
  Frame Generation, adjustable effects and same-source comparison.
- Native-resolution NR, optional 2×–4× SR before or after NR, and native-only
  processing; live layout changes preserve the graphics device and reset history.
- Optional fractional input resize with independent sharp/smooth filtering, a 0.25×–4× slider and live resolution preview.
- CPU Auto checkbox, live logarithmic cycle slider, exact value and remembered
  manual speed per environment; common CPU changes also apply live through MCP.
- Fullscreen top-edge menu, volume/mute, screenshots and save/load state.
- Configurable mouse capture/release; Caps Lock by default.
- Built-in authenticated MCP over local HTTP and a same-executable stdio bridge:
  23 tools, observation/control modes, optional remote connections.
- Portable Windows x64 packaging, pinned dependencies/submodules, model-free
  graphics assets, reproducible setup scripts and Windows build CI.
- Fresh-release fixes: portable graphics paths, complete native .NET bundling,
  and no mandatory DirectX debug-layer installation for graphics initialization.
