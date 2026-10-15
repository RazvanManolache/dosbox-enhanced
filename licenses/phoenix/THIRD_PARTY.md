# Third-party source notices

This document covers the vendored dependency sources currently included in
`third_party/`. Their original copyright notices, license headers and license
files are retained and apply to the corresponding files.

Separately installed SDKs and runtime libraries, with tested versions and setup
links, are listed in [EXTERNAL_DEPENDENCIES.md](third_party/EXTERNAL_DEPENDENCIES.md).

| Dependency | Purpose | License and notice location |
| --- | --- | --- |
| DLSS5 compatibility host | 64-bit graphics helper and transport | MIT; contributor attribution in [LICENSE](third_party/compatibility_bridge/LICENSE), [NOTICE](third_party/compatibility_bridge/NOTICE), and source import manifest |
| ReShade helper runtime (build input) | Private D3D12 helper environment | BSD-3-Clause; [license](third_party/compatibility_bridge/ReShade-LICENSE.txt), retained in embedded compatibility notices |
| MinHook | Windows API hooks | BSD-style terms and Hacker Disassembler Engine component notices in [LICENSE.txt](third_party/minhook/LICENSE.txt) and source headers |
| ReShade SDK headers | Addon API and overlay integration | `BSD-3-Clause OR MIT` declarations and Patrick Mours copyright notices in [the SDK headers](third_party/reshade/reshade.hpp) |
| Dear ImGui headers for ReShade | Addon user interface | MIT; Omar Cornut attribution in [LICENSE.txt](third_party/imgui_reshade/LICENSE.txt) and source headers |
| nlohmann JSON | JSON configuration and messages | MIT; Niels Lohmann attribution and additional component notices in [json.hpp](third_party/nlohmann/json.hpp) |
| NVIDIA NVAPI headers | NVIDIA driver interfaces | MIT declarations and NVIDIA copyright notices in [the headers](third_party/nvapi/nvapi.h); accompanying [License.txt](third_party/nvapi/License.txt) also describes the NVAPI libraries, which are not included here |
| NVIDIA Streamline headers | Host feature interfaces | MIT permission text and NVIDIA copyright notices in [sl.h](third_party/streamline/sl.h) and the other supplied headers |
| Intel XeSS API headers | Optional D3D12 temporal upscaling | MIT declarations and Intel copyright notices in [the headers](third_party/xess/xess.h); [MIT text](third_party/xess/MIT_LICENSE.txt), SDK [license](third_party/xess/LICENSE.txt) and [source/version](third_party/xess/README.md). No XeSS binaries included |
| DLSSNR-AMD shader interfaces | AMD cooperative-matrix and coherent-activation GLSL interfaces | MIT; mochizuki0323 attribution in [LICENSE](third_party/amd_nr/LICENSE), [NOTICE](third_party/amd_nr/NOTICE) and [provenance](third_party/amd_nr/PROVENANCE.md) |
| DLSS-NR on Intel Xe2 source subset | Intel GPU shaders and runtime/reference adapters | Apache-2.0; uzbekunknown and contributor attribution in [LICENSE](third_party/intel_nr/LICENSE), upstream [NOTICE](third_party/intel_nr/NOTICE) and [provenance](third_party/intel_nr/PROVENANCE.md) |
| dlssnr-pytorch-derived layout declarations | Attribution for declarations adapted into `native_runtime/weight_layouts.h`; the former Python runtime modules are excluded | MIT; Mauri de Souza Meneguzzo attribution in [LICENSE](third_party/portable_nr/LICENSE), [NOTICE](third_party/portable_nr/NOTICE) and [provenance](third_party/portable_nr/PROVENANCE.md) |

Keep the applicable license text, copyright notices and disclaimers when
redistributing these sources. Consult each component's terms for binary
distribution requirements; this summary does not replace those terms.

## Scope of this source subset

The AMD and Intel dependency subsets are included with their licenses,
attribution and dependency-specific provenance. Portable runtime license and
attribution files remain for the derived weight-layout declarations. Modified source files
identify their adaptations. Some supplied source describes model tensor layouts;
this inventory does not relabel that material as original project work.

The selected native RTX kernel sources and production graph are included so
the addon can be built from this repository. Experimental kernel alternatives
and the tuning history remain excluded.

No NVIDIA model DLL, extracted model weights, NGX SDK, vendor runtime binaries
or compiled addon is included in this subset. Build scripts that reference an
external SDK or runtime do not supply it or grant redistribution rights to it.
Any separately obtained SDKs or binaries remain subject to their own terms.

This dependency inventory describes the included third-party sources. It does
not establish rights to omitted material or grant an overall project license.
