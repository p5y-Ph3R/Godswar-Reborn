Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
) -Force

$script:MaximumEvidenceBytes = 1536
$script:AllowedEvidenceLines = @(
    '[controlled-host] privacy-safe evidence channel started',
    '[controlled-host] secure listeners ready',
    '[controlled-host] TLS policy accepted',
    '[controlled-host] accepted secure preface response written',
    '[controlled-host] TLS client authenticated',
    '[controlled-host] UDP endpoint authenticated and bound',
    '[secure-acceptance] phase4 fault campaign enabled',
    '[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32',
    '[secure-acceptance] snapshot ACK drop window completed',
    '[secure-acceptance] one-way TLS fallback observed',
    '[secure-acceptance] authoritative correction forced reason=not_ready',
    '[secure-acceptance] post-fallback TLS movement observed no_switchback=true',
    '[secure-acceptance] phase4 fault campaign expired',
    '[controlled-host] secure server stopping'
)

function New-RebornControlledHostEvidencePath {
    param(
        [Parameter(Mandatory)][string]$EvidenceDirectory
    )

    $directory = [IO.Path]::GetFullPath($EvidenceDirectory)
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    Assert-RebornDirectoryPath `
        $directory 'controlled-host evidence directory' | Out-Null

    $name =
        'secure-server-' +
        "$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')).log"
    $path = Join-Path $directory $name
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to overwrite controlled-host evidence: $path"
    }
    return $path
}

function Assert-RebornControlledHostPrivacyEvidence {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$RequireStopped
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'controlled-host privacy-safe evidence'
    $bytes = [IO.File]::ReadAllBytes($resolved)
    if ($bytes.Length -eq 0 -or
        $bytes.Length -gt $script:MaximumEvidenceBytes) {
        throw 'Controlled-host evidence has an invalid bounded size.'
    }
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw 'Controlled-host evidence must be BOM-free UTF-8.'
    }

    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($bytes)
    if ($text.IndexOf([char]0) -ge 0) {
        throw 'Controlled-host evidence contains a NUL character.'
    }
    $lines = @(
        $text -split '\r\n|\n|\r' |
            Where-Object { $_.Length -ne 0 }
    )
    if ($lines.Count -eq 0 -or
        $lines.Count -gt $script:AllowedEvidenceLines.Count) {
        throw 'Controlled-host evidence has an invalid event count.'
    }
    $allowed = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($allowedLine in $script:AllowedEvidenceLines) {
        [void]$allowed.Add($allowedLine)
    }
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($line in $lines) {
        if (-not $allowed.Contains($line) -or
            -not $seen.Add($line)) {
            throw (
                'Controlled-host evidence contains a disallowed or ' +
                'duplicate event.')
        }
    }
    if ($lines[0] -cne
        '[controlled-host] privacy-safe evidence channel started') {
        throw 'Controlled-host evidence does not start at the privacy gate.'
    }
    if ($RequireStopped -and
        $lines[-1] -cne
        '[controlled-host] secure server stopping') {
        throw 'Controlled-host evidence lacks the final stopping event.'
    }

    [pscustomobject]@{
        Path = $resolved
        Bytes = $bytes.Length
        Events = $lines.Count
        Started = $true
        Stopped =
            $lines[-1] -ceq
                '[controlled-host] secure server stopping'
    }
}

function Protect-RebornControlledHostPrivacyEvidence {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'controlled-host evidence ACL target'
    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($reader)
    foreach ($principal in @($administrators, $system)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    $security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $reader,
            [Security.AccessControl.FileSystemRights]::Read,
            [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $resolved -AclObject $security

    Assert-RebornControlledHostReadOnlyArtifactAcl `
        $resolved -File -AllowCurrentUserOwner | Out-Null
    return $resolved
}

function Get-RebornControlledHostAllowedEvidenceLines {
    return @($script:AllowedEvidenceLines)
}

Export-ModuleMember -Function @(
    'New-RebornControlledHostEvidencePath',
    'Assert-RebornControlledHostPrivacyEvidence',
    'Protect-RebornControlledHostPrivacyEvidence',
    'Get-RebornControlledHostAllowedEvidenceLines'
)
