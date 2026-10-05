param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$ExpectedVersion = '0.2.0-preview.8'
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
if ($files.Count -ne $manifest.Count -or @($files | Where-Object { -not $known.Contains($_.FullName) }).Count -gt 0) { throw 'Manifest does not describe every package file' }
if ($files | Where-Object { $_.Name -match '^(connection|sources|local-music|local-libraries|local-playlists|appearance)\.json(\.lock)?$|verification\.json$|\.pdb$|\.bin$|^ffmpeg\.exe$' }) { throw 'Development or personal artifact found' }
foreach ($required in @('FnMusic.App.exe', 'FnMusic.App.dll', 'FnMusic.App.pri', 'App.xbf', 'MainWindow.xbf', 'MonochromeResources.xbf',
    'FFmpegInteropX.dll', 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'Start-FnMusic.cmd',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'PREVIEW_VALIDATION.md', 'NATIVE_DECODER_DEPENDENCIES.md', 'native-decoder-build.json',
    'licenses/FFmpegInteropX-Apache-2.0.txt', 'licenses/FFmpegInteropX-build/mp3-preroll.patch',
    'licenses/FFmpegInteropX-build/toolchain-lock.json', 'licenses/FFmpegInteropX-build/build-native-decoder.py')) {
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
[pscustomobject]@{ Version = $version; VerifiedFiles = $files.Count; TotalFiles = $files.Count + 1; DecoderSha256 = $decoder.decoderSha256 }
