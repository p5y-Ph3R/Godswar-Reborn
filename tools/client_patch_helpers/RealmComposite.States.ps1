function Get-RealmCompositeStateMap {
    $definitions = @(
        @(
            '74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C',
            'CurrentComposite', $false, $false, $false
        ),
        @(
            '9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA',
            'ManualRealmSelectionPatched', $true, $false, $false
        ),
        @(
            'C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9',
            'CharacterBackGuardPatched', $false, $true, $false
        ),
        @(
            '318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF',
            'ManualRealmSelectionAndCharacterBackGuardPatched',
            $true, $true, $false
        ),
        @(
            '8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F',
            'OctagramVisualPatched', $false, $false, $true
        ),
        @(
            '4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB',
            'OctagramVisualAndManualRealmSelectionPatched',
            $true, $false, $true
        ),
        @(
            'FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61',
            'OctagramVisualAndCharacterBackGuardPatched',
            $false, $true, $true
        ),
        @(
            'FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5',
            'OctagramVisualManualRealmSelectionAndCharacterBackGuardPatched',
            $true, $true, $true
        )
    )

    $states = @{}
    foreach ($definition in $definitions) {
        $states[$definition[0]] = [pscustomobject]@{
            Hash = $definition[0]
            Name = $definition[1]
            ManualPatched = [bool]$definition[2]
            GuardPatched = [bool]$definition[3]
            OctagramPatched = [bool]$definition[4]
        }
    }
    return $states
}

function Get-RealmCompositePeerState {
    param(
        [hashtable]$States,
        [object]$State,

        [ValidateSet('ManualRealmSelection', 'CharacterBackGuard')]
        [string]$Toggle
    )

    $manualPatched = $State.ManualPatched
    $guardPatched = $State.GuardPatched
    if ($Toggle -eq 'ManualRealmSelection') {
        $manualPatched = -not $manualPatched
    }
    else {
        $guardPatched = -not $guardPatched
    }

    return Get-RealmCompositeState $States $manualPatched $guardPatched `
        $State.OctagramPatched $Toggle
}

function Get-RealmCompositeState {
    param(
        [hashtable]$States,
        [bool]$ManualPatched,
        [bool]$GuardPatched,
        [bool]$OctagramPatched,
        [string]$Label = 'state'
    )

    $matches = @($States.Values | Where-Object {
            $_.ManualPatched -eq $ManualPatched -and
            $_.GuardPatched -eq $GuardPatched -and
            $_.OctagramPatched -eq $OctagramPatched
        })
    if ($matches.Count -ne 1) {
        throw "Internal realm-composite $Label mapping failed."
    }
    return $matches[0]
}

function Get-RealmCompositeOctagramStatus {
    param([object]$State)

    if ($State.OctagramPatched) {
        return 'Applied'
    }
    return 'Reverted'
}
