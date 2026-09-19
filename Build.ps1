param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Checked {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE" }
}

$compiler = Join-Path $PSScriptRoot 'build/toolchain/ziglang/zig.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $PSScriptRoot 'build/toolchain/zig-x86_64-windows-0.14.1/zig.exe'
}
if (!(Test-Path -LiteralPath $compiler)) {
    New-Item -ItemType Directory -Force -Path 'build/toolchain' | Out-Null
    $release = (Invoke-RestMethod 'https://ziglang.org/download/index.json').'0.14.1'.'x86_64-windows'
    $archive = Join-Path $PSScriptRoot 'build/toolchain/zig.zip'
    Invoke-WebRequest $release.tarball -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $release.shasum) { throw 'Zig SHA256 mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath 'build/toolchain' -Force
}
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $PSScriptRoot 'build/zig-cache'
New-Item -ItemType Directory -Force -Path 'native/bin' | Out-Null
Invoke-Checked $compiler @('c++', '-std=c++17', '-O2', '-shared', '-static', '-Wall', '-Wextra', 'native/engine.cpp', '-o', 'native/bin/winairplay_audio.dll', '-lole32', '-luuid', '-lavrt')
Invoke-Checked $compiler @('c++', '-std=c++17', '-O2', '-UNDEBUG', '-static', '-Wall', '-Wextra', 'native/audio_buffer_test.cpp', '-o', 'build/audio_buffer_test.exe')
Invoke-Checked (Join-Path $PSScriptRoot 'build/audio_buffer_test.exe') @()
Invoke-Checked 'dotnet' @('build', 'WinAirPlay.sln', '-c', 'Release')
Invoke-Checked 'dotnet' @('run', '--project', 'tests/WinAirPlay.Tests', '-c', 'Release', '--no-build')
if (!$SkipPublish) {
    Invoke-Checked 'dotnet' @('publish', 'src/winairplay/WinAirPlay.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', 'dist/WinAirPlay2')
    Copy-Item -LiteralPath 'dist/WinAirPlay2/WinAirPlay.exe' -Destination 'dist/WinAirPlay.exe' -Force
    Copy-Item -LiteralPath 'native/bin/winairplay_audio.dll' -Destination 'dist/winairplay_audio.dll' -Force
    Copy-Item -LiteralPath 'LICENSE' -Destination 'dist/LICENSE' -Force
    Compress-Archive -Path 'dist/WinAirPlay.exe', 'dist/winairplay_audio.dll', 'dist/LICENSE' -DestinationPath 'dist/WinAirPlay2-win-x64.zip' -Force
}
