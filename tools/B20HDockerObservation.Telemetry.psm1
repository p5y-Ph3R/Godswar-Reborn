Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Integrity.psm1')

function Assert-B20TelemetryCondition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-B20Condition {
    param([bool]$Condition, [string]$Message)

    Assert-B20TelemetryCondition $Condition $Message
}

. (Join-Path $PSScriptRoot 'B20RetirementEvidence.StrictJson.ps1')

function Read-B20BoundedJsonFile {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][long]$MaximumBytes,
        [Parameter(Mandatory)][string]$Context
    )

    Assert-B20TelemetryCondition (
        Test-Path -LiteralPath $LiteralPath -PathType Leaf) (
        "$Context does not exist.")
    Assert-B20NoReparsePoints `
        (Split-Path -Parent $LiteralPath) $LiteralPath $Context
    Assert-B20TelemetryCondition (
        (Get-Item -LiteralPath $LiteralPath).Length -le $MaximumBytes) (
        "$Context exceeds its bounded size.")
    $raw = Get-Content -LiteralPath $LiteralPath -Raw
    Assert-B20NoDuplicateJsonProperties $raw
    return $raw | ConvertFrom-Json
}

function Get-B20PrometheusApi {
    param(
        [Parameter(Mandatory)][string]$Path,
        [long]$MaximumCharacters = 16MB
    )

    $raw = Invoke-B20Command docker @(
        'exec',
        'godswar-b20h-prometheus',
        'wget',
        '-T', '10',
        '-t', '1',
        '-qO-',
        "http://127.0.0.1:9091$Path")
    Assert-B20TelemetryCondition ($raw.Length -le $MaximumCharacters) (
        'Prometheus response exceeds its bounded size.')
    Assert-B20NoDuplicateJsonProperties $raw
    $response = $raw | ConvertFrom-Json
    Assert-B20TelemetryCondition ($response.status -ceq 'success') (
        "Prometheus rejected '$Path'.")
    return $response
}

function Get-B20PrometheusCurrentValue {
    param([Parameter(Mandatory)][string]$Query)

    $encoded = [Uri]::EscapeDataString($Query)
    $response = Get-B20PrometheusApi `
        "/api/v1/query?query=$encoded" 1MB
    Assert-B20TelemetryCondition (
        $response.data.resultType -ceq 'vector') (
        "Query '$Query' returned the wrong result type.")
    $result = @($response.data.result)
    Assert-B20TelemetryCondition (
        $result.Count -eq 1 -and @($result[0].value).Count -eq 2) (
        "Query '$Query' did not return exactly one current series.")
    $value = [double]::Parse(
        [string]$result[0].value[1],
        [Globalization.CultureInfo]::InvariantCulture)
    Assert-B20TelemetryCondition (
        -not [double]::IsNaN($value) -and
        -not [double]::IsInfinity($value)) (
        "Query '$Query' returned a non-finite value.")
    return $value
}

function Get-B20RawSeries {
    param(
        [Parameter(Mandatory)][string]$Selector,
        [Parameter(Mandatory)][double]$EndEpoch,
        [Parameter(Mandatory)][long]$RangeSeconds,
        [int]$MaximumSeries = 1
    )

    $culture = [Globalization.CultureInfo]::InvariantCulture
    $query = "${Selector}[$($RangeSeconds)s]"
    $path = '/api/v1/query?query={0}&time={1}' -f
        [Uri]::EscapeDataString($query),
        $EndEpoch.ToString('0.###', $culture)
    $response = Get-B20PrometheusApi $path
    Assert-B20TelemetryCondition (
        $response.data.resultType -ceq 'matrix') (
        "Prometheus query '$query' returned the wrong result type.")
    $result = @($response.data.result)
    Assert-B20TelemetryCondition ($result.Count -le $MaximumSeries) (
        "Prometheus query '$query' exceeded the series bound.")
    return @($result | ForEach-Object {
        $values = @($_.values)
        Assert-B20TelemetryCondition ($values.Count -le 100000) (
            "Prometheus query '$query' exceeded the sample bound.")
        [pscustomobject]@{
            Metric = $_.metric
            Points = @($values | ForEach-Object {
                Assert-B20TelemetryCondition (@($_).Count -eq 2) (
                    "Prometheus query '$query' returned a malformed sample.")
                $timestamp = [double]::Parse([string]$_[0], $culture)
                $value = [double]::Parse([string]$_[1], $culture)
                Assert-B20TelemetryCondition (
                    -not [double]::IsNaN($timestamp) -and
                    -not [double]::IsInfinity($timestamp) -and
                    -not [double]::IsNaN($value) -and
                    -not [double]::IsInfinity($value)) (
                    "Prometheus query '$query' returned a non-finite sample.")
                [pscustomobject]@{
                    Timestamp = $timestamp
                    Value = $value
                }
            })
        }
    })
}

function Get-B20LegacyEvidence {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Series,
        [Parameter(Mandatory)][long]$StartMilliseconds,
        [Parameter(Mandatory)][long]$ConfirmationMilliseconds,
        [Parameter(Mandatory)][bool]$WindowCovered
    )

    $maximum = 0d
    $resets = 0
    $missingConfirmation = 0
    # Counters are lazy; observer-ready=1 independently proves safe absence.
    foreach ($item in $Series) {
        $values = [double[]]@(
            $item.Points | Where-Object {
                $timestamp = [long][Math]::Round($_.Timestamp * 1000d)
                $timestamp -ge $StartMilliseconds -and
                    $timestamp -le $ConfirmationMilliseconds
            } | ForEach-Object { $_.Value }
        )
        if ($values.Count -eq 0) {
            continue
        }
        Assert-B20TelemetryCondition (
            @($values | Where-Object {
                $_ -lt 0 -or $_ -ne [Math]::Floor($_)
            }).Count -eq 0) (
            'A legacy invocation series is not an integer counter.')
        $maximum += ($values | Measure-Object -Maximum).Maximum
        $resets += Get-B20DecreaseCount $values
        if ($WindowCovered) {
            $samples = Get-B20TimestampSet $item.Points `
                $StartMilliseconds $ConfirmationMilliseconds
            if (-not $samples.Contains($ConfirmationMilliseconds)) {
                $missingConfirmation++
            }
        }
    }
    return [pscustomobject]@{
        Maximum = $maximum
        Resets = $resets
        MissingConfirmation = $missingConfirmation
    }
}

function Set-B20AtomicLatestText {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][string]$Text
    )

    $encoding = [Text.UTF8Encoding]::new($false)
    $temporaryPath = "$LiteralPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$LiteralPath.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, $encoding)
        if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $LiteralPath, $backupPath)
        } else {
            [IO.File]::Move($temporaryPath, $LiteralPath)
        }
    } finally {
        foreach ($path in @($temporaryPath, $backupPath)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }
}

Export-ModuleMember -Function @(
    'Read-B20BoundedJsonFile',
    'Get-B20PrometheusApi',
    'Get-B20PrometheusCurrentValue',
    'Get-B20RawSeries',
    'Get-B20LegacyEvidence',
    'Set-B20AtomicLatestText'
)
