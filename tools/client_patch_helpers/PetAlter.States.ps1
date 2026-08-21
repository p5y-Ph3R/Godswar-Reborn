Set-StrictMode -Version Latest

# Exact byte states for the reviewed UTF-8/BOM/CRLF Pet_Alter.xml resource.
# The three dimensions are independent and each patcher preserves the other two.
$script:RebornPetAlterStates = @(
    [pscustomobject]@{
        Factors = 'Stock'; Rebirth = 'Level50'; AgilityRebound = 'Enabled'
        Sha256 = '0E2349F555DC125601DC7D51924B79A25F9BFD1E2288C514B2E2B4AFD5377844'
    },
    [pscustomobject]@{
        Factors = 'Stock'; Rebirth = 'Level50'; AgilityRebound = 'Disabled'
        Sha256 = '0634024EE1164A221CFA76E7455E19EBDF907899490CD142E0CC55BC5B702311'
    },
    [pscustomobject]@{
        Factors = 'Stock'; Rebirth = 'Level30'; AgilityRebound = 'Enabled'
        Sha256 = '22A5EE7ACBA76A3F345E633E9042E0A6926271F95AD8C71628C4312A0C9DA52F'
    },
    [pscustomobject]@{
        Factors = 'Stock'; Rebirth = 'Level30'; AgilityRebound = 'Disabled'
        Sha256 = '71DFCD4497CC7A0F624DC873DD5E3F95F32CC99BEA13D95011BDEDE833EDF89D'
    },
    [pscustomobject]@{
        Factors = 'Patched'; Rebirth = 'Level50'; AgilityRebound = 'Enabled'
        Sha256 = 'E97ADE5D6BE0E3DED334AEA1C1EBB3EFA84FCC7D04CAE0E3417E036CB6D2C0BA'
    },
    [pscustomobject]@{
        Factors = 'Patched'; Rebirth = 'Level50'; AgilityRebound = 'Disabled'
        Sha256 = '7A2224ED8942C2D71A5BC91D9E91A0168C2DEDF19B73CA7DB11F7D271A7F394C'
    },
    [pscustomobject]@{
        Factors = 'Patched'; Rebirth = 'Level30'; AgilityRebound = 'Enabled'
        Sha256 = '74BA1124C9956C6E065DCFCD6FA0E9A2FAA4F09994A4699E1D737509D00B8C51'
    },
    [pscustomobject]@{
        Factors = 'Patched'; Rebirth = 'Level30'; AgilityRebound = 'Disabled'
        Sha256 = '7389BFA5D1D8DF812603DF44424ADCA090C44F6DAC6F6CD4D5858CC504C3CF90'
    }
)

function Resolve-RebornPetAlterState([string]$Path) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    $matches = @($script:RebornPetAlterStates | Where-Object {
        $_.Sha256 -ceq $hash
    })
    if ($matches.Count -ne 1) {
        throw "Unsupported Pet_Alter.xml (SHA-256 $hash): $Path"
    }
    $matches[0]
}

function Find-RebornPetAlterState(
    [ValidateSet('Stock', 'Patched')]
    [string]$Factors,
    [ValidateSet('Level50', 'Level30')]
    [string]$Rebirth,
    [ValidateSet('Enabled', 'Disabled')]
    [string]$AgilityRebound
) {
    $matches = @($script:RebornPetAlterStates | Where-Object {
        $_.Factors -ceq $Factors -and $_.Rebirth -ceq $Rebirth -and
            $_.AgilityRebound -ceq $AgilityRebound
    })
    if ($matches.Count -ne 1) {
        throw 'The requested Pet_Alter.xml state is not defined.'
    }
    $matches[0]
}
