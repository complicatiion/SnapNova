<#
.SYNOPSIS
    Builds the SnapNova release: snapnova_v<version>_portable.zip (+ .sha256) and, if Inno Setup 6 is installed,
    snapnova_v<version>_setup.exe (+ .sha256).

.DESCRIPTION
    1. Runs the tests (skip with -SkipTests)
    2. Publishes the app self-contained (no .NET runtime needed on the target system)
    3. Checks that the license files are part of the output and that no FFmpeg binary slipped in
    4. Packs the portable ZIP (with SnapNova.portable marker) and writes the SHA-256
    5. Compiles build\installer\snapnova.iss with ISCC (skip with -SkipInstaller)

.EXAMPLE
    build\build.cmd                                   (double-click; uses pwsh or Windows PowerShell)
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1 -SkipTests -SkipInstaller
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SkipTests,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
[xml] $props = Get-Content -Raw -Path (Join-Path $root 'Directory.Build.props')
$versionNode = $props.SelectSingleNode('//Version')
if ($null -eq $versionNode) { throw 'Version not found in Directory.Build.props.' }
$version = $versionNode.InnerText.Trim()

$name = "snapnova_v$version"
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish\SnapNova'
$portableRoot = Join-Path $artifacts "$($name)_portable"
$zip = Join-Path $artifacts "$($name)_portable.zip"

Write-Host "SnapNova $version · $Configuration · $Runtime" -ForegroundColor DarkYellow

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'SnapNova.slnx') -c $Configuration -p:SnapNovaSetup=false
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

foreach ($dir in $publishDir, $portableRoot) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# SnapNovaSetup=false: the MSBuild setup target is skipped here, this script builds the setup itself below.
dotnet publish (Join-Path $root 'src\SnapNova.App\SnapNova.App.csproj') -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:SnapNovaSetup=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publishing SnapNova failed.' }

# Self-contained check: the .NET runtime must be part of the output (no runtime installation on the target PC).
foreach ($runtimeFile in 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll') {
    if (-not (Test-Path (Join-Path $publishDir $runtimeFile))) { throw "Not self-contained – missing in output: $runtimeFile" }
}
$runtimeConfig = Get-Content -Raw -Path (Join-Path $publishDir 'SnapNova.runtimeconfig.json') | ConvertFrom-Json
if ($null -eq $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) {
    throw 'Not self-contained – SnapNova.runtimeconfig.json has no includedFrameworks.'
}

# Shipped next to the app: licenses and README. FFmpeg must never be part of the output (opt-in download only).
foreach ($required in 'SnapNova.exe', 'README.md', 'licenses\LICENSE.md', 'licenses\third-party\FFMPEG.md') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Missing in output: $required" }
}
if (Get-ChildItem -Path $publishDir -Recurse -Include 'ffmpeg*.exe', 'avcodec*.dll', 'avformat*.dll' -ErrorAction SilentlyContinue) {
    throw 'FFmpeg binaries found in the output – SnapNova must not bundle FFmpeg.'
}

# Portable ZIP: same files plus the marker (data in .\data).
$staging = Join-Path $portableRoot 'SnapNova'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $staging -Recurse
Set-Content -Path (Join-Path $staging 'SnapNova.portable') -Value 'Portable mode: settings and the library index are stored in .\data' -Encoding ascii

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii
Write-Host "Portable: $zip" -ForegroundColor Green
Write-Host "SHA-256:  $hash"

# Installer (Inno Setup 6).
if (-not $SkipInstaller) {
    $iscc = @(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($null -eq $iscc) {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found – installer skipped. https://jrsoftware.org/isinfo.php'
    }
    else {
        & $iscc "/DAppVersion=$version" "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'installer\snapnova.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        $setup = Join-Path $artifacts "$($name)_setup.exe"
        $setupHash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$setup.sha256" -Value "$setupHash  $(Split-Path -Leaf $setup)" -Encoding ascii
        Write-Host "Setup:    $setup" -ForegroundColor Green
        Write-Host "SHA-256:  $setupHash"
    }
}
