$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'dist'
$destination = Join-Path $env:LOCALAPPDATA 'Programs/WinAirPlay'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($file in @('WinAirPlay.exe', 'winairplay_audio.dll', 'LICENSE')) {
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
