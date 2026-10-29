# DOSBox Enhanced 0.6.2

- Background automation starts minimized and does not steal focus or capture the host pointer. Modal notifications are logged in this mode.
- MCP OCR supports a bounded crop and optional yellow/white text isolation.
- DOS mouse clicks accept a 20–5000 ms hold duration (80 ms by default).
- Phoenix source is pinned to 0.19. DOSBox uses its standalone native library; addon-only presentation and label controls are not DOSBox features.
- Matching original/enhanced screenshot pairs, existing environments and saved controls are retained.

Replace app/runtime files while preserving your data folder. No games, personal settings, model DLLs or weights are bundled. Retained native kernels target sm_120; other GPU generations are not qualified here.

Fresh validation covers the host self-tests and a source build of the pinned native library and bridge. Existing renderer/runtime receipts retain their original hashes and scope; this release makes no new NVIDIA NR/SR, Frame Generation, broad gameplay or performance claim. See BUILDING.md and dependencies.lock.json for reproducible source inputs.
