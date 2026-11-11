<#
.SYNOPSIS
    Builds every PolicyUnlocker release artifact:
      1. Portable  : self-contained single-file exe packed in a .zip
      2. Installer : Inno Setup .exe (setup wizard, Start menu entry, uninstaller)
      3. Store     : MSIX package for Microsoft Store upload (plus optional signing)
    All outputs go to <repo>\artifacts together with SHA256SUMS.txt.

.PARAMETER Version
    Release version, x.y.z (for example 1.1.0). The MSIX uses x.y.z.0.

.PARAMETER Configuration
    Build configuration. Default: Release.

.NOTES
    Optional MSIX signing (needed only to sideload the .msix on a PC; Partner Center
    signs Store submissions itself). Set both environment variables:
      MSIX_PFX_BASE64   base64 content of a .pfx whose subject equals the manifest Publisher
      MSIX_PFX_PASSWORD password of that .pfx
    If they are not set, the .msix is produced unsigned.

    Requirements: .NET 10 SDK, Windows 10/11 SDK (makeappx, signtool), Inno Setup 6 (ISCC.exe).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like x.y.z, got '$Version'."
}

$root      = Split-Path -Parent $PSScriptRoot
$rid       = 'win-x64'
$name      = "PolicyUnlocker-$Version"
$artifacts = Join-Path $root 'artifacts'
$publish   = Join-Path $artifacts 'publish'
$portable  = Join-Path $artifacts "$name-portable"
$staging   = Join-Path $artifacts 'msix-staging'

Set-Location $root

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$File' exited with code $LASTEXITCODE." }
}

function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'ISCC.exe not found. Install Inno Setup 6: winget install --id JRSoftware.InnoSetup'
}

function Find-SdkTool {
    param([string]$Tool)
    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Tool" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $found) { throw "$Tool not found. Install the Windows 10/11 SDK." }
    return $found
}

# ---------------------------------------------------------------- clean
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force $artifacts | Out-Null

# ---------------------------------------------------------------- 1. publish
Write-Host "==> Publishing self-contained single-file build ($rid, $Version)"
Invoke-Native 'dotnet' @(
    'publish', 'PolicyUnlocker.csproj',
    '-c', $Configuration,
    '-r', $rid,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    "-p:Version=$Version",
    "-p:FileVersion=$Version.0",
    "-p:AssemblyVersion=$Version.0",
    '-o', $publish
)
$exe = Join-Path $publish 'PolicyUnlocker.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }

# ---------------------------------------------------------------- 2. portable
Write-Host '==> Building portable zip'
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item $exe $portable
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $portable
$zip = Join-Path $artifacts "$name-portable-$rid.zip"
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip

# ---------------------------------------------------------------- 3. installer
Write-Host '==> Building Inno Setup installer'
$iscc = Find-Iscc
Invoke-Native $iscc @(
    "/DAppVersion=$Version",
    "/DSourceDir=$publish",
    "/DOutputDir=$artifacts",
    "/DRepoRoot=$root",
    (Join-Path $root 'installer\PolicyUnlocker.iss')
)

# ---------------------------------------------------------------- 4. MSIX (Store)
Write-Host '==> Building MSIX package for Microsoft Store'
$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = $null

New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item $exe $staging
Copy-Item (Join-Path $root 'Images') (Join-Path $staging 'Images') -Recurse

# The package root must contain AppxManifest.xml; the version is set to x.y.z.0 in a copy.
[xml]$manifest = Get-Content (Join-Path $root 'Package.appxmanifest') -Raw
$manifest.Package.Identity.Version = "$Version.0"
$manifest.Save((Join-Path $staging 'AppxManifest.xml'))

$msix = Join-Path $artifacts "$name-store.msix"
Invoke-Native $makeappx @('pack', '/d', $staging, '/p', $msix, '/o')

if ($env:MSIX_PFX_BASE64 -and $env:MSIX_PFX_PASSWORD) {
    Write-Host '==> Signing MSIX'
    $signtool = Find-SdkTool 'signtool.exe'
    $pfx = Join-Path ([IO.Path]::GetTempPath()) ("msix-" + [guid]::NewGuid() + '.pfx')
    try {
        [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:MSIX_PFX_BASE64))
        Invoke-Native $signtool @('sign', '/fd', 'SHA256', '/f', $pfx, '/p', $env:MSIX_PFX_PASSWORD, $msix)
    }
    finally {
        Remove-Item $pfx -Force -ErrorAction SilentlyContinue
    }
}
else {
    Write-Host '    (no MSIX_PFX_* variables set: package left unsigned, fine for Store upload)'
}

# ---------------------------------------------------------------- 5. cleanup + checksums
Remove-Item $publish, $portable, $staging -Recurse -Force

Write-Host '==> Writing SHA256SUMS.txt'
$sums = Get-ChildItem $artifacts -File | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sums | Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii

Write-Host ''
Write-Host "Release $Version artifacts:"
Get-ChildItem $artifacts -File | ForEach-Object {
    '  {0,-55} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
