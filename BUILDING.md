# Building DOSBox Enhanced 1.0 Beta

## Host and DOS engine

Use Windows x64, PowerShell, Git and the .NET 9 SDK. From the repository root:

This is the same folder used for local development and GitHub. Source stays in
`src/` and `native/`; pinned dependencies stay in `external/`. Do not create a
separate publishing checkout under `release/`. Installed app files (`app/`,
`runtime/`) and personal state (`data/`, graphics configuration) are ignored by Git.

```powershell
.\build.ps1
```

This downloads the pinned DOSBox Pure 1.0-preview7 Windows x64 release, verifies
both archive and DLL hashes, publishes the self-contained app, and runs the
regression checks. No C++ compiler, GPU or Python is needed
for this host build. NuGet restores the pinned SharpCompress library for optional
RAR model downloads. It runs DOS games with their original graphics.

`setup-runtime.ps1 -CoreLibrary C:\SDKs\dosbox_pure_libretro.dll` can instead use
a core you built. Its source is pinned under `external/dosbox-pure`; upstream's
Makefile and build instructions describe its MinGW/Linux cross-build. A custom
core changes the tested runtime and save-state compatibility.

## External projects

| Submodule | Role |
| --- | --- |
| `external/dosbox-pure` | Corresponding DOS engine source, pinned to 1.0-preview7 |
| `external/phoenix` | Native RTX library source, JSON header and model-free kernel source |
| `external/streamline` | NVIDIA Streamline / NGX headers |
| `external/dlss` | NVIDIA DLSS SDK import library |

Every Git revision and public URL is in `dependencies.lock.json`, and the Git
index records real submodule gitlinks. For a clone:

```powershell
git submodule update --init --recursive
```

For a downloaded source ZIP, or to fetch just the four required projects:

```powershell
.\tools\Initialize-Dependencies.ps1
```

The bootstrap checks exact revisions and skips optional LFS-only documentation.
It never resets an existing checkout or silently changes its selected revision.
The full recursive clone may also fetch NVIDIA's optional Image Scaling module.

### Public Phoenix DLSS5 native API

Phoenix DLSS5 source is pinned to **1.0 Beta**, commit `dfc0fc03b05ec3615b018f11f7bc7e8eee744ef7`.
The native C API includes the shared tuning and pipeline calls used by the UI.
The build applies `tools/phoenix-msvc-shader.patch` while compiling: it splits
an oversized NIS raw string into adjacent literals without changing shader bytes.
The patch is reversed after compilation so the submodule stays clean.
Generated SDK output includes both `phoenix_native.h` and `phoenix.h`.

Source builds use `out/phoenix-build-<revision>` and `out/phoenix-sdk`; these are
ignored compiler outputs. Keep one working app, and use `Cleanup-Generated.cmd`
when the compiler outputs are no longer needed.

## Native graphics

Add CMake 3.22+ and Visual Studio 2022 C++ tools / Windows SDK. With a matching
Phoenix DLSS5 native SDK (`bin`, `include`, `lib`, and compiled `native` kernel bundle):

```powershell
.\build.ps1 -Graphics -PhoenixSdk C:\SDKs\Phoenix-native
```

Or build the native library directly from the pinned public source:

```powershell
.\build.ps1 -Graphics -BuildPhoenix
```

Phoenix DLSS5's CMake configure step requires a Python interpreter for its development
tooling. The native library target itself does not require CUDA. For neural
inference, also compile the model-free production kernels with CUDA 13.1:

```powershell
.\build.ps1 -Graphics -BuildPhoenix -CudaCompiler 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.1\bin\nvcc.exe'
```

Repeated builds reuse the matching CMake build directory. The current production kernels target
sm_120. To use the GPU, complete [graphics setup](docs/GRAPHICS.md) with your
separately obtained model. A CPU-only CI build cannot validate neural inference.

## Tests

```powershell
& .\app\DosboxEnhanced.exe --self-test
& .\app\DosboxEnhanced.exe --ui-test
& .\app\DosboxEnhanced.exe --mcp-test
& .\app\DosboxEnhanced.exe --mouse-test
& .\app\DosboxEnhanced.exe --cpu-test
```

The interactive checks open real windows and need an interactive Windows desktop; they
temporarily move focus and, for input checks, the pointer. Run them sequentially.
Tests create their own DOS fixtures; no game installation or personal profile is
needed. Reports go to ignored `tests/results/`. CI builds the host and native
bridge and runs the regression suite; the interactive and GPU checks run locally.

The optional GPU pipeline probe needs Python with Pillow and NumPy on the
developer's machine, plus configured graphics. Use a native-resolution PNG
(for example, `dosbox_screenshot` with `enhanced: false`):

```powershell
python tests/graphics_pipeline_probe.py phoenix --input original.png --output tests/results/nr-first.json --order after_nr --factor 4
python tests/graphics_pipeline_probe.py nvidia --input original.png --output tests/results/sr-first.json --order before_nr --factor 4 --window --frames 120
python tests/graphics_pipeline_probe.py off --input original.png --output tests/results/native-identity.json --no-upscale
```

It checks native/neural/output dimensions, exact identity with both stages off,
GPU counters and output pixels. `--window` also requires observed generated
presentations; keep the test window active. These tools are not app dependencies.

No game library, local settings, logs, keys, caches, binaries or build outputs
belong in Git. `tools/Package-Release.ps1` uses an allowlist to make the portable
archive and excludes `data/`, generated graphics configuration, and model DLLs.

`--cpu-test` generates a small DOS program and queries actual CPU settings before/after live updates. The GPU probe accepts `--input-factor 0.25`–`4` (including fractions), `--input-filter nearest|linear` and `--reconfigure`. With NR/SR off it verifies exact nearest-neighbor enlargement, bilinear reference error, and native identity.

### Paired capture validation

`app/DosboxEnhanced.exe --screenshot-test` runs an animated, locally generated DOS fixture and verifies matching source/output capture, PNG round-trips, receipts, both renderer routes when available, menu actions, reset and stop. The receipt records unavailable NGX routes explicitly. `--mcp-test --non-interactive` runs HTTP/stdio integration without foreground mouse-capture checks; the default `--mcp-test` still requires an interactive desktop. Audio-device availability and skipped interaction checks are recorded in test receipts.
