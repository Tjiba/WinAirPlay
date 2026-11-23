$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'dist'
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'WinAirPlay.exe')) { $source = $PSScriptRoot }
$destination = Join-Path $env:LOCALAPPDATA 'Programs/WinAirPlay'
$executable = Join-Path $destination 'WinAirPlay.exe'
if (Get-Process -Name 'WinAirPlay*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    throw 'Quit WinAirPlay from the tray menu, then run the installer again.'
}
foreach ($file in @('WinAirPlay.exe', 'LICENSE', 'Uninstall.ps1')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing $file. Extract the complete ZIP before installing." }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($file in @('WinAirPlay.exe', 'LICENSE', 'Uninstall.ps1')) {
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
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinAirPlay'
$version = (Get-Item -LiteralPath $executable).VersionInfo
$properties = @{
    DisplayName = 'WinAirPlay'
    DisplayVersion = '{0}.{1}.{2}' -f $version.ProductMajorPart, $version.ProductMinorPart, $version.ProductBuildPart
    Publisher = 'Timo'
    DisplayIcon = "$executable,0"
    InstallLocation = $destination
    InstallDate = Get-Date -Format 'yyyyMMdd'
    UninstallString = '"' + (Join-Path $PSHOME 'powershell.exe') + '" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Uninstall.ps1') + '"'
    URLInfoAbout = 'https://github.com/Tjiba/WinAirPlay'
}
New-Item -Path $uninstallKey -Force | Out-Null
foreach ($name in $properties.Keys) {
    New-ItemProperty -LiteralPath $uninstallKey -Name $name -Value $properties[$name] -PropertyType String -Force | Out-Null
}
$size = (Get-ChildItem -LiteralPath $destination -File | Measure-Object -Property Length -Sum).Sum
New-ItemProperty -LiteralPath $uninstallKey -Name EstimatedSize -Value ([int][Math]::Ceiling($size / 1KB)) -PropertyType DWord -Force | Out-Null
foreach ($name in @('NoModify', 'NoRepair')) {
    New-ItemProperty -LiteralPath $uninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null
}
Write-Output "Installed WinAirPlay: $destination"
Write-Output 'WinAirPlay is listed in Windows Settings > Apps > Installed apps.'
Write-Output 'Search for WinAirPlay in the Windows Start menu to open it.'
