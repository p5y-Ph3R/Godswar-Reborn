function Get-PetOwnerMergeOctagramXmlState([byte[]]$Data) {
    $oldHash = 'E55050B49BB5DBED6F6A4A8D2BBB78237177A6FDA065155522034462C479748C'
    $newHash = 'A6BBB855D8DC1092B867A9DED096C42348C991D847AB0EBB93C3127D9A8A96BE'
    $hash = Get-PetOwnerMergeOctagramSha256 $Data
    if ($hash -ne $oldHash -and $hash -ne $newHash) {
        throw "Unsupported Pet.xml SHA-256/state: $hash"
    }
    if ($Data.Length -lt 3 -or -not (Test-Bytes $Data 0 (
            Convert-HexBytes 'EF BB BF'))) {
        throw 'Pet.xml is not the audited UTF-8 BOM document.'
    }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($Data, 3, $Data.Length - 3)
    $profileCount = [regex]::Matches(
        $text, '(?m)^    <Pet\d+_[01]>\r?$').Count
    $rowCounts = @{}
    foreach ($samsara in @(0, 8, 20, 90)) {
        $rowCounts[$samsara] = [regex]::Matches(
            $text,
            '(?m)^        <PetModel Samsara="' + $samsara +
                '"[^\r\n]*/>\r?$').Count
    }
    $octagramReferences = [regex]::Matches(
        $text,
        'unitefile="\\\\Characters\\\\PetUniteEffect\\\\' +
            'e_he_0004_all\.gwm"').Count
    $octagram = $hash -eq $newHash
    if ($profileCount -ne 90 -or $rowCounts[0] -ne 90 -or
        $rowCounts[8] -ne 90 -or $rowCounts[20] -ne 90 -or
        $rowCounts[90] -ne $(if ($octagram) { 90 } else { 0 }) -or
        $octagramReferences -ne $(if ($octagram) { 90 } else { 0 })) {
        throw 'Pet.xml owner-Merge octagram row shape is invalid.'
    }
    return [pscustomobject]@{
        Hash = $hash
        Octagram = $octagram
        Text = $text
        Utf8 = $utf8
        OldHash = $oldHash
        NewHash = $newHash
    }
}

function Convert-PetOwnerMergeOctagramXml(
    [byte[]]$Data,
    [bool]$TargetOctagram
) {
    $state = Get-PetOwnerMergeOctagramXmlState $Data
    if ($state.Octagram -eq $TargetOctagram) { return ,([byte[]]$Data.Clone()) }

    $converted = if ($TargetOctagram) {
        $pattern = [regex]::new(
            '(?m)^(?<row>        <PetModel Samsara="20"[^\r\n]*/>)' +
            '(?=\r?$)')
        $counter = [pscustomobject]@{ Count = 0 }
        $result = $pattern.Replace(
            $state.Text,
            [Text.RegularExpressions.MatchEvaluator]{
                param($match)
                $counter.Count++
                $row = $match.Groups['row'].Value
                if ($row -notmatch 'e_he_0003_all\.gwm') {
                    throw 'Samsara 20 row lost effect 0003.'
                }
                $clone = $row.Replace(
                    'Samsara="20"', 'Samsara="90"').Replace(
                    'e_he_0003_all.gwm', 'e_he_0004_all.gwm')
                return $row + "`r`n" + $clone
            })
        if ($counter.Count -ne 90) {
            throw "Pet.xml expected 90 Samsara 20 rows; found $($counter.Count)."
        }
        $result
    }
    else {
        $pattern = [regex]::new(
            '(?m)^(?<row>        <PetModel Samsara="20"[^\r\n]*/>)\r\n' +
            '(?<clone>        <PetModel Samsara="90"[^\r\n]*/>)' +
            '(?=\r?$)')
        $counter = [pscustomobject]@{ Count = 0 }
        $result = $pattern.Replace(
            $state.Text,
            [Text.RegularExpressions.MatchEvaluator]{
                param($match)
                $row = $match.Groups['row'].Value
                $expectedClone = $row.Replace(
                    'Samsara="20"', 'Samsara="90"').Replace(
                    'e_he_0003_all.gwm', 'e_he_0004_all.gwm')
                if ($match.Groups['clone'].Value -cne $expectedClone) {
                    throw 'Samsara 90 row is not an exact managed clone.'
                }
                $counter.Count++
                return $row
            })
        if ($counter.Count -ne 90) {
            throw "Pet.xml expected 90 managed Samsara 90 rows; found $($counter.Count)."
        }
        $result
    }

    [byte[]]$body = $state.Utf8.GetBytes($converted)
    [byte[]]$output = [byte[]]::new($body.Length + 3)
    Copy-Bytes (Convert-HexBytes 'EF BB BF') $output 0
    Copy-Bytes $body $output 3
    $expectedHash = if ($TargetOctagram) { $state.NewHash } else { $state.OldHash }
    if ((Get-PetOwnerMergeOctagramSha256 $output) -ne $expectedHash) {
        throw 'Generated Pet.xml failed exact owner-Merge octagram validation.'
    }
    [void](Get-PetOwnerMergeOctagramXmlState $output)
    return ,$output
}
