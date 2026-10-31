#!/bin/bash
# Builds cuo_mp3 for linux-x64 or macOS (arm64, x64 or universal) with CMake and the system C compiler.
#   linux-x64:  external/lib64/libcuo_mp3.so     (JIT)   external/lib64-static/libcuo_mp3.a  (NativeAOT)
#   macOS:      external/osx/libcuo_mp3.dylib    (JIT)   external/osx-static/libcuo_mp3.a    (NativeAOT)
# Usage: bash src/native/cuo_mp3/build-unix.sh [arm64|x86_64|universal]   (the argument is macOS only; default universal)
# Needs cmake >= 3.16 and cc (gcc or clang). Not run on the Windows dev box: untested until someone runs it on
# Linux and macOS (2026-10-31).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
build="${TMPDIR:-/tmp}/cuo-mp3-build"
rm -rf "$build"

case "$(uname -s)" in
  Linux)
    out="$root/external/lib64"; outs="$root/external/lib64-static"; shared="libcuo_mp3.so"
    cmake -S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release
    ;;
  Darwin)
    out="$root/external/osx"; outs="$root/external/osx-static"; shared="libcuo_mp3.dylib"
    case "${1:-universal}" in
      arm64)     archs="arm64" ;;
      x86_64)    archs="x86_64" ;;
      universal) archs="arm64;x86_64" ;;
      *) echo "unknown arch $1"; exit 1 ;;
    esac
    cmake -S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="$archs" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0
    ;;
  *)
    echo "use build-win-x64.sh on Windows"; exit 1 ;;
esac

cmake --build "$build" --config Release -j
mkdir -p "$out" "$outs"
cp "$build/$shared" "$out/"
cp "$build/libcuo_mp3.a" "$outs/"
ls -l "$out/$shared" "$outs/libcuo_mp3.a"
