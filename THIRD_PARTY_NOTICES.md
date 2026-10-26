# Component credits and licenses

The DOSBox Enhanced host and graphics bridge are provided under
**GPL-2.0-or-later**; see `LICENSE`. External components keep their own licenses.
The Git submodules are separately maintained projects, pinned in
`dependencies.lock.json`. These notices do not replace their license texts.

| Component | Source / attribution | Terms and included notices |
| --- | --- | --- |
| DOSBox Pure | Bernhard Schelling and contributors; built on DOSBox | GPL-2.0-or-later; `docs/DOSBOX_LICENSE`, `external/dosbox-pure` |
| Phoenix native library and model-free kernels | DLSS5 Phoenix, Razvan Manolache and contributors | Apache-2.0 and retained component terms; `licenses/phoenix/`, upstream `LICENSE`, `NOTICE`, `THIRD_PARTY.md` |
| nlohmann JSON | Niels Lohmann and contributors; used through Phoenix's JSON header | MIT; `licenses/phoenix/nlohmann-json-MIT.txt` |
| NVIDIA NVAPI headers | NVIDIA Corporation; Phoenix build input | Retained upstream header/SDK terms; `licenses/phoenix/NVIDIA-NVAPI-headers-MIT.txt` |
| NVIDIA Streamline headers | NVIDIA Corporation | `external/streamline/license.txt` and `3rd-party-licenses.md` |
| NVIDIA DLSS SDK | NVIDIA Corporation | `licenses/nvidia-sdk/LICENSE.txt`, `external/dlss`; headers/helper library are build inputs |
| SharpCompress | Adam Hathcock and contributors; pinned NuGet 0.50.7 | MIT; `licenses/SharpCompress-MIT.txt` |
| NVIDIA Image Scaling | NVIDIA Corporation; embedded in Phoenix native pipeline | `licenses/NVIDIA-Image-Scaling-LICENSE.txt`, `licenses/NVIDIA-Image-Scaling-NOTICE.txt` |
| .NET / WPF / ASP.NET Core | Microsoft and contributors | MIT and third-party notices from the self-contained runtime distribution; `licenses/dotnet/` |

The portable archive includes DOSBox Pure and a model-free Phoenix build. It does
**not** include NVIDIA model DLLs, extracted weights, games, or the NVIDIA
Streamline/DLSS runtime DLLs. Optional graphics setup downloads NVIDIA's official
SDK separately and retains its `license.txt`, `3rd-party-licenses.md`,
`nvngx_dlss.license.txt` and `reflex.license.txt` beside the extracted files.

Phoenix's library does not contain the neural model. Its generated kernel bundle
contains executable kernels and the production graph, with no model weights.
See the upstream Phoenix notices for its retained NVIDIA/JSON/layout attributions.

Screenshots show locally installed games for demonstration. SkyRoads, Titus the Fox, Hexxagon, Fate of Atlantis
and Doom II and their artwork belong to their respective creators and
rights holders; no game files are supplied. Capture details and exact runtime
observations are in `docs/CAPTURES.md`.

Corresponding DOSBox Pure source is pinned at the 1.0-preview7 tag in the
`external/dosbox-pure` submodule and source bootstrap. The host/bridge source,
build scripts and public Phoenix source pin accompany the source release.

Optional OpenNR model download: community runtime from NODIX DLSS-5-MANAGER 1.3.7. No manager executable, model DLL or weights are bundled in this release. The external project retains its own terms.
