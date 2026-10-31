# cuo_mp3: dr_mp3 (external/dr_libs/dr_mp3.h, v0.7.4) behind a flat C API for the client.
# Builds a shared library (JIT builds load it) and a static library (NativeAOT links it into the executable).
# Entry points: build-linux.sh, build-macos.sh, build-windows.sh (MSVC from Git Bash; produced the committed files);
# this CMake file also works there with the Visual Studio generator.
cmake_minimum_required(VERSION 3.16)
project(cuo_mp3 C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)   # the static archive is linked into a PIE executable by ILC
set(CMAKE_C_VISIBILITY_PRESET hidden)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_library(cuo_mp3 SHARED cuo_mp3.c)
target_compile_definitions(cuo_mp3 PRIVATE CUO_MP3_SHARED)

add_library(cuo_mp3_static STATIC cuo_mp3.c)
set_target_properties(cuo_mp3_static PROPERTIES OUTPUT_NAME cuo_mp3)
if(MSVC)
  # Same name as the DLL's import library otherwise; keep the static one apart.
  set_target_properties(cuo_mp3_static PROPERTIES OUTPUT_NAME cuo_mp3_static)
  target_compile_options(cuo_mp3_static PRIVATE /Zl)
endif()

if(APPLE)
  set_target_properties(cuo_mp3 PROPERTIES INSTALL_NAME_DIR "@rpath")
endif()
