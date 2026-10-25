<#
.SYNOPSIS
    Builds FNA3D.dll (Release, win-x64) from external/FNA/lib/FNA3D (github.com/modernuo/FNA3D) and copies it to
    external/x64/FNA3D.dll, with external/x64/FNA3D.version recording the fork commit.

.DESCRIPTION
    See docs/modern-client/fna-fork.md. Needs CMake and the Visual Studio C++ tools (found with vswhere).
    Links against our SDL fork: headers from external/SDL, SDL3.lib from the SDL build folder (built with
    tools\sdl\build-sdl.ps1 -NoCopy when it is missing). The CRT is linked statically, so the DLL imports only
    SDL3.dll and KERNEL32.dll. The build folder is outside the repo (default %TEMP%\cuo-fna3d-build) and is replaced.
    Commit the DLL and FNA3D.version together with the submodule pointers.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fna3d\build-fna3d.ps1
#>
param(
    [string]$BuildDir = (Join-Path $env:TEMP 'cuo-fna3d-build'),
    [string]$SdlBuildDir = (Join-Path $env:TEMP 'cuo-sdl-build'),
    # Build only; don't copy into external/x64
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$src = Join-Path $repo 'external\FNA\lib\FNA3D'
$sdlInclude = Join-Path $repo 'external\SDL\include'

if (-not (Test-Path (Join-Path $src 'CMakeLists.txt'))) {
    throw "external\FNA\lib\FNA3D is empty: run git submodule update --init --recursive external/FNA"
}
if (-not (Test-Path (Join-Path $src 'MojoShader\CMakeLists.txt'))) {
    throw "external\FNA\lib\FNA3D\MojoShader is empty: run git -C external/FNA submodule update --init --recursive lib/FNA3D"
}
if (-not (Test-Path (Join-Path $sdlInclude 'SDL3\SDL.h'))) {
    throw "external\SDL is empty: run git submodule update --init external/SDL"
}

$sdlLib = Join-Path $SdlBuildDir 'SDL3.lib'
if (-not (Test-Path $sdlLib)) {
    & (Join-Path $repo 'tools\sdl\build-sdl.ps1') -BuildDir $SdlBuildDir -NoCopy
    if (-not (Test-Path $sdlLib)) { throw "no SDL3.lib in $SdlBuildDir" }
}

$commit = (git -C $src rev-parse HEAD).Trim()
$dirty = if (git -C $src status --porcelain) { '-dirty' } else { '' }
$branch = (git -C (Join-Path $repo 'external\FNA') config -f .gitmodules submodule.lib/FNA3D.branch)
$fnaCommit = (git -C (Join-Path $repo 'external\FNA') rev-parse HEAD).Trim()

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found (vswhere)' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

if (Test-Path $BuildDir) { Remove-Item -Recurse -Force $BuildDir }

$configure = "cmake -S `"$src`" -B `"$BuildDir`" -G `"NMake Makefiles`" -DCMAKE_BUILD_TYPE=Release " +
             "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded " +
             "`"-DSDL3_INCLUDE_DIRS=$sdlInclude`" `"-DSDL3_LIBRARIES=$sdlLib`""
cmd /c "call `"$vcvars`" >nul && $configure && cmake --build `"$BuildDir`" --parallel"
if ($LASTEXITCODE -ne 0) { throw "FNA3D build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $BuildDir 'FNA3D.dll'
if (-not (Test-Path $dll)) { throw "no FNA3D.dll in $BuildDir" }

if (-not $NoCopy) {
    $x64 = Join-Path $repo 'external\x64'
    Copy-Item $dll (Join-Path $x64 'FNA3D.dll') -Force
    $sdlVersion = (Get-Content (Join-Path $x64 'SDL3.version') -TotalCount 1)
    @(
        "FNA3D.dll built from github.com/modernuo/FNA3D $branch at $commit$dirty",
        "FNA (github.com/modernuo/FNA) at $fnaCommit",
        "Linked against: $sdlVersion",
        "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm') with tools\fna3d\build-fna3d.ps1 (Release, NMake, MSVC x64, static CRT)",
        "SHA256 $(-join ([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($dll)) | ForEach-Object { $_.ToString('X2') }))"
    ) | Set-Content -Encoding ASCII (Join-Path $x64 'FNA3D.version')
    Write-Host "Copied to $x64\FNA3D.dll; $x64\FNA3D.version:"
    Get-Content (Join-Path $x64 'FNA3D.version') | ForEach-Object { Write-Host "  $_" }
}
