param(
    [string]$Payload = 'dist/Melvune-0.2.0-rc.9-win-x64',
    [string]$Name,
    [switch]$PortableOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'build-environment.ps1')
Initialize-MelvuneGitEnvironment $root
$payloadPath = if ([IO.Path]::IsPathRooted($Payload)) { [IO.Path]::GetFullPath($Payload) } else { [IO.Path]::GetFullPath((Join-Path $root $Payload)) }
$release = Get-Content -LiteralPath (Join-Path $payloadPath 'release.json') -Raw | ConvertFrom-Json
$version = [string]$release.version
if ($release.product -ne 'Melvune' -or $version -ne '0.2.0-rc.9') { throw 'This writer requires a Melvune 0.2.0-rc.9 payload. Retired products are not supported.' }
if ($release.sourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid payload source revision.' }
if ([string]::IsNullOrEmpty($Name)) { $Name = 'Melvune-' + $version + '-win-x64-portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
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
$packagingSource = & git -c "safe.directory=$($root.Replace('\','/'))" -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $packagingSource -notmatch '^[a-f0-9]{40}$') { throw 'Packaging source revision unavailable.' }
if ($packagingSource.Trim() -ne $release.sourceCommit) { throw 'Payload and packaging source revisions differ. Build both from the same checkpoint.' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'Run scripts/native.ps1 bootstrap first.' }
$work = Join-Path $root ".tools/portable/$Name"
New-Item -ItemType Directory -Force -Path $work,$output | Out-Null
$project = Join-Path $root 'packaging/portable/Melvune.Portable.csproj'
& $sdk restore $project --configfile (Join-Path $root 'native/NuGet.Config') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher restore failed.' }
& $sdk publish $project -c Release --no-restore --nologo ('-p:Version=' + $version) -o (Join-Path $work 'launcher')
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher build failed.' }
$portable = Join-Path $output ('Melvune-' + $version + '-win-x64-portable.exe')
$target = [IO.File]::Create($portable)
try {
    foreach ($file in @((Join-Path $work 'launcher/Melvune.Portable.exe'), $archivePath)) {
        $inputStream = [IO.File]::OpenRead($file)
        try { $inputStream.CopyTo($target) } finally { $inputStream.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new($target)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('MELVUNE-PAYLOAD-1'))
    $bytes = New-Object byte[] 32
    for ($i = 0; $i -lt 32; $i++) { $bytes[$i] = [Convert]::ToByte($zipHash.Substring($i * 2, 2), 16) }
    $writer.Write($bytes)
    $writer.Write([long](Get-Item -LiteralPath $archivePath).Length)
    $writer.Flush()
} finally { $target.Dispose() }
$metadata = [ordered]@{ product='Melvune'; version=$version; architecture='win-x64'; packageKind='portable-only'; payloadSource=$release.sourceCommit; packagingSource=$packagingSource.Trim(); payloadZipSha256=$zipHash; signed=$false; artifacts=@([IO.Path]::GetFileName($portable)) }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding UTF8
Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
& python (Join-Path $PSScriptRoot 'verify-portable-package.py') --packages $output --payload $payloadPath --report (Join-Path $work 'portable-verification.json')
if ($LASTEXITCODE -ne 0) { throw 'Portable package verification failed.' }
Write-Output "Windows portable package: $output"
if (-not $PortableOnly) {
    & (Join-Path $PSScriptRoot 'package-installers.ps1') -Payload $payloadPath -Name ($Name + '-installers')
}
