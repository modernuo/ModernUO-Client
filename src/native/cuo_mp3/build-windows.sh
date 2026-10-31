#!/bin/bash
# Builds cuo_mp3 (dr_mp3) for win-x64 with MSVC, from Git Bash (no vcvars, no cmd/PowerShell). One command:
#   bash src/native/cuo_mp3/build-windows.sh
# Writes exactly what the csproj files look for (both are committed, like SDL3.dll):
#   external/x64/cuo_mp3.dll            JIT builds and tests (ClassicUO.IO.csproj copies it to the output folder)
#   external/x64-static/cuo_mp3.lib     NativeAOT publish -r win-x64 links it into cuo.exe (DirectPInvoke)
# Prerequisites: Git Bash, Visual Studio 2022+ (or Build Tools) with "MSVC x64/x86 build tools" and a Windows 10/11
# SDK, found through vswhere. On GitHub's windows runners these are preinstalled; run it with `shell: bash`.
set -euo pipefail
export MSYS2_ARG_CONV_EXCL='*'   # keep cl's arguments as written

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
fail() { echo "build-windows.sh: $*" >&2; exit 1; }

case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ;; *) fail "this is $(uname -s); use build-linux.sh or build-macos.sh" ;; esac
command -v cygpath >/dev/null 2>&1 || fail "missing cygpath: run this from Git Bash (Git for Windows)"
vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
[ -x "$vswhere" ] || fail "missing vswhere.exe: install Visual Studio 2022+ or the VS Build Tools"
vs="$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
[ -n "$vs" ] || fail "missing the MSVC x64 tools: add the 'MSVC v14x - VS C++ x64/x86 build tools' component"
vs="$(cygpath -u "$vs")"
msvcver="$(tr -d '\r\n ' < "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt")"
msvc="$vs/VC/Tools/MSVC/$msvcver"
[ -x "$msvc/bin/Hostx64/x64/cl.exe" ] || fail "missing cl.exe under $msvc"
kits="/c/Program Files (x86)/Windows Kits/10"
sdkver="$(ls "$kits/Include" 2>/dev/null | grep '^10\.' | sort -V | tail -1 || true)"
[ -n "$sdkver" ] || fail "missing a Windows 10/11 SDK (Windows Kits/10/Include)"

w() { cygpath -w "$1"; }
export PATH="$msvc/bin/Hostx64/x64:$PATH"
export INCLUDE="$(w "$msvc/include");$(w "$kits/Include/$sdkver/ucrt");$(w "$kits/Include/$sdkver/um");$(w "$kits/Include/$sdkver/shared")"
export LIB="$(w "$msvc/lib/x64");$(w "$kits/Lib/$sdkver/ucrt/x64");$(w "$kits/Lib/$sdkver/um/x64")"

obj="$(mktemp -d "${TEMP:-/tmp}/cuo-mp3-build.XXXXXX")"
trap 'rm -rf "$obj"' EXIT
mkdir -p "$root/external/x64" "$root/external/x64-static"
src="$(w "$here/cuo_mp3.c")"
common="-nologo -O2 -W3 -GS -Gy -DNDEBUG"

# Static library for NativeAOT: -Zl leaves out the default CRT name, so the final link picks the CRT that ILC uses.
cl $common -Zl -c "$src" "-Fo$(w "$obj/cuo_mp3_static.obj")"
lib -nologo "-out:$(w "$root/external/x64-static/cuo_mp3.lib")" "$(w "$obj/cuo_mp3_static.obj")"

# DLL for JIT builds: static CRT (-MT), so it needs no VC++ redistributable.
cl $common -Zi -MT -DCUO_MP3_SHARED -c "$src" "-Fo$(w "$obj/cuo_mp3.obj")" "-Fd$(w "$obj/cuo_mp3.pdb")"
link -nologo -DLL -OPT:REF -OPT:ICF -DEBUG "-PDB:$(w "$obj/cuo_mp3_dll.pdb")" "-out:$(w "$root/external/x64/cuo_mp3.dll")" "$(w "$obj/cuo_mp3.obj")"
rm -f "$root/external/x64/cuo_mp3.exp" "$root/external/x64/cuo_mp3.lib"

# The seven exports the managed side binds (Mp3Native.cs) must be in the DLL.
exports="$(dumpbin -nologo -exports "$(w "$root/external/x64/cuo_mp3.dll")")"
for f in version open_memory close get_info read_s16 seek get_frame_count; do
  grep -qw "cuo_mp3_$f" <<<"$exports" || fail "cuo_mp3.dll does not export cuo_mp3_$f"
done

echo "built with MSVC $msvcver, SDK $sdkver:"
ls -l "$root/external/x64/cuo_mp3.dll" "$root/external/x64-static/cuo_mp3.lib"
