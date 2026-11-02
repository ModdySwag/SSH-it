# Builds the release artifacts: publish, installer, hashes. It does NOT install, sign or publish.
# Run from the repository root. Bump the version in src/SshIt/SshIt.csproj and installer/setup.iss first.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

Get-Process SSH-it -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

dotnet publish (Join-Path $root 'src/SshIt/SshIt.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
    -o (Join-Path $root 'src/SshIt/publish')
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

& $iscc (Join-Path $root 'installer/setup.iss')
if ($LASTEXITCODE -ne 0) { throw "installer failed ($LASTEXITCODE)" }

$setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'SSH-it-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
$exe = Join-Path $root 'src/SshIt/publish/SSH-it.exe'
'setup   ' + $setup.Name + '  ' + $setup.Length + ' bytes'
'sha256  ' + (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
'app exe ' + (Get-FileHash $exe -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()
'version ' + (Get-Item $exe).VersionInfo.FileVersion
