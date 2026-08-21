using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Godswar.Server.Protocol;

namespace Godswar.Server.LegacyLoginProbe;

internal static class Program
{
    private const int MaximumPackets = 4_096;
    private const long MaximumBytes = 16L * 1024 * 1024;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            using var deadline =
                new CancellationTokenSource(TimeSpan.FromSeconds(20));
            object result = options.Mode == LegacyProbeMode.LoginRouting
                ? await LoginRoutingProbe.RunAsync(
                    options.ToLoginRoutingOptions(),
                    deadline.Token)
                : await ProbeAsync(options, deadline.Token);
            var json = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });
            if (options.OutputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                var outputPath = Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(
                    outputPath,
                    json,
                    deadline.Token);
                Console.WriteLine(outputPath);
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"Legacy login probe failed: {error.GetType().Name}: " +
                error.Message.Replace('\r', ' ').Replace('\n', ' '));
            return 1;
        }
    }

    private static async Task<ProbeResult> ProbeAsync(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<PacketRecord>();
        await using var peer = await LegacyProbePeer.ConnectAsync(
            options.Address,
            options.GamePort,
            cancellationToken);

        await peer.SendAsync(
            ProbePackets.GameLogin(
                options.Username,
                options.RealmIdentifier,
                options.RealmId),
            cancellationToken);
        await ReadThroughOpcodeAsync(
            peer,
            records,
            "game-login",
            Opcodes.RoleInfo,
            cancellationToken);

        if (options.AdmissionOnly)
        {
            return CreateResult(options, records);
        }

        await peer.SendAsync(
            ProbePackets.EnterGame(),
            cancellationToken);
        await ReadThroughOpcodeAsync(
            peer,
            records,
            "enter-game",
            Opcodes.GameServerReady,
            cancellationToken);

        await peer.SendAsync(
            ProbePackets.ServerTimeRequest(),
            cancellationToken);
        await peer.SendAsync(
            ProbePackets.ClientReady(),
            cancellationToken);
        await peer.SendAsync(
            ProbePackets.PlayerDetailRequest(),
            cancellationToken);
        await peer.SendAsync(
            ProbePackets.EnterUiReady(),
            cancellationToken);

        var observedNpc = false;
        while (true)
        {
            var packet = await ReadAndRecordAsync(
                peer,
                records,
                "post-ready",
                cancellationToken);
            var opcode = ReadOpcode(packet);
            observedNpc |= opcode == 10020;
            if (observedNpc && opcode == 10167)
            {
                break;
            }
        }

        await peer.SendAsync(
            ProbePackets.NpcDialogOpen(options.NpcId),
            cancellationToken);
        await ReadThroughOpcodeAsync(
            peer,
            records,
            "npc-dialog",
            Opcodes.NpcDialogOpen,
            cancellationToken);

        return CreateResult(options, records);
    }

    private static ProbeResult CreateResult(
        ProbeOptions options,
        IReadOnlyList<PacketRecord> records) =>
        new(
            options.Label,
            options.Address.ToString(),
            options.GamePort,
            options.Username,
            options.RealmId,
            options.RealmIdentifier,
            options.AdmissionOnly,
            DateTimeOffset.UtcNow,
            records);

    private static async Task ReadThroughOpcodeAsync(
        LegacyProbePeer peer,
        List<PacketRecord> records,
        string phase,
        ushort terminalOpcode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReadAndRecordAsync(
                peer,
                records,
                phase,
                cancellationToken);
            if (ReadOpcode(packet) == terminalOpcode)
            {
                return;
            }
        }
    }

    private static async Task<byte[]> ReadAndRecordAsync(
        LegacyProbePeer peer,
        List<PacketRecord> records,
        string phase,
        CancellationToken cancellationToken)
    {
        if (records.Count >= MaximumPackets)
        {
            throw new InvalidDataException(
                $"Packet limit {MaximumPackets} exceeded.");
        }

        var packet = await peer.ReadAsync(cancellationToken);
        var totalBytes = records.Sum(record => (long)record.Length) +
            packet.Length;
        if (totalBytes > MaximumBytes)
        {
            throw new InvalidDataException(
                $"Byte limit {MaximumBytes} exceeded.");
        }

        var opcode = ReadOpcode(packet);
        records.Add(new PacketRecord(
            records.Count,
            phase,
            opcode,
            Opcodes.Name(opcode),
            packet.Length,
            Convert.ToHexString(SHA256.HashData(packet)),
            Convert.ToBase64String(packet)));
        return packet;
    }

    private static ushort ReadOpcode(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);

    private sealed record ProbeResult(
        string Label,
        string Host,
        int GamePort,
        string Username,
        byte RealmId,
        string RealmIdentifier,
        bool AdmissionOnly,
        DateTimeOffset CompletedAt,
        IReadOnlyList<PacketRecord> Packets);

    private sealed record PacketRecord(
        int Index,
        string Phase,
        ushort Opcode,
        string Name,
        int Length,
        string Sha256,
        string ClearBytesBase64);

    private sealed record ProbeOptions(
        LegacyProbeMode Mode,
        string Label,
        IPAddress Address,
        int LoginPort,
        int GamePort,
        string Username,
        string? Password,
        byte RealmId,
        string RealmIdentifier,
        string? ExpectedGameHost,
        IReadOnlyList<LoginRoutingExpectedRealm> ExpectedRealms,
        bool AdmissionOnly,
        uint NpcId,
        string? OutputPath)
    {
        public LoginRoutingProbeOptions ToLoginRoutingOptions() =>
            new(
                Label,
                Address,
                LoginPort,
                Username,
                Password ?? throw new ArgumentException(
                    "--password is required in login-routing mode."),
                RealmId,
                RealmIdentifier,
                ExpectedGameHost ?? throw new ArgumentException(
                    "--expected-game-host is required in login-routing mode."),
                GamePort,
                ExpectedRealms);

        public static ProbeOptions Parse(string[] args)
        {
            var mode = LegacyProbeMode.Game;
            string? label = null;
            IPAddress? address = null;
            var loginPort = 5_998;
            var gamePort = 7000;
            string? username = null;
            string? password = null;
            byte realmId = 1;
            var realmIdentifier = "KAL3jcIzqGgKvOf1dbYZKC8cS";
            string? expectedGameHost = null;
            var expectedRealms = new List<LoginRoutingExpectedRealm>();
            var admissionOnly = false;
            uint npcId = 5083;
            string? output = null;

            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(
                        "Arguments must be name/value pairs.");
                }

                switch (args[index])
                {
                    case "--mode":
                        mode = args[index + 1] switch
                        {
                            "game" => LegacyProbeMode.Game,
                            "login-routing" =>
                                LegacyProbeMode.LoginRouting,
                            _ => throw new ArgumentException(
                                "--mode must be game or login-routing.")
                        };
                        break;
                    case "--label":
                        label = args[index + 1];
                        break;
                    case "--host":
                        if (!IPAddress.TryParse(args[index + 1], out address) ||
                            address.AddressFamily !=
                                System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            throw new ArgumentException(
                                "--host must be an IPv4 literal.");
                        }
                        break;
                    case "--login-port":
                        if (!int.TryParse(args[index + 1], out loginPort) ||
                            loginPort is < 1 or > 65_535)
                        {
                            throw new ArgumentException(
                                "--login-port must be 1..65535.");
                        }
                        break;
                    case "--game-port":
                        if (!int.TryParse(args[index + 1], out gamePort) ||
                            gamePort is < 1 or > 65_535)
                        {
                            throw new ArgumentException(
                                "--game-port must be 1..65535.");
                        }
                        break;
                    case "--username":
                        username = args[index + 1];
                        break;
                    case "--password":
                        password = args[index + 1];
                        break;
                    case "--realm-id":
                        if (!byte.TryParse(args[index + 1], out realmId) ||
                            realmId == 0)
                        {
                            throw new ArgumentException(
                                "--realm-id must be 1..255.");
                        }
                        break;
                    case "--realm-identifier":
                        realmIdentifier = args[index + 1];
                        break;
                    case "--expected-game-host":
                        expectedGameHost = args[index + 1];
                        break;
                    case "--expected-realm":
                        expectedRealms.Add(ParseExpectedRealm(
                            args[index + 1]));
                        break;
                    case "--admission-only":
                        if (!bool.TryParse(
                            args[index + 1],
                            out admissionOnly))
                        {
                            throw new ArgumentException(
                                "--admission-only must be true or false.");
                        }
                        break;
                    case "--npc-id":
                        if (!uint.TryParse(args[index + 1], out npcId) ||
                            npcId == 0)
                        {
                            throw new ArgumentException(
                                "--npc-id must be a positive UInt32 value.");
                        }
                        break;
                    case "--output":
                        output = args[index + 1];
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown argument '{args[index]}'.");
                }
            }

            if (address is null || string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException(
                    "--host and --username are required.");
            }
            if (username.Length > 31)
            {
                throw new ArgumentException(
                    "--username must fit the 32-byte legacy field.");
            }
            if (realmIdentifier.Length != 25 ||
                realmIdentifier.Any(
                    static character => character is < '!' or > '~'))
            {
                throw new ArgumentException(
                    "--realm-identifier must be exactly 25 printable ASCII bytes.");
            }
            if (password is not null &&
                (password.Length > 32 ||
                 password.Any(
                    static character => character is < ' ' or > '~')))
            {
                throw new ArgumentException(
                    "--password must be at most 32 printable ASCII bytes.");
            }
            if (mode == LegacyProbeMode.LoginRouting)
            {
                if (password is null ||
                    string.IsNullOrWhiteSpace(expectedGameHost) ||
                    expectedRealms.Count == 0)
                {
                    throw new ArgumentException(
                        "Login routing requires --password, " +
                        "--expected-game-host, and at least one " +
                        "--expected-realm.");
                }
            }

            return new ProbeOptions(
                mode,
                string.IsNullOrWhiteSpace(label)
                    ? address.ToString()
                    : label,
                address,
                loginPort,
                gamePort,
                username,
                password,
                realmId,
                realmIdentifier,
                expectedGameHost,
                expectedRealms,
                admissionOnly,
                npcId,
                output);
        }

        private static LoginRoutingExpectedRealm ParseExpectedRealm(
            string value)
        {
            var fields = value.Split(':');
            if (fields.Length != 3 ||
                !byte.TryParse(fields[0], out var realmId) ||
                realmId == 0 ||
                string.IsNullOrWhiteSpace(fields[1]) ||
                fields[1].Length > 35 ||
                fields[1].Any(
                    static character => character is < '!' or > '~') ||
                !bool.TryParse(fields[2], out var recommended))
            {
                throw new ArgumentException(
                    "--expected-realm must be id:name:true|false.");
            }

            return new LoginRoutingExpectedRealm(
                realmId,
                fields[1],
                recommended);
        }
    }
}
