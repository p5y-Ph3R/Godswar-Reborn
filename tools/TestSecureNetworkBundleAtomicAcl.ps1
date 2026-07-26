[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureNetworkBundleFiles.psm1'
) -Force

$root = Join-Path (
    [IO.Path]::GetTempPath()
) "reborn-atomic-acl-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $destination = Join-Path $root 'receipt.json'
    $staged = Join-Path $root 'receipt.json.pending'
    [IO.File]::WriteAllText(
        $destination,
        '{"state":"before"}',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $staged,
        '{"state":"after"}',
        [Text.UTF8Encoding]::new($false))

    $destinationAcl = Get-Acl -LiteralPath $destination
    $destinationAcl.SetAccessRuleProtection(
        $true,
        $true)
    Set-Acl -LiteralPath $destination -AclObject $destinationAcl
    $beforeSddl = (Get-Acl -LiteralPath $destination).Sddl
    $stagedSddl = (Get-Acl -LiteralPath $staged).Sddl
    if ($beforeSddl -ceq $stagedSddl) {
        throw 'ACL fixture did not create distinct descriptors.'
    }

    $expectedSha256 =
        (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
    Move-RebornStagedFileAtomic `
        $staged `
        $destination `
        $expectedSha256 | Out-Null

    $afterSddl = (Get-Acl -LiteralPath $destination).Sddl
    if ($afterSddl -cne $beforeSddl) {
        throw 'Atomic replacement did not preserve the destination ACL.'
    }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne
        $expectedSha256) {
        throw 'Atomic replacement did not publish the staged content.'
    }
    if (Test-Path -LiteralPath $staged) {
        throw 'Atomic replacement left the staged file behind.'
    }

    [pscustomobject]@{
        Result = 'Passed'
        DistinctFixtureAcl = $true
        DestinationAclPreserved = $true
        ContentPublished = $true
    }
} finally {
    if (Test-Path -LiteralPath $root) {
        [IO.Directory]::Delete($root, $true)
    }
}
