#Requires -Version 7.2
param([switch]$Run)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'build-environment.ps1')
Initialize-MelvuneGitEnvironment $root
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { throw 'Run scripts/native.ps1 bootstrap first' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
. (Join-Path $PSScriptRoot 'ensure-native-decoder.ps1')
Ensure-MelvuneNativeDecoder $root
$previewCreated = (Get-Date).ToUniversalTime()
$name = $previewCreated.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$preview = Join-Path $root ('dist/previews/' + $name)
New-Item -ItemType Directory -Path $preview | Out-Null
$marker = [ordered]@{product='Melvune';kind='development-preview';formatVersion=1;createdUtc=$previewCreated.ToString('o');configuration='Debug';state='building'}
$marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $preview 'preview.json') -Encoding utf8
& $sdk restore (Join-Path $root 'native/Melvune.sln') --configfile (Join-Path $root 'native/NuGet.Config') --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Preview restore failed' }
& $sdk publish (Join-Path $root 'native/src/Melvune.App/Melvune.App.csproj') -c Debug --no-restore --nologo -o $preview
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed; incomplete directory retained for investigation' }
foreach ($file in @('Melvune.App.exe','Melvune.App.pri','App.xbf','MainWindow.xbf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $preview $file))) { throw ('Missing preview file: ' + $file) }
}
$marker.state = 'ready'
$marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $preview 'preview.json') -Encoding utf8
Write-Output ('Development preview: ' + $preview)
if ($Run) {
    $previewProfile = Join-Path $root '.review-audit/development-profile'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $preview 'Melvune.App.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $preview
    $start.ArgumentList.Add('--data-directory=' + $previewProfile)
    [void][Diagnostics.Process]::Start($start)
}
