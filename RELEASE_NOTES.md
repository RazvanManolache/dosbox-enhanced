# DOSBox Enhanced 0.6.1

- Camera dropdown: save the displayed image, original DOS pixels, or matching original/enhanced screenshots.
- Pairs use one completed graphics call, even while the game moves. Each image retains its own resolution.
- Saved pairs include a JSON receipt with effects, dimensions, frame identity and PNG checksums.
- MCP: `dosbox_screenshot` with `{"pair":true}` returns both images and metadata without saving files. Works in observation mode.
- Missing graphics, reset and stop return an explicit unavailable result; no mismatched fallback.
- NGX initializes on demand for NVIDIA NR/SR. Phoenix without SR can run independently when NGX initialization fails.
- Phoenix source pinned to 0.18 (`76aaad6200d2`). Its standalone rendering code is unchanged from 0.17; addon compatibility fixes do not change DOSBox rendering.

Existing environments and one-pass game presets are retained. No games, personal settings, model DLLs or weights are bundled. Historical game images remain from v0.5; setup/control images remain from v0.6. Bundled native kernels target sm_120; other GPU generations are not certified by this release.

Download the Windows portable ZIP to run, or the source ZIP to build with the pinned dependencies. Existing users can replace app/runtime files while preserving their data folder.

Validation: 60 host checks, 23 capture checks with real Phoenix processing, and 189 authenticated HTTP/stdio MCP checks in explicit non-interactive mode. PNGs round-trip exact pixels and match their receipts.

Current validation limits: NVIDIA NR/SR report NGX unsupported-hardware error `0xBAD00001` on this machine with driver 616.64, also reproduced using the prior v0.6 bridge and the pinned NVIDIA runtimes. No fresh NR/SR or Frame Generation success is claimed. Foreground mouse interaction and desktop visual inspection were unavailable in this session; Windows also exposed no audio output device. Historical v0.6 GPU/interaction results retain their original scope.
