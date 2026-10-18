# Native graphics integration

The app now executes Phoenix DLSS5 native processing, NVIDIA DLSS5 neural rendering (feature
18), NVIDIA DLSS Super Resolution and Streamline Frame Generation. Earlier notes
requesting a bridge are superseded by this implementation.

## Upstream API

The current source pins public Phoenix DLSS5 **0.8.1-beta** at
`4c9b75ff42b6945352f66f309dc84404e498f4cf` and builds its native C API directly.
The former compatibility patch has been removed. See
[upstream native-library documentation](https://github.com/RazvanManolache/dlss5-phoenix-addon/blob/4c9b75ff42b6945352f66f309dc84404e498f4cf/docs/NATIVE_LIBRARY.md)
and [the update verification](PHOENIX_UPSTREAM_UPDATE.md).

## Frame path

DOSBox Pure supplies packed BGRA8 and actual source dimensions. A latest-frame
mailbox separates emulation/audio from the graphics worker, which owns the D3D12
device, queue and graphics sessions. Old epochs are discarded on session/effect
changes. Source resolution changes and scene cuts reset temporal processing.

By default the bridge converts native BGRA8 pixels to RGBA16F without resizing.
Optional `input_factor` (number, 0.25–4) resizes the source first; `input_filter` selects
nearest-neighbor or bilinear filtering. Dimensions round to the nearest whole pixel (half up, minimum 1); sampling uses the actual rounded dimensions. Sharp sampling selects the pixel containing each output pixel center (right/bottom at an exact boundary). Smooth sampling uses bilinear interpolation without an antialias kernel. Disabled input resize uses factor 1
and preserves the source pixels. Native source and processing-input dimensions
are reported separately; `pre_resize` reflects the selected input stage. It estimates
motion with 16×16-block luminance matching over a ±8-pixel search and supplies a
flat R32F depth plane. These are image-derived guides, never engine geometry.

- `after_nr` (default): native color → NR at native dimensions → NVIDIA SR.
- `before_nr`: native color → NVIDIA SR → NR at output dimensions. NR motion
  vectors are resampled and scaled from the native motion field.
- SR off: color, NR and output all retain native dimensions. Display fitting
  and DOS pixel-aspect correction are separate from neural processing.
- Output is 2×, 3× or 4× native size when SR is enabled. A source/layout change
  recreates the session's textures, model features and swapchain; history resets.

Phoenix DLSS5 borrows the D3D12 device/queue through `phoenix_native_frame_d3d12`.
The NVIDIA DLSS5 choice executes signed `nvngx_dlssnr.dll` through its NGX exports.
Host composition applies intensity, Warm/Cool looks and the same-source split.
SR and FG are independent switches. Phoenix DLSS5 supports repeated passes; NVIDIA DLSS5
uses one pass. Tone and structure are supplied to the selected renderer.

Intensity zero equals the original spatially displayed input. Enable/bypass,
SR and FG are independent preferences. A GPU error clears enhanced output and
restores the original DOS image, with an explicit error status. Changing effects
reuses the device instead of reloading the entire graphics stack.

## Presentation

Frame Generation uses signed Streamline 2.14.1 runtimes with manual D3D12/DXGI
interface upgrades. The swapchain uses three application backbuffers, flip
discard, frame-latency support and allow-tearing capability. Presentation requests
VSync. Streamline receives frame tokens, camera constants, estimated motion,
flat depth, HUD-less composition and the actual backbuffer, plus Reflex markers.
The renderer waits for tagged-input completion before modifying resources.

NVIDIA DLSS5 skips generated presentation for child windows and background windows.
`NativeView` therefore owns a native top-level presentation window following a
WPF layout placeholder. It preserves the surrounding app controls and forwards
input. An owned popup places the fullscreen menu above the game image.
FG is suspended during pause, fullscreen menus/panels, placement changes and
background use; it resumes when the app is focused and stable. Toggle changes
recreate the swapchain as required by the SDK. DOS aspect correction is applied
to the presentation window independently of the processing canvas.

The native surface receives mouse input; keyboard focus remains on the WPF game
stage. Coordinates account for physical display pixels. Window destruction
follows graphics-worker draining. Streamline's interposer is pinned for process
lifetime because its process-wide hooks must remain valid; sessions, models,
textures, swapchains and queues are released on shutdown.

## Configuration and ABI

`setup-graphics.ps1` stages Phoenix DLSS5's native DLL/kernel bundle and the required
signed NVIDIA DLSS5/Streamline libraries under `runtime/`. `graphics.json` records
their absolute paths, a writable app cache and the external model DLL/hash.
The bridge and SDK have matching x64 C++ headers/libraries. Runtime versions and
hashes are recorded in `GRAPHICS_RUNTIME_MANIFEST.json`. Python is not used by
the app.

`native/bridge.h` defines the cdecl C ABI:

```c
void* db_graphics_create(const char* config_utf8, void* hwnd);
int db_graphics_frame(void* session, const uint8_t* bgra, size_t capacity,
                     uint32_t width, uint32_t height, const char* settings_utf8,
                     uint8_t* output, size_t output_capacity);
size_t db_graphics_status(void* session, char* destination, size_t capacity);
const char* db_graphics_error(void);
int db_graphics_destroy(void* session);
```

Creation, frames and destruction stay on the creator thread. Output is packed
native-size BGRA8 with SR off, or 2×–4× native-size BGRA8 with SR on; capacities are checked. Calls are synchronous. Status reports
source/processing/output geometry, actual renderer/SR counts, presentation and
generated-frame counts, FG status bits, reset, timings and guide provenance.
Processing errors also save D3D12/DXGI diagnostics under the app cache.

## Boundaries

The local runtime checks establish real vendor/native execution, changing image
output, working controls and generated presentations on RTX 5080. DOS does not
expose modern engine lighting, depth or motion buffers. Image-derived motion and
flat depth can cause ghosting and pixel/text artifacts, particularly during fast
scrolling. No broad quality, native-3D-guide parity or all-GPU compatibility claim
is made. See `VALIDATION.md` for measured coverage.

Session configuration requires `source_width` and `source_height`, plus `upscaling`, `upscale_order` (`after_nr`/`before_nr`) and `upscale_factor` (2–4). Frame dimensions must match the session; call `db_graphics_reconfigure` on geometry/order changes. It drains GPU work, suspends FG and rebuilds size-dependent resources while keeping the device and runtime initialization alive. Status includes native, neural and output dimensions and the actual `pre_resize` flag. `input_factor` defaults to 1 and `input_filter` to `nearest`; both are passed during creation/reconfiguration.
