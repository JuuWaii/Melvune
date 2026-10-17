param(
    [Parameter(Mandatory = $true)][string]$Packages,
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$Report
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $root '.tools/dotnet'
$env:DOTNET_ROLL_FORWARD = 'Major'
$wix = Join-Path $root '.tools/wix/wix.exe'
$work = Join-Path $root ('.review-audit/installer-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
& $wix msi decompile (Join-Path $Packages 'Melvune-0.2.0-rc.9-win-x64-setup.msi') -o (Join-Path $work 'msi.wxs') -x (Join-Path $work 'msi')
if ($LASTEXITCODE -ne 0) { throw 'MSI extraction failed' }
& $wix burn extract (Join-Path $Packages 'Melvune-0.2.0-rc.9-win-x64-setup.exe') -o (Join-Path $work 'burn') -oba (Join-Path $work 'burn-ba')
if ($LASTEXITCODE -ne 0) { throw 'Setup EXE extraction failed' }
& python (Join-Path $PSScriptRoot 'verify-windows-packages.py') --packages $Packages --payload $Payload --msi-source (Join-Path $work 'msi.wxs') --msi-extracted (Join-Path $work 'msi') --burn-extracted (Join-Path $work 'burn') --burn-ba (Join-Path $work 'burn-ba') --report $Report
if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed' }
