param([string]$Name = ('FnMusic-0.2.0-rc.1-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9._-]+$' -or $Name -in @('.', '..')) { throw 'Invalid package name' }
$repo = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $repo ('dist/' + $Name)
$archivePath = $destination + '.zip'
$checksumPath = $destination + '.SHA256SUMS.txt'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) { throw 'Output already exists; choose a new name' }
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.tools/nuget'
$sdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
& $sdk restore (Join-Path $repo 'native/FnMusic.sln') --configfile (Join-Path $repo 'native/NuGet.Config') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
. (Join-Path $PSScriptRoot 'ensure-native-decoder.ps1')
Ensure-FnMusicNativeDecoder $repo
& $sdk publish (Join-Path $repo 'native/src/FnMusic.App/FnMusic.App.csproj') -c Release --no-restore --nologo -p:DebugSymbols=false -p:DebugType=None -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in @('FnMusic.App.exe', 'FnMusic.App.pri', 'App.xbf', 'MainWindow.xbf', 'MonochromeResources.xbf', 'FFmpegInteropX.dll', 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'Assets/FnMusic.ico', 'Assets/FnMusic.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Missing preview file: $required" }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD_PARTY_NOTICES.md'), (Join-Path $repo 'docs/USER_GUIDE.md'), (Join-Path $repo 'docs/RC1_USER_VALIDATION.md'), (Join-Path $repo 'docs/releases/0.2.0-rc.1.md'), (Join-Path $repo 'docs/BUILDING_SOURCES.md') -Destination $destination
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
& python (Join-Path $repo 'scripts/prepare-distribution.py') (Join-Path $decoderLicenses 'corresponding-source')
if ($LASTEXITCODE -ne 0) { throw 'Corresponding source preparation failed' }
Copy-Item -LiteralPath (Join-Path $repo 'native/src/FnMusic.App/packages.lock.json') -Destination (Join-Path $decoderLicenses 'packages.lock.json')
$revision = & git -c ('safe.directory=' + $repo.Replace('\', '/')) -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source revision unavailable' }
@{ version = '0.2.0-rc.1'; sourceCommit = $revision.Trim(); architecture = 'win-x64' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'release.json') -Encoding utf8
@'
@echo off
cd /d "%~dp0"
start "" "%~dp0FnMusic.App.exe"
'@ | Set-Content -LiteralPath (Join-Path $destination 'Start-FnMusic.cmd') -Encoding ascii
$files = Get-ChildItem -LiteralPath $destination -Recurse -File
if ($files | Where-Object { [IO.Path]::GetRelativePath($destination, $_.FullName) -match '^(local-index|nas-metadata)[\\/]' }) { throw 'Personal music index found in preview' }
if ($files | Where-Object { $_.Name -match '^(connection|sources|local-music|local-libraries|local-playlists|appearance)\.json(\.lock)?$|verification\.json$|\.pdb$|\.bin$|^ffmpeg\.exe$' }) { throw 'Unexpected development or personal artifact in preview' }
$manifest = $files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($destination, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
ConvertTo-Json -InputObject @($manifest) -Depth 3 | Set-Content -LiteralPath (Join-Path $destination 'SHA256.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'check-preview.ps1') -Path $destination -ExpectedVersion '0.2.0-rc.1'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $true)
$archiveDigest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
($archiveDigest + '  ' + [IO.Path]::GetFileName($archivePath)) | Set-Content -LiteralPath $checksumPath -Encoding ascii
Write-Output $destination
Write-Output $archivePath
Write-Output $checksumPath
