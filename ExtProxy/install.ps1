# Legacy IFEO installer — DO NOT use for portable GMToolBox.
# Portable distribution lives in ..\dist and never writes into Ascension install.
#
# Prefer:  powershell -File ..\build.ps1
# Then run: ..\dist\GMToolBox.exe

$ErrorActionPreference = "Stop"
Write-Host "DEPRECATED: ExtProxy\install.ps1 no longer deploys into the Ascension folder."
Write-Host "Build portable dist with:  powershell -File ..\build.ps1"
Write-Host "Run:  ..\dist\GMToolBox.exe"
Write-Host "Ascension.exe / Maps / MMAPS are configured inside GMToolBox (Paths…)."
exit 1
