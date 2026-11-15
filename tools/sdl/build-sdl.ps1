<#
.SYNOPSIS
    Builds SDL3.dll (Release, win-x64) from the external/SDL submodule (github.com/modernuo/SDL) and copies it to
    external/x64/SDL3.dll, with external/x64/SDL3.version recording the fork commit.

.DESCRIPTION
    See docs/modern-client/sdl-fork.md. Needs CMake and the Visual Studio C++ tools (found with vswhere).
    The build folder is outside the repo (default %TEMP%\cuo-sdl-build) and is replaced.
    Commit the DLL and SDL3.version together with the submodule pointer, so normal builds need no C toolchain.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\sdl\build-sdl.ps1
#>
param(
    [string]$BuildDir = (Join-Path $env:TEMP 'cuo-sdl-build'),
    # Build only; don't copy into external/x64
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$src = Join-Path $repo 'external\SDL'

if (-not (Test-Path (Join-Path $src 'CMakeLists.txt'))) {
    throw "external\SDL is empty: run git submodule update --init external/SDL"
}

$commit = (git -C $src rev-parse HEAD).Trim()
$short = $commit.Substring(0, 9)
$dirty = if (git -C $src status --porcelain) { '-dirty' } else { '' }
$branch = (git -C $repo config -f .gitmodules submodule.external/SDL.branch)
# SDL_GetRevision() returns "SDL-<version>-<git describe> (<vendor info>)"; the vendor info names the fork commit,
# so a log line shows which DLL is loaded even from a shallow clone
$vendor = "modernuo $branch $short$dirty"

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found (vswhere)' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

if (Test-Path $BuildDir) { Remove-Item -Recurse -Force $BuildDir }

$configure = "cmake -S `"$src`" -B `"$BuildDir`" -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=Release -DSDL_SHARED=ON -DSDL_STATIC=OFF " +
             "-DSDL_LIBC=OFF -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF `"-DSDL_VENDOR_INFO=$vendor`""
cmd /c "call `"$vcvars`" >nul && $configure && cmake --build `"$BuildDir`" --parallel"
if ($LASTEXITCODE -ne 0) { throw "SDL build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $BuildDir 'SDL3.dll'
if (-not (Test-Path $dll)) { throw "no SDL3.dll in $BuildDir" }

if (-not $NoCopy) {
    $x64 = Join-Path $repo 'external\x64'
    Copy-Item $dll (Join-Path $x64 'SDL3.dll') -Force
    @(
        "SDL3.dll built from github.com/modernuo/SDL $branch at $commit$dirty",
        "SDL_VENDOR_INFO: $vendor",
        "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm') with tools\sdl\build-sdl.ps1 (Release, NMake, MSVC x64)",
        "SHA256 $(-join ([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($dll)) | ForEach-Object { $_.ToString('X2') }))"
    ) | Set-Content -Encoding ASCII (Join-Path $x64 'SDL3.version')
    Write-Host "Copied to $x64\SDL3.dll; $x64\SDL3.version:"
    Get-Content (Join-Path $x64 'SDL3.version') | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "Built $dll"
}
