#!/bin/bash
# Builds cuo_mp3 (dr_mp3) for linux-x64 with CMake and the system C compiler. One command, run from anywhere:
#   bash src/native/cuo_mp3/build-linux.sh
# Writes exactly what the csproj files look for:
#   external/lib64/libcuo_mp3.so          JIT builds and tests (ClassicUO.IO.csproj copies it to the output folder)
#   external/lib64-static/libcuo_mp3.a    NativeAOT publish -r linux-x64 links it into the executable (ClassicUO.Client.csproj)
# Prerequisites: cmake >= 3.16, a C compiler (cc, gcc or clang; CC= picks one), make or ninja. On Ubuntu:
#   sudo apt-get install -y cmake build-essential
# Status (2026-10-31): bash -n checked only; first real run will be on a CI Linux runner.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"

[ "$(uname -s)" = "Linux" ] || { echo "build-linux.sh: this is $(uname -s); use build-macos.sh or build-windows.sh" >&2; exit 1; }
[ "$(uname -m)" = "x86_64" ] || echo "build-linux.sh: warning: host is $(uname -m); the output goes to the linux-x64 folders" >&2

missing=()
command -v cmake >/dev/null 2>&1 || missing+=("cmake (>= 3.16)")
cc="${CC:-}"
if [ -z "$cc" ]; then
  for c in cc gcc clang; do command -v "$c" >/dev/null 2>&1 && { cc="$c"; break; }; done
fi
[ -n "$cc" ] && command -v "$cc" >/dev/null 2>&1 || missing+=("a C compiler (cc, gcc or clang)")
command -v make >/dev/null 2>&1 || command -v ninja >/dev/null 2>&1 || missing+=("make or ninja")
if [ ${#missing[@]} -gt 0 ]; then
  echo "build-linux.sh: missing: ${missing[*]}" >&2
  echo "  Ubuntu/Debian: sudo apt-get install -y cmake build-essential" >&2
  exit 1
fi

build="$(mktemp -d "${TMPDIR:-/tmp}/cuo-mp3-build.XXXXXX")"
trap 'rm -rf "$build"' EXIT
CC="$cc" cmake -S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release -j

out="$root/external/lib64"; outs="$root/external/lib64-static"
mkdir -p "$out" "$outs"
cp "$build/libcuo_mp3.so" "$out/libcuo_mp3.so"
cp "$build/libcuo_mp3.a" "$outs/libcuo_mp3.a"

# The seven exports the managed side binds (Mp3Native.cs) must be visible in the shared library.
for f in version open_memory close get_info read_s16 seek get_frame_count; do
  nm -D --defined-only "$out/libcuo_mp3.so" | grep -q " cuo_mp3_$f\$" || { echo "build-linux.sh: libcuo_mp3.so does not export cuo_mp3_$f" >&2; exit 1; }
done
echo "built with $("$cc" --version | head -1):"
ls -l "$out/libcuo_mp3.so" "$outs/libcuo_mp3.a"
