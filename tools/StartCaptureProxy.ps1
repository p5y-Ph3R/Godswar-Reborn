param(
    [Parameter(Mandatory = $true)]
    [string]$LoginHost,

    [int]$LoginPort = 5999,
    [int]$LocalLoginPort = 5999,
    [int]$LocalGamePort = 7000,
    [string]$LocalAdvertisedHost = "127.1.1.110",
    [string]$PostgresConnectionString = "Host=127.0.0.1;Port=5432;Database=godswar;Username=godswar;Password=godswar_dev_password;Pooling=true",
    [string]$Out = ".\captures\godswar-proxy.log",
    [Nullable[int]]$MonsterMapId = $null
)

$proxyArgs = @(
    "--login-host", $LoginHost,
    "--login-port", $LoginPort,
    "--local-login-port", $LocalLoginPort,
    "--local-game-port", $LocalGamePort,
    "--local-advertised-host", $LocalAdvertisedHost,
    "--postgres-connection-string", $PostgresConnectionString,
    "--out", $Out
)
if ($null -ne $MonsterMapId) {
    $proxyArgs += @("--monster-map-id", [string]$MonsterMapId)
}

dotnet run --project .\tools\Godswar.CaptureProxy -- @proxyArgs
