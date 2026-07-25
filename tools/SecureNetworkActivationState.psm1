Set-StrictMode -Version Latest

$script:ActivationRegistrySubKey = 'SOFTWARE\Reborn\NetworkManifest'
$script:ActivationModeValue = 'ActivationMode'
$script:ActivationEnvironmentValue = 'Environment'
$script:ActivationSequenceFloorValue = 'HighestAcceptedSequence'

function New-RebornActivationState {
    param(
        [Parameter(Mandatory)]
        [ValidateRange(0, 1)]
        [UInt64]$Mode,

        [Parameter(Mandatory)]
        [ValidateRange(0, 3)]
        [UInt64]$Environment,

        [Parameter(Mandatory)]
        [UInt64]$SequenceFloor,

        [bool]$Exists = $true
    )

    if ($Mode -eq 1 -and ($Environment -lt 1 -or $SequenceFloor -eq 0)) {
        throw 'SecureRequired activation needs an environment and nonzero sequence floor.'
    }

    [pscustomobject]@{
        Exists = $Exists
        Mode = [UInt64]$Mode
        Environment = [UInt64]$Environment
        SequenceFloor = [UInt64]$SequenceFloor
    }
}

function Get-RebornActivationStateDescriptor {
    [pscustomobject]@{
        Hive = 'HKEY_LOCAL_MACHINE'
        RegistryView = 'Registry64'
        SubKey = $script:ActivationRegistrySubKey
        Mode = $script:ActivationModeValue
        Environment = $script:ActivationEnvironmentValue
        SequenceFloor = $script:ActivationSequenceFloorValue
        ModeValueKind = 'DWord'
        EnvironmentValueKind = 'DWord'
        SequenceValueKind = 'QWord'
    }
}

function ConvertTo-RebornUInt64 {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $parsed = [UInt64]0
    if (-not [UInt64]::TryParse(
            [Convert]::ToString(
                $Value,
                [Globalization.CultureInfo]::InvariantCulture),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "$Label is not an unsigned 64-bit integer."
    }
    return $parsed
}

function Get-RebornFileActivationState {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        return New-RebornActivationState `
            -Mode 0 `
            -Environment 0 `
            -SequenceFloor 0 `
            -Exists $false
    }

    $document = Get-Content -LiteralPath $resolved -Raw |
        ConvertFrom-Json
    if ($document.schemaVersion -ne 1) {
        throw 'Unsupported offline activation-state document.'
    }

    $mode = ConvertTo-RebornUInt64 $document.activationMode 'ActivationMode'
    $environment = ConvertTo-RebornUInt64 $document.environment 'Environment'
    $floor = ConvertTo-RebornUInt64 $document.sequenceFloor 'SequenceFloor'
    return New-RebornActivationState `
        -Mode $mode `
        -Environment $environment `
        -SequenceFloor $floor
}

function Write-RebornFileActivationState {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$State
    )

    $validated = New-RebornActivationState `
        -Mode (ConvertTo-RebornUInt64 $State.Mode 'ActivationMode') `
        -Environment (ConvertTo-RebornUInt64 $State.Environment 'Environment') `
        -SequenceFloor (
            ConvertTo-RebornUInt64 $State.SequenceFloor 'SequenceFloor')
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $resolved
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    $temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = [ordered]@{
        schemaVersion = 1
        activationMode = $validated.Mode.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
        environment = $validated.Environment.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
        sequenceFloor = $validated.SequenceFloor.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
    } | ConvertTo-Json
    $encoding = New-Object Text.UTF8Encoding($false)
    try {
        $stream = New-Object IO.FileStream(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $bytes = $encoding.GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            $old = "$resolved.$([Guid]::NewGuid().ToString('N')).old"
            try {
                [IO.File]::Replace($temporary, $resolved, $old, $true)
            }
            finally {
                if (Test-Path -LiteralPath $old -PathType Leaf) {
                    Remove-Item -LiteralPath $old -Force
                }
            }
        } else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Open-RebornActivationRegistryKey {
    param([bool]$Writable)

    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        if ($Writable) {
            return $base.CreateSubKey(
                $script:ActivationRegistrySubKey,
                [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
        }
        return $base.OpenSubKey($script:ActivationRegistrySubKey, $false)
    }
    finally {
        $base.Dispose()
    }
}

function Get-RebornHklmActivationState {
    $key = Open-RebornActivationRegistryKey $false
    if ($null -eq $key) {
        return New-RebornActivationState `
            -Mode 0 `
            -Environment 0 `
            -SequenceFloor 0 `
            -Exists $false
    }

    try {
        foreach ($name in @(
            $script:ActivationModeValue,
            $script:ActivationEnvironmentValue
        )) {
            if ($key.GetValueKind($name) -ne
                [Microsoft.Win32.RegistryValueKind]::DWord) {
                throw "HKLM activation value $name must be REG_DWORD."
            }
        }
        if ($key.GetValueKind($script:ActivationSequenceFloorValue) -ne
            [Microsoft.Win32.RegistryValueKind]::QWord) {
            throw (
                "HKLM activation value " +
                "$script:ActivationSequenceFloorValue must be REG_QWORD."
            )
        }

        return New-RebornActivationState `
            -Mode ([UInt64]$key.GetValue($script:ActivationModeValue)) `
            -Environment (
                [UInt64]$key.GetValue($script:ActivationEnvironmentValue)) `
            -SequenceFloor (
                [UInt64]$key.GetValue($script:ActivationSequenceFloorValue))
    }
    finally {
        $key.Dispose()
    }
}

function Write-RebornHklmActivationState {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [switch]$AllowHklmWrite
    )

    if (-not $AllowHklmWrite) {
        throw 'HKLM activation writes require explicit -AllowHklmWrite.'
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'HKLM activation writes require an elevated administrator process.'
    }

    $validated = New-RebornActivationState `
        -Mode (ConvertTo-RebornUInt64 $State.Mode 'ActivationMode') `
        -Environment (ConvertTo-RebornUInt64 $State.Environment 'Environment') `
        -SequenceFloor (
            ConvertTo-RebornUInt64 $State.SequenceFloor 'SequenceFloor')
    if ($validated.Mode -gt [Int64]::MaxValue -or
        $validated.Environment -gt [Int64]::MaxValue -or
        $validated.SequenceFloor -gt [Int64]::MaxValue) {
        throw 'Windows REG_QWORD activation values cannot exceed Int64.MaxValue.'
    }

    $key = Open-RebornActivationRegistryKey $true
    try {
        $security =
            New-Object Security.AccessControl.RegistrySecurity
        $security.SetAccessRuleProtection($true, $false)
        $inheritance =
            [Security.AccessControl.InheritanceFlags]::ContainerInherit
        $propagation =
            [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow
        foreach ($entry in @(
            @(
                'S-1-5-18',
                [Security.AccessControl.RegistryRights]::FullControl
            ),
            @(
                'S-1-5-32-544',
                [Security.AccessControl.RegistryRights]::FullControl
            ),
            @(
                'S-1-5-32-545',
                [Security.AccessControl.RegistryRights]::ReadKey
            )
        )) {
            $identity =
                [Security.Principal.SecurityIdentifier]::new(
                    [string]$entry[0])
            $rule =
                [Security.AccessControl.RegistryAccessRule]::new(
                $identity,
                [Security.AccessControl.RegistryRights]$entry[1],
                $inheritance,
                $propagation,
                $allow)
            $security.AddAccessRule($rule)
        }
        $key.SetAccessControl($security)

        $key.SetValue(
            $script:ActivationModeValue,
            [Int32]$validated.Mode,
            [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue(
            $script:ActivationEnvironmentValue,
            [Int32]$validated.Environment,
            [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue(
            $script:ActivationSequenceFloorValue,
            [Int64]$validated.SequenceFloor,
            [Microsoft.Win32.RegistryValueKind]::QWord)
        $key.Flush()
    }
    finally {
        $key.Dispose()
    }
}

function Get-RebornActivationState {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')]
        [string]$Provider,

        [string]$Path
    )

    if ($Provider -eq 'OfflineFile') {
        if ([string]::IsNullOrWhiteSpace($Path)) {
            throw 'OfflineFile activation state requires -Path.'
        }
        return Get-RebornFileActivationState $Path
    }
    return Get-RebornHklmActivationState
}

function Write-RebornActivationState {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('OfflineFile', 'Hklm')]
        [string]$Provider,

        [string]$Path,

        [Parameter(Mandatory)]
        [object]$State,

        [switch]$AllowHklmWrite
    )

    if ($Provider -eq 'OfflineFile') {
        if ([string]::IsNullOrWhiteSpace($Path)) {
            throw 'OfflineFile activation state requires -Path.'
        }
        Write-RebornFileActivationState $Path $State
        return
    }
    Write-RebornHklmActivationState $State -AllowHklmWrite:$AllowHklmWrite
}

Export-ModuleMember -Function @(
    'Get-RebornActivationStateDescriptor',
    'Get-RebornActivationState',
    'Write-RebornActivationState',
    'New-RebornActivationState'
)
