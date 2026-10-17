param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Name
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'build-environment.ps1')
Initialize-MelvuneGitEnvironment $root
if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+$') { throw 'Unsafe output name' }
$payloadPath = if ([IO.Path]::IsPathRooted($Payload)) { [IO.Path]::GetFullPath($Payload) } else { [IO.Path]::GetFullPath((Join-Path $root $Payload)) }
$release = Get-Content -LiteralPath (Join-Path $payloadPath 'release.json') -Raw | ConvertFrom-Json
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $release.sourceCommit -ne $revision -or $release.product -ne 'Melvune' -or $release.version -ne '0.2.0-rc.9') { throw 'Installer source must match the current verified payload' }
$output = Join-Path $root ('dist/' + $Name)
if (Test-Path -LiteralPath $output) { throw 'Output already exists' }
& (Join-Path $PSScriptRoot 'check-preview.ps1') -Path $payloadPath -ExpectedVersion $release.version
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
$env:DOTNET_ROOT = Join-Path $root '.tools/dotnet'
$env:DOTNET_ROLL_FORWARD = 'Major'
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
$wix = Join-Path $root '.tools/wix/wix.exe'
if (-not (Test-Path -LiteralPath $wix)) {
    & $sdk tool install wix --version 4.0.6 --tool-path (Join-Path $root '.tools/wix') --configfile (Join-Path $root 'native/NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'WiX tool preparation failed' }
}
$wixVersion = & $wix --version
if ($LASTEXITCODE -ne 0 -or $wixVersion -notmatch '^4\.0\.6(?:\+|$)') { throw 'Pinned WiX 4.0.6 required' }
foreach ($extension in @('WixToolset.Bal.wixext/4.0.6','WixToolset.UI.wixext/4.0.6')) {
    & $wix extension add -g $extension
    if ($LASTEXITCODE -ne 0) { throw 'WiX extension preparation failed' }
}
$work = Join-Path $root ('.tools/installers/' + $Name)
New-Item -ItemType Directory -Path $work,$output -Force | Out-Null
$source = Join-Path $work 'package.wxs'
& python (Join-Path $PSScriptRoot 'create-installer-source.py') $payloadPath $source
if ($LASTEXITCODE -ne 0) { throw 'Installer authoring failed' }
$msiName = 'Melvune-0.2.0-rc.9-win-x64-setup.msi'
$exeName = 'Melvune-0.2.0-rc.9-win-x64-setup.exe'
$msi = Join-Path $output $msiName
$setup = Join-Path $output $exeName
& $wix build $source -arch x64 -ext WixToolset.UI.wixext/4.0.6 -culture zh-CN -o $msi -pdbtype none
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
& $wix build (Join-Path $root 'packaging/bundle.wxs') -arch x64 -ext WixToolset.Bal.wixext/4.0.6 -d "MsiPath=$msi" -d "BrandingDir=$(Join-Path $root 'assets/branding')" -o $setup -pdbtype none
if ($LASTEXITCODE -ne 0) { throw 'Setup EXE build failed' }
Copy-Item -LiteralPath (Join-Path $root 'packaging/WIX_NOTICE.md'),(Join-Path $root 'packaging/WIX_LICENSE.txt') -Destination $output
$zipDigest = (Get-FileHash -LiteralPath ($payloadPath + '.zip') -Algorithm SHA256).Hash
[ordered]@{product='Melvune';version=$release.version;architecture='win-x64';packageKind='installers';msiVersion='0.2.1009';wixVersion='4.0.6';signed=$false;payloadSource=$revision;packagingSource=$revision;payloadZipSha256=$zipDigest;artifacts=@($exeName,$msiName)} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
& (Join-Path $PSScriptRoot 'verify-installers.ps1') -Packages $output -Payload $payloadPath -Report (Join-Path $work 'verification.json')
Write-Output "Windows installers: $output"
