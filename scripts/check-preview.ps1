param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$ExpectedVersion = '0.2.0-rc.2'
)
$ErrorActionPreference = 'Stop'
$previewRoot = (Get-Item -LiteralPath $Path).FullName
$previewPrefix = $previewRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $previewRoot 'SHA256.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 16MB) { throw 'Manifest exceeds verification bound' }
$manifest = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
if ($manifest.Count -lt 1 -or $manifest.Count -gt 10000) { throw 'Invalid manifest count' }
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
        $relative.Contains(':') -or $relative -match '[\x00-\x1f]' -or
        @($relative -split '[/\\]' | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
        $entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid manifest entry' }
    $resolved = [IO.Path]::GetFullPath((Join-Path $previewRoot $relative))
    if (-not $resolved.StartsWith($previewPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $known.Add($resolved)) { throw 'Duplicate or escaped manifest entry' }
    $item = Get-Item -LiteralPath $resolved
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid package file' }
    if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Hash mismatch: $relative" }
}
$contents = @(Get-ChildItem -LiteralPath $previewRoot -Recurse -Force)
if (($contents | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) -or
    ((Get-Item -LiteralPath $previewRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package contains a link' }
$files = @($contents | Where-Object { -not $_.PSIsContainer -and $_.FullName -ne $manifestPath })
if ($files | Where-Object { [IO.Path]::GetRelativePath($previewRoot, $_.FullName) -match '^(local-index|nas-metadata|playback-information)[\\/]' }) { throw 'Personal music index found in preview' }
if ($files.Count -ne $manifest.Count -or @($files | Where-Object { -not $known.Contains($_.FullName) }).Count -gt 0) { throw 'Manifest does not describe every package file' }
if ($files | Where-Object { $_.Name -match '^(connection|sources|local-music|local-libraries|local-playlists|navidrome-connections|appearance|desktop-integration)\.json(\.lock)?$|verification\.json$|\.pdb$|\.bin$|^ffmpeg\.exe$' }) { throw 'Development or personal artifact found' }
$requiredFiles = @('FnMusic.App.exe', 'FnMusic.App.dll', 'FnMusic.App.pri', 'App.xbf', 'MainWindow.xbf', 'MonochromeResources.xbf',
    'FFmpegInteropX.dll', 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'Start-FnMusic.cmd',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'NATIVE_DECODER_DEPENDENCIES.md', 'native-decoder-build.json',
    'licenses/FFmpegInteropX-Apache-2.0.txt', 'licenses/FFmpegInteropX-build/mp3-preroll.patch',
    'licenses/FFmpegInteropX-build/toolchain-lock.json', 'licenses/FFmpegInteropX-build/build-native-decoder.py')
if ($ExpectedVersion -in @('0.2.0-rc.1','0.2.0-rc.2')) {
    $requiredFiles += @('USER_GUIDE.md', (('RC' + $(if ($ExpectedVersion -eq '0.2.0-rc.2') { '2' } else { '1' }) + '_USER_VALIDATION.md')), ($ExpectedVersion + '.md'), 'BUILDING_SOURCES.md', 'release.json',
        'licenses/corresponding-source/decoder-corresponding-source.zip', 'licenses/corresponding-source/decoder-distribution.json',
        'licenses/corresponding-source/runtime-license-index.json', 'licenses/packages.lock.json')
} else { $requiredFiles += 'PREVIEW_VALIDATION.md' }
if ($ExpectedVersion -eq '0.2.0-rc.2') {
    $requiredFiles += @('DESKTOP_INTEGRATION.md', 'PLAYBACK_INFORMATION_PROTOCOL.md', 'examples/InfoClient/FnMusic.InfoClient.exe', 'examples/InfoClient/README.md')
}
foreach ($required in $requiredFiles) {
    if (-not $known.Contains([IO.Path]::GetFullPath((Join-Path $previewRoot $required)))) { throw "Required file missing: $required" }
}
$version = (Get-Item -LiteralPath (Join-Path $previewRoot 'FnMusic.App.exe')).VersionInfo.ProductVersion
if ($version -ne $ExpectedVersion -and -not $version.StartsWith($ExpectedVersion + '+', [StringComparison]::Ordinal)) { throw 'Unexpected application version' }
$decoder = Get-Content -LiteralPath (Join-Path $previewRoot 'native-decoder-build.json') -Raw | ConvertFrom-Json
if ($decoder.sourceCommit -ne '841fc8e79346a235906f1dbd45be53034b2e2f52' -or
    $decoder.version -ne '2.1.0.1-fnmusic-mp3-preroll') { throw 'Unexpected native decoder provenance' }
foreach ($binding in @(
    @('FFmpegInteropX.dll', $decoder.decoderSha256),
    @('licenses/FFmpegInteropX-build/mp3-preroll.patch', $decoder.patchSha256),
    @('licenses/FFmpegInteropX-build/toolchain-lock.json', $decoder.lockSha256),
    @('licenses/FFmpegInteropX-build/build-native-decoder.py', $decoder.scriptSha256))) {
    if ($binding[1] -notmatch '^[0-9A-Fa-f]{64}$' -or
        (Get-FileHash -LiteralPath (Join-Path $previewRoot $binding[0]) -Algorithm SHA256).Hash -ne $binding[1]) { throw 'Decoder material does not match provenance' }
}
if ($ExpectedVersion -in @('0.2.0-rc.1','0.2.0-rc.2')) {
    $distribution = Get-Content -LiteralPath (Join-Path $previewRoot 'licenses/corresponding-source/decoder-distribution.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath (Join-Path $previewRoot 'licenses/corresponding-source/decoder-corresponding-source.zip') -Algorithm SHA256).Hash -ne $distribution.sourceArchiveSha256) { throw 'Corresponding source archive mismatch' }
    foreach ($binary in $distribution.binarySha256.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $previewRoot $binary.Name) -Algorithm SHA256).Hash -ne $binary.Value) { throw 'FFmpeg binary provenance mismatch' }
    }
    $release = Get-Content -LiteralPath (Join-Path $previewRoot 'release.json') -Raw | ConvertFrom-Json
    if ($release.version -ne $ExpectedVersion -or $release.sourceCommit -notmatch '^[a-f0-9]{40}$' -or $release.architecture -ne 'win-x64') { throw 'Release identity mismatch' }
}
[pscustomobject]@{ Version = $version; VerifiedFiles = $files.Count; TotalFiles = $files.Count + 1; DecoderSha256 = $decoder.decoderSha256 }
