param(
    # When Launch/Deploy runs from inside GmToolbox.exe, skip rebuilding the
    # toolbox itself (the running EXE/DLL would be locked).
    [switch]$SkipToolbox
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$clangCandidates = @(
    (Join-Path $root "llvm-mingw-i686\llvm-mingw-20260616-ucrt-i686\bin\i686-w64-mingw32-clang.exe"),
    "C:\Users\Dean\gg\AscensionMoveHook\llvm-mingw-i686\llvm-mingw-20260616-ucrt-i686\bin\i686-w64-mingw32-clang.exe"
)
# Auto-discover a WinGet-installed llvm-mingw (x86_64 host ships an i686 target).
$wingetBase = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
if (Test-Path $wingetBase) {
    $wingetClang = Get-ChildItem -Path $wingetBase -Filter "i686-w64-mingw32-clang.exe" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($wingetClang) { $clangCandidates += $wingetClang }
}
$onPath = Get-Command "i686-w64-mingw32-clang.exe" -ErrorAction SilentlyContinue
if ($onPath) { $clangCandidates += $onPath.Source }
$clang = $clangCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $clang) {
    throw "Missing i686 clang. Install llvm-mingw or place it under ExtProxy\llvm-mingw-i686\"
}

$proxyOut = Join-Path $root "ExtProxy64.dll"
$def = Join-Path $root "Extensions.def"
& $clang -shared -O2 -std=c11 -Wall -Wextra `
    -o $proxyOut `
    (Join-Path $root "ProxyMain.c") `
    (Join-Path $root "PktIpc.c") `
    (Join-Path $root "EntitlementGate.c") `
    (Join-Path $root "ObjectMgr.c") `
    (Join-Path $root "FogExplore.c") `
    (Join-Path $root "NavHeight.c") `
    (Join-Path $root "NavPath.c") `
    (Join-Path $root "OverlayD3d9.c") `
    (Join-Path $root "TeleportMirror.c") `
    (Join-Path $root "InstanceBus.c") `
    (Join-Path $root "ChatReport.c") `
    (Join-Path $root "OffsetResolve.c") `
    $def `
    -lkernel32 -luser32 -lgdi32 -lws2_32
if ($LASTEXITCODE -ne 0) { throw "ExtProxy64.dll build failed" }

$bootProj = Join-Path (Split-Path -Parent $root) "AscensionBoot\build.ps1"
if (-not (Test-Path $bootProj)) { throw "Missing AscensionBoot project: $bootProj" }
& $bootProj -Clang $clang
if ($LASTEXITCODE -ne 0) { throw "AscensionBoot build failed" }
$bootOut = Join-Path $root "AscensionBoot.exe"
if (-not (Test-Path $bootOut)) {
    $bootOut = Join-Path (Split-Path -Parent $root) "AscensionBoot\AscensionBoot.exe"
}

Write-Host "Built:"
Get-Item $proxyOut, $bootOut | Format-Table Name, Length, LastWriteTime -AutoSize

# Always stage into portable dist + toolbox output so Launch never keeps a stale DLL.
$stageRoots = @(
    (Join-Path $root "..\dist"),
    (Join-Path $root "..\dist_build"),
    (Join-Path $root "..\GmToolbox\bin\Release\net8.0-windows"),
    (Join-Path $root "..\GmToolbox\bin\Release\net10.0-windows")
)
foreach ($stage in $stageRoots) {
    if (-not (Test-Path $stage)) { continue }
    Copy-Item -Force $proxyOut (Join-Path $stage "ExtProxy64.dll")
    Copy-Item -Force $bootOut (Join-Path $stage "AscensionBoot.exe")
    $instRoot = Join-Path $stage "Runtime"
    if (Test-Path $instRoot) {
        Get-ChildItem $instRoot -Directory -Filter "inst*" -ErrorAction SilentlyContinue | ForEach-Object {
            $dllDst = Join-Path $_.FullName "ExtProxy64.dll"
            $bootDst = Join-Path $_.FullName "AscensionBoot.exe"
            try {
                Copy-Item -Force $proxyOut $dllDst -ErrorAction Stop
            } catch {
                $newPath = "$dllDst.new"
                Copy-Item -Force $proxyOut $newPath
                Write-Warning "Locked $dllDst - staged $newPath (close client / re-Launch to apply)"
            }
            try {
                Copy-Item -Force $bootOut $bootDst -ErrorAction Stop
            } catch {
                Copy-Item -Force $bootOut "$bootDst.new" -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Host "Staged -> $stage"
}

$tool = Join-Path $root "..\GmToolbox\GmToolbox.csproj"
if (-not $SkipToolbox -and (Test-Path $tool)) {
    Write-Host "Building GmToolbox..."
    dotnet build $tool -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "GmToolbox build failed - proxy artifacts are still built."
    }
    # Re-stage after toolbox build (bin may have been cleaned/relinked).
    $binOut = Join-Path $root "..\GmToolbox\bin\Release\net8.0-windows"
    if (Test-Path $binOut) {
        Copy-Item -Force $proxyOut (Join-Path $binOut "ExtProxy64.dll")
        Copy-Item -Force $bootOut (Join-Path $binOut "AscensionBoot.exe")
    }
} elseif ($SkipToolbox) {
    Write-Host "SkipToolbox: ExtProxy artifacts only (GmToolbox not rebuilt)."
}
