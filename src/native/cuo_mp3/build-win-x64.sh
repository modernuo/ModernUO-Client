#!/bin/bash
# Builds cuo_mp3 for win-x64 with MSVC, from Git Bash (no vcvars/cmd needed):
#   external/x64/cuo_mp3.dll            JIT builds load it (copied next to cuo.exe by ClassicUO.Client.csproj)
#   external/x64-static/cuo_mp3.lib     NativeAOT links it into cuo.exe (DirectPInvoke)
# Usage: bash src/native/cuo_mp3/build-win-x64.sh
# Needs Visual Studio with the C++ x64 tools and a Windows 10/11 SDK (found with vswhere).
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'   # keep cl's arguments as written

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
vs="$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
vs="$(cygpath -u "$vs")"
msvcver="$(tr -d '\r\n ' < "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt")"
msvc="$vs/VC/Tools/MSVC/$msvcver"
kits="/c/Program Files (x86)/Windows Kits/10"
sdkver="$(ls "$kits/Include" | grep '^10\.' | sort -V | tail -1)"

w() { cygpath -w "$1"; }
export PATH="$msvc/bin/Hostx64/x64:$PATH"
export INCLUDE="$(w "$msvc/include");$(w "$kits/Include/$sdkver/ucrt");$(w "$kits/Include/$sdkver/um");$(w "$kits/Include/$sdkver/shared")"
export LIB="$(w "$msvc/lib/x64");$(w "$kits/Lib/$sdkver/ucrt/x64");$(w "$kits/Lib/$sdkver/um/x64")"

obj="${TEMP:-/tmp}/cuo-mp3-build"
rm -rf "$obj"; mkdir -p "$obj" "$root/external/x64" "$root/external/x64-static"
src="$(w "$here/cuo_mp3.c")"
common="-nologo -O2 -W3 -GS -Gy -DNDEBUG"

# Static library for NativeAOT: -Zl leaves out the default CRT name, so the final link picks the CRT that ILC uses.
cl $common -Zl -c "$src" "-Fo$(w "$obj/cuo_mp3_static.obj")"
lib -nologo "-out:$(w "$root/external/x64-static/cuo_mp3.lib")" "$(w "$obj/cuo_mp3_static.obj")"

# DLL for JIT builds: static CRT (-MT), so it needs no VC++ redistributable.
cl $common -Zi -MT -DCUO_MP3_SHARED -c "$src" "-Fo$(w "$obj/cuo_mp3.obj")" "-Fd$(w "$obj/cuo_mp3.pdb")"
link -nologo -DLL -OPT:REF -OPT:ICF -DEBUG "-PDB:$(w "$obj/cuo_mp3_dll.pdb")" "-out:$(w "$root/external/x64/cuo_mp3.dll")" "$(w "$obj/cuo_mp3.obj")"
rm -f "$root/external/x64/cuo_mp3.exp" "$root/external/x64/cuo_mp3.lib"

echo "built with MSVC $msvcver, SDK $sdkver:"
ls -l "$root/external/x64/cuo_mp3.dll" "$root/external/x64-static/cuo_mp3.lib"
