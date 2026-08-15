# DecaFfxShim - мост DecaEngine <-> AMD ffx-api (см. DecaFfxShim.cpp).
# Сборка (из native/DecaFfxShim, окружение vcvars64 НЕ требуется - генератор VS найдёт тулчейн):
#   cmake -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
# Артефакт build/Release/DecaFfxShim.dll кладётся рядом с DecaEngine.Editor.exe вместе с
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

# nvsdk_ngx_d.lib - вариант под /MD (динамический CRT, дефолт CMake); статический CRT потребовал
# бы nvsdk_ngx_s.lib. Рантайм nvngx_dlss.dll NGX ищет сам рядом с экзешником.
# dxguid - определения CLSID_D3D12SDKConfiguration и прочих GUID-ов (см. DecaAgility_Init).
target_link_libraries(DecaFfxShim PRIVATE d3d12 dxgi dxguid
    ${CMAKE_CURRENT_SOURCE_DIR}/../../external/dlss/lib/nvsdk_ngx_d.lib)

# Юникод для swprintf-сообщений и отключение min/max-макросов Windows.
target_compile_definitions(DecaFfxShim PRIVATE UNICODE _UNICODE NOMINMAX)

# Свежесобранный шим сам едет в NativeLibrary - деплой-набор нативных либ, который редактор при
# старте раскладывает в свой bin (см. DecaEngine.Editor/NativeLibraryDeployer.cs). Руками копировать
# больше ничего не нужно.
add_custom_command(TARGET DecaFfxShim POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:DecaFfxShim>
        ${CMAKE_CURRENT_SOURCE_DIR}/../../NativeLibrary/DecaFfxShim.dll)
