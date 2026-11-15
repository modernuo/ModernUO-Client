#!/bin/bash
# Builds cuo_mp3 (dr_mp3) for linux-x64 with CMake and the system C compiler. One command, run from anywhere:
#   bash src/native/cuo_mp3/build-linux.sh
# Writes exactly what the csproj files look for:
#   external/lib64/libcuo_mp3.so          JIT builds and tests (ClassicUO.IO.csproj copies it to the output folder)
#   external/lib64-static/libcuo_mp3.a    NativeAOT publish -r linux-x64 links it into the executable (ClassicUO.Client.csproj)
# Prerequisites: cmake >= 3.16, a C compiler (cc, gcc or clang; CC= picks one), make or ninja. On Ubuntu:
#   sudo apt-get install -y cmake build-essential
# The output must be x86-64 (it is installed as linux-x64). On another host (e.g. aarch64) the script refuses unless
# cross-compilation is asked for explicitly: CUO_MP3_CROSS=1 plus an x86-64 compiler in CC (e.g.
# CC=x86_64-linux-gnu-gcc) or a CMake toolchain file in CMAKE_TOOLCHAIN_FILE. Either way, readelf checks that both
# outputs are ELF64 x86-64 before anything is installed.
# Status (2026-10-31, arch guard added after Codex round 2): bash -n checked only; first real run will be on a CI Linux runner.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"

[ "$(uname -s)" = "Linux" ] || { echo "build-linux.sh: this is $(uname -s); use build-macos.sh or build-windows.sh" >&2; exit 1; }
host_arch="$(uname -m)"
toolchain="${CMAKE_TOOLCHAIN_FILE:-}"
if [ "$host_arch" != "x86_64" ]; then
  if [ "${CUO_MP3_CROSS:-}" != "1" ] || { [ -z "${CC:-}" ] && [ -z "$toolchain" ]; }; then
    echo "build-linux.sh: host is $host_arch, but the output is installed as linux-x64." >&2
    echo "  Build on an x86_64 host, or cross-compile explicitly: CUO_MP3_CROSS=1 CC=x86_64-linux-gnu-gcc bash $0" >&2
    echo "  (or CUO_MP3_CROSS=1 CMAKE_TOOLCHAIN_FILE=<x86-64 toolchain file>)." >&2
    exit 1
  fi
  echo "build-linux.sh: cross-compiling for x86_64 on $host_arch (CUO_MP3_CROSS=1)" >&2
fi

missing=()
command -v cmake >/dev/null 2>&1 || missing+=("cmake (>= 3.16)")
cc="${CC:-}"
if [ -z "$cc" ]; then
  for c in cc gcc clang; do command -v "$c" >/dev/null 2>&1 && { cc="$c"; break; }; done
fi
[ -n "$cc" ] && command -v "$cc" >/dev/null 2>&1 || missing+=("a C compiler (cc, gcc or clang)")
command -v make >/dev/null 2>&1 || command -v ninja >/dev/null 2>&1 || missing+=("make or ninja")
command -v readelf >/dev/null 2>&1 || missing+=("readelf (binutils)")
command -v nm >/dev/null 2>&1 || missing+=("nm (binutils)")
if [ ${#missing[@]} -gt 0 ]; then
  echo "build-linux.sh: missing: ${missing[*]}" >&2
  echo "  Ubuntu/Debian: sudo apt-get install -y cmake build-essential" >&2
  exit 1
fi

build="$(mktemp -d "${TMPDIR:-/tmp}/cuo-mp3-build.XXXXXX")"
trap 'rm -rf "$build"' EXIT
cmake_args=(-S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release)
[ -n "$toolchain" ] && cmake_args+=("-DCMAKE_TOOLCHAIN_FILE=$toolchain")
CC="$cc" cmake "${cmake_args[@]}"
cmake --build "$build" --config Release -j

so="$build/libcuo_mp3.so"; ar="$build/libcuo_mp3.a"

# Verified before anything is installed. Architecture: the shared library and every object in the archive must be
# ELF64 x86-64 (readelf -h prints one header per archive member).
check_x64() {
  local hdr
  hdr="$(readelf -h "$1")" || { echo "build-linux.sh: readelf failed on $1" >&2; exit 1; }
  local classes machines
  classes="$(awk -F: '/^ *Class:/ { gsub(/^ +| +$/, "", $2); print $2 }' <<<"$hdr" | sort -u)"
  machines="$(awk -F: '/^ *Machine:/ { gsub(/^ +| +$/, "", $2); print $2 }' <<<"$hdr" | sort -u)"
  if [ "$classes" != "ELF64" ] || [ "$machines" != "Advanced Micro Devices X86-64" ]; then
    echo "build-linux.sh: $1 is not ELF64 x86-64 (class: ${classes:-none}; machine: ${machines:-none}); nothing installed" >&2
    exit 1
  fi
}
check_x64 "$so"
check_x64 "$ar"

# The seven exports the managed side binds (Mp3Native.cs) must be visible in the shared library.
for f in version open_memory close get_info read_s16 seek get_frame_count; do
  nm -D --defined-only "$so" | grep -q " cuo_mp3_$f\$" || { echo "build-linux.sh: libcuo_mp3.so does not export cuo_mp3_$f; nothing installed" >&2; exit 1; }
done

out="$root/external/lib64"; outs="$root/external/lib64-static"
mkdir -p "$out" "$outs"
cp "$so" "$out/libcuo_mp3.so"
cp "$ar" "$outs/libcuo_mp3.a"
echo "built with $("$cc" --version | head -1):"
ls -l "$out/libcuo_mp3.so" "$outs/libcuo_mp3.a"
