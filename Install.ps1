$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'dist'
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'WinAirPlay.exe')) { $source = $PSScriptRoot }
$destination = Join-Path $env:LOCALAPPDATA 'Programs/WinAirPlay'
$executable = Join-Path $destination 'WinAirPlay.exe'
if (Get-Process WinAirPlay -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    throw 'Quit WinAirPlay from the tray menu, then run the installer again.'
}
foreach ($file in @('WinAirPlay.exe', 'LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing $file. Extract the complete ZIP before installing." }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($file in @('WinAirPlay.exe', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination -Force
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'WinAirPlay.lnk'))
$shortcut.TargetPath = Join-Path $destination 'WinAirPlay.exe'
$shortcut.WorkingDirectory = $destination
$shortcut.IconLocation = "$destination\WinAirPlay.exe,0"
$shortcut.Description = 'Stream PC audio to AirPlay speakers'
$shortcut.Save()
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -LiteralPath $runKey -Name WinAirPlay -ErrorAction SilentlyContinue) {
    Set-ItemProperty -LiteralPath $runKey -Name WinAirPlay -Value ('"' + $shortcut.TargetPath + '" --startup')
}
Write-Output "Installed WinAirPlay: $destination"
Write-Output 'Search for WinAirPlay in the Windows Start menu to open it.'
