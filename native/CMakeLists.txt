cmake_minimum_required(VERSION 3.22)
project(DosboxGraphics LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(PHOENIX_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../external/phoenix")
set(STREAMLINE_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../external/streamline")
set(DLSS_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../external/dlss")
set(NATIVE_SDK "${CMAKE_CURRENT_SOURCE_DIR}/../out/phoenix-sdk" CACHE PATH "Phoenix native SDK")
if(NOT EXISTS "${NATIVE_SDK}/include/phoenix_native.h")
  message(FATAL_ERROR "Phoenix native SDK missing; see BUILDING.md")
endif()
add_library(dosbox_nvngx SHARED bridge.cpp)
set_property(TARGET dosbox_nvngx PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_include_directories(dosbox_nvngx PRIVATE "${NATIVE_SDK}/include" "${PHOENIX_ROOT}/native_runtime"
 "${PHOENIX_ROOT}/third_party/nlohmann" "${STREAMLINE_ROOT}/include" "${STREAMLINE_ROOT}/external/ngx-sdk/include")
target_compile_definitions(dosbox_nvngx PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(dosbox_nvngx PRIVATE /O2 /EHsc /arch:AVX2 /bigobj)
target_link_options(dosbox_nvngx PRIVATE /PDBALTPATH:dosbox_nvngx.pdb)
target_link_libraries(dosbox_nvngx PRIVATE "${NATIVE_SDK}/lib/phoenix_native.lib"
 "${DLSS_ROOT}/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib"
 d3d12 dxgi dxguid d3dcompiler user32 bcrypt wintrust crypt32 version shell32 advapi32)

