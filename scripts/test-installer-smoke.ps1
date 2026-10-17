param(
    [Parameter(Mandatory = $true)][string]$Packages,
    [Parameter(Mandatory = $true)][string]$Payload
)
$ErrorActionPreference = 'Stop'
# Only run on the disposable GitHub runner, never against a user's installation.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') { throw 'Installer smoke checks require a disposable Windows Actions runner' }
$root = Split-Path $PSScriptRoot -Parent
$packagesPath = if ([IO.Path]::IsPathRooted($Packages)) { [IO.Path]::GetFullPath($Packages) } else { [IO.Path]::GetFullPath((Join-Path $root $Packages)) }
$payloadPath = if ([IO.Path]::IsPathRooted($Payload)) { [IO.Path]::GetFullPath($Payload) } else { [IO.Path]::GetFullPath((Join-Path $root $Payload)) }
$work = Join-Path $env:RUNNER_TEMP ('Melvune 安装验收 [' + [Guid]::NewGuid().ToString('N') + ']')
New-Item -ItemType Directory -Path $work | Out-Null
$defaultPath = Join-Path $env:LOCALAPPDATA 'Programs/Melvune'
$profile = Join-Path $env:LOCALAPPDATA 'Melvune.Native'
if ((Test-Path -LiteralPath $defaultPath) -or (Test-Path -LiteralPath $profile) -or (Test-Path 'HKCU:\Software\Melvune\Installer')) { throw 'Existing installation/profile; refusing smoke check' }
New-Item -ItemType Directory -Path $profile | Out-Null
$sentinel = Join-Path $profile 'installer-verification-marker.txt'
$sentinelText = [Guid]::NewGuid().ToString('N')
Set-Content -LiteralPath $sentinel -Value $sentinelText
function Invoke-InstallerCommand([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(240000)) { throw 'Installer did not finish within four minutes' }
    if ($process.ExitCode -notin @(0,3010)) { throw ('Installer failed with exit code ' + $process.ExitCode) }
}
function Assert-Installed([string]$Directory) {
    foreach ($file in Get-ChildItem -LiteralPath $payloadPath -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($payloadPath, $file.FullName)
        $actual = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $actual) -or (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw ('Installed payload mismatch: ' + $relative) }
    }
    $savedPath = (Get-ItemProperty 'HKCU:\Software\Melvune\Installer' -Name InstallFolder).InstallFolder
    if ($savedPath.TrimEnd('\') -ne $Directory.TrimEnd('\')) { throw 'Chosen install directory was not saved' }
    if (-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'Melvune.lnk'))) { throw 'Start menu shortcut missing' }
}
function Assert-Uninstalled([string]$Directory) {
    if (Test-Path -LiteralPath (Join-Path $Directory 'Melvune.App.exe')) { throw 'Application remains after uninstall' }
    if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'Melvune.lnk')) { throw 'Start menu shortcut remains after uninstall' }
    if (Get-ItemProperty 'HKCU:\Software\Melvune\Installer' -Name InstallFolder -ErrorAction SilentlyContinue) { throw 'Installer directory registration remains after uninstall' }
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $sentinelText) { throw 'Uninstall changed personal configuration' }
}
$msi = Join-Path $packagesPath 'Melvune-0.2.0-rc.9-win-x64-setup.msi'
$setup = Join-Path $packagesPath 'Melvune-0.2.0-rc.9-win-x64-setup.exe'
$msiPath = Join-Path $work 'MSI 自选目录'
Invoke-InstallerCommand 'msiexec.exe' @('/i',$msi,'/qn','/norestart',('INSTALLFOLDER=' + $msiPath),'/l*v',(Join-Path $work 'msi-install.log'))
Assert-Installed $msiPath
Invoke-InstallerCommand 'msiexec.exe' @('/x',$msi,'/qn','/norestart','/l*v',(Join-Path $work 'msi-uninstall.log'))
Assert-Uninstalled $msiPath
foreach ($entry in @(@{Name='exe-default';Path=$defaultPath;Custom=$false},@{Name='exe-custom';Path=(Join-Path $work 'EXE 自选目录');Custom=$true})) {
    $arguments = @('-quiet','-norestart','-log',(Join-Path $work ($entry.Name + '-install.log')))
    if ($entry.Custom) { $arguments += ('InstallFolder=' + $entry.Path) }
    Invoke-InstallerCommand $setup $arguments
    Assert-Installed $entry.Path
    Invoke-InstallerCommand $setup @('-uninstall','-quiet','-norestart','-log',(Join-Path $work ($entry.Name + '-uninstall.log')))
    Assert-Uninstalled $entry.Path
}
Write-Output 'Installer smoke checks passed: MSI custom path, EXE default/custom paths, saved install directory, start menu and profile preservation.'
