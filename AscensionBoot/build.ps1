param(
    [string]$Clang = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root

$clangCandidates = @()
if ($Clang) { $clangCandidates += $Clang }
$clangCandidates += @(
    (Join-Path $repo "ExtProxy\llvm-mingw-i686\llvm-mingw-20260616-ucrt-i686\bin\i686-w64-mingw32-clang.exe"),
    "C:\Users\Dean\gg\AscensionMoveHook\llvm-mingw-i686\llvm-mingw-20260616-ucrt-i686\bin\i686-w64-mingw32-clang.exe"
)
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
    throw "Missing i686 clang. Install llvm-mingw i686 or pass -Clang."
}

$bootOut = Join-Path $root "AscensionBoot.exe"
& $clang -O2 -std=c11 -Wall -Wextra -municode `
    -o $bootOut `
    (Join-Path $root "AscensionBoot.c") `
    -lkernel32 -luser32 -lshell32
if ($LASTEXITCODE -ne 0) { throw "AscensionBoot build failed" }

Write-Host "Built $bootOut"
Get-Item $bootOut | Format-Table Name, Length, LastWriteTime -AutoSize

$proxyDir = Join-Path $repo "ExtProxy"
if (Test-Path $proxyDir) {
    Copy-Item -Force $bootOut (Join-Path $proxyDir "AscensionBoot.exe")
    Write-Host "Copied beside ExtProxy for GMT launch."
}
