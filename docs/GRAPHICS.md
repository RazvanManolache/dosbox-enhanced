# Graphics setup

Open **Graphics setup / download files…** in the effects panel.

- The component list shows the DOS engine, Phoenix library, bridge, kernels, model and NVIDIA runtimes found on disk.
- **Download / repair missing graphics files** installs the pinned NVIDIA Streamline SR/FG runtime. Archive and file hashes and Windows signatures are checked before activation.
- Optional: tick **OpenNR compatibility runtime** to download NODIX DLSS-5-MANAGER 1.3.7. This is community-modified runtime code, not an NVIDIA-signed DLL; both archive and DLL hashes are pinned. Only its model DLL is extracted. The manager is never installed or run.
- Or choose your own **signed NVIDIA model DLL**. Select Original or Lightweight and press **Use selected model**; unavailable model resources are refused.
- **Repair bundled engines** downloads this exact DOSBox release and checks GitHub's asset digest plus the file manifest before extracting only DOS/Phoenix runtime components.
- Restart from the panel to apply changed runtimes/models. The replacement waits for the old process to exit, including its MCP listener and GPU work.
- All downloaded files and model settings stay in ignored local runtime/data folders. Models and vendor runtimes are excluded from our release assets.

## Phoenix 0.17 controls

Settings are saved per environment. Quick NR/SR order, input resize, NVIDIA FG and one-pass defaults remain available.

- **Detail & temporal:** reuse history, automatic mask, scene-change detection, skin conditioning (−1 follows structure), color strength, neural size 10–100%, 1–8 central blocks and eight repeat counts (0–4).
- **Performance:** fixed / responsiveness / quality priority, target processing FPS and maximum processing milliseconds (0 disables each budget). Targets do not guarantee game FPS.
- **Monitoring:** off, basic or detailed. Status shows actual model and effective tuning; GPU timings are sampled and add overhead.
- **Color finishing:** brightness, contrast, saturation and gamma, applied through Phoenix's shared processing pipeline.
- These advanced controls apply to the Phoenix renderer. NVIDIA NR, SR and FG remain independent choices. The Original / Lightweight choice is app-wide and takes effect after restart.
- Start with one network pass. Block repeats and whole-network passes are separate controls. The shared block limit is 1–8; Original has eight central blocks, while Lightweight uses its two available blocks and repeat counts.

![Setup and downloads](images/graphics-setup.png)

## Processing order

- **NR first → upscale** (default): process the native DOS frame, then run NVIDIA SR.
- **Upscale first → NR**: run NVIDIA SR on the native frame, then process the larger image.
- **Upscale size**: 2×, 3× or 4× native dimensions. A 320×200 game at 4× produces 1280×800 before DOS aspect correction for display.
- **SR off**: NR and output stay at the input dimensions (native unless optional input resize is enabled). Window scaling is separate.
- Input resize is optional and off by default. Drag the 0.25×–4× slider in 0.05× steps, including 1.25× and 1.5×. A live preview shows the actual pixel dimensions; release the mouse to apply. Choose sharp (nearest-neighbor) or smooth (bilinear) filtering. It runs before both NR and SR and is independent of their switches.
- With input resize on, NR and SR receive the resized input. Input 1.25× plus SR 2× gives 2.5× total output (320×200 → 400×250 → 800×500). With both resize and SR off, processing stays native.
- Motion is estimated from the processing input; SR-first rescales the NR motion vectors to match its larger input.
- Order and size are saved per environment and exposed through MCP. Resolution or layout changes recreate graphics resources and reset temporal history.

Graphics use estimated image motion and a flat depth plane. No real 3D engine
geometry is available. Temporal artifacts are possible; this experiment does not
promise better image quality or a particular frame rate in every game.

![Phoenix DLSS5 with NVIDIA upscaling before neural rendering](images/upscale-first.png)

![Optional input resize before NR and NVIDIA SR](images/input-resize.png)

## Per-game starting points

- [Four-game gallery, settings and 40 comparison trials](GAME_GALLERY.md).
- [Preset arguments for MCP](GAME_PRESETS.json); apply them to a separate environment for each game.
- Keep one pass. Native NR at maximum strength often deforms thin pixel-art lines; try SR first or a sharp fractional input resize.
