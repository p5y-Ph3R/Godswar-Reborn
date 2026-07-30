[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'SecureClientOriginIdentity.psm1'
) -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-BytesEqual {
    param([byte[]]$Expected, [byte[]]$Actual, [string]$Message)

    Assert-True (
        [Convert]::ToBase64String($Expected) -ceq
            [Convert]::ToBase64String($Actual)
    ) $Message
}

function Get-OriginIdentityHeaderSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $matches = [regex]::Matches(
        [IO.File]::ReadAllText($Path),
        '0x(?<byte>[0-9A-Fa-f]{2})')
    Assert-True (
        $matches.Count -eq 32
    ) 'Generated Origin header did not contain exactly 32 bytes.'
    return (
        $matches |
            ForEach-Object {
                $_.Groups['byte'].Value.ToUpperInvariant()
            }) -join ''
}

$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()) (
    'reborn-origin-identity-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

try {
    $headerPath = Join-Path $temporaryRoot 'OriginIdentity.generated.h'
    $candidatePath = Join-Path $temporaryRoot 'Origin.exe'
    $snapshot = [byte[]]@(0xEF, 0xBB, 0xBF, 0x41, 0x0A, 0x42, 0x0D, 0x0A)
    [IO.File]::WriteAllBytes($headerPath, $snapshot)
    [IO.File]::WriteAllBytes(
        $candidatePath,
        [Text.Encoding]::ASCII.GetBytes('reviewed-candidate-origin'))
    $candidateSha256 = (
        Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256
    ).Hash

    $success = Invoke-WithRebornSecureClientOriginIdentity `
        -HeaderPath $headerPath `
        -CandidateOriginPath $candidatePath `
        -Action {
            param([string]$selectedSha256)

            Assert-True (
                $selectedSha256 -ceq $candidateSha256
            ) 'Candidate file hash was not selected.'
            Assert-True (
                (Get-OriginIdentityHeaderSha256 $headerPath) -ceq
                    $candidateSha256
            ) 'Candidate generated header did not contain its file hash.'
            'success'
        }
    Assert-True ($success -ceq 'success') 'Success action result was lost.'
    Assert-BytesEqual `
        $snapshot `
        ([IO.File]::ReadAllBytes($headerPath)) `
        'Successful scope did not restore exact header bytes.'

    $caught = $false
    try {
        Invoke-WithRebornSecureClientOriginIdentity `
            -HeaderPath $headerPath `
            -CandidateOriginPath $candidatePath `
            -Action {
                param([string]$selectedSha256)

                Assert-True (
                    $selectedSha256 -ceq $candidateSha256
                ) 'Failure scope did not select the candidate identity.'
                throw 'expected-build-failure'
            }
    }
    catch {
        $caught = $_.Exception.Message -eq 'expected-build-failure'
    }
    Assert-True $caught 'Expected action failure was not preserved.'
    Assert-BytesEqual `
        $snapshot `
        ([IO.File]::ReadAllBytes($headerPath)) `
        'Failed scope did not restore exact header bytes.'

    $sealedSha256 =
        'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'
    Invoke-WithRebornSecureClientOriginIdentity `
        -HeaderPath $headerPath `
        -CandidateOriginPath '' `
        -Action {
            param([string]$selectedSha256)

            Assert-True (
                $selectedSha256 -ceq $sealedSha256
            ) 'No-candidate scope did not preserve the sealed V6 identity.'
            Assert-True (
                (Get-OriginIdentityHeaderSha256 $headerPath) -ceq
                    $sealedSha256
            ) 'No-candidate header did not contain the sealed V6 identity.'
        }
    Assert-BytesEqual `
        $snapshot `
        ([IO.File]::ReadAllBytes($headerPath)) `
        'Sealed scope did not restore exact header bytes.'

    $repositoryHeader = Join-Path (
        Split-Path -Parent $PSScriptRoot) (
        'client\network-shim\src\' +
        'SecureClientOriginIdentity.generated.h')
    Assert-True (
        (Get-OriginIdentityHeaderSha256 $repositoryHeader) -ceq
            $sealedSha256
    ) 'Repository Origin placeholder did not pin the sealed V6 identity.'

    [pscustomobject]@{
        Result = 'SecureClientOriginIdentityPassed'
        CandidateSha256 = $candidateSha256
        SealedSha256 = $sealedSha256
        ExactSuccessRestore = $true
        ExactFailureRestore = $true
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporary =
        [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (-not $resolved.StartsWith(
            "$systemTemporary\reborn-origin-identity-test-",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove unexpected identity-test directory.'
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
