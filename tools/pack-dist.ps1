# Build a portable GMToolBox zip: exe + ExtProxy + boot + base addons.
# User only sets Ascension.exe, then Launch.
param(
    [string]$OutDir = "",
    [string]$ZipName = "ascension-gm-dist.zip"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not (Test-Path (Join-Path $repo "ExtProxy\build.ps1"))) {
    throw "Run from the repo that contains ExtProxy/ and GmToolbox/"
}
if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts" }
$stage = Join-Path $OutDir "dist"
$zipPath = Join-Path $OutDir $ZipName

$baseAddons = @(
    "GmShared",
    "GmUI",
    "GmTooltipFix",
    "GmTeleport",
    "GmMapTeleport",
    "GmCmds"
)

Write-Host "Building ExtProxy + AscensionBoot..."
& (Join-Path $repo "ExtProxy\build.ps1") -SkipToolbox
if ($LASTEXITCODE -ne 0) { throw "ExtProxy build failed" }

Write-Host "Publishing GMToolBox..."
$gmtOut = Join-Path $OutDir "gmt-publish"
if (Test-Path $gmtOut) { Remove-Item $gmtOut -Recurse -Force }
dotnet publish (Join-Path $repo "GmToolbox\GmToolbox.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $gmtOut
if ($LASTEXITCODE -ne 0) { throw "GMToolBox publish failed" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $stage "AddOns"), (Join-Path $stage "Config") | Out-Null

Copy-Item (Join-Path $gmtOut "GMToolBox.exe") (Join-Path $stage "GMToolBox.exe") -Force
Copy-Item (Join-Path $repo "ExtProxy\ExtProxy64.dll") (Join-Path $stage "ExtProxy64.dll") -Force
$boot = Join-Path $repo "ExtProxy\AscensionBoot.exe"
if (-not (Test-Path $boot)) { $boot = Join-Path $repo "AscensionBoot\AscensionBoot.exe" }
Copy-Item $boot (Join-Path $stage "AscensionBoot.exe") -Force

$www = Join-Path $gmtOut "wwwroot"
if (Test-Path $www) { Copy-Item $www (Join-Path $stage "wwwroot") -Recurse -Force }

$patterns = Join-Path $repo "GmToolbox\Config\offset-patterns.json"
if (Test-Path $patterns) {
    Copy-Item $patterns (Join-Path $stage "Config\offset-patterns.json") -Force
}

$srcAddons = Join-Path $repo "addons"
foreach ($name in $baseAddons) {
    $from = Join-Path $srcAddons $name
    if (-not (Test-Path $from)) { throw "Missing base addon: $from" }
    Copy-Item $from (Join-Path $stage "AddOns\$name") -Recurse -Force
}

@'
{
  "ascensionExe": "",
  "mapsDir": "",
  "mmapsDir": "",
  "autoSyncAddons": true,
  "instanceCount": 1
}
'@ | Set-Content -Encoding UTF8 (Join-Path $stage "Config\settings.json")

Copy-Item (Join-Path $repo "README.md") (Join-Path $stage "README.md") -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $stage "LICENSE") -Force -ErrorAction SilentlyContinue

@'
# GMToolBox portable

1. Unzip this folder anywhere (keep GMToolBox.exe next to ExtProxy64.dll, AscensionBoot.exe, AddOns, and Config).
2. Run GMToolBox.exe.
3. Settings → set the path to your stock Ascension.exe (the folder that also has Extensions.dll and Data\).
4. Click Launch.

Offsets are scanned when you save the path and again on Launch.
Launch copies ExtProxy (unlocked Lua + GmTeleport / GmFace / …) and the base addons into the client:

- GmShared, GmUI, GmTooltipFix
- GmTeleport — /gmteleport /tpface
- GmMapTeleport — right-click / middle-click world map
- GmCmds — slash helpers

Maps / mmtiles are optional (nav height only). Discord is optional.
'@ | Set-Content -Encoding UTF8 (Join-Path $stage "START-HERE.txt")

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force
Write-Host "Wrote $zipPath"
Get-Item $zipPath | Format-Table Name, Length, LastWriteTime -AutoSize
