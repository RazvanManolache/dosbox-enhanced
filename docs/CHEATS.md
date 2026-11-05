# Cheats

Open the calculator-style **Cheats** icon in the toolbar. Cheats is docked on the right, sharing the panel with the **DLSS** tab. Drag the divider to resize it; double-click the divider to reset its width. Width and the selected main tab are remembered. Clicking the active toolbar icon hides the panel; clicking the other icon switches tabs. Search forms, lists and locks survive hiding or switching tabs.

1. Start a game and note a value such as health, lives or money.
2. Choose a numeric type (try 16-bit unsigned, then 8-bit unsigned if necessary). Game / conventional RAM is the default region; protected-mode games may use Extended RAM.
3. Select Exact, enter the value and press First scan. Or start with Unknown initial value.
4. Return to the game and change the value. Use Next scan with Exact, Changed, Decreased, Increased, or a specified change amount. Repeat until only a few candidates remain.
5. Select a result and Add selected to watches. In Watches, give it a name, enter a target and Apply / write value.
6. Tick Lock selected value to enforce the target each emulated frame. Untick it or press Unlock all to release it. Unlocking does not restore the original value.

Watches refresh while Cheats is visible. Polling pauses on the DLSS tab or when hidden; locks continue inside the emulator. Their names, addresses, types and targets are saved per running environment/program under private `data/cheats`. Locks start disabled on a new session and clear after program/memory-map changes, reset or save-state loading. Hiding the cheat panel leaves active locks running; the toolbar tooltip reports the active lock count.

Types include 1/2/4/8-byte signed and unsigned integers and 4/8-byte floats. Integers accept decimal and `0x` hex. DOS values may be unaligned; aligned-only scanning is optional. Scan results show values from the last two scans; Watches show live values. Searches are cancellable and display pages of 200 candidates, with up to 64 MiB scanned at once.

Addresses use DOSBox Pure's normalized memory map. Game/conventional RAM normally starts at zero; DOS/system and extended memory have separate ranges. They are not Windows process addresses or physical DOS segment:offset addresses. Only RAM exposed by the core is accessed.

The same operations, pointer searches and disassembly are available through the built-in MCP tools; see [MCP](MCP.md). The decoder uses the managed MIT-licensed Iced library, included in the app; no extra installation is required.

## Pointer chains

1. Find the live value with Search. Open **Pointers**, then choose **Use search result** / **Use watch** or enter its mapped target address.
2. Choose the pointer format, maximum depth/offset, alignment and RAM region. Start with depth 3 and offset 256. **Find chains** searches backwards from the target.
3. Restart the game or move to another level, find the value's new address, then **Rescan saved chains**. This filters existing candidates using their original options, eliminating paths that no longer reach the target. It does not discover new roots.
4. Select a chain and **Add chain to watches**. The numeric type comes from Search. Edits and locks resolve the path again each time; a lock follows a moving target every emulated frame.

Candidates and pointer watches are saved privately per game. Candidates survive resets and new sessions so they can be rescanned. Locking remains manual after each session/reset. Null, cyclic, unavailable or overlapping pointer locks are disabled; an unavailable watch remains saved for later use. Watch addresses show the latest resolved location; candidate target addresses are from the last scan. No root is guaranteed stable across program versions or relocation.

| Pointer format | Interpretation |
|---|---|
| 32-bit flat DOS address | Little-endian guest physical/identity-mapped linear address, translated into the core's normalized map |
| 16:16 segment:offset | Little-endian offset word followed by segment word; physical address = segment × 16 + offset |
| 16-bit offset + segment | Little-endian offset plus the explicit Near segment × 16; provide the game's correct segment |
| 32-bit core mapped address | The stored value already uses normalized core coordinates; only choose this for such pointers |

Offsets are applied in the chosen pointer coordinate space before mapping into RAM. For `[root] +4 → +8`, read the pointer at root, add 4, read the next pointer there, then add 8 to reach the value. Negative offsets are optional. **20-bit wrap** models A20-off address arithmetic; it is manual. Physical translation is enabled only for a verified DOSBox Pure map or a raw physical-RAM export. Protected-mode paging and selector bases are not translated; many non-flat pointers will need a different interpretation. The core does not supply DS or other CPU registers automatically.

Searches scan up to 64 MiB and 1,000,000 pointer slots, with depth 1–6, offsets up to ±65536, 1,000 results and an approximately eight-second CPU search budget. A capped search is marked **PARTIAL**, including after rescans. Narrow the region/offset/depth if needed. Cancel retains the previous candidate list. Rescanning captures all RAM, subject to the same 64 MiB limit.

## Disassembly

- Open **Cheats → Disassembly**, enter a mapped address and choose **16** or **32** bits.
- Set **Logical IP** to the code's offset for correct relative-branch labels (for a COM entry point, normally `0x100`). Mapped RAM address, physical DOS address and logical IP are separate columns.
- **Decode / refresh** reads up to 128 instructions. **Next page** advances by the bytes decoded. Reads stay within one RAM region; invalid/truncated instructions are marked.
- Pause emulation for a stable view of changing code. This is read-only instruction decoding, with no register inspection, stepping, breakpoints, assembler or automatic code/data detection. Data may decode as valid instructions.

Developer checks: `app/DosboxEnhanced.exe --cheat-test` verifies the workflow against an original DOS fixture; `--cheat-ui-test --background` checks the panel and live value controls using software UI rendering without submitting graphics frames.
