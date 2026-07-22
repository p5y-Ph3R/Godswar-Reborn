param(
    [string]$ClientRoot = 'C:\Godswar Origin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$target = Join-Path $ClientRoot 'Localization\en_us\Text\NPCDescription.dat'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "English NPC description table not found: $target"
}

[byte[]]$data = [IO.File]::ReadAllBytes($target)
[byte[]]$before = [Text.Encoding]::Unicode.GetBytes(
    "SYS_NPC_4`t|cffFFFF00Salary|cffffffff")
[byte[]]$after = [Text.Encoding]::Unicode.GetBytes(
    "SYS_NPC_4`t|cffFFFF00Gear Enhancement|cffffffff")

function Find-ByteSequence([byte[]]$Haystack, [byte[]]$Needle) {
    $matches = [Collections.Generic.List[int]]::new()
    for ($offset = 0; $offset -le $Haystack.Length - $Needle.Length; $offset++) {
        $matched = $true
        for ($index = 0; $index -lt $Needle.Length; $index++) {
            if ($Haystack[$offset + $index] -ne $Needle[$index]) {
                $matched = $false
                break
            }
        }
        if ($matched) {
            $matches.Add($offset)
        }
    }
    return $matches.ToArray()
}

$alreadyApplied = @(Find-ByteSequence $data $after)
if ($alreadyApplied.Count -eq 1) {
    Write-Output "Gear Mentor localization is already corrected: $target"
    exit 0
}
if ($alreadyApplied.Count -ne 0) {
    throw "Corrected Gear Mentor label occurred $($alreadyApplied.Count) times; expected zero or one."
}

$matches = @(Find-ByteSequence $data $before)
if ($matches.Count -ne 1) {
    throw "Original Gear Mentor label occurred $($matches.Count) times; expected exactly one."
}

$offset = $matches[0]
[byte[]]$updated = [byte[]]::new($data.Length - $before.Length + $after.Length)
[Array]::Copy($data, 0, $updated, 0, $offset)
[Array]::Copy($after, 0, $updated, $offset, $after.Length)
$suffixOffset = $offset + $before.Length
[Array]::Copy(
    $data,
    $suffixOffset,
    $updated,
    $offset + $after.Length,
    $data.Length - $suffixOffset)
[IO.File]::WriteAllBytes($target, $updated)

Write-Output "Corrected SYS_NPC_4 from Salary to Gear Enhancement: $target"
