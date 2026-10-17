function Ensure-MelvuneNativeDecoder([string]$RepositoryRoot) {
    $decoderBuild = Join-Path $RepositoryRoot '.tools/native-decoder/build'
    $decoderManifest = Join-Path $decoderBuild 'build-manifest.json'
    $decoderInputs = @{
        patchSha256 = 'native/decoder/mp3-preroll.patch'
        scriptSha256 = 'scripts/build-native-decoder.py'
        lockSha256 = 'native/decoder/toolchain-lock.json'
        decoderSha256 = '.tools/native-decoder/build/FFmpegInteropX.dll'
    }
    $decoderCurrent = (Test-Path -LiteralPath $decoderManifest) -and (Test-Path -LiteralPath (Join-Path $decoderBuild 'decoder.props'))
    if ($decoderCurrent) {
        try {
            $decoderRecord = Get-Content -LiteralPath $decoderManifest -Raw | ConvertFrom-Json
            foreach ($decoderInput in $decoderInputs.GetEnumerator()) {
                $decoderInputPath = Join-Path $RepositoryRoot $decoderInput.Value
                if (-not (Test-Path -LiteralPath $decoderInputPath) -or
                    (Get-FileHash -LiteralPath $decoderInputPath -Algorithm SHA256).Hash -ne $decoderRecord.($decoderInput.Key)) {
                    $decoderCurrent = $false
                }
            }
        } catch { $decoderCurrent = $false }
    }
    if (-not $decoderCurrent) {
        Write-Output 'Preparing pinned MP3 preroll decoder in .tools/native-decoder (Python 3.11+ required).'
        $decoderPython = (Get-Command python -ErrorAction Stop).Source
        & $decoderPython (Join-Path $RepositoryRoot 'scripts/build-native-decoder.py')
        if ($LASTEXITCODE -ne 0) { throw 'Pinned native decoder build failed.' }
    }
}
