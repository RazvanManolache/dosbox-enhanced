# 0.6 release checks

- 54 host checks, 56 interactive UI checks, 194 authenticated MCP checks.
- Real Phoenix and NVIDIA rendering/SR, 48 live resolution/order/resize transitions, exact native bypass.
- Original and Lightweight models, changed pixels from advanced controls, and accepted processing budgets with effective tuning telemetry.
- Foreground presentation: 1,499 generated frames over 1,500 submitted frames; 2,999 presentations, NVIDIA status bits 0.
- Clean setup downloaded and authenticated NVIDIA runtime files and the optional OpenNR runtime. Invalid hashes and cancellation are covered by host tests.
- Tested on RTX 5080. Native bundle targets sm_120. This does not certify older GPUs, every game or a particular frame rate.
- [Compact evidence and runtime hashes](RELEASE_0.6_VALIDATION.json).

The older measurements below describe the original v0.5 release and retained showcase.

# 0.5 release checks

The Windows x64 release was built and tested locally. GitHub Actions runs the
host regression checks and a separate native graphics compilation job. GPU and
interactive input checks below were performed locally.

| Suite | Passed checks |
| --- | ---: |
| Regression / defaults / configuration | 44 |
| Interactive UI and environment/program picker | 56 |
| Authenticated MCP HTTP and stdio integration | 189 |
| Mouse capture, real relative input and escape | 35 |
| Live CPU control | 8 |
| Total | **332** |

The UI checks create their own DOS programs, test label hit-testing, accessible
checkbox activation and keyboard input, per-environment settings, program names/order/visibility,
copies, search, launch, fullscreen and disk reload. An XMS probe verifies the
configured 12 MB machine through DOS file I/O.

MCP checks exercise the real loopback HTTP endpoint and same-executable stdio
bridge: auth/origin/session behavior, eight observation and 15 control tools,
program/environment/drive changes, settings, input, screenshots, mouse release,
local-only versus optional LAN access, bounded requests, approved-root link
boundaries and server shutdown. These protocol checks do not require neural
inference; the graphics run below verifies the GPU separately. New protocol
checks cover processing order/factor, native mode and invalid-value rejection.

Native-resolution checks also exercised both renderers with SR before and after
NR, SR disabled, and 2×/3×/4× output. A 320×200 input remained 320×200 through
native NR and produced 1280×800 at 4×; SR-first ran NR at 1280×800. With NR and
SR off, output matched the source pixels exactly. The old 640×384 preliminary
resize has been removed. Separate D3D12 presentation runs observed generated
frames with SR-first, NR-first and native-size output. These checks establish
execution and dimensions, not superior image quality.

On RTX 5080 / driver 616.64, a real Doom II run using the final native libraries,
DOSBox Pure 1.0-preview7, the separately supplied model and Streamline 2.14.1
observed **110 neural frames, 110 NVIDIA SR frames and 91 generated presentations**,
with NVIDIA Frame Generation status bits **0**. Frame Generation suspended on
pause and menu reveal. Source input was 320 × 200. These counts prove execution;
they are not a performance or image-quality guarantee.

The original v0.5 host build and its patched Phoenix/native bridge build both
completed locally. Native dependency inspection confirms the packaged Phoenix DLSS5
and bridge DLLs depend on Windows system libraries (plus the packaged Phoenix DLSS5
DLL), without a separate MSVC runtime installation. The managed application
bundles its native .NET libraries for extraction rather than relying on build
machine sidecars.

The main gallery uses one pass and maximum intensity/tone/structure. The isolated
four-pass illustration is explicitly labeled. [Capture method, game credits and
runtime counter snapshots](CAPTURES.md) accompany the screenshots.

Release packaging uses a file allowlist. User data, profiles, host mappings,
connection keys, games, save states, caches, debug symbols, generated graphics
configuration, NVIDIA DLSS5 runtime DLLs and model weights are excluded. Runtime and
archive SHA-256 manifests accompany the prepared assets. See the release audit
report next to the archives for the final file/privacy checks.




The final GPU probe repeated 24 live geometry/order changes per renderer on one device (48 total), including synthetic 640×480, 720×400 and 321×201 sources, plus input resize enabled/disabled. A live app run covered the six native modes and eight input-resize variants across both renderers, orders and filters. Fractional resizing (1.25×/1.5×), 0.25× reduction and odd pixel dimensions passed. Nearest sampling matched its pixel-center reference exactly; separable bilinear error was at most one byte. [Machine-readable evidence](PIPELINE_EVIDENCE.json).

The CPU fixture queried DOSBox itself with `config -get "cpu cycles"`: 10000 → 12345 → auto → 12345 → 20000, all without replacing the running DOS machine. Tests cover remembered manual speed and preserving advanced syntax.

Four additional live game captures (SkyRoads, Titus the Fox, Hexxagon and Fate of Atlantis) verified new generated presentations in every session with status bits 0. Ten combinations per game were compared on a frozen source (40 candidates). This is a visual preset selection for specific scenes, not a quality or performance benchmark. [Gallery evidence](GALLERY_EVIDENCE.json).

The current source now builds Phoenix DLSS5 0.8.1-beta directly from its published API. The compatibility patch is removed. [Upstream update verification](PHOENIX_UPSTREAM_UPDATE.md) records source/machine-code equivalence and checks of the rebuilt SDK. The original v0.5 downloads retain their tested binaries.
