#requires -Version 7
<#
.SYNOPSIS
  One build script for Indigo: compile, test and package (game + session server).
  It lives in src\, so it also works in the public repo after copying src\ alone (without scripts\, game_src\ and mods\).

.DESCRIPTION
  The version comes from Directory.Build.props (SharedVersion) – that is the single place to change it.
  1. dotnet build NFS2SE.sln (the whole solution, tests included).
  2. dotnet test NFS2SE.sln (unless -SkipTests).
  3. Publish packages to out\publish\ + zips:
     - game (NFS.Desktop), win-x64 self-contained, single-file with the native libraries next to the exe
       (falls back to a plain folder on failure or with -Folder),
     - session server (NFS.Server), single-file, for every runtime in -ServerRuntimes.
  No Android (separate NFS2SE.Android.sln) and no Full package with game data
  (the original data and mods live outside src\ – scripts\deploy.ps1 in the private repo does that).
  The script never launches the built programs (no windows).

.PARAMETER Configuration
  Build configuration: Release (default) or Debug.

.PARAMETER ServerRuntimes
  Server runtimes; defaults to win-x64, linux-x64, linux-arm64 (Raspberry Pi).

.PARAMETER SkipTests
  Skip dotnet test (quick publish).

.PARAMETER NoServer
  Do not build the server packages.

.PARAMETER NoGame
  Do not build the game package.

.PARAMETER Folder
  Build the game as a plain folder instead of single-file (diagnostics).

.EXAMPLE
  pwsh -NoProfile -File src\Build.ps1
  pwsh -NoProfile -File src\Build.ps1 -SkipTests -ServerRuntimes win-x64
  pwsh -NoProfile -File src\Build.ps1 -NoGame -Folder
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',
  [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
  [string[]]$ServerRuntimes = @('win-x64', 'linux-x64', 'linux-arm64'),
  [switch]$SkipTests,
  [switch]$NoServer,
  [switch]$NoGame,
  [switch]$Folder
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$solution  = Join-Path $root 'NFS2SE.sln'
$outRoot   = Join-Path $root 'out\publish'
$results   = [System.Collections.Generic.List[object]]::new()

# Release version – the only place to change it is Directory.Build.props (SharedVersion).
$propsPath = Join-Path $root 'Directory.Build.props'
$Version = ([xml](Get-Content $propsPath -Raw)).SelectSingleNode('//SharedVersion').InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($Version)) { throw "SharedVersion not found in $propsPath" }

function Reset-Dir([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

function Invoke-Publish([string]$project, [string]$runtime, [string]$outDir, [string[]]$extra) {
    dotnet publish (Join-Path $root "src\$project") -c $Configuration -r $runtime --self-contained true `
        -o $outDir @extra | Out-Host
    return $LASTEXITCODE
}

function Complete-Package([string]$name, [string]$outDir, [string]$layout) {
    $zip = "$outDir.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $results.Add([pscustomobject]@{
        Package = $name
        Layout  = $layout
        Files   = (Get-ChildItem $outDir -Recurse -File | Measure-Object).Count
        'Zip MB' = '{0:N1}' -f ((Get-Item $zip).Length / 1MB)
    })
}

# --- 1. Build + tests ---------------------------------------------------------------------------------------------
Write-Host "== build $solution ($Configuration) =="
dotnet build $solution -c $Configuration --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with code $LASTEXITCODE" }

if (-not $SkipTests) {
    Write-Host '== test =='
    dotnet test $solution -c $Configuration --no-build --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited with code $LASTEXITCODE" }
}

# --- 2. Game (win-x64) --------------------------------------------------------------------------------------------
if (-not $NoGame) {
    $gameName = "Indigo-$Version-win-x64"
    $gameOut  = Join-Path $outRoot $gameName
    Reset-Dir $gameOut

    # Single-file: the .NET code goes into one exe, but the native libraries (SDL2.dll, soft_oal.dll) stay next to it –
    # MonoGame loads them from disk with its own loader, so IncludeNativeLibrariesForSelfExtract=true does NOT work.
    $singleOk = $false
    if (-not $Folder) {
        $code = Invoke-Publish 'NFS.Desktop' 'win-x64' $gameOut @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=none')
        if ($code -ne 0) { Write-Warning "single-file publish exited with code $code – falling back to folder." }
        elseif (-not (Test-Path (Join-Path $gameOut 'Indigo.exe'))) { Write-Warning 'single-file: no Indigo.exe – falling back to folder.' }
        elseif (-not (Test-Path (Join-Path $gameOut 'SDL2.dll'))) { Write-Warning 'single-file: no SDL2.dll next to the exe – falling back to folder.' }
        else { $singleOk = $true }
        if (-not $singleOk) { Reset-Dir $gameOut }
    }
    if (-not $singleOk) {
        $code = Invoke-Publish 'NFS.Desktop' 'win-x64' $gameOut @()
        if ($code -ne 0) { throw "game publish (folder) exited with code $code" }
    }

    Complete-Package $gameName $gameOut ($(if ($singleOk) { 'single-file' } else { 'folder' }))
}

# --- 3. Session server ---------------------------------------------------------------------------------------------
if (-not $NoServer) {
    foreach ($rid in $ServerRuntimes) {
        $name = "Indigo-Server-$Version-$rid"
        $dir  = Join-Path $outRoot $name
        Reset-Dir $dir
        $code = Invoke-Publish 'NFS.Server' $rid $dir @('-p:PublishSingleFile=true', '-p:DebugType=none')
        if ($code -ne 0) { throw "server publish ($rid) exited with code $code" }
        $exe = if ($rid -like 'win-*') { 'Indigo-Server.exe' } else { 'Indigo-Server' }
        if (-not (Test-Path (Join-Path $dir $exe))) { throw "server publish ($rid) did not create $exe" }
        Complete-Package $name $dir 'single-file'
    }
}

Write-Host ''
Write-Host "Indigo $Version – packages in $outRoot"
if ($results.Count -gt 0) { $results | Format-Table -AutoSize | Out-Host }
