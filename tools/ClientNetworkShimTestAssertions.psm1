$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ExpectedPattern
    )

    try {
        & $Operation
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedPattern) {
            throw "Wrong refusal for ${Label}: $($_.Exception.Message)"
        }
        Write-Host "Expected refusal ($Label): $($_.Exception.Message)"
        return
    }

    throw "Expected operation to be refused: $Label"
}

Export-ModuleMember -Function 'Assert-True', 'Assert-Throws'
