# Legacy uninstall — portable GMToolBox does not install into Ascension.
# Optional: remove leftover Ascension.launch.exe / ExtProxy64.dll from an old deploy.
$ErrorActionPreference = "Stop"
$live = "C:\Ascension\Launcher\resources\ascension-live"
$ifeoKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\Ascension.exe"

if (Test-Path $ifeoKey) {
    Remove-ItemProperty -Path $ifeoKey -Name Debugger -ErrorAction SilentlyContinue
    Write-Host "Removed IFEO debugger for Ascension.exe (if present)"
}

if (Test-Path $live) {
    foreach ($name in @(
        "AscensionBoot.exe", "AscensionBoot.exe.new",
        "ExtProxy64.dll", "ExtProxy64.dll.new",
        "Ascension.launch.exe", "ExtProxy64.log", "ExtProxy64.pid"
    )) {
        $p = Join-Path $live $name
        if (Test-Path $p) {
            try {
                Remove-Item -Force $p
                Write-Host "Removed leftover $name from live (legacy)"
            } catch {
                Write-Host "Could not remove $name (locked?): $($_.Exception.Message)"
            }
        }
    }
}

Write-Host "Stock Extensions.dll / Ascension.exe were never replaced by portable GMToolBox."
Write-Host "Use dist\GMToolBox.exe going forward."
