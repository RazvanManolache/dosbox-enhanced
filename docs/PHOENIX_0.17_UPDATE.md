# Phoenix 0.17 integration in DOSBox Enhanced 0.6

- Pinned source: `f8f4ddf729e8d9a117269ade006314b87845649d`, public tag 0.17.
- Native tuning and pipeline APIs drive the detail, temporal, performance and color panels; the same settings persist per environment and are exposed through MCP.
- Both Original and Lightweight models ran on the tested RTX 5080. Model selection is app-wide and applied after restart.
- Native SDK includes both public headers. The retained MSVC patch splits a large NIS shader string into adjacent literals with identical bytes; the build reverses it afterwards.
- Existing model-free sm_120 kernels are reused. Other physical GPU generations are not certified by this release.
- Main game showcases retain v0.5's per-game one-pass presets. Setup/control images show v0.6; no new screenshot-quality comparison is claimed for the older showcase.
- Models, weights, user environments and saves are excluded from source and portable assets.

[Current release validation](RELEASE_0.6_VALIDATION.json). [Graphics controls and downloads](GRAPHICS.md).
