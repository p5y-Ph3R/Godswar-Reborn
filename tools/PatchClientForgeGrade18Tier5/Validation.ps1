function Assert-BinaryContext([byte[]]$Bytes, [hashtable]$Site) {
    if ($Site.Offset -lt $Site.Prefix.Count -or
        $Site.Offset + $Site.Suffix.Count -ge $Bytes.Count) {
        throw "Origin.exe site '$($Site.Name)' is outside the file."
    }

    for ($index = 0; $index -lt $Site.Prefix.Count; $index++) {
        if ($Bytes[$Site.Offset - $Site.Prefix.Count + $index] -ne $Site.Prefix[$index]) {
            throw "Origin.exe prefix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    for ($index = 0; $index -lt $Site.Suffix.Count; $index++) {
        if ($Bytes[$Site.Offset + 1 + $index] -ne $Site.Suffix[$index]) {
            throw "Origin.exe suffix mismatch at $($Site.Name) (0x$('{0:X}' -f $Site.Offset))."
        }
    }
    if ($Site.Allowed -notcontains $Bytes[$Site.Offset]) {
        throw "Origin.exe byte mismatch at $($Site.Name): got 0x$('{0:X2}' -f $Bytes[$Site.Offset])."
    }
}
