# Working checkout and retained files

- This project root is the only working checkout and GitHub source tree. Build, test, commit and publish from here. Never create a second source checkout inside `release/` or copy source into a separate publishing folder.
- `external/` contains the pinned dependency submodules. Use these for builds; do not point CMake at a second checkout hidden in release output.
- The installed app uses the top-level `app`, `runtime`, `system`, configuration files and `data`. Preserve saved environments and external game mappings.
- Keep one current development build and the final published release ZIPs, checksums, publication audit and final documentation.
- All first-party source lives in `src/` and `native/`, with shared build scripts, tests and documentation beside them. Ignored legacy adapters are local reference only and excluded from compilation; the app uses `src/NativeProcessor.cs`.
- `release/v0.6.2/prepared` holds the current published archives and checksums; old published archives remain historical releases. `release/v0.5/private` holds private release receipts and maintenance tools, never another app source tree.
- Personal inventory scripts and machine-specific receipts belong in ignored `data/maintenance/`. The consolidation receipt there preserves prior local differences without retaining a second source tree.
- Treat `src/bin`, `src/obj`, `native/build` and `app-update` as disposable staging/compiler output. Do not retain an obsolete `app-update` executable beside the current `app` build. Keep compact test-result JSON receipts; discard raw test screenshots after their useful evidence is retained in final documentation.
- Keep Git object storage self-contained when relocating checkouts. Do not leave submodule `objects/info/alternates` pointing to an old workspace.
- Keep unique maintenance scripts and small final validation receipts. Remove duplicate downloads, extracted verification copies, copied test games, raw capture experiments and obsolete build outputs after verification.
- Do not create timestamped full-release copies such as `updated-readme-*`, `updated-showcase-*`, `final2`, or successive backup folders. Use one disposable staging directory and clean it when finished.
- README and screenshot edits do not justify repackaging unchanged executables. Replace the working output; retain another build only when the user explicitly asks or an active rollback requires it.
- Before cleanup, verify exact paths and dependencies. Never follow game junctions or symbolic links during recursive cleanup, and never delete the actual external game library.
- Games, saves, personal settings, credentials, model DLLs and weights remain private and stay out of source Git and public release assets.
