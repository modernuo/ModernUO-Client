#!/bin/bash
# Builds cuo_mp3 (dr_mp3) for macOS with CMake and Apple clang. One command, run from anywhere:
#   bash src/native/cuo_mp3/build-macos.sh [universal|arm64|x86_64]     (default universal: arm64 + x86_64 in one file)
# Writes exactly what the csproj files look for (the same files serve osx-arm64 and osx-x64 when universal):
#   external/osx/libcuo_mp3.dylib         JIT builds and tests (ClassicUO.IO.csproj copies it to the output folder)
#   external/osx-static/libcuo_mp3.a      NativeAOT publish -r osx-* links it into the executable (ClassicUO.Client.csproj)
# Prerequisites: Xcode command line tools (xcode-select --install) and cmake >= 3.16 (brew install cmake).
# Minimum macOS of the output: 11.0. Status (2026-10-31): bash -n checked only; first real run will be on a CI macOS runner.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"

[ "$(uname -s)" = "Darwin" ] || { echo "build-macos.sh: this is $(uname -s); use build-linux.sh or build-windows.sh" >&2; exit 1; }

case "${1:-universal}" in
  universal) archs="arm64;x86_64"; want="arm64 x86_64" ;;
  arm64)     archs="arm64";        want="arm64" ;;
  x86_64)    archs="x86_64";       want="x86_64" ;;
  *) echo "build-macos.sh: unknown arch '$1' (universal, arm64 or x86_64)" >&2; exit 1 ;;
esac

missing=()
command -v cmake >/dev/null 2>&1 || missing+=("cmake >= 3.16 (brew install cmake)")
xcrun --find clang >/dev/null 2>&1 || missing+=("Apple clang (xcode-select --install)")
command -v make >/dev/null 2>&1 || command -v ninja >/dev/null 2>&1 || missing+=("make or ninja (xcode-select --install)")
if [ ${#missing[@]} -gt 0 ]; then
  echo "build-macos.sh: missing: ${missing[*]}" >&2
  exit 1
fi

build="$(mktemp -d "${TMPDIR:-/tmp}/cuo-mp3-build.XXXXXX")"
trap 'rm -rf "$build"' EXIT
cmake -S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="$archs" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0
cmake --build "$build" --config Release -j

out="$root/external/osx"; outs="$root/external/osx-static"
mkdir -p "$out" "$outs"
cp "$build/libcuo_mp3.dylib" "$out/libcuo_mp3.dylib"
cp "$build/libcuo_mp3.a" "$outs/libcuo_mp3.a"

for a in $want; do
  lipo "$out/libcuo_mp3.dylib" -verify_arch "$a" || { echo "build-macos.sh: libcuo_mp3.dylib lacks $a" >&2; exit 1; }
  lipo "$outs/libcuo_mp3.a" -verify_arch "$a" || { echo "build-macos.sh: libcuo_mp3.a lacks $a" >&2; exit 1; }
done
for f in version open_memory close get_info read_s16 seek get_frame_count; do
  nm -gU "$out/libcuo_mp3.dylib" | grep -q " _cuo_mp3_$f\$" || { echo "build-macos.sh: libcuo_mp3.dylib does not export cuo_mp3_$f" >&2; exit 1; }
done
echo "built with $(xcrun clang --version | head -1):"
lipo -info "$out/libcuo_mp3.dylib" "$outs/libcuo_mp3.a"
ls -l "$out/libcuo_mp3.dylib" "$outs/libcuo_mp3.a"
