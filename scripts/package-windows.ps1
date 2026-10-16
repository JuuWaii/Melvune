param(
    [string]$Payload = 'dist/FnMusic-0.2.0-rc.2-win-x64',
    [string]$Name,
    [switch]$PortableOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$payloadPath = if ([IO.Path]::IsPathRooted($Payload)) { [IO.Path]::GetFullPath($Payload) } else { [IO.Path]::GetFullPath((Join-Path $root $Payload)) }
$release = Get-Content -LiteralPath (Join-Path $payloadPath 'release.json') -Raw | ConvertFrom-Json
$version = [string]$release.version
if ($version -notin @('0.2.0-rc.1', '0.2.0-rc.2', '0.2.0-rc.3','0.2.0-rc.4', '0.2.0-rc.5', '0.2.0-rc.6', '0.2.0-rc.7')) { throw 'Unsupported payload version.' }
if (-not $PortableOnly -and $version -ne '0.2.0-rc.2') { throw 'The full installer is pinned to rc.2. Use -PortableOnly for this payload version.' }
if ([string]::IsNullOrEmpty($Name)) { $Name = 'FnMusic-' + $version + '-win-x64-' + $(if ($PortableOnly) { 'portable-' } else { 'windows-' }) + (Get-Date -Format 'yyyyMMdd-HHmmss') }
if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+$') { throw 'Unsafe output name.' }
$archivePath = $payloadPath + '.zip'
$output = Join-Path $root "dist/$Name"
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new name.' }
& (Join-Path $PSScriptRoot 'check-preview.ps1') -Path $payloadPath -ExpectedVersion $version
if (-not $?) { throw 'Payload verification failed.' }
if (-not (Test-Path -LiteralPath $archivePath)) { throw 'Matching portable ZIP is required.' }
$zipHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$sidecar = $payloadPath + '.SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $sidecar) -or (Get-Content -LiteralPath $sidecar -Raw).Trim() -cne ($zipHash + '  ' + [IO.Path]::GetFileName($archivePath))) { throw 'ZIP checksum mismatch.' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'Run scripts/native.ps1 bootstrap first.' }
if (-not $PortableOnly) {
    $wix = Join-Path $root '.tools/wix/wix.exe'
    if (-not (Test-Path -LiteralPath $wix)) {
        & $sdk tool install wix --version 4.0.6 --tool-path (Join-Path $root '.tools/wix') --configfile (Join-Path $root 'native/NuGet.Config')
        if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
    }
    $wixVersion = & $wix --version
    if ($LASTEXITCODE -ne 0 -or $wixVersion -notmatch '^4\.0\.6(?:\+|$)') { throw 'WiX 4.0.6 is required.' }
    & $wix extension add -g WixToolset.Bal.wixext/4.0.6
    if ($LASTEXITCODE -ne 0) { throw 'WiX extension preparation failed.' }
    & $wix extension add -g WixToolset.UI.wixext/4.0.6
    if ($LASTEXITCODE -ne 0) { throw 'WiX UI extension preparation failed.' }
    }
    $work = Join-Path $root ".tools/installers/$Name"
    New-Item -ItemType Directory -Force -Path $work,$output | Out-Null
    $project = Join-Path $root 'packaging/portable/FnMusic.Portable.csproj'
    & $sdk restore $project --configfile (Join-Path $root 'native/NuGet.Config') --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Portable launcher restore failed.' }
    & $sdk publish $project -c Release --no-restore --nologo ('-p:Version=' + $version) -o (Join-Path $work 'launcher')
    if ($LASTEXITCODE -ne 0) { throw 'Portable launcher build failed.' }
    $portable = Join-Path $output ('FnMusic-' + $version + '-win-x64-portable.exe')
    $target = [IO.File]::Create($portable)
    try {
        foreach ($file in @((Join-Path $work 'launcher/FnMusic.Portable.exe'), $archivePath)) {
            $inputStream = [IO.File]::OpenRead($file)
            try { $inputStream.CopyTo($target) } finally { $inputStream.Dispose() }
        }
        $writer = [IO.BinaryWriter]::new($target)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('FNMUSIC-PAYLOAD-1'))
        $bytes = New-Object byte[] 32
        for ($i = 0; $i -lt 32; $i++) { $bytes[$i] = [Convert]::ToByte($zipHash.Substring($i * 2, 2), 16) }
        $writer.Write($bytes)
        $writer.Write([long](Get-Item -LiteralPath $archivePath).Length)
        $writer.Flush()
    } finally { $target.Dispose() }
if (-not $PortableOnly) {
    $source = Join-Path $work 'package.wxs'
    & python (Join-Path $PSScriptRoot 'create-installer-source.py') $payloadPath $source
    if ($LASTEXITCODE -ne 0) { throw 'Installer authoring failed.' }
    $msi = Join-Path $output 'FnMusic-0.2.0-rc.2-win-x64-setup.msi'
    & $wix build $source -arch x64 -ext WixToolset.UI.wixext/4.0.6 -culture zh-CN -o $msi -pdbtype none
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    $setup = Join-Path $output 'FnMusic-0.2.0-rc.2-win-x64-setup.exe'
    & $wix build (Join-Path $root 'packaging/bundle.wxs') -arch x64 -ext WixToolset.Bal.wixext/4.0.6 -d "MsiPath=$msi" -d "BrandingDir=$(Join-Path $root 'assets/branding')" -o $setup -pdbtype none
    if ($LASTEXITCODE -ne 0) { throw 'EXE installer build failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'packaging/WIX_NOTICE.md'),(Join-Path $root 'packaging/WIX_LICENSE.txt') -Destination $output
    }
    $packagingSource = & git -c "safe.directory=$($root.Replace('\','/'))" -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $packagingSource -notmatch '^[a-f0-9]{40}$') { throw 'Packaging source revision unavailable.' }
    $metadata = [ordered]@{ version=$version; architecture='win-x64'; packageKind=$(if ($PortableOnly) { 'portable-only' } else { 'windows-full' }); payloadSource=$release.sourceCommit; packagingSource=$packagingSource.Trim(); payloadZipSha256=$zipHash; signed=$false; artifacts=@([IO.Path]::GetFileName($portable)) }
    if (-not $PortableOnly) {
        $metadata.msiVersion = '0.1.1002'
        $metadata.wixVersion = '4.0.6'
        $metadata.artifacts += @([IO.Path]::GetFileName($msi), [IO.Path]::GetFileName($setup))
    }
    $metadata |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding UTF8
    Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } |
        Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
    if ($PortableOnly) {
        & python (Join-Path $PSScriptRoot 'verify-portable-package.py') --packages $output --payload $payloadPath --report (Join-Path $work 'portable-verification.json')
        if ($LASTEXITCODE -ne 0) { throw 'Portable package verification failed.' }
    }
    Write-Output "Windows packages: $output"
