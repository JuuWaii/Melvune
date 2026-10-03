param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($exe) -ne 'FnMusic.App.exe') { throw 'Choose a Debug FnMusic.App.exe built from this repository.' }
$passwordA = $null
$passwordB = $null
$startInfo = $null
$process = $null
try {
    $server = Read-Host 'NAS music URL'
    $accountA = Read-Host 'Test account with library access'
    $passwordA = Read-Host 'Password for the first test account' -AsSecureString
    $accountB = Read-Host 'Test account with no library access'
    $passwordB = Read-Host 'Password for the second test account' -AsSecureString
    $startInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = 'Hidden'
    # ArgumentList is unavailable in Windows PowerShell 5.1 / .NET Framework.
    # This single fixed flag does not require dynamic argument quoting.
    $startInfo.Arguments = '--verify-native-accounts'
    $startInfo.EnvironmentVariables['FNM_TEST_SERVER'] = $server
    $startInfo.EnvironmentVariables['FNM_TEST_ACCOUNT_A'] = $accountA
    $startInfo.EnvironmentVariables['FNM_TEST_ACCOUNT_B'] = $accountB
    $startInfo.EnvironmentVariables['FNM_TEST_PASSWORD_A'] = [Net.NetworkCredential]::new('', $passwordA).Password
    $startInfo.EnvironmentVariables['FNM_TEST_PASSWORD_B'] = [Net.NetworkCredential]::new('', $passwordB).Password
    $started = Get-Date
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit(150000)) { throw 'Account verification timed out.' }
    $report = Get-Item -LiteralPath (Join-Path (Split-Path $exe -Parent) 'account-verification.json')
    if ($report.LastWriteTime -lt $started) { throw 'No fresh verification report.' }
    $result = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 4
    if ($result.stage -ne 'complete' -or $result.error -or @($result.checks.PSObject.Properties | Where-Object Value -ne $true).Count) {
        throw 'Account verification did not pass; inspect the sanitized report above.'
    }
} finally {
    if ($startInfo) {
        foreach ($key in @('FNM_TEST_SERVER','FNM_TEST_ACCOUNT_A','FNM_TEST_ACCOUNT_B','FNM_TEST_PASSWORD_A','FNM_TEST_PASSWORD_B')) { [void]$startInfo.EnvironmentVariables.Remove($key) }
    }
    if ($passwordA) { $passwordA.Dispose() }
    if ($passwordB) { $passwordB.Dispose() }
    if ($process) { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
}
