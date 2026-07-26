Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
) -Force

function New-RebornControlledHostDatabaseBackupReceipt {
    param(
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$Sha256,
        [Parameter(Mandatory)][string]$SourcePath
    )

    [ordered]@{
        schemaVersion = 2
        mode = 'ControlledHostDatabaseBackup'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        path = [IO.Path]::GetFileName($TargetPath)
        sha256 = $Sha256.ToUpperInvariant()
        source = [IO.Path]::GetFullPath($SourcePath)
        readerSid =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    }
}

function Get-RebornControlledHostDatabaseBackupState {
    param(
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedSha256,
        [switch]$AllowTestOwner
    )

    $target = [IO.Path]::GetFullPath($TargetPath)
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    $expected = $ExpectedSha256.ToUpperInvariant()
    if (-not (Split-Path -Parent $target).Equals(
            (Split-Path -Parent $receipt),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Database backup and receipt must share one protected root.'
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        return [pscustomobject]@{
            State = 'SourceVerified'
            TargetPath = $target
            ReceiptPath = $receipt
        }
    }
    try {
        $root = Split-Path -Parent $target
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $root -AllowCurrentUserOwner:$AllowTestOwner | Out-Null
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $target -File -AllowCurrentUserOwner:$AllowTestOwner |
            Out-Null
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash `
                -cne $expected -or
            -not (Test-Path -LiteralPath $receipt -PathType Leaf)) {
            throw 'Database backup or receipt is absent or changed.'
        }
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $receipt -File -AllowCurrentUserOwner:$AllowTestOwner |
            Out-Null
        $item = Get-Item -LiteralPath $receipt
        if ($item.Length -lt 128 -or $item.Length -gt 8192) {
            throw 'Database backup receipt is outside its size budget.'
        }
        $record = Get-Content -LiteralPath $receipt -Raw |
            ConvertFrom-Json
        $expectedProperties = @(
            'schemaVersion',
            'mode',
            'createdUtc',
            'path',
            'sha256',
            'source',
            'readerSid'
        )
        $actualProperties = @($record.PSObject.Properties.Name)
        $created = [DateTimeOffset]::MinValue
        $reader =
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (
            $actualProperties.Count -ne $expectedProperties.Count -or
            @($actualProperties | Where-Object {
                $_ -cnotin $expectedProperties
            }).Count -ne 0 -or
            $record.schemaVersion -ne 2 -or
            [string]$record.mode -cne
                'ControlledHostDatabaseBackup' -or
            [string]$record.path -cne
                [IO.Path]::GetFileName($target) -or
            [string]$record.sha256 -cne $expected -or
            [string]$record.source -cne
                [IO.Path]::GetFullPath([string]$record.source) -or
            [string]$record.readerSid -cne $reader -or
            -not [DateTimeOffset]::TryParseExact(
                [string]$record.createdUtc,
                'O',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$created) -or
            $created.Offset -ne [TimeSpan]::Zero
        ) {
            throw 'Database backup receipt authority is not exact.'
        }
        return [pscustomobject]@{
            State = 'Protected'
            TargetPath = $target
            ReceiptPath = $receipt
            ReaderSid = $reader
            Record = $record
        }
    }
    catch {
        return [pscustomobject]@{
            State = 'Conflict'
            TargetPath = $target
            ReceiptPath = $receipt
            Error = $_.Exception.Message
        }
    }
}

Export-ModuleMember -Function @(
    'New-RebornControlledHostDatabaseBackupReceipt',
    'Get-RebornControlledHostDatabaseBackupState'
)
