# Built-in DOSBox MCP

Open the **plug / AI connection** icon in the top menu. Enable the server, choose observation or
control access, then copy the configuration for your AI client. Copy the provided
AI instructions into its conversation after connecting. This app must remain
open. The server controls the embedded DOS session, not the Windows desktop.

## Connections

The app implements MCP Streamable HTTP with JSON responses at `/mcp`, plus a
stdio adapter in the same executable. It supports protocol versions 2025-11-25,
2025-06-18, 2025-03-26 and 2024-11-05. It does not implement legacy HTTP+SSE.
An initialize request returns `Mcp-Session-Id`; subsequent requests send that
header and the negotiated `MCP-Protocol-Version`. Send
`notifications/initialized` before tool calls. GET returns 405; DELETE ends a
session. HTTP POST requests send `Content-Type: application/json`,
`Accept: application/json, text/event-stream` and bearer authorization.

For command-based clients, use the popup's complete JSON configuration:

```json
{
  "mcpServers": {
    "dosbox-enhanced": {
      "command": "C:\\Apps\\DOSBoxEnhanced\\app\\DosboxEnhanced.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

Use the current path copied from the panel if you move the app. The adapter
reads the app's saved connection settings, authenticates locally, and forwards
JSON-RPC without writing diagnostics to stdout. It does not launch a second
emulator or enable a disabled server. Client EOF ends the bridge and HTTP session.
The bridge needs no Python, Node or globally installed .NET runtime.

For an HTTP client, copy the HTTP configuration and connection key from the
panel. Default URL: `http://127.0.0.1:8387/mcp`. The app binds IPv4 loopback only
unless **Allow connections from other computers** is checked. Remote mode binds
IPv4 interfaces; use a copied network address, with the same bearer key.
Traffic uses HTTP without TLS. The checkbox does not open firewall/router ports.
Unchecking it closes the network listener and clears existing sessions. Changing
permissions/port/key requires reconnecting clients. The key is saved locally
under `data/preferences.json`, and is not included in tool results or activity.

## Tools

| Tool | Purpose | Observe only |
|---|---|---|
| dosbox_status | Running session, geometry, graphics/audio/server status | Yes |
| dosbox_list_environments | Saved environments and mapped drive letters | Yes |
| dosbox_list_programs | Managed program menu, hidden entries, names, order, availability and pagination | Yes |
| dosbox_list_files | Bounded mapped-folder listing with curated titles/launchers | Yes |
| dosbox_screenshot | Raw or available enhanced PNG as MCP image content | Yes |
| dosbox_read_text | VGA text when RAM is exposed, otherwise local Windows OCR | Yes |
| dosbox_read_memory | Bounded RAM read if the core exposes it | Yes |
| dosbox_get_configuration | Effective INI, startup, mappings and core-option catalog | Yes |
| dosbox_launch | Launch an environment or mapped DOS program/game folder | No |
| dosbox_session | Pause/resume/reset/reload/stop/save/load state | No |
| dosbox_type_text | Bounded ASCII input with Enter/tab support | No |
| dosbox_press_keys | Key/chord press with bounded hold and automatic release | No |
| dosbox_mouse | DOS-relative movement, left/right/middle clicks and wheel | No |
| dosbox_mouse_capture | Capture/release the Windows mouse and configure its shortcut/options | No |
| dosbox_audio | App-only volume and mute | No |
| dosbox_display | Fullscreen, separate environment/effects panels, menu, aspect and sampling | No |
| dosbox_effects | Renderer, intensity/tone/structure/look/passes, SR/FG, upscale order and factor | No |
| dosbox_configure | General DOSBox and DOSBox Pure frontend settings | No |
| dosbox_create_folder | Create a mapped folder without overwriting | No |
| dosbox_manage_programs | Scan, show/hide, rename, reorder and enable/disable automatic additions | No |
| dosbox_environment | Create, copy, rename/edit, select or remove an environment profile | No |
| dosbox_drive | Map an approved drive/subfolder, edit label/type or remove its mapping | No |
| dosbox_quick_run | Standalone runner using a program inside an approved mapped drive | No |

There are **23 tools**, including **8 read-only tools**. Observation mode omits
and rejects all 15 control tools. HTTP and the self-contained stdio bridge expose
the same tools; reconnect an existing client after updating the app.

Control mode can type arbitrary DOS commands and run DOS programs. Mounted
folders are writable, so callers must respect the user's intent. Observation
mode omits control tools and rejects direct attempts to call them. File tools
accept DOS paths inside explicitly mapped drives, reject traversal/file links,
and permit known curated game junctions. They do not mount arbitrary host paths.
DOS commands themselves have the emulator's existing filesystem capabilities.

The current DOSBox Pure core reports no libretro RAM buffer; memory reads return
an explicit error. Screen reading still works through the installed Windows OCR
engine, using an enlarged original framebuffer. OCR can misread pixel fonts;
use screenshots to check uncertain text. No cloud OCR service is contacted.
Unsupported Lua, CPU registers, port I/O and video recording are not advertised.

## General settings

Example tool arguments:

```json
{
  "environment_id": "my-games",
  "settings": {
    "cpu": {"core": "normal", "cycles": "max"},
    "mixer": {"rate": "44100"},
    "dos": {"xms": "true"},
    "libretro": {"dosbox_pure_force60fps": "false"}
  },
  "restart": true
}
```

The tool saves the profile and optionally reloads a matching running session.
Reset alone does not reload INI settings. Standard section/key overrides have
no small fixed allowlist; values must be one line. Unknown/unsupported settings
can be ignored by the core. The `[libretro]` section is routed to the core's
frontend options rather than written into DOS INI. The core's live definitions
are returned by `dosbox_get_configuration`. The host keeps
`dosbox_pure_conf=outside` to load environment configurations. Drive mappings and
startup/BAT programs manage autoexec commands separately.

`dosbox_get_configuration` also returns the searchable structured setting
catalog and cached core definitions after DOS stops. `settingsIni` exports
settings for direct import through `dosbox_configure`'s `ini` argument. `ini`
also contains generated drive/startup commands and is for inspection, not
settings import. Pass either `settings` or `ini`. `replace: true` discards
omitted overrides and resets common app settings; `null` values remove a key,
or a whole section. INI text is limited to 64 KB; transport requests are bounded
to 256 KB, including JSON escaping. Drive mappings/startup remain managed separately.

## Mouse, panels and workspace

Configure mouse capture and its user shortcut with `dosbox_mouse_capture`:

```json
{"release_shortcut":"Caps Lock","capture_on_click":true,"hide_pointer":true,"sensitivity":1.0}
```

`{"captured":false}` always frees the Windows mouse, even without a DOS session.
In fullscreen it reveals the menu. `{"captured":true}` uses the actual capture
mechanism only when the running, unpaused game window is already foreground;
the tool does not activate a background window or capture an options dialog.
`dosbox_status.mouse` reports all these choices and the live capture state.
`dosbox_mouse` continues to automate DOS without host capture; its movement
values are independent of physical-mouse sensitivity. `dosbox_press_keys`
targets DOS directly, so use the capture tool to release the Windows pointer.

`dosbox_display` accepts `environment_panel`, `effects_panel` and `menu_visible`
alongside the existing fullscreen/aspect/sampling fields. Individual panel flags
override `panels`. Windowed choices persist. Hiding the menu applies only in
fullscreen; the windowed toolbar remains available.

`dosbox_manage_programs` accepts `scan`, `discover_new`, `updates` and `order`:

```json
{
  "environment_id":"my-games",
  "discover_new":true,
  "updates":{"C:\\GAMES\\GAME\\CONAN.COM":{"visible":true,"name":"Conan"}},
  "order":["C:\\GAMES\\GAME\\CONAN.COM"]
}
```

Omitted programs keep their relative order at the end. Scanning appends new
programs and preserves hidden entries and custom names. `scan:true` also works
with automatic additions disabled. Listing is read-only and paginated using
`offset`/`count`; it can include or omit hidden entries. Program scans enforce
the same mapped-folder/link boundary as file tools.

Use `dosbox_environment` with `action:create/copy/update/select/remove`.
Copies have independent settings/menu lists and shared folder mappings; removal
never deletes game folders or saved states. `dosbox_drive` uses existing approved
sources: give the destination environment/letter and `source_environment_id`,
`source_drive`, and optional relative `path`. New host roots must first be mapped
by the user in the app. Drive removal clears startup if it depended on that
letter. `dosbox_quick_run` takes an approved source environment, drive and relative
EXE/COM/BAT path, mounts its parent as C:, and leaves the saved startup unchanged.

MCP tools do not enable the server, grant themselves control or enable remote
connections. Those permissions remain in the user-operated options panel.

The environment tab's configuration editor uses the same model, with grouped controls,
the installed core's option catalog and a synchronized Advanced INI page.
Common CPU/memory controls
are synchronized when advanced values fit those controls; other overrides remain
visible in the advanced editor and effective configuration.

The searchable program picker groups all saved environments. Environment changes,
program names, visibility and order set through MCP update that picker directly,
including changes to environments that are not currently selected. Picking a
program selects its owning environment; Play launches that environment's mapping
and settings. Copy and DOSBox configuration are available in the environment tab.

## Validation

`--mcp-test` starts a temporary environment and tests authenticated HTTP and the
published executable's stdio bridge, real keyboard/text/mouse input, PNG capture,
Windows OCR, audio/display/effects, state round-trip, arbitrary setting persistence
and reload, path/input validation, access modes, LAN binding and disabling.
It restores preferences and removes its temporary profile. Reports and the
rendered settings panel are under `tests/results/mcp-integration.json` and
`mcp-panel.png`. LAN checks use this machine's own network address; a separate
remote computer/firewall path has not been tested.

Protocol references: [MCP transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
and [tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).
The user-provided [dosbox-mcp project](https://github.com/dosbox-automation/dosbox-mcp)
was used to compare capabilities; this implementation uses this app's own core,
HTTP service and executable rather than its Python bridge.

## Native-resolution graphics controls

- `upscaling: false`: NR and output stay at the processing input size. Set `input_resize: false` too for native size.
- `input_resize: true`, `input_scale: 0.25`–`4` (fractional values such as `1.25` supported), `input_filter: "nearest"` or `"linear"`: optional resize before NR/SR, off by default.
- `upscale_order: "after_nr"`: native NR then NVIDIA SR (default).
- `upscale_order: "before_nr"`: NVIDIA SR first, then NR at the larger size.
- `upscale_factor: 2`, `3` or `4`: multiply native dimensions (default 4).
- These settings persist per environment. `dosbox_status` reports actual input,
  neural and output dimensions in graphics status; `dosbox_screenshot` returns
  the actual processed size. SR and Frame Generation remain independent.

## Live CPU cycles

- Use `dosbox_configure` with `settings: {"cpu": {"cycles": "12345"}}`, `"auto"` or `"max"`.
- Common cycle changes apply to the running environment without restarting. The result includes `cpuAppliedLive`; other settings still use the normal reload rules.
- `dosbox_status.cpu` reports the requested value, live-control availability and acknowledged frontend updates.
- Exact values are verified separately through DOSBox's own `config -get "cpu cycles"`, not inferred from the UI label.
- Advanced CPU syntax remains supported through configuration and may require reload. Per-environment Auto/manual history is preserved.
