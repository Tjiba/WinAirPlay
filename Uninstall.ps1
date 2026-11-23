$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/WinAirPlay'))
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne $destination) {
    throw 'Run the installed uninstaller from LOCALAPPDATA/Programs/WinAirPlay.'
}
$executable = Join-Path $destination 'WinAirPlay.exe'
if (Get-Process -Name 'WinAirPlay*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show('Quit WinAirPlay from the tray menu, then uninstall again.', 'WinAirPlay') | Out-Null
    exit 1
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -LiteralPath $runKey -Name WinAirPlay -ErrorAction SilentlyContinue
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'WinAirPlay.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
Remove-Item -LiteralPath $destination -Recurse -Force
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinAirPlay'
if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
Write-Output 'WinAirPlay uninstalled. Saved settings and logs were kept.'
