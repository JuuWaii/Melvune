function Initialize-MelvuneGitEnvironment([string]$RepositoryRoot) {
    # Git canonicalizes mapped drives to UNC paths. Trust only this checkout,
    # only in this process and its build children; never change global config.
    $item = Get-Item -LiteralPath $RepositoryRoot -ErrorAction Stop
    $gitRoot = $item.FullName
    if ($item.PSDrive.DisplayRoot) {
        $gitRoot = $item.PSDrive.DisplayRoot.TrimEnd('\') + $gitRoot.Substring($item.PSDrive.Root.TrimEnd('\').Length)
    }
    $gitRoot = $gitRoot.Replace('\', '/')
    $count = 0
    if ($env:GIT_CONFIG_COUNT -and -not [int]::TryParse($env:GIT_CONFIG_COUNT, [ref]$count)) { throw 'Invalid inherited Git configuration count.' }
    for ($index = 0; $index -lt $count; $index++) {
        if ([Environment]::GetEnvironmentVariable('GIT_CONFIG_KEY_' + $index) -eq 'safe.directory' -and
            [Environment]::GetEnvironmentVariable('GIT_CONFIG_VALUE_' + $index) -eq $gitRoot) { return }
    }
    [Environment]::SetEnvironmentVariable('GIT_CONFIG_KEY_' + $count, 'safe.directory', 'Process')
    [Environment]::SetEnvironmentVariable('GIT_CONFIG_VALUE_' + $count, $gitRoot, 'Process')
    $env:GIT_CONFIG_COUNT = [string]($count + 1)
}
