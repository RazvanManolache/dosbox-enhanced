# Phoenix DLSS5 public API update

- Public tag: [0.8.1-beta](https://github.com/RazvanManolache/dlss5-phoenix-addon/tree/0.8.1-beta).
- Pinned commit: [`4c9b75ff42b6945352f66f309dc84404e498f4cf`](https://github.com/RazvanManolache/dlss5-phoenix-addon/commit/4c9b75ff42b6945352f66f309dc84404e498f4cf).
- The native C API is now published upstream. The compatibility patch is removed.
- Source builds compile the submodule directly and reuse a CMake output folder per revision.
- No DOSBox host, graphics bridge, environment, MCP or showcase settings changed.

## Verification

- **39 native compilation inputs** match the implementation shipped in v0.5, after line-ending normalization.
- **44 production inputs** match, covering the 41 CUDA kernels, graph, catalogue and builder.
- The rebuilt Phoenix DLSS5 and DOSBox bridge DLLs have **identical machine-code sections** to the released DLLs. Other build metadata may differ.
- The complete app and native bridge build passed using the public source without a patch.
- **44 host checks** passed.
- All **three upstream native tests** passed: spatial library, C header and resize regression.
- A real DOS session verified both renderers, NVIDIA SR, generated presentations with status bits 0, and FG suspension on pause/menu reveal.
- GPU probes passed **48 live layout changes** across Phoenix DLSS5 and NVIDIA DLSS5, including fractional input resize.
- [Source comparisons, machine-code hashes and runtime checks](PHOENIX_UPSTREAM_UPDATE.json).

## Published downloads

- The tested **v0.5 portable download and checksums remain unchanged**; this source update needs no replacement binaries.
- The v0.5 tag and its source ZIP remain a reproducible record of the original release, including its historical compatibility patch.
- The current repository uses the new public Phoenix DLSS5 reference. Its existing one-pass game gallery remains valid for the shipped implementation.
