# DOSBox Enhanced 0.6

- Updated Phoenix DLSS5 to 0.17 (`f8f4ddf729e8`).
- Original / Lightweight selection; advanced temporal, detail, block, processing-scale and color controls.
- Processing budgets, priority, GPU monitoring and matching per-environment MCP settings.
- Graphics setup with verified NVIDIA runtime downloads, optional pinned OpenNR compatibility runtime, existing-model import and bundled engine repair.
- Numeric quick sliders and collapsible advanced panels.
- Existing environments and one-pass game presets are retained.

Validation: 54 self-tests, 56 UI checks, 194 MCP checks, both renderers, 48 live layout changes, Original/Lightweight GPU execution, advanced-control pixel changes and verified setup downloads. Foreground presentation test: 1,499 generated frames, NVIDIA status bits 0. RTX 5080; bundled native kernels target sm_120.

No games, personal settings, model DLLs or weights are bundled. Game showcase images are retained from v0.5; setup/control images show v0.6. Other GPU generations are not certified by this DOSBox release.

Download the Windows portable ZIP to run, or the source ZIP to build with the documented pinned dependencies. Existing users can replace app/runtime files while preserving their data folder.
