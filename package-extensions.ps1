<#
  Builds store-ready browser-extension packages from the single source manifest
  (extension/manifest.json):

    dist/voxinator-chrome-<ver>.zip   -> Chrome Web Store
    dist/voxinator-firefox-<ver>.zip  -> Firefox AMO

  The dev manifest carries BOTH background styles (service_worker + scripts) and the Firefox
  gecko block so it loads unpacked in either browser. For the stores we emit clean, browser-
  correct manifests instead:
    Chrome  -> background.service_worker only; no browser_specific_settings.
    Firefox -> background.scripts (event page) only; keeps the gecko id + min version.

  Source of truth is extension/manifest.json — this script only swaps the differing blocks, and
  asserts each block exists so it fails loudly (rather than shipping a wrong manifest) if the
  manifest is ever reformatted.

  Usage:  powershell -ExecutionPolicy Bypass -File package-extensions.ps1 [-Version 0.3.4] [-SkipIcons]
#>
param(
  [string]$Version,
  [switch]$SkipIcons
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ext  = Join-Path $root "extension"
$dist = Join-Path $root "dist"

# Everything except manifest.json (which is generated per-browser below).
$shared = @("background.js", "content.js", "page.js", "options.html", "options.js", "icons")

$base = Get-Content (Join-Path $ext "manifest.json") -Raw
if (-not $Version) {
  if ($base -match '"version"\s*:\s*"([^"]+)"') { $Version = $Matches[1] }
  else { throw "Could not read version from extension/manifest.json" }
}
Write-Host "Packaging Voxinator extension v$Version"

# Refresh the PNG icons from the brand logo so packages always carry current branding.
if (-not $SkipIcons) {
  $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
  if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
  try {
    & $dotnet run --project (Join-Path $root "tools\icongen") -- --pngs (Join-Path $ext "icons") | Out-Host
  } catch {
    Write-Warning "Icon regeneration failed ($_). Falling back to the committed icons."
  }
}
if (-not (Test-Path (Join-Path $ext "icons\icon128.png"))) { throw "extension/icons/icon128.png missing — run with .NET available or commit the icons." }

# ---- per-browser manifest blocks (exact text from extension/manifest.json) ----
$bgBoth = @"
  "background": {
    "service_worker": "background.js",
    "scripts": ["background.js"]
  },
"@
$bgChrome = @"
  "background": {
    "service_worker": "background.js"
  },
"@
$bgFirefox = @"
  "background": {
    "scripts": ["background.js"]
  },
"@
# browser_specific_settings is the final top-level key; removing it for Chrome also drops the
# comma after the preceding "action" block.
$bssBlock = @"
  },
  "browser_specific_settings": {
    "gecko": {
      "id": "voxinator@local",
      "strict_min_version": "128.0"
    }
  }
}
"@
$bssRemoved = @"
  }
}
"@

function New-Manifest([string]$browser) {
  $m = $base
  if (-not $m.Contains($bgBoth)) { throw "background block not found in manifest.json (reformatted?) — update package-extensions.ps1" }
  if ($browser -eq "chrome") {
    $m = $m.Replace($bgBoth, $bgChrome)
    if (-not $m.Contains($bssBlock)) { throw "browser_specific_settings block not found — update package-extensions.ps1" }
    $m = $m.Replace($bssBlock, $bssRemoved)
  } else {
    $m = $m.Replace($bgBoth, $bgFirefox)
  }
  # honor a -Version override (no-op when version came from the manifest itself)
  $m = $m -replace '("version"\s*:\s*")[^"]+(")', ('${1}' + $Version + '${2}')
  $null = $m | ConvertFrom-Json   # assert valid JSON before we ship it
  return $m
}

function Save-Utf8NoBom([string]$path, [string]$text) {
  [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# Zip a directory with FORWARD-SLASH entry names. PowerShell 5.1's Compress-Archive (and .NET
# Framework's ZipFile.CreateFromDirectory) store Windows backslashes, which violate the ZIP spec
# and make Firefox AMO treat "icons\icon16.png" as a single filename — breaking icon references.
function New-Zip([string]$sourceDir, [string]$zipPath) {
  Add-Type -AssemblyName System.IO.Compression | Out-Null
  Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  $base = (Resolve-Path $sourceDir).Path.TrimEnd('\') + '\'
  $fs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
  $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($file in Get-ChildItem $sourceDir -Recurse -File) {
      $rel = $file.FullName.Substring($base.Length) -replace '\\', '/'
      $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
      $es = $entry.Open()
      $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
      $es.Write($bytes, 0, $bytes.Length)
      $es.Dispose()
    }
  } finally {
    $zip.Dispose()
    $fs.Dispose()
  }
}

function Build-Package([string]$browser) {
  $stage = Join-Path $dist "stage-$browser"
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $stage | Out-Null

  foreach ($f in $shared) { Copy-Item (Join-Path $ext $f) (Join-Path $stage $f) -Recurse }
  Save-Utf8NoBom (Join-Path $stage "manifest.json") (New-Manifest $browser)

  $zip = Join-Path $dist "voxinator-$browser-$Version.zip"
  New-Zip $stage $zip
  Remove-Item $stage -Recurse -Force

  $kb = [math]::Round((Get-Item $zip).Length / 1KB, 1)
  Write-Host "  wrote $zip ($kb KB)"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Build-Package "chrome"
Build-Package "firefox"
Write-Host ""
Write-Host "Done."
Write-Host "  Chrome Web Store : dist\voxinator-chrome-$Version.zip"
Write-Host "  Firefox AMO      : dist\voxinator-firefox-$Version.zip"
