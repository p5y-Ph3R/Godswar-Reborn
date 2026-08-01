param(
    [string]$Container = 'godswar-postgres',
    [string]$Database = 'godswar',
    [string]$User = 'godswar',
    [string]$OutputPath =
        'src/Godswar.Server/Infrastructure/WorldContent/Baselines/MonsterContentBaseline.v1.gz'
)

$ErrorActionPreference = 'Stop'

$query = @'
SELECT json_build_object(
    'mapId', map_id,
    'sceneKey', scene_key,
    'templateKey', template_key,
    'displayName', display_name,
    'objectId', object_id,
    'x', pos_x,
    'z', pos_z,
    'packet', encode(clear_bytes, 'base64')
)::text
FROM monster_spawn_packets
WHERE object_id BETWEEN 1 AND 4294967295
ORDER BY map_id, object_id, template_key;
'@

$lines = @(& docker exec $Container psql -X -q -A -t `
    -v ON_ERROR_STOP=1 -U $User -d $Database -c $query)
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to export the reviewed monster candidate.'
}

$rows = @($lines |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { $_ | ConvertFrom-Json })
if ($rows.Count -eq 0 -or $rows.Count -gt 100000) {
    throw "Monster candidate count $($rows.Count) is invalid."
}

$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
function Write-Bytes {
    param(
        [IO.BinaryWriter]$Writer,
        [byte[]]$Value
    )
    $Writer.Write([int]$Value.Length)
    $Writer.Write($Value)
}
function Write-String {
    param(
        [IO.BinaryWriter]$Writer,
        [string]$Value
    )
    Write-Bytes $Writer $strictUtf8.GetBytes($Value)
}

$raw = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($raw, $strictUtf8, $true)
$writer.Write($strictUtf8.GetBytes('GWMONB01'))
$writer.Write([int]$rows.Count)
foreach ($row in $rows) {
    $packet = [Convert]::FromBase64String(
        ([string]$row.packet).Replace("`r", '').Replace("`n", ''))
    $writer.Write([int16]$row.mapId)
    Write-String $writer ([string]$row.sceneKey)
    Write-String $writer ([string]$row.templateKey)
    Write-String $writer ([string]$row.displayName)
    $writer.Write([uint32]$row.objectId)
    $writer.Write([single]$row.x)
    $writer.Write([single]$row.z)
    Write-Bytes $writer $packet
}
$writer.Dispose()

$compressed = [IO.MemoryStream]::new()
$gzip = [IO.Compression.GZipStream]::new(
    $compressed,
    [IO.Compression.CompressionLevel]::Optimal,
    $true)
$raw.Position = 0
$raw.CopyTo($gzip)
$gzip.Dispose()
$artifact = $compressed.ToArray()

$canonical = [IO.MemoryStream]::new()
$hashWriter = [IO.BinaryWriter]::new($canonical, $strictUtf8, $true)
$hashWriter.Write([int]1)
Write-String $hashWriter 'monsters'
foreach ($row in $rows) {
    $packet = [Convert]::FromBase64String(
        ([string]$row.packet).Replace("`r", '').Replace("`n", ''))
    $hashWriter.Write([int16]$row.mapId)
    Write-String $hashWriter ([string]$row.sceneKey)
    Write-String $hashWriter ([string]$row.templateKey)
    Write-String $hashWriter ([string]$row.displayName)
    $hashWriter.Write([uint32]$row.objectId)
    $hashWriter.Write([single]$row.x)
    $hashWriter.Write([single]$row.z)
    Write-Bytes $hashWriter $packet
}
$hashWriter.Dispose()

$resolved = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) |
    Out-Null
[IO.File]::WriteAllBytes($resolved, $artifact)

$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $revision = [BitConverter]::ToString(
        $sha256.ComputeHash($canonical.ToArray())).Replace('-', '')
    $artifactHash = [BitConverter]::ToString(
        $sha256.ComputeHash($artifact)).Replace('-', '')
}
finally {
    $sha256.Dispose()
}

[pscustomobject]@{
    OutputPath = $resolved
    EntryCount = $rows.Count
    Revision = $revision
    ArtifactSha256 = $artifactHash
    CompressedBytes = $artifact.Length
}
