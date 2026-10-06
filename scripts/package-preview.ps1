param([string]$Name = ('FnMusic-0.2.0-preview.10-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9._-]+$' -or $Name -in @('.', '..')) { throw 'Invalid package name' }
$repo = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $repo ('dist/' + $Name)
if (Test-Path -LiteralPath $destination) { throw 'Output already exists; choose a new name' }
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.tools/nuget'
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
& $sdk restore (Join-Path $repo 'native/FnMusic.sln') --configfile (Join-Path $repo 'native/NuGet.Config') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
. (Join-Path $PSScriptRoot 'ensure-native-decoder.ps1')
Ensure-FnMusicNativeDecoder $repo
& $sdk publish (Join-Path $repo 'native/src/FnMusic.App/FnMusic.App.csproj') -c Release --no-restore --nologo -p:DebugSymbols=false -p:DebugType=None -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in @('FnMusic.App.exe', 'FnMusic.App.pri', 'App.xbf', 'MainWindow.xbf', 'MonochromeResources.xbf', 'FFmpegInteropX.dll', 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Missing preview file: $required" }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD_PARTY_NOTICES.md'), (Join-Path $repo 'docs/PREVIEW_VALIDATION.md') -Destination $destination
$decoderLicenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Path $decoderLicenses | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'licenses/FFmpegInteropX-Apache-2.0.txt') -Destination $decoderLicenses
$decoderBuildMaterials = Join-Path $decoderLicenses 'FFmpegInteropX-build'
New-Item -ItemType Directory -Path $decoderBuildMaterials | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'native/decoder/mp3-preroll.patch'), (Join-Path $repo 'native/decoder/toolchain-lock.json'), (Join-Path $repo 'scripts/build-native-decoder.py') -Destination $decoderBuildMaterials
$ffmpegPackage = Join-Path $env:NUGET_PACKAGES 'ffmpeginteropx.desktop.ffmpeg/8.1.2'
foreach ($licenseFile in (Get-ChildItem -LiteralPath (Join-Path $ffmpegPackage 'licenses') -File)) {
    Copy-Item -LiteralPath $licenseFile.FullName -Destination $decoderLicenses
}
Copy-Item -LiteralPath (Join-Path $repo 'docs/NATIVE_DECODER_DEPENDENCIES.md') -Destination $destination
@'
@echo off
cd /d "%~dp0"
start "" "%~dp0FnMusic.App.exe"
'@ | Set-Content -LiteralPath (Join-Path $destination 'Start-FnMusic.cmd') -Encoding ascii
$files = Get-ChildItem -LiteralPath $destination -Recurse -File
if ($files | Where-Object { $_.Name -match '^(connection|sources|local-music|local-libraries|local-playlists|appearance)\.json(\.lock)?$|verification\.json$|\.pdb$|\.bin$|^ffmpeg\.exe$' }) { throw 'Unexpected development or personal artifact in preview' }
$manifest = $files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($destination, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
ConvertTo-Json -InputObject @($manifest) -Depth 3 | Set-Content -LiteralPath (Join-Path $destination 'SHA256.json') -Encoding utf8
Write-Output $destination
