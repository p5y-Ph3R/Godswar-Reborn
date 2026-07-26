Set-StrictMode -Version Latest

$script:UnsafeDotnetEnvironmentNames =
    [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    'DOTNET_STARTUP_HOOKS',
    'DOTNET_ADDITIONAL_DEPS',
    'DOTNET_SHARED_STORE',
    'DOTNET_DiagnosticPorts',
    'DOTNET_DefaultDiagnosticPortSuspend',
    'DOTNET_EnableDiagnostics',
    'DOTNET_EnableDiagnostics_IPC',
    'DOTNET_EnableDiagnostics_Debugger',
    'DOTNET_EnableDiagnostics_Profiler',
    'DOTNET_HOST_PATH',
    'DOTNET_MULTILEVEL_LOOKUP',
    'DOTNET_RUNTIME_ID',
    'DOTNET_BUNDLE_EXTRACT_BASE_DIR',
    'DOTNET_ROOT',
    'DOTNET_ROOT(x86)'
)) {
    [void]$script:UnsafeDotnetEnvironmentNames.Add($name)
}

function Test-RebornControlledHostUnsafeEnvironmentName {
    param([Parameter(Mandatory)][string]$Name)

    foreach ($prefix in @('CORECLR_', 'COR_', 'COMPLUS_')) {
        if ($Name.StartsWith(
                $prefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    if ($script:UnsafeDotnetEnvironmentNames.Contains($Name)) {
        return $true
    }
    foreach ($prefix in @(
        'DOTNET_ROOT_',
        'DOTNET_ROLL_FORWARD',
        'DOTNET_ALTJIT',
        'DOTNET_JITNAME'
    )) {
        if ($Name.StartsWith(
                $prefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-RebornControlledHostUnsafeProcessEnvironmentNames {
    $unsafe = [Collections.Generic.List[string]]::new()
    $environment = [Environment]::GetEnvironmentVariables(
        [EnvironmentVariableTarget]::Process)
    foreach ($key in $environment.Keys) {
        if ($key -is [string] -and
            (Test-RebornControlledHostUnsafeEnvironmentName $key)) {
            $unsafe.Add($key)
        }
    }
    $values = $unsafe.ToArray()
    [Array]::Sort($values, [StringComparer]::OrdinalIgnoreCase)
    return $values
}

function Assert-RebornControlledHostSafeProcessEnvironment {
    $unsafe =
        @(Get-RebornControlledHostUnsafeProcessEnvironmentNames)
    if ($unsafe.Count -ne 0) {
        throw (
            'Unsafe inherited runtime-loader or diagnostics environment ' +
            "is set: $($unsafe -join ',')")
    }
    return $true
}

function Assert-RebornControlledHostUnsetEnvironmentNames {
    param([Parameter(Mandatory)][string[]]$Names)

    foreach ($name in $Names) {
        if (-not [string]::IsNullOrEmpty(
                [Environment]::GetEnvironmentVariable(
                    $name,
                    [EnvironmentVariableTarget]::Process))) {
            throw "Controlled-host environment must initially omit: $name"
        }
    }
    return $true
}

function Assert-RebornControlledHostNoUnreviewedGodswarEnvironment {
    param([Parameter(Mandatory)][string[]]$ExpectedNames)

    $expected =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ExpectedNames) {
        [void]$expected.Add($name)
    }
    $unexpected = [Collections.Generic.List[string]]::new()
    $environment = [Environment]::GetEnvironmentVariables(
        [EnvironmentVariableTarget]::Process)
    foreach ($key in $environment.Keys) {
        if ($key -is [string] -and
            $key.StartsWith(
                'GODSWAR_',
                [StringComparison]::OrdinalIgnoreCase) -and
            -not $expected.Contains($key)) {
            $unexpected.Add($key)
        }
    }
    $values = $unexpected.ToArray()
    [Array]::Sort($values, [StringComparer]::OrdinalIgnoreCase)
    if ($values.Count -ne 0) {
        throw (
            'Unreviewed inherited GODSWAR environment is set: ' +
            ($values -join ','))
    }
    return $true
}

function Set-RebornControlledHostSanitizedChildEnvironment {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.ProcessStartInfo]$StartInfo
    )

    $keys = @($StartInfo.EnvironmentVariables.Keys)
    foreach ($key in $keys) {
        if ($key -is [string] -and
            (Test-RebornControlledHostUnsafeEnvironmentName $key)) {
            $StartInfo.EnvironmentVariables.Remove($key)
        }
    }
    $StartInfo.EnvironmentVariables['DOTNET_EnableDiagnostics'] = '0'
    $StartInfo.EnvironmentVariables['COMPlus_EnableDiagnostics'] = '0'
    return $StartInfo
}

function Get-RebornControlledHostDiagnosticsDisabledEnvironment {
    return [ordered]@{
        DOTNET_EnableDiagnostics = '0'
        COMPlus_EnableDiagnostics = '0'
    }
}

Export-ModuleMember -Function @(
    'Test-RebornControlledHostUnsafeEnvironmentName',
    'Get-RebornControlledHostUnsafeProcessEnvironmentNames',
    'Assert-RebornControlledHostSafeProcessEnvironment',
    'Assert-RebornControlledHostUnsetEnvironmentNames',
    'Assert-RebornControlledHostNoUnreviewedGodswarEnvironment',
    'Set-RebornControlledHostSanitizedChildEnvironment',
    'Get-RebornControlledHostDiagnosticsDisabledEnvironment'
)
