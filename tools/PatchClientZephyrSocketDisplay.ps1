[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $repositoryRoot `
    'Localization\en_us\Settings\Sys\EquipStoneInfo.xml'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "The reviewed Zephyr socket metadata is missing: $sourcePath"
}

$expected = @(
    [pscustomobject]@{ Id = '21'; Name = 'ZephyrAttunement'; Icon = '620,8' }
    [pscustomobject]@{ Id = '22'; Name = 'ZephyrTempering'; Icon = '620,8' }
    [pscustomobject]@{ Id = '23'; Name = 'ZephyrManaBurnResistance'; Icon = '620,8' }
    [pscustomobject]@{ Id = '24'; Name = 'ZephyrCooldownExtensionResistance'; Icon = '620,8' }
)

function Assert-ZephyrSocketMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    foreach ($definition in $expected) {
        $matches = @($document.EquipStoneInfo.ChildNodes | Where-Object {
            $_.NodeType -eq [Xml.XmlNodeType]::Element -and
            $_.GetAttribute('ID') -ceq $definition.Id
        })
        if ($matches.Count -ne 1) {
            throw "EquipStoneInfo must contain ID $($definition.Id) exactly once: $Path"
        }
        $node = $matches[0]
        if ($node.Name -cne $definition.Name -or
            $node.GetAttribute('Percent') -cne '1' -or
            $node.GetAttribute('Texture') -cne `
                './Localization/en_us/UI/Texture/Icon5.gwo' -or
            $node.GetAttribute('IconPos') -cne $definition.Icon) {
            throw "EquipStoneInfo ID $($definition.Id) is not the reviewed Zephyr definition: $Path"
        }
    }
}

Assert-ZephyrSocketMetadata -Path $sourcePath
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$sourceFingerprint = [Convert]::ToBase64String($sourceBytes)
$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$localizationRoot = Join-Path $resolvedRoot 'Localization'
$locales = @('en_us', 'zh_cn') | Where-Object {
    Test-Path -LiteralPath (Join-Path $localizationRoot $_) -PathType Container
}
if ($locales -notcontains 'en_us') {
    throw "The en_us client localization is missing below $resolvedRoot."
}

foreach ($locale in $locales) {
    $destination = Join-Path $localizationRoot `
        "$locale\Settings\Sys\EquipStoneInfo.xml"
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        throw "The client socket metadata file is missing: $destination"
    }

    $destinationBytes = [IO.File]::ReadAllBytes($destination)
    $destinationFingerprint = [Convert]::ToBase64String(
        $destinationBytes)
    if ($destinationFingerprint -cne $sourceFingerprint) {
        if ($Check) {
            throw "Zephyr socket display metadata is not installed: $destination"
        }

        $temporary = "$destination.zephyr-$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::WriteAllBytes($temporary, $sourceBytes)
            Move-Item -LiteralPath $temporary -Destination $destination `
                -Force
        }
        finally {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }

    Assert-ZephyrSocketMetadata -Path $destination
    if ($Check) {
        Write-Host "Verified Zephyr socket display metadata: $locale"
    }
    else {
        Write-Host "Installed Zephyr socket display metadata: $locale"
    }
}
