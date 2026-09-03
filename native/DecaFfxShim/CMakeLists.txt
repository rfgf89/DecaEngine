# DecaFfxShim - DecaEngine <-> AMD ffx-api bridge (see DecaFfxShim.cpp).
# Build (from native/DecaFfxShim; no vcvars64 needed, the VS generator finds the toolchain):
#   cmake -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
# build/Release/DecaFfxShim.dll ships next to DecaEngine.Editor.exe together with
# external/fidelityfx/bin/amd_fidelityfx_upscaler_dx12.dll.
cmake_minimum_required(VERSION 3.20)
project(DecaFfxShim CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(DecaFfxShim SHARED DecaFfxShim.cpp)

target_include_directories(DecaFfxShim PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/diligentcore
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/fidelityfx/api/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/fidelityfx/upscalers/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/dlss/include
)

# nvsdk_ngx_d.lib is the /MD (dynamic CRT, CMake default) variant; a static CRT would need
# nvsdk_ngx_s.lib. dxguid supplies CLSID_D3D12SDKConfiguration (see DecaAgility_Init).
target_link_libraries(DecaFfxShim PRIVATE d3d12 dxgi dxguid
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/dlss/lib/nvsdk_ngx_d.lib)

# UNICODE for the swprintf messages; NOMINMAX disables the Windows min/max macros.
target_compile_definitions(DecaFfxShim PRIVATE UNICODE _UNICODE NOMINMAX)

# NativeLibrary is the deploy set the editor unpacks into its bin at startup
# (see DecaEngine.Editor/NativeLibraryDeployer.cs).
add_custom_command(TARGET DecaFfxShim POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:DecaFfxShim>
        ${CMAKE_CURRENT_SOURCE_DIR}/../../NativeLibrary/DecaFfxShim.dll)
