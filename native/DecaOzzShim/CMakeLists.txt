# DecaOzzShim - мост DecaEngine <-> ozz-animation (см. DecaOzzShim.cpp).
#
# Сначала один раз выкачивается и собирается сам ozz. Его исходники НЕ в репозитории: external/
# целиком в .gitignore, как diligentcore и fidelityfx, - поэтому на чистой машине шаг обязателен.
#   git clone --depth 1 --branch 0.17.0 https://github.com/guillaumeblanc/ozz-animation.git external/ozz
#
# Сборка ozz (из корня репозитория):
#   cmake -S external/ozz -B external/ozz/build -G "Visual Studio 17 2022" -A x64 ^
#         -Dozz_build_samples=OFF -Dozz_build_howtos=OFF -Dozz_build_tests=OFF ^
#         -Dozz_build_tools=OFF -Dozz_build_data=OFF -Dozz_build_fbx=OFF -Dozz_build_gltf=OFF ^
#         -Dozz_build_msvc_rt_dll=ON
#   cmake --build external/ozz/build --config Release
#
# Затем шим (из native/DecaOzzShim):
#   cmake -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
#
# ozz_build_msvc_rt_dll=ON - НЕ вкусовщина: он даёт /MD (динамический CRT), тот же, что у шима по
# умолчанию. При расхождении линковка падает на дублирующихся символах CRT, а если и соберётся, то
# аллокации ozz и шима пойдут в РАЗНЫЕ кучи - падение случится позже и далеко от причины.
cmake_minimum_required(VERSION 3.20)
project(DecaOzzShim CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(OZZ_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../../external/ozz)
set(OZZ_BUILD ${OZZ_ROOT}/build)

add_library(DecaOzzShim SHARED DecaOzzShim.cpp)

target_include_directories(DecaOzzShim PRIVATE ${OZZ_ROOT}/include)

# Суффикс _r у библиотек - это ozz_build_postfix (Release). Порядок важен для статической линковки:
# offline зависит от animation, animation - от base.
target_link_libraries(DecaOzzShim PRIVATE
    ${OZZ_BUILD}/src/animation/offline/Release/ozz_animation_offline_r.lib
    ${OZZ_BUILD}/src/animation/runtime/Release/ozz_animation_r.lib
    ${OZZ_BUILD}/src/base/Release/ozz_base_r.lib)

target_compile_definitions(DecaOzzShim PRIVATE NOMINMAX)

# Свежесобранный шим едет в NativeLibrary - деплой-набор, который редактор раскладывает в свой bin
# при старте (см. DecaEngine.Editor/NativeLibraryDeployer.cs), ровно как DecaFfxShim.
add_custom_command(TARGET DecaOzzShim POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:DecaOzzShim>
        ${CMAKE_CURRENT_SOURCE_DIR}/../../NativeLibrary/DecaOzzShim.dll)
