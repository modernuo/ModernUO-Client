<#
.SYNOPSIS
    Publishes the ClassicUO client as a standalone NativeAOT exe (win-x64, x86-64-v3) for run-fps-test.ps1.

.DESCRIPTION
    Output goes to -OutDir (default C:\Repositories\cuo-fps-test\bin), outside the repo. The folder is replaced.
    The patched FNA3D.dll and the other native libraries come from external\x64 (the csproj's publish step).
    build-info.txt records the commit so the results file can name the build.
    Needs the .NET SDK and the Visual Studio C++ tools (found with vswhere).
#>
param(
    [string]$OutDir = 'C:\Repositories\cuo-fps-test\bin'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\ClassicUO.Client\ClassicUO.Client.csproj'
$env:DOTNET_GCHeapHardLimit = '0x200000000'

if (Test-Path $OutDir) {
    Remove-Item -Recurse -Force $OutDir
}

$publish = "dotnet publish `"$project`" -c Release -r win-x64 -o `"$OutDir`" -nologo -p:IlcUseEnvironmentalTools=true"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = $null

if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath

    if ($vs) {
        $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    }
}

if ($vcvars -and (Test-Path $vcvars)) {
    cmd /c "call `"$vcvars`" >nul && $publish"
}
else {
    Write-Warning 'Visual Studio C++ tools not found with vswhere; publishing without vcvars64.'
    Invoke-Expression ($publish -replace ' -p:IlcUseEnvironmentalTools=true', '')
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

$commit = (git -C $repo rev-parse --short HEAD).Trim()
$branch = (git -C $repo rev-parse --abbrev-ref HEAD).Trim()
$dirty = if (git -C $repo status --porcelain --untracked-files=no -- src poc) { ' + uncommitted changes in src/poc' } else { '' }
"$branch $commit$dirty, published $(Get-Date -Format 'yyyy-MM-dd HH:mm'), NativeAOT win-x64 x86-64-v3" |
    Set-Content -Encoding ASCII (Join-Path $OutDir 'build-info.txt')

$size = (Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Published to {0} ({1:N1} MB): {2}" -f $OutDir, ($size / 1MB), (Get-Content (Join-Path $OutDir 'build-info.txt')))
