param(
    [string]$Configuration = "Release",
    [string]$Version = "3.0.2.0",
    [string]$InnoSetupCompiler = $env:ISCC_EXE
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot "src/NINA.Plugins.Fujifilm/bin/x64/$Configuration/net8.0-windows/publish"
$script = Join-Path $repoRoot "installer/FujifilmPlugin.iss"

if (-not (Test-Path $publishDir)) {
    throw "Publish directory does not exist: $publishDir. Build the plugin first."
}

foreach ($required in @(
    "NINA.Plugins.Fujifilm.dll",
    "plugin.json",
    "XAPI.dll",
    "XSDK.DAT",
    "FF0002API.dll",
    "Configuration/Assets/CameraConfigs/X-T2.json",
    "Configuration/Assets/CameraConfigs/GFX100RF.json"
)) {
    $path = Join-Path $publishDir $required
    if (-not (Test-Path $path)) {
        throw "Cannot build installer; required publish file is missing: $path"
    }
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InnoSetupCompiler = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not (Test-Path $InnoSetupCompiler)) {
    throw "Inno Setup compiler not found. Install Inno Setup 6 or set ISCC_EXE."
}

New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "dist") | Out-Null
& $InnoSetupCompiler "/DMyAppVersion=$Version" $script

$installer = Join-Path $repoRoot "dist/NINA.Fujifilm.Plugin-$Version-Setup.exe"
if (-not (Test-Path $installer)) {
    throw "Installer was not produced: $installer"
}

$hash = Get-FileHash -Algorithm SHA256 $installer
$hashLine = "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $installer)"
Set-Content -Path "$installer.sha256" -Value $hashLine -NoNewline

Write-Host "Built installer: $installer"
Write-Host "SHA256: $($hash.Hash.ToLowerInvariant())"
