function New-RealmCompositeFixture {
    param(
        [byte[]]$SourceBytes,
        [bool]$ManualPatched,
        [bool]$GuardPatched,
        [bool]$OctagramPatched
    )

    $result = [byte[]]$SourceBytes.Clone()

    $manualBytes = if ($ManualPatched) {
        Convert-HexBytes '90 90 90 90 90'
    }
    else {
        Convert-HexBytes 'E8 92 23 00 00'
    }
    Copy-Bytes $manualBytes $result 0x1F9A19

    $guardHook = if ($GuardPatched) {
        Convert-HexBytes 'E9 25 8B 34 00 90'
    }
    else {
        Convert-HexBytes '8B 0D A0 60 57 01'
    }
    $guardCave = [byte[]]::new(112)
    if ($GuardPatched) {
        $guardCode = Convert-HexBytes @'
83 3D 4C 5F 57 01 02 75 12
A1 A0 60 57 01 85 C0 74 14
A1 8C 60 57 01 85 C0 74 0B
8B 0D A0 60 57 01 E9 B6 74 CB FF
BF 02 00 00 00 C6 05 66 5C 57 01 01
89 3D 50 5F 57 01 E9 CD 74 CB FF
'@
        Copy-Bytes $guardCode $guardCave 0
    }
    Copy-Bytes $guardHook $result 0x1F58B6
    Copy-Bytes $guardCave $result 0x53E3E0

    if ($OctagramPatched) {
        $visualHook = Convert-HexBytes 'E8 4B CE 29 00 90 90 90 90'
        $selector = Convert-HexBytes @'
8B4C24188A59088A51098854241A80FB10750980FA5A7204B35AEB1480FB0C72
0D80FB0E7204B314EB06B308EB0230DB8A883C0600008A9068060000884C2413
88542418E8F702B6FFC3
'@
        $scaler = Convert-HexBytes @'
9C600FB644243EBA0000803F83F81E7219BA0000A03F83F83C720FBA0000C03F
83F85A7205BA000000408BB72C06000085F67408525252E80480D4FF619D8B4E
1C51E869E8D5FFC3
'@
        $visualCave = [byte[]]::new(208)
        Copy-Bytes $selector $visualCave 0
        Copy-Bytes $scaler $visualCave 0x50
    }
    else {
        $visualHook = Convert-HexBytes 'E8 3B CE 29 00 90 90 90 90'
        $visualCave = Convert-HexBytes @'
8B4C24188A59088A51098854241A8A883C0600008A9068060000884C24138854
241880FB0C720D80FB0E7204B314EB06B308EB0230DBE80503B6FFC300000000
9C600FB644243EBA0000803F83F81E7219BA0000A03F83F83C720FBA0000C03F
83F85A7205BA000000408BB72C06000085F67408525252E81480D4FF619D8B4E
1C51E879E8D5FFC3000000000000000000000000000000000000000000000000
0000000000000000000000000000000000000000000000000000000000000000
00000000000000000000000000000000
'@
    }
    if ($visualCave.Length -ne 208) {
        throw 'Internal realm-composite visual fixture length is invalid.'
    }
    Copy-Bytes $visualHook $result 0x2A1780
    Copy-Bytes $visualCave $result 0x53E580

    return ,$result
}
