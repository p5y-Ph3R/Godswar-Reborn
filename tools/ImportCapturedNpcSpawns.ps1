param(
    [string]$Container = "godswar-postgres",
    [string]$Database = "godswar",
    [string]$User = "godswar",
    [Nullable[int]]$MonsterMapId = $null,
    [string]$CaptureSessionId = ""
)

if ($null -ne $MonsterMapId -and
    ($MonsterMapId -lt [int16]::MinValue -or $MonsterMapId -gt [int16]::MaxValue)) {
    throw "MonsterMapId must fit a PostgreSQL smallint."
}

$normalizedCaptureSessionId = $null
if (![string]::IsNullOrWhiteSpace($CaptureSessionId)) {
    try {
        $normalizedCaptureSessionId = ([guid]::Parse($CaptureSessionId)).ToString()
    } catch {
        throw "CaptureSessionId must be a valid UUID."
    }
}

$transactionScopeSql = if ($null -eq $normalizedCaptureSessionId) {
    ""
} else {
    "AND capture_session_id = '$normalizedCaptureSessionId'::uuid"
}

function Test-FiniteSingle([single]$Value) {
    return ![single]::IsNaN($Value) -and ![single]::IsInfinity($Value)
}

function Test-ReservedPlayerObjectId([uint32]$ObjectId) {
    return $ObjectId -eq 0x1448 -or ($ObjectId -ge 0x6000 -and $ObjectId -le 0x7FFF)
}

$rows = docker exec $Container psql -U $User -d $Database -At -F "|" -c @"
SELECT encode(clear_bytes, 'hex')
FROM packet_transactions
WHERE upper(connection_name) = 'GAME'
  AND direction = 'S2C'
  AND opcode = 10020
  $transactionScopeSql
ORDER BY id;
"@

$culture = [System.Globalization.CultureInfo]::InvariantCulture
$sql = New-Object System.Text.StringBuilder
$count = 0

foreach ($row in $rows) {
    if ([string]::IsNullOrWhiteSpace($row)) {
        continue
    }

    $hex = $row.Trim()
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }

    if ($bytes.Length -lt 108) {
        continue
    }

    $length = [BitConverter]::ToUInt16($bytes, 0)
    if ($length -lt 108 -or $length -ne $bytes.Length) {
        continue
    }

    $nameBytes = New-Object System.Collections.Generic.List[byte]
    for ($i = 44; $i -lt $length; $i++) {
        if ($bytes[$i] -eq 0) {
            break
        }

        $nameBytes.Add($bytes[$i])
    }

    $templateKey = [Text.Encoding]::ASCII.GetString($nameBytes.ToArray())
    if ($templateKey.StartsWith("Sparta_")) {
        $mapId = 0
        $sceneKey = "Sparta"
    } elseif ($templateKey.StartsWith("Athens_")) {
        $mapId = 1
        $sceneKey = "Athens"
    } else {
        continue
    }

    $secondUnderscore = $templateKey.IndexOf("_", 7)
    if ($secondUnderscore -lt 0) {
        continue
    }

    $npcKey = $templateKey.Substring(0, $secondUnderscore)
    $objectId = [BitConverter]::ToUInt32($bytes, 8)
    $rawX = [BitConverter]::ToSingle($bytes, 28)
    $rawZ = [BitConverter]::ToSingle($bytes, 36)
    if ($objectId -eq 0 -or
        (Test-ReservedPlayerObjectId $objectId) -or
        !(Test-FiniteSingle $rawX) -or
        !(Test-FiniteSingle $rawZ)) {
        continue
    }

    $x = $rawX.ToString($culture)
    $z = $rawZ.ToString($culture)

    [void]$sql.AppendLine(@"
INSERT INTO npc_spawn_packets (map_id, scene_key, npc_key, template_key, object_id, pos_x, pos_z, clear_bytes, source, first_seen_at, last_seen_at, capture_count)
VALUES ($mapId, '$sceneKey', '$npcKey', '$templateKey', $objectId, $x, $z, decode('$hex', 'hex'), 'manual_import', now(), now(), 1)
ON CONFLICT (map_id, template_key) DO UPDATE
SET object_id = EXCLUDED.object_id,
    pos_x = EXCLUDED.pos_x,
    pos_z = EXCLUDED.pos_z,
    clear_bytes = EXCLUDED.clear_bytes,
    source = EXCLUDED.source,
    last_seen_at = EXCLUDED.last_seen_at;
"@)
    $count++
}

if ($count -eq 0) {
    Write-Host "No Athens/Sparta NPC spawn packets found in packet_transactions."
} else {
    $sql.ToString() | docker exec -i $Container psql -U $User -d $Database
}

$detailRows = docker exec $Container psql -U $User -d $Database -At -F "|" -c @"
SELECT opcode, encode(clear_bytes, 'hex')
FROM packet_transactions
WHERE upper(connection_name) = 'GAME'
  AND direction = 'S2C'
  AND opcode IN (10077, 10080)
  $transactionScopeSql
ORDER BY id;
"@

$detailSql = New-Object System.Text.StringBuilder
$detailCount = 0
foreach ($row in $detailRows) {
    if ([string]::IsNullOrWhiteSpace($row)) {
        continue
    }

    $parts = $row.Split("|", 2)
    if ($parts.Length -ne 2) {
        continue
    }

    $opcode = [int]$parts[0]
    $hex = $parts[1].Trim()
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }

    if ($bytes.Length -lt 8) {
        continue
    }

    $objectId = [BitConverter]::ToUInt32($bytes, 4)
    if ($opcode -eq 10077) {
        $column = "detail_10077"
    } elseif ($opcode -eq 10080) {
        $column = "detail_10080"
    } else {
        continue
    }

    [void]$detailSql.AppendLine("UPDATE npc_spawn_packets SET $column = decode('$hex', 'hex'), last_seen_at = now() WHERE object_id = $objectId;")
    $detailCount++
}

if ($detailCount -gt 0) {
    $detailSql.ToString() | docker exec -i $Container psql -U $User -d $Database
}

$templateRows = docker exec $Container psql -U $User -d $Database -At -F "|" -c @"
SELECT template_key, source_map_id, scene_key, display_name
FROM monster_templates
WHERE source_map_id IS NOT NULL
ORDER BY template_key, source_map_id, source_key;
"@

$monsterTemplates = @{}
foreach ($row in $templateRows) {
    if ([string]::IsNullOrWhiteSpace($row)) {
        continue
    }

    $parts = $row.Split("|", 4)
    if ($parts.Length -ne 4) {
        continue
    }

    if (!$monsterTemplates.ContainsKey($parts[0])) {
        $monsterTemplates[$parts[0]] = New-Object System.Collections.Generic.List[object]
    }

    $monsterTemplates[$parts[0]].Add([pscustomobject]@{
        MapId = [int]$parts[1]
        SceneKey = $parts[2]
        DisplayName = $parts[3]
    })
}

$monsterSql = New-Object System.Text.StringBuilder
$monsterCount = 0
$missingMonsterScopeCount = 0
$unresolvedMonsterCount = 0

foreach ($row in $rows) {
    if ([string]::IsNullOrWhiteSpace($row)) {
        continue
    }

    $hex = $row.Trim()
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }

    if ($bytes.Length -lt 108) {
        continue
    }

    $length = [BitConverter]::ToUInt16($bytes, 0)
    if ($length -lt 108 -or $length -ne $bytes.Length) {
        continue
    }

    $objectType = [BitConverter]::ToUInt32($bytes, 4)
    if (($objectType -band 0xFF) -ne 0x12) {
        continue
    }

    $nameBytes = New-Object System.Collections.Generic.List[byte]
    for ($i = 44; $i -lt $length; $i++) {
        if ($bytes[$i] -eq 0) {
            break
        }

        $nameBytes.Add($bytes[$i])
    }

    $templateKey = [Text.Encoding]::ASCII.GetString($nameBytes.ToArray())
    if ($templateKey.StartsWith("Sparta_") -or $templateKey.StartsWith("Athens_")) {
        continue
    }

    if (!$monsterTemplates.ContainsKey($templateKey)) {
        continue
    }

    if ($null -eq $MonsterMapId -or $null -eq $normalizedCaptureSessionId) {
        $missingMonsterScopeCount++
        continue
    }

    $candidates = @($monsterTemplates[$templateKey])
    $matchingCandidates = @($candidates | Where-Object { $_.MapId -eq $MonsterMapId })
    if ($matchingCandidates.Count -eq 0) {
        $unresolvedMonsterCount++
        continue
    }

    $template = $matchingCandidates[0]
    $objectId = [BitConverter]::ToUInt32($bytes, 8)
    $rawX = [BitConverter]::ToSingle($bytes, 28)
    $rawZ = [BitConverter]::ToSingle($bytes, 36)
    if ($objectId -eq 0 -or
        (Test-ReservedPlayerObjectId $objectId) -or
        !(Test-FiniteSingle $rawX) -or
        !(Test-FiniteSingle $rawZ)) {
        continue
    }

    $x = $rawX.ToString($culture)
    $z = $rawZ.ToString($culture)
    $sceneKey = $template.SceneKey.Replace("'", "''")
    $displayName = $template.DisplayName.Replace("'", "''")
    $escapedTemplateKey = $templateKey.Replace("'", "''")

    [void]$monsterSql.AppendLine(@"
INSERT INTO monster_spawn_packets (map_id, scene_key, template_key, display_name, object_id, pos_x, pos_z, clear_bytes, source, first_seen_at, last_seen_at, capture_count)
VALUES ($($template.MapId), '$sceneKey', '$escapedTemplateKey', '$displayName', $objectId, $x, $z, decode('$hex', 'hex'), 'manual_import', now(), now(), 1)
ON CONFLICT (map_id, object_id) DO UPDATE
SET template_key = EXCLUDED.template_key,
    display_name = EXCLUDED.display_name,
    pos_x = EXCLUDED.pos_x,
    pos_z = EXCLUDED.pos_z,
    clear_bytes = EXCLUDED.clear_bytes,
    source = EXCLUDED.source,
    last_seen_at = EXCLUDED.last_seen_at;
"@)
    $monsterCount++
}

if ($monsterCount -gt 0) {
    $monsterSql.ToString() | docker exec -i $Container psql -U $User -d $Database
}

Write-Host "Imported $count Athens/Sparta NPC spawn packets, $detailCount NPC detail packets, and $monsterCount monster spawn packets. Skipped $missingMonsterScopeCount monster packets without an explicit map/session scope and $unresolvedMonsterCount packets unavailable on the requested map."
if ($missingMonsterScopeCount -gt 0) {
    Write-Host "Re-run with -MonsterMapId <map> -CaptureSessionId <uuid> to import monster spawn packets without mixing capture sessions."
}
