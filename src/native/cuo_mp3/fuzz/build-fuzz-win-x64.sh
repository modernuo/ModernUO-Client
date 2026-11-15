#!/bin/bash
# Builds the cuo_mp3 libFuzzer target with MSVC (-fsanitize=fuzzer,address), from Git Bash.
# Usage: bash src/native/cuo_mp3/fuzz/build-fuzz-win-x64.sh <out-dir> [nosimd]
#   <out-dir>/fuzz_cuo_mp3.exe (plus the ASan runtime DLL copied next to it)
#   nosimd: compile dr_mp3 with DR_MP3_NO_SIMD (the scalar paths)
# Seeds: make-seeds.sh. Run: <out-dir>/fuzz_cuo_mp3.exe -max_len=65536 -max_total_time=600 <corpus-dir> <seed-dir>
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'

here="$(cd "$(dirname "$0")" && pwd)"
out="$(mkdir -p "$1" && cd "$1" && pwd)"
extra=""
[ "${2:-}" = "nosimd" ] && extra="-DDR_MP3_NO_SIMD"

vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
vs="$(cygpath -u "$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')")"
msvcver="$(tr -d '\r\n ' < "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt")"
msvc="$vs/VC/Tools/MSVC/$msvcver"
kits="/c/Program Files (x86)/Windows Kits/10"
sdkver="$(ls "$kits/Include" | grep '^10\.' | sort -V | tail -1)"
w() { cygpath -w "$1"; }
export PATH="$msvc/bin/Hostx64/x64:$PATH"
export INCLUDE="$(w "$msvc/include");$(w "$kits/Include/$sdkver/ucrt");$(w "$kits/Include/$sdkver/um");$(w "$kits/Include/$sdkver/shared")"
export LIB="$(w "$msvc/lib/x64");$(w "$kits/Lib/$sdkver/ucrt/x64");$(w "$kits/Lib/$sdkver/um/x64")"

cd "$out"
cl -nologo -O2 -Zi -MT -fsanitize=fuzzer -fsanitize=address $extra \
   "$(w "$here/fuzz_cuo_mp3.c")" "$(w "$here/../cuo_mp3.c")" "-Fe$(w "$out/fuzz_cuo_mp3.exe")" -link -INCREMENTAL:NO
cp "$msvc/bin/Hostx64/x64/clang_rt.asan_dynamic-x86_64.dll" "$out/" 2>/dev/null || true
ls -l "$out/fuzz_cuo_mp3.exe"
