param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$compiler = Join-Path $PSScriptRoot 'build/toolchain/inno/tools/ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $archive = Join-Path $PSScriptRoot 'build/toolchain/inno.zip'
    New-Item -ItemType Directory -Force -Path (Split-Path $archive) | Out-Null
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4') { throw 'Inno Setup SHA256 mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath 'build/toolchain/inno' -Force
}
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Inno Setup signature verification failed' }
$project = [xml](Get-Content -LiteralPath 'src/winairplay/WinAirPlay.csproj')
$version = $project.Project.PropertyGroup.Version
& $compiler "/DAppVersion=$version" "/O$([IO.Path]::GetFullPath($OutputDirectory))" 'installer/WinAirPlay.iss'
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
