param(
    [string]$EquipForgePath = 'C:\Godswar Origin\Localization\en_us\Settings\Sys\EquipForge.xml',
    [string]$BijouForgePath = 'C:\Godswar Origin\Localization\en_us\Settings\Sys\BijouForge.xml',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Godswar.Server\State\EquipmentForgeCatalog.Generated.cs'),
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ElementChildren {
    param([System.Xml.XmlElement]$Root)

    return @($Root.ChildNodes | Where-Object { $_ -is [System.Xml.XmlElement] })
}

function Get-RequiredInt {
    param(
        [System.Xml.XmlElement]$Node,
        [string]$Attribute
    )

    if (-not $Node.HasAttribute($Attribute)) {
        throw "Element '$($Node.Name)' is missing required attribute '$Attribute'."
    }

    $value = 0
    if (-not [int]::TryParse(
            $Node.GetAttribute($Attribute),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$value)) {
        throw "Element '$($Node.Name)' has invalid integer '$Attribute'."
    }

    return $value
}

function Get-OptionalInt {
    param(
        [System.Xml.XmlElement]$Node,
        [string]$Attribute
    )

    if (-not $Node.HasAttribute($Attribute)) {
        return $null
    }

    return Get-RequiredInt $Node $Attribute
}

function Get-IntVector {
    param(
        [System.Xml.XmlElement]$Node,
        [string]$Attribute,
        [switch]$Optional
    )

    if (-not $Node.HasAttribute($Attribute)) {
        if ($Optional) {
            return ,([int[]]@())
        }

        throw "Element '$($Node.Name)' is missing required attribute '$Attribute'."
    }

    $values = [System.Collections.Generic.List[int]]::new()
    foreach ($part in $Node.GetAttribute($Attribute).Split(',')) {
        $trimmed = $part.Trim()
        if ($trimmed.Length -eq 0) {
            throw "Element '$($Node.Name)' has an empty value in '$Attribute'."
        }

        $value = 0
        if (-not [int]::TryParse(
                $trimmed,
                [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$value)) {
            throw "Element '$($Node.Name)' has invalid integer vector '$Attribute'."
        }

        $values.Add($value)
    }

    return ,$values.ToArray()
}

function Format-NullableUInt {
    param([AllowNull()][Nullable[int]]$Value)

    if ($null -eq $Value) {
        return 'null'
    }

    return "$([int]$Value)u"
}

function Format-NullableInt {
    param([AllowNull()][Nullable[int]]$Value)

    if ($null -eq $Value) {
        return 'null'
    }

    return ([int]$Value).ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Add-Vector {
    param(
        [System.Collections.Generic.List[object]]$Vectors,
        [System.Collections.Generic.Dictionary[string, int]]$Indices,
        [int[]]$Values
    )

    $key = [string]::Join(',', $Values)
    $index = 0
    if ($Indices.TryGetValue($key, [ref]$index)) {
        return $index
    }

    $index = $Vectors.Count
    $Vectors.Add($Values)
    $Indices.Add($key, $index)
    return $index
}

function New-VectorTable {
    return [pscustomobject]@{
        Values = [System.Collections.Generic.List[object]]::new()
        Indices = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
    }
}

function Write-VectorTable {
    param(
        [System.Text.StringBuilder]$Builder,
        [string]$Name,
        [System.Collections.Generic.List[object]]$Vectors
    )

    [void]$Builder.AppendLine("    private static readonly int[][] $Name =")
    [void]$Builder.AppendLine('    [')
    foreach ($vector in $Vectors) {
        $contents = [string]::Join(', ', [int[]]$vector)
        [void]$Builder.AppendLine("        [$contents],")
    }
    [void]$Builder.AppendLine('    ];')
    [void]$Builder.AppendLine()
}

if (-not (Test-Path -LiteralPath $EquipForgePath -PathType Leaf)) {
    throw "EquipForge source was not found: $EquipForgePath"
}
if (-not (Test-Path -LiteralPath $BijouForgePath -PathType Leaf)) {
    throw "BijouForge source was not found: $BijouForgePath"
}

[xml]$equipDocument = Get-Content -Raw -LiteralPath $EquipForgePath
[xml]$bijouDocument = Get-Content -Raw -LiteralPath $BijouForgePath
$equipNodes = Get-ElementChildren $equipDocument.DocumentElement
$bijouNodes = Get-ElementChildren $bijouDocument.DocumentElement

if ($equipNodes.Count -ne 611) {
    throw "Expected 611 EquipForge rules, found $($equipNodes.Count)."
}
if (($equipNodes | ForEach-Object { Get-RequiredInt $_ 'ID' } | Sort-Object -Unique).Count -ne $equipNodes.Count) {
    throw 'EquipForge contains duplicate IDs.'
}
if (($bijouNodes | ForEach-Object { Get-RequiredInt $_ 'ID' } | Sort-Object -Unique).Count -ne $bijouNodes.Count) {
    throw 'BijouForge contains duplicate IDs.'
}

$baseProyAdd = New-VectorTable
$appendProyAdd = New-VectorTable
$bmoney = New-VectorTable
$cmoney = New-VectorTable
$round = New-VectorTable
$equipmentRows = [System.Collections.Generic.List[object]]::new()
$materialRows = [System.Collections.Generic.List[object]]::new()

foreach ($node in $equipNodes) {
    $equipmentRows.Add([pscustomobject]@{
        ID = Get-RequiredInt $node 'ID'
        NextID = Get-OptionalInt $node 'NextID'
        BadID = Get-OptionalInt $node 'BadID'
        Probability = Get-OptionalInt $node 'Probability'
        Amoney = Get-RequiredInt $node 'Amoney'
        BaseIndex = Add-Vector $baseProyAdd.Values $baseProyAdd.Indices (Get-IntVector $node 'BaseProyAdd')
        AppendIndex = Add-Vector $appendProyAdd.Values $appendProyAdd.Indices (Get-IntVector $node 'AppendProyAdd')
        BmoneyIndex = Add-Vector $bmoney.Values $bmoney.Indices (Get-IntVector $node 'Bmoney')
        CmoneyIndex = Add-Vector $cmoney.Values $cmoney.Indices (Get-IntVector $node 'Cmoney')
    })
}

foreach ($node in $bijouNodes) {
    $materialRows.Add([pscustomobject]@{
        ID = Get-RequiredInt $node 'ID'
        NextID = Get-OptionalInt $node 'NextID'
        MaterialType = Get-RequiredInt $node 'MaterialType'
        MaterialProyAdd = Get-OptionalInt $node 'MaterialProyAdd'
        RoundIndex = Add-Vector $round.Values $round.Indices (Get-IntVector $node 'Round' -Optional)
    })
}

$equipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $EquipForgePath).Hash
$bijouHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $BijouForgePath).Hash
$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('// <auto-generated>')
[void]$builder.AppendLine('// Generated by tools/GenerateEquipmentForgeCatalog.ps1.')
[void]$builder.AppendLine("// EquipForge.xml SHA256: $equipHash")
[void]$builder.AppendLine("// BijouForge.xml SHA256: $bijouHash")
[void]$builder.AppendLine('// </auto-generated>')
[void]$builder.AppendLine()
[void]$builder.AppendLine('namespace Godswar.Server.State;')
[void]$builder.AppendLine()
[void]$builder.AppendLine('internal sealed record EquipmentForgeRule(')
[void]$builder.AppendLine('    uint ItemId,')
[void]$builder.AppendLine('    uint? NextItemId,')
[void]$builder.AppendLine('    uint? BadItemId,')
[void]$builder.AppendLine('    int? Probability,')
[void]$builder.AppendLine('    int Amoney,')
[void]$builder.AppendLine('    IReadOnlyList<int> BaseProyAdd,')
[void]$builder.AppendLine('    IReadOnlyList<int> AppendProyAdd,')
[void]$builder.AppendLine('    IReadOnlyList<int> Bmoney,')
[void]$builder.AppendLine('    IReadOnlyList<int> Cmoney);')
[void]$builder.AppendLine()
[void]$builder.AppendLine('internal static class EquipmentForgeCatalog')
[void]$builder.AppendLine('{')
[void]$builder.AppendLine('    public const int ShippedRuleCount = 611;')
[void]$builder.AppendLine()
Write-VectorTable $builder 'BaseProyAddVectors' $baseProyAdd.Values
Write-VectorTable $builder 'AppendProyAddVectors' $appendProyAdd.Values
Write-VectorTable $builder 'BmoneyVectors' $bmoney.Values
Write-VectorTable $builder 'CmoneyVectors' $cmoney.Values
[void]$builder.AppendLine('    public static IReadOnlyList<EquipmentForgeRule> All { get; } =')
[void]$builder.AppendLine('    [')
foreach ($row in $equipmentRows) {
    $nextID = Format-NullableUInt $row.NextID
    $badID = Format-NullableUInt $row.BadID
    $probability = Format-NullableInt $row.Probability
    [void]$builder.AppendLine(
        "        new($($row.ID)u, $nextID, $badID, $probability, $($row.Amoney), BaseProyAddVectors[$($row.BaseIndex)], AppendProyAddVectors[$($row.AppendIndex)], BmoneyVectors[$($row.BmoneyIndex)], CmoneyVectors[$($row.CmoneyIndex)]),")
}
[void]$builder.AppendLine('    ];')
[void]$builder.AppendLine()
[void]$builder.AppendLine('    private static readonly IReadOnlyDictionary<uint, EquipmentForgeRule> ByItemId =')
[void]$builder.AppendLine('        All.ToDictionary(rule => rule.ItemId);')
[void]$builder.AppendLine()
[void]$builder.AppendLine('    public static bool TryGet(uint itemId, out EquipmentForgeRule rule)')
[void]$builder.AppendLine('    {')
[void]$builder.AppendLine('        return ByItemId.TryGetValue(itemId, out rule!);')
[void]$builder.AppendLine('    }')
[void]$builder.AppendLine('}')
[void]$builder.AppendLine()
[void]$builder.AppendLine('internal sealed record ForgingMaterialRule(')
[void]$builder.AppendLine('    uint ItemId,')
[void]$builder.AppendLine('    uint? NextItemId,')
[void]$builder.AppendLine('    int MaterialType,')
[void]$builder.AppendLine('    int? MaterialProyAdd,')
[void]$builder.AppendLine('    IReadOnlyList<int> Round)')
[void]$builder.AppendLine('{')
[void]$builder.AppendLine('    public int ProbabilityBonus => MaterialProyAdd ?? 0;')
[void]$builder.AppendLine()
[void]$builder.AppendLine('    public bool AllowsRound(int round)')
[void]$builder.AppendLine('    {')
[void]$builder.AppendLine('        if (Round.Count == 0)')
[void]$builder.AppendLine('        {')
[void]$builder.AppendLine('            return true;')
[void]$builder.AppendLine('        }')
[void]$builder.AppendLine()
[void]$builder.AppendLine('        if (Round.Count == 2 && Round[0] <= Round[1])')
[void]$builder.AppendLine('        {')
[void]$builder.AppendLine('            return round >= Round[0] && round <= Round[1];')
[void]$builder.AppendLine('        }')
[void]$builder.AppendLine()
[void]$builder.AppendLine('        return Round.Contains(round);')
[void]$builder.AppendLine('    }')
[void]$builder.AppendLine('}')
[void]$builder.AppendLine()
[void]$builder.AppendLine('internal static class ForgingMaterialRuleCatalog')
[void]$builder.AppendLine('{')
[void]$builder.AppendLine("    public const int ShippedRuleCount = $($materialRows.Count);")
[void]$builder.AppendLine()
Write-VectorTable $builder 'RoundVectors' $round.Values
[void]$builder.AppendLine('    public static IReadOnlyList<ForgingMaterialRule> All { get; } =')
[void]$builder.AppendLine('    [')
foreach ($row in $materialRows) {
    $nextID = Format-NullableUInt $row.NextID
    $materialProyAdd = Format-NullableInt $row.MaterialProyAdd
    [void]$builder.AppendLine(
        "        new($($row.ID)u, $nextID, $($row.MaterialType), $materialProyAdd, RoundVectors[$($row.RoundIndex)]),")
}
[void]$builder.AppendLine('    ];')
[void]$builder.AppendLine()
[void]$builder.AppendLine('    private static readonly IReadOnlyDictionary<uint, ForgingMaterialRule> ByItemId =')
[void]$builder.AppendLine('        All.ToDictionary(rule => rule.ItemId);')
[void]$builder.AppendLine()
[void]$builder.AppendLine('    public static bool TryGet(uint itemId, out ForgingMaterialRule rule)')
[void]$builder.AppendLine('    {')
[void]$builder.AppendLine('        return ByItemId.TryGetValue(itemId, out rule!);')
[void]$builder.AppendLine('    }')
[void]$builder.AppendLine('}')

$generated = $builder.ToString().Replace("`r`n", "`n")
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
if ($Check) {
    if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Leaf)) {
        throw "Generated catalog is missing: $resolvedOutput"
    }

    $current = [System.IO.File]::ReadAllText($resolvedOutput).Replace("`r`n", "`n")
    if (-not [string]::Equals($current, $generated, [System.StringComparison]::Ordinal)) {
        throw "Generated catalog is stale: $resolvedOutput"
    }

    Write-Host "Equipment forge catalog is current ($($equipmentRows.Count) equipment, $($materialRows.Count) material rules)."
    return
}

$outputDirectory = Split-Path -Parent $resolvedOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[System.IO.File]::WriteAllText(
    $resolvedOutput,
    $generated,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $resolvedOutput ($($equipmentRows.Count) equipment, $($materialRows.Count) material rules)."
