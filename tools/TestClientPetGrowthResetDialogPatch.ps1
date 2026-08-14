[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$patcher = Join-Path $PSScriptRoot 'PatchClientPetGrowthResetDialog.ps1'
$sourceRoot = 'C:\Godswar Origin'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'godswar-pet-growth-dialog-' + [guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $temporaryRoot 'backups'
$previousPatchedSha256 =
    '8DAF1D1DC6DDAC9806066F373F2497686D6C70F74C89035EFEB19ACED94633A9'
$encoding = [Text.UTF8Encoding]::new($true)

function Get-Utf8BomBytes([string]$Text) {
    $content = $Text.TrimStart([char]0xFEFF)
    return [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($content))
}

function Set-PreviousCombinedPatchFixture([string]$Path) {
    $source = [IO.File]::ReadAllText($Path, $encoding)
    $newline = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }
    $basicOriginal = @(
        "`t`tif SubID == 120 then",
        "`t`t`tFirstWin_Text1:SetText(NF_L0_P104);",
        "`t`t`tFirstWin_Text1:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:SetText(NF_L0_P124);",
        "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
    ) -join $newline
    $basicPrevious = @(
        "`t`tif SubID == 120 then",
        "`t`t`tFirstWin_Text1:SetText(NF_L0_P104);",
        "`t`t`tFirstWin_Text1:Visible(true);",
        "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
        "`t`t`tFirstWin_ButtonA1:SetPosition(250,100);",
        "`t`t`tFirstWin_ButtonA1:Visible(true);",
        "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
        "`t`t`tFirstWin_ButtonA2:SetPosition(325,100);",
        "`t`t`tFirstWin_ButtonA2:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:SetText(NF_L0_P124);",
        "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
    ) -join $newline
    $growthOriginal = @(
        "`t`telseif SubID == 130 then",
        "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
        "`t`t`tFirstWin_Text1:Visible(true);`t",
        "`t`t`tFirstWin_ButtonA3:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
        "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
    ) -join $newline
    $growthPatched = @(
        "`t`telseif SubID == 130 then",
        "`t`t`tFirstWin_Text1:SetText(NG_L0_P104);",
        "`t`t`tFirstWin_Text1:Visible(true);`t",
        "`t`t`tFirstWin_ButtonA1:SetText(NF_X0_2);",
        "`t`t`tFirstWin_ButtonA1:SetPosition(250,100);",
        "`t`t`tFirstWin_ButtonA1:Visible(true);",
        "`t`t`tFirstWin_ButtonA2:SetText(NF_X0_3);",
        "`t`t`tFirstWin_ButtonA2:SetPosition(325,100);",
        "`t`t`tFirstWin_ButtonA2:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:Visible(true);",
        "`t`t`tFirstWin_ButtonA3:SetText(NG_L0_P112);",
        "`t`t`tFirstWin_ButtonA3:SetPosition(400,100);"
    ) -join $newline
    if ([regex]::Matches(
            $source,
            [regex]::Escape($basicOriginal)).Count -ne 1 -or
        [regex]::Matches(
            $source,
            [regex]::Escape($growthOriginal)).Count -ne 1) {
        throw 'Ready fixture does not contain both original reset blocks.'
    }
    $candidate = $source.Replace($basicOriginal, $basicPrevious)
    $candidate = $candidate.Replace($growthOriginal, $growthPatched)
    [IO.File]::WriteAllBytes($Path, (Get-Utf8BomBytes $candidate))
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne
        $previousPatchedSha256) {
        throw 'Previous combined-patch fixture failed exact hash verification.'
    }
}

function Get-Rectangle([string]$Text, [string]$ElementName) {
    $pattern = '<' + [regex]::Escape($ElementName) +
        '\b[^>]*Rectangle="(?<x1>\d+)\s*,\s*(?<y1>\d+)\s*,\s*' +
        '(?<x2>\d+)\s*,\s*(?<y2>\d+)"'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Layout rectangle was not found for $ElementName."
    }
    $x1 = [int]$match.Groups['x1'].Value
    $y1 = [int]$match.Groups['y1'].Value
    $x2 = [int]$match.Groups['x2'].Value
    $y2 = [int]$match.Groups['y2'].Value
    return [pscustomobject]@{
        Width = $x2 - $x1
        Height = $y2 - $y1
    }
}

function Get-SubIdBlock(
    [string]$Text,
    [int]$SubId,
    [string]$NextCondition) {
    $startPattern = '(?m)^\s*(?:if|elseif) SubID == ' + $SubId +
        ' then\s*$'
    $start = [regex]::Match($Text, $startPattern)
    if (-not $start.Success) {
        throw "Sub-ID $SubId block was not found."
    }
    $tail = $Text.Substring($start.Index)
    $end = $tail.IndexOf($NextCondition, [StringComparison]::Ordinal)
    if ($end -lt 1) {
        throw "Sub-ID $SubId block boundary was not found."
    }
    return $tail.Substring(0, $end)
}

try {
    foreach ($locale in @('en_us', 'zh_cn')) {
        $destination = Join-Path $temporaryRoot (
            "Localization\$locale\UI\XML\NpcFun\NpcFunPett.lua")
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot (
            "Localization\$locale\UI\XML\NpcFun\NpcFunPett.lua")) `
            -Destination $destination

        $layoutDestination = Join-Path $temporaryRoot (
            "Localization\$locale\UI\XML\NpcFun.xml")
        Copy-Item -LiteralPath (Join-Path $sourceRoot (
            "Localization\$locale\UI\XML\NpcFun.xml")) `
            -Destination $layoutDestination
    }

    $ready = @(& $patcher -Mode Status -ClientRoot $temporaryRoot)
    if ($ready.Count -eq 2 -and
        @($ready | Where-Object Status -ne 'Ready').Count -eq 2) {
        & $patcher -Mode Revert -ClientRoot $temporaryRoot `
            -BackupRoot $backupRoot | Out-Null
        $ready = @(& $patcher -Mode Status -ClientRoot $temporaryRoot)
    }
    if ($ready.Count -ne 2 -or
        @($ready | Where-Object Status -ne 'Ready').Count -ne 0) {
        throw 'Both locale fixtures must begin in Ready state.'
    }

    foreach ($entry in $ready) {
        Set-PreviousCombinedPatchFixture $entry.Path
    }
    $previous = @(& $patcher -Mode Status -ClientRoot $temporaryRoot)
    if ($previous.Count -ne 2 -or
        @($previous | Where-Object Status -ne 'PreviousPatch').Count -ne 0) {
        throw 'Both locales must recognize the previous live patch exactly.'
    }

    $applied = @(& $patcher -Mode Apply -ClientRoot $temporaryRoot `
        -BackupRoot $backupRoot)
    if ($applied.Count -ne 2 -or
        @($applied | Where-Object Status -ne 'Patched').Count -ne 0) {
        throw 'Both locale fixtures must apply atomically.'
    }
    foreach ($entry in $applied) {
        $text = [IO.File]::ReadAllText($entry.Path)
        $basic = Get-SubIdBlock $text 120 'elseif SubID == 129 then'
        $growth = Get-SubIdBlock $text 130 `
            'elseif math.mod(SubID ,100) == 8 then'
        foreach ($needle in @(
            'FirstWin_ButtonA1:Visible(false);',
            'FirstWin_ButtonA2:Visible(false);',
            'FirstWin_ButtonA3:Visible(true);',
            'FirstWin_ButtonA3:SetText(NF_L0_P124);',
            'FirstWin_ButtonA3:SetPosition(400,100);'
        )) {
            if (-not $basic.Contains($needle)) {
                throw "$($entry.Locale) Basic/Savvy page is missing: $needle"
            }
        }
        foreach ($forbidden in @(
            'FirstWin_ButtonA1:Visible(true);',
            'FirstWin_ButtonA2:Visible(true);'
        )) {
            if ($basic.Contains($forbidden)) {
                throw "$($entry.Locale) Basic/Savvy page exposes: $forbidden"
            }
        }
        foreach ($needle in @(
                'PhoenixGrowthClearComparison();',
                'FirstWin_ButtonA1:SetText(NF_X0_2);',
                'FirstWin_ButtonA1:SetPosition(440,240);',
                'FirstWin_ButtonA1:Visible(true);',
                'FirstWin_ButtonA2:SetText(NF_X0_3);',
                'FirstWin_ButtonA2:SetPosition(515,240);',
                'FirstWin_ButtonA2:Visible(true);',
                'FirstWin_ButtonA3:SetText(NG_L0_P112);',
                'FirstWin_ButtonA3:SetPosition(25,240);',
                'PhoenixGrowthShowNew(BtnID,SubID,math.mod(SubID ,100));',
                'PhoenixGrowthShowCurrent(BtnID,SubID,math.mod(SubID ,100));'
        )) {
            if (-not $growth.Contains($needle)) {
                throw "$($entry.Locale) Growth page is missing: $needle"
            }
        }

        $layoutPath = Join-Path $temporaryRoot (
            "Localization\$($entry.Locale)\UI\XML\NpcFun.xml")
        $layout = [IO.File]::ReadAllText($layoutPath)
        $window = Get-Rectangle $layout 'FirstWin'
        $ok = Get-Rectangle $layout 'FirstWin_ButtonA1'
        $cancel = Get-Rectangle $layout 'FirstWin_ButtonA2'
        $reset = Get-Rectangle $layout 'FirstWin_ButtonA3'
        foreach ($index in 1..12) {
            $row = Get-Rectangle $layout "FirstWin_Button$index"
            if ($row.Width -ne 220 -or $row.Height -ne 21) {
                throw "$($entry.Locale) comparison row $index changed size."
            }
        }
        foreach ($needle in @(
                'local PhoenixGrowthNew = {};',
                'local PhoenixGrowthCurrent = {};',
                'local Button = win:GetChild("FirstWin_Button" .. Index);',
                'local Button = win:GetChild("FirstWin_Button" .. (Index + 6));',
                'Button:SetPosition(25,60 + Index * 20);',
                'Button:SetPosition(340,60 + Index * 20);',
                '|cFFF79709',
                '|cff39D8B8',
                'FirstWin_Text2:SetPosition(25,205);',
                'local NewTotal = 0;',
                'local CurrentTotal = 0;',
                'string.char(229,189,147,229,137,141',
                'math.mod(SubID ,100) >= 20',
                'math.mod(SubID ,100) <= 25'
        )) {
            if (-not $text.Contains($needle)) {
                throw "$($entry.Locale) comparison UI is missing: $needle"
            }
        }
        $helperEnd = $text.IndexOf('--', [StringComparison]::Ordinal)
        if ($helperEnd -lt 1 -or
            $text.Substring(0, $helperEnd).Contains(
                'win:GetChild("FirstWin_Button" .. BtnID)')) {
            throw "$($entry.Locale) comparison rows still trust callback BtnID."
        }

        $okX = 440
        $okY = 240
        $cancelX = 515
        $cancelY = 240
        $resetX = 25
        $resetY = 240
        if ($okX -lt 0 -or $okY -lt 0 -or
            $okX + $ok.Width -gt $window.Width -or
            $okY + $ok.Height -gt $window.Height -or
            $cancelX -lt 0 -or $cancelY -lt 0 -or
            $cancelX + $cancel.Width -gt $window.Width -or
            $cancelY + $cancel.Height -gt $window.Height -or
            $resetX + $reset.Width -gt $window.Width -or
            $resetY + $reset.Height -gt $window.Height -or
            $resetX + $reset.Width -ge $okX -or
            $okX + $ok.Width -ge $cancelX -or
            25 + 220 -gt $window.Width -or
            340 + 220 -gt $window.Width) {
            throw "$($entry.Locale) result controls exceed or overlap FirstWin."
        }
    }

    $idempotent = @(& $patcher -Mode Apply -ClientRoot $temporaryRoot `
        -BackupRoot $backupRoot)
    if (@($idempotent |
            Where-Object Status -ne 'Already patched').Count -ne 0) {
        throw 'Repeated Apply must be idempotent.'
    }

    $reverted = @(& $patcher -Mode Revert -ClientRoot $temporaryRoot `
        -BackupRoot $backupRoot)
    if ($reverted.Count -ne 2 -or
        @($reverted | Where-Object Status -ne 'Reverted').Count -ne 0) {
        throw 'Both locale fixtures must revert atomically.'
    }
    if (@(& $patcher -Mode Status -ClientRoot $temporaryRoot |
            Where-Object Status -ne 'Ready').Count -ne 0) {
        throw 'Round trip did not reproduce the original locale state.'
    }

    $readyApplied = @(& $patcher -Mode Apply -ClientRoot $temporaryRoot `
        -BackupRoot $backupRoot)
    if ($readyApplied.Count -ne 2 -or
        @($readyApplied | Where-Object Status -ne 'Patched').Count -ne 0) {
        throw 'Both original locale fixtures must apply atomically.'
    }
    & $patcher -Mode Revert -ClientRoot $temporaryRoot `
        -BackupRoot $backupRoot | Out-Null

    foreach ($locale in @('en_us', 'zh_cn')) {
        $layoutPath = Join-Path $temporaryRoot (
            "Localization\$locale\UI\XML\NpcFun.xml")
        $originalBytes = [IO.File]::ReadAllBytes($layoutPath)
        try {
            $layoutText = [IO.File]::ReadAllText($layoutPath)
            $unsupported = $layoutText.Replace(
                'Rectangle="380,112,980,402"',
                'Rectangle="380,112,980,401"')
            if ($unsupported -eq $layoutText) {
                throw "$locale layout fixture has no FirstWin geometry."
            }
            [IO.File]::WriteAllText($layoutPath, $unsupported)
            $rejected = $false
            try {
                & $patcher -Mode Status -ClientRoot $temporaryRoot |
                    Out-Null
            }
            catch {
                $rejected = $_.Exception.Message.Contains(
                    "Unsupported $locale Pet Growth layout SHA-256")
            }
            if (-not $rejected) {
                throw "$locale unsupported layout geometry was not refused."
            }
        }
        finally {
            [IO.File]::WriteAllBytes($layoutPath, $originalBytes)
        }
    }

    foreach ($path in @(
            $patcher,
            $PSCommandPath,
            (Join-Path $PSScriptRoot `
                'client_patch_helpers\PetGrowthResetDialog.Resources.ps1'),
            (Join-Path $PSScriptRoot `
                'client_patch_helpers\PetGrowthResetDialog.Patcher.ps1'))) {
        if ((Get-Item -LiteralPath $path).Length -ge 20KB) {
            throw "Pet Growth patch file exceeds 20KB: $path"
        }
    }

    'PASS Phoenix comparison UI: suffix-addressed 12 native rows, current Luck, totals, guarded exact-hash upgrade/apply/revert'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
