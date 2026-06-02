#requires -version 5
<#
.SYNOPSIS
  Build, package, and (optionally) publish a Voxinator release with Velopack.

.DESCRIPTION
  1. Publishes the engine self-contained (win-x64).
  2. Packs it with Velopack -> Releases\ (Setup.exe + full/delta packages), creating Desktop +
     Start-Menu shortcuts with the speaker logo and a branded install splash.
  3. With -Upload, publishes the packages to GitHub Releases (the app's auto-update feed).

.EXAMPLE
  .\release.ps1                       # build + pack locally, version from the csproj
  .\release.ps1 -Version 1.0.1        # override version
  .\release.ps1 -Upload -Token <PAT>  # also publish to GitHub Releases
#>
param(
  [string]$Version,
  [switch]$Upload,
  [string]$Token = $env:GITHUB_TOKEN
)
$ErrorActionPreference = "Stop"

$root     = $PSScriptRoot
$proj     = Join-Path $root "engine\Voxinator.csproj"
$publish  = Join-Path $root "engine\publish"
$icon     = Join-Path $root "engine\voxinator.ico"
$splash   = Join-Path $root "engine\assets\splash.png"
$releases = Join-Path $root "Releases"
$repoUrl  = "https://github.com/LoganO37/Voxinator"

# Resolve dotnet: prefer the per-user SDK install (some machines have an SDK-less dotnet on PATH),
# else fall back to PATH.
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet -or -not (Test-Path $dotnet)) { throw "dotnet not found." }
# Point the framework-dependent vpk tool at this dotnet's shared runtime.
$env:DOTNET_ROOT = Split-Path $dotnet

# Version: -Version param, else <Version> from the csproj.
if (-not $Version) {
  [xml]$x = Get-Content $proj
  $Version = ($x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if (-not $Version) { throw "No version found. Pass -Version x.y.z or set <Version> in the csproj." }
Write-Host "== Voxinator release $Version ==" -ForegroundColor Cyan

# Ensure the Velopack CLI (vpk) is available.
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) {
  Write-Host "Installing vpk (Velopack CLI)..." -ForegroundColor Yellow
  & $dotnet tool install -g vpk | Out-Host
  $vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
}
if (-not (Test-Path $vpk)) { throw "vpk not found after install. Ensure %USERPROFILE%\.dotnet\tools is on PATH." }

# 1. Publish self-contained.
Write-Host "Publishing (self-contained win-x64)..." -ForegroundColor Yellow
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# 2. Pack with Velopack.
Write-Host "Packing with Velopack..." -ForegroundColor Yellow
& $vpk pack `
  --packId Voxinator `
  --packTitle "Voxinator" `
  --packVersion $Version `
  --packDir $publish `
  --mainExe voxinator.exe `
  --icon $icon `
  --splashImage $splash `
  --shortcuts "Desktop,StartMenuRoot" `
  --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Write-Host "Packaged to $releases" -ForegroundColor Green
Get-ChildItem $releases | Select-Object Name, @{n="KB";e={[int]($_.Length/1KB)}} | Format-Table -AutoSize

# 3. Optional: publish to GitHub Releases (the auto-update feed).
if ($Upload) {
  if (-not $Token) { throw "Upload requires -Token <PAT> or `$env:GITHUB_TOKEN." }
  Write-Host "Uploading to GitHub Releases..." -ForegroundColor Yellow
  & $vpk upload github `
    --repoUrl $repoUrl `
    --publish `
    --releaseName "Voxinator $Version" `
    --token $Token `
    --outputDir $releases
  if ($LASTEXITCODE -ne 0) { throw "upload failed" }
  Write-Host "Uploaded Voxinator $Version to $repoUrl/releases" -ForegroundColor Green
}
