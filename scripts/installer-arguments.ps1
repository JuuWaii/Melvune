function ConvertTo-MelvuneInstallerArguments {
    param([Parameter(Mandatory = $true)][string[]]$Items)
    # msiexec uses its own property parser: PROPERTY="value with spaces",
    # rather than the CRT-style "PROPERTY=value with spaces" from ArgumentList.
    # Installer inputs are paths/switches; embedded quotes and control chars
    # are not supported and are rejected instead of becoming command syntax.
    $formatted = foreach ($item in $Items) {
        if ($item -match '["\x00-\x1f]') { throw 'Invalid installer argument' }
        if ($item -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $Matches[1] + '="' + $Matches[2] + '"'
        } elseif ($item -match '\s' -or $item.Length -eq 0) {
            '"' + $item + '"'
        } else { $item }
    }
    return $formatted -join ' '
}
