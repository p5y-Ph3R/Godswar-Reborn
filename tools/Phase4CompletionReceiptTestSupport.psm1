Set-StrictMode -Version Latest

function Assert-RebornPhase4CompletionOriginPinTamperRejected {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Pins,
        [Parameter(Mandatory)][scriptblock]$ReadAction
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    $checksumPath = [IO.Path]::ChangeExtension($resolved, '.sha256')
    $original = [IO.File]::ReadAllBytes($resolved)
    $originalChecksum = [IO.File]::ReadAllBytes($checksumPath)
    try {
        foreach ($name in @(
            'stockOriginSha256',
            'candidateOriginSha256'
        )) {
            $record =
                [Text.UTF8Encoding]::new($false, $true).GetString(
                    $original) |
                ConvertFrom-Json
            $record.pins.$name = 'D' * 64
            $tampered = [Text.UTF8Encoding]::new($false).GetBytes(
                ($record | ConvertTo-Json -Compress -Depth 8))
            try {
                [IO.File]::WriteAllBytes($resolved, $tampered)
                $sha =
                    (Get-FileHash -LiteralPath $resolved `
                        -Algorithm SHA256).Hash
                [IO.File]::WriteAllText(
                    $checksumPath,
                    $sha,
                    [Text.Encoding]::ASCII)
                $message = ''
                try {
                    & $ReadAction $resolved $Pins | Out-Null
                }
                catch {
                    $message = $_.Exception.Message
                }
                if ($message -notlike "*pin changed: $name*") {
                    throw (
                        'Completion did not reject the isolated Origin ' +
                        "pin change: $name")
                }
            }
            finally {
                [Array]::Clear($tampered, 0, $tampered.Length)
                [IO.File]::WriteAllBytes($resolved, $original)
                [IO.File]::WriteAllBytes(
                    $checksumPath,
                    $originalChecksum)
            }
        }
    }
    finally {
        [Array]::Clear($original, 0, $original.Length)
        [Array]::Clear(
            $originalChecksum,
            0,
            $originalChecksum.Length)
    }
}

Export-ModuleMember -Function `
    'Assert-RebornPhase4CompletionOriginPinTamperRejected'
