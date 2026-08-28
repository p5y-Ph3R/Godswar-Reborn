[CmdletBinding()]
param(
    [string]$ClientPath = 'C:\Godswar Origin',
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$relativePaths = @(
    'Localization\en_us\UI\XML\NpcFun\NpcFunRepetition.lua',
    'Localization\zh_cn\UI\XML\NpcFun\NpcFunRepetition.lua'
)
$anchor = "`t`telseif SubID ==206 then"
$branch = @"
		elseif SubID== 207 then
			local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText("|cffFFFF00*Medusa Island (Mythic)|cFFFFFFFF");
			Button:Visible(true);
			Button:SetPosition(25,235);
"@
$utf8Bom = [Text.UTF8Encoding]::new($true, $true)

foreach ($relativePath in $relativePaths) {
    $path = Join-Path $ClientPath $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing Medusa dialogue script: $path"
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 20000 -or
        $bytes.Length -lt 3 -or
        $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or
        $bytes[2] -ne 0xBF) {
        throw "Medusa dialogue script has an unsupported size or encoding: $path"
    }

    $text = $utf8Bom.GetString($bytes, 3, $bytes.Length - 3)
    $branchCount = ([regex]::Matches(
        $text,
        'elseif SubID== 207 then',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)).Count
    if ($branchCount -eq 0) {
        if ($ValidateOnly) {
            throw "Mythic option is not installed: $path"
        }
        if ($text.IndexOf($anchor, [StringComparison]::Ordinal) -lt 0 -or
            $text.IndexOf(
                $anchor,
                $text.IndexOf($anchor, [StringComparison]::Ordinal) + 1,
                [StringComparison]::Ordinal) -ge 0) {
            throw "Medusa dialogue insertion anchor is not exact: $path"
        }

        $text = $text.Replace(
            $anchor,
            $branch + "`r`n" + $anchor,
            [StringComparison]::Ordinal)
        [IO.File]::WriteAllText($path, $text, $utf8Bom)
    }
    elseif ($branchCount -ne 1 -or
            $text.IndexOf(
                'Button:SetText("|cffFFFF00*Medusa Island ' +
                '(Mythic)|cFFFFFFFF");',
                [StringComparison]::Ordinal) -lt 0 -or
            $text.IndexOf(
                'Button:SetPosition(25,235);',
                [StringComparison]::Ordinal) -lt 0) {
        throw "Medusa Mythic option is contradictory: $path"
    }

    $installed = Get-Item -LiteralPath $path
    if ($installed.Length -ge 20000) {
        throw "Patched Medusa dialogue script exceeds 20 KB: $path"
    }
    Write-Output "Medusa Mythic option ready: $path"
}
