$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$testId = 'WinAirPlay.SetupTest'
$testRoot = [IO.Path]::GetFullPath((Join-Path $PWD 'build/setup-test'))
$appDirectory = Join-Path $testRoot 'app'
$legacyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $testId
$installedKey = $legacyKey + '_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($testId + '.lnk')
$compiler = Join-Path $PWD 'build/toolchain/inno/tools/ISCC.exe'
& $compiler '/DAppVersion=2.1.1' "/DAppId=$testId" "/O$testRoot" 'installer/WinAirPlay.iss'
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed' }
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDirectory 'Uninstall.ps1') -Value '# Legacy installer fixture'
New-Item -Path $legacyKey -Force | Out-Null
New-ItemProperty -LiteralPath $legacyKey -Name DisplayName -Value 'WinAirPlay installer test (legacy)' -Force | Out-Null
New-ItemProperty -LiteralPath $runKey -Name $testId -Value 'legacy startup' -Force | Out-Null
$preservedFile = Join-Path $appDirectory 'preserved-settings.json'
Set-Content -LiteralPath $preservedFile -Value 'preserved'
foreach ($attempt in 1..2) {
    $process = Start-Process -FilePath (Join-Path $testRoot 'WinAirPlay-Setup.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=french', ('/DIR="' + $appDirectory + '"'), ('/LOG="' + $testRoot + '/install-' + $attempt + '.log"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installation failed: $($process.ExitCode)" }
    $entry = Get-ItemProperty -LiteralPath $installedKey
    if ($entry.DisplayVersion -ne '2.1.1' -or !(Test-Path -LiteralPath $shortcut)) { throw 'Registration or shortcut missing' }
    if (Test-Path -LiteralPath $legacyKey) { throw 'Duplicate legacy entry remains' }
    if (Test-Path -LiteralPath (Join-Path $appDirectory 'Uninstall.ps1')) { throw 'Legacy uninstaller remains' }
    if ((Get-Content -LiteralPath $preservedFile) -ne 'preserved') { throw 'Settings changed' }
    if ((Get-ItemPropertyValue -LiteralPath $runKey -Name $testId) -ne ('"' + $appDirectory + '\WinAirPlay.exe" --startup')) { throw 'Startup preference changed' }
    Write-Output "PASS installation/upgrade ${attempt}: version, registration, legacy migration, shortcut, startup and preserved files"
}
$process = Start-Process -FilePath (Join-Path $appDirectory 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Uninstall failed: $($process.ExitCode)" }
if ((Test-Path -LiteralPath $installedKey) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath (Join-Path $appDirectory 'WinAirPlay.exe'))) { throw 'Uninstall left app, registration or shortcut' }
if (Get-ItemProperty -LiteralPath $runKey -Name $testId -ErrorAction SilentlyContinue) { throw 'Uninstall left startup entry' }
if ((Get-Content -LiteralPath $preservedFile) -ne 'preserved') { throw 'Uninstall removed untracked user data' }
Write-Output 'PASS native uninstall removes app, registration, shortcut and startup; preserves untracked user data'
