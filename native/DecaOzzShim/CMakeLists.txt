# DecaOzzShim - DecaEngine <-> ozz-animation bridge (see DecaOzzShim.cpp).
#
# ozz itself is built once first. Its sources are NOT in the repo: external/ is gitignored
# wholesale, like diligentcore and fidelityfx, so this step is required on a clean machine.
#   git clone --depth 1 --branch 0.17.0 https://github.com/guillaumeblanc/ozz-animation.git external/ozz
#
# Build ozz (from the repo root):
#   cmake -S external/ozz -B external/ozz/build -G "Visual Studio 17 2022" -A x64 ^
#         -Dozz_build_samples=OFF -Dozz_build_howtos=OFF -Dozz_build_tests=OFF ^
#         -Dozz_build_tools=OFF -Dozz_build_data=OFF -Dozz_build_fbx=OFF -Dozz_build_gltf=OFF ^
#         -Dozz_build_msvc_rt_dll=ON
#   cmake --build external/ozz/build --config Release
#
# Then the shim (from native/DecaOzzShim):
#   cmake -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
#
# ozz_build_msvc_rt_dll=ON is mandatory: it selects /MD (dynamic CRT), the shim's default.
# A mismatch fails linking on duplicate CRT symbols, or links and puts ozz and the shim on
# SEPARATE heaps - a crash far from its cause.
cmake_minimum_required(VERSION 3.20)
project(DecaOzzShim CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(OZZ_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../external/ozz)
set(OZZ_BUILD ${OZZ_ROOT}/build)

add_library(DecaOzzShim SHARED DecaOzzShim.cpp)

target_include_directories(DecaOzzShim PRIVATE ${OZZ_ROOT}/include)

# The _r suffix is ozz_build_postfix (Release). Link order matters: offline depends on
# animation, animation on base.
target_link_libraries(DecaOzzShim PRIVATE
    ${OZZ_BUILD}/src/animation/offline/Release/ozz_animation_offline_r.lib
    ${OZZ_BUILD}/src/animation/runtime/Release/ozz_animation_r.lib
    ${OZZ_BUILD}/src/base/Release/ozz_base_r.lib)

target_compile_definitions(DecaOzzShim PRIVATE NOMINMAX)

# NativeLibrary is the deploy set the editor unpacks into its bin at startup
# (see DecaEngine.Editor/NativeLibraryDeployer.cs).
add_custom_command(TARGET DecaOzzShim POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:DecaOzzShim>
        ${CMAKE_CURRENT_SOURCE_DIR}/../../NativeLibrary/DecaOzzShim.dll)
