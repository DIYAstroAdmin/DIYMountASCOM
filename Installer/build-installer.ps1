<#
.SYNOPSIS
    Builds the Release driver and compiles the Inno Setup installer.

.DESCRIPTION
    Requires Visual Studio (or Build Tools) with MSBuild, and Inno Setup 6
    (https://jrsoftware.org/isinfo.php, or: winget install JRSoftware.InnoSetup).
    The setup EXE is written to Installer\Output.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Locate MSBuild through vswhere.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found - install Visual Studio or the VS Build Tools.' }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found.' }

& $msbuild (Join-Path $root 'DIYMountASCOM.csproj') /t:Rebuild /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }

# Locate the Inno Setup compiler (machine-wide or per-user install).
$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc (Join-Path $PSScriptRoot 'DIYMountSetup.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

Get-ChildItem (Join-Path $PSScriptRoot 'Output\*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { "Installer: $($_.FullName)" }
