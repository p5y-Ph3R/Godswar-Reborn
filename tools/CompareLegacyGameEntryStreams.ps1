[CmdletBinding()]
param(
    [string]$Username = 'test2',
    [int]$Port = 7000,
    [string[]]$Hosts = @('127.1.1.110', '127.1.1.111')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CipherTables {
    $path = Join-Path $PSScriptRoot `
        '..\src\Godswar.Server\Packets\ReferencePackets.Generated.cs'
    $source = Get-Content -LiteralPath $path -Raw
    $one = [regex]::Match(
        $source,
        'HashOne\s*=>\s*new byte\[\]\s*\{(?<body>.*?)\};',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    $two = [regex]::Match(
        $source,
        'HashTwo\s*=>\s*new byte\[\]\s*\{(?<body>.*?)\};',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $one.Success -or -not $two.Success) {
        throw 'Could not read the generated legacy cipher tables.'
    }

    $parse = {
        param([string]$Body)
        [byte[]]@([regex]::Matches($Body, '0x(?<hex>[0-9A-Fa-f]{2})') |
            ForEach-Object {
                [Convert]::ToByte($_.Groups['hex'].Value, 16)
            })
    }
    $hashOne = & $parse $one.Groups['body'].Value
    $hashTwo = & $parse $two.Groups['body'].Value
    if ($hashOne.Length -ne 256 -or $hashTwo.Length -ne 256) {
        throw 'Legacy cipher tables must each contain 256 bytes.'
    }

    return [pscustomobject]@{
        HashOne = $hashOne
        HashTwo = $hashTwo
    }
}

function Invoke-Cipher {
    param(
        [byte[]]$Bytes,
        [int]$Pointer,
        [byte[]]$HashOne,
        [byte[]]$HashTwo
    )

    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        $cursor = $Pointer
        $Bytes[$index] = $Bytes[$index] -bxor
            $HashOne[$cursor] -bxor $HashTwo[$cursor]
        $Pointer = ($cursor + 1) -band 0xff
    }

    return $Pointer
}

function New-LegacyPacket {
    param(
        [uint16]$Opcode,
        [byte[]]$Payload = @()
    )

    [byte[]]$packet = New-Object byte[] (4 + $Payload.Length)
    [Buffer]::BlockCopy(
        [BitConverter]::GetBytes([uint16]$packet.Length),
        0,
        $packet,
        0,
        2)
    [Buffer]::BlockCopy(
        [BitConverter]::GetBytes($Opcode),
        0,
        $packet,
        2,
        2)
    if ($Payload.Length -gt 0) {
        [Buffer]::BlockCopy($Payload, 0, $packet, 4, $Payload.Length)
    }
    return $packet
}

function Read-Exactly {
    param(
        [IO.Stream]$Stream,
        [byte[]]$Buffer
    )

    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($read -eq 0) {
            throw 'The game server closed the probe connection.'
        }
        $offset += $read
    }
}

$tables = Get-CipherTables
$hashOne = [byte[]]$tables.HashOne
$hashTwo = [byte[]]$tables.HashTwo

foreach ($hostName in $Hosts) {
    $client = [Net.Sockets.TcpClient]::new()
    $client.NoDelay = $true
    $client.Connect($hostName, $Port)
    $stream = $client.GetStream()
    $stream.ReadTimeout = 3000
    $stream.WriteTimeout = 3000
    $cipherState = [pscustomobject]@{
        SendPointer = 0
        ReceivePointer = 0
    }
    $packetIndex = 0

    try {
        Write-Output "HOST|$hostName|$Port"

        $send = {
            param([byte[]]$Packet)
            $cipherState.SendPointer = Invoke-Cipher `
                -Bytes $Packet `
                -Pointer $cipherState.SendPointer `
                -HashOne $hashOne `
                -HashTwo $hashTwo
            $stream.Write($Packet, 0, $Packet.Length)
            $stream.Flush()
        }

        $drain = {
            param(
                [string]$Stage,
                [int]$QuietMilliseconds = 250,
                [int]$MaximumMilliseconds = 2500
            )

            $clock = [Diagnostics.Stopwatch]::StartNew()
            $lastPacketAt = 0L
            $receivedAny = $false
            while ($clock.ElapsedMilliseconds -lt $MaximumMilliseconds) {
                if (-not $stream.DataAvailable) {
                    $quietFor = if ($receivedAny) {
                        $clock.ElapsedMilliseconds - $lastPacketAt
                    }
                    else {
                        $clock.ElapsedMilliseconds
                    }
                    if ($quietFor -ge $QuietMilliseconds) {
                        break
                    }
                    Start-Sleep -Milliseconds 10
                    continue
                }

                [byte[]]$header = New-Object byte[] 2
                Read-Exactly -Stream $stream -Buffer $header
                $cipherState.ReceivePointer = Invoke-Cipher `
                    -Bytes $header `
                    -Pointer $cipherState.ReceivePointer `
                    -HashOne $hashOne `
                    -HashTwo $hashTwo
                $length = [BitConverter]::ToUInt16($header, 0)
                if ($length -lt 4 -or $length -gt 65535) {
                    throw "Invalid packet length $length from $hostName."
                }
                [byte[]]$tail = New-Object byte[] ($length - 2)
                Read-Exactly -Stream $stream -Buffer $tail
                $cipherState.ReceivePointer = Invoke-Cipher `
                    -Bytes $tail `
                    -Pointer $cipherState.ReceivePointer `
                    -HashOne $hashOne `
                    -HashTwo $hashTwo
                $opcode = [BitConverter]::ToUInt16($tail, 0)
                [byte[]]$packet = $header + $tail
                $previewLength = $packet.Length
                $preview = ($packet[0..($previewLength - 1)] |
                    ForEach-Object { $_.ToString('X2') }) -join ''
                Write-Output (
                    "PACKET|$hostName|$packetIndex|$Stage|$length|" +
                    "$opcode|$preview")
                $packetIndex++
                $receivedAny = $true
                $lastPacketAt = $clock.ElapsedMilliseconds
            }
        }

        [byte[]]$loginPayload = New-Object byte[] 32
        $nameBytes = [Text.Encoding]::ASCII.GetBytes($Username)
        [Buffer]::BlockCopy(
            $nameBytes,
            0,
            $loginPayload,
            0,
            [Math]::Min($nameBytes.Length, $loginPayload.Length))
        & $send (New-LegacyPacket -Opcode 10000 -Payload $loginPayload)
        & $drain 'login' 350 3000

        & $send (New-LegacyPacket -Opcode 10002)
        & $drain 'role-1'
        & $send (New-LegacyPacket -Opcode 10002)
        & $drain 'role-2'
        & $send (New-LegacyPacket -Opcode 10006)
        & $drain 'enter'
        & $send (New-LegacyPacket -Opcode 10007)
        & $drain 'client-ready'

        [byte[]]$detailPayload = New-Object byte[] 8
        [Buffer]::BlockCopy(
            [BitConverter]::GetBytes([uint32]0x1448),
            0,
            $detailPayload,
            0,
            4)
        & $send (New-LegacyPacket -Opcode 10200 -Payload $detailPayload)
        & $drain 'player-detail' 350 3000

        [byte[]]$ackPayload = New-Object byte[] 12
        [Buffer]::BlockCopy(
            [BitConverter]::GetBytes([uint32]0x1448),
            0,
            $ackPayload,
            0,
            4)
        [Buffer]::BlockCopy(
            [BitConverter]::GetBytes([uint32]1),
            0,
            $ackPayload,
            4,
            4)
        & $send (New-LegacyPacket -Opcode 10202 -Payload $ackPayload)
        & $drain 'detail-ack'

        & $send (New-LegacyPacket -Opcode 10357)
        & $drain 'ui-ready' 500 5000
    }
    finally {
        $stream.Dispose()
        $client.Dispose()
    }
}
