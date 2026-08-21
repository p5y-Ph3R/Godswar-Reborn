using System.Net;

namespace Godswar.Server.CombatDummyHost;

internal sealed record CombatDummyHostOptions(
    IPAddress Address,
    int GamePort,
    TimeSpan HeartbeatInterval,
    TimeSpan ReconnectDelay,
    bool ExitAfterReady,
    string ReadinessFile,
    string SingletonFile,
    string OwnerFile)
{
    internal const int CorpseRetentionSeconds = 5;
    internal const int MinimumReconnectSeconds = 6;
    internal const int DefaultReconnectSeconds = 10;

    internal TimeSpan CorpseRetentionDelay =>
        TimeSpan.FromSeconds(CorpseRetentionSeconds);

    internal TimeSpan PostRemovalReconnectDelay =>
        ReconnectDelay - CorpseRetentionDelay;

    public static CombatDummyHostOptions Parse(string[] args)
    {
        var address = IPAddress.Parse("127.1.1.111");
        var gamePort = 7000;
        var heartbeatSeconds = 30;
        // Match the captured ten-second death-to-respawn lifecycle. The
        // connection keeps ownership for the five-second corpse window, then
        // the remaining delay separates 0x2728 removal from same-ID re-entry.
        var reconnectSeconds = DefaultReconnectSeconds;
        var exitAfterReady = false;
        var readinessFile = Path.GetFullPath(
            "artifacts/development-combat-dummies/readiness.json");
        var singletonFile = Path.GetFullPath(
            "artifacts/development-combat-dummies/host.lock");
        var ownerFile = Path.GetFullPath(
            "artifacts/development-combat-dummies/owner.json");
        var identityManifest = CombatDummyDefinition.IdentityManifest;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--host":
                    address = ParseAddress(Next(args, ref index));
                    break;
                case "--game-port":
                    gamePort = ParseGamePort(Next(args, ref index));
                    break;
                case "--heartbeat-seconds":
                    heartbeatSeconds = ParseRange(
                        Next(args, ref index),
                        "--heartbeat-seconds",
                        5,
                        60);
                    break;
                case "--reconnect-seconds":
                    reconnectSeconds = ParseRange(
                        Next(args, ref index),
                        "--reconnect-seconds",
                        MinimumReconnectSeconds,
                        30);
                    break;
                case "--once":
                    exitAfterReady = true;
                    break;
                case "--readiness-file":
                    readinessFile = Path.GetFullPath(Next(args, ref index));
                    break;
                case "--singleton-file":
                    singletonFile = Path.GetFullPath(Next(args, ref index));
                    break;
                case "--owner-file":
                    ownerFile = Path.GetFullPath(Next(args, ref index));
                    break;
                case "--identity-manifest":
                    identityManifest = Next(args, ref index);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[index]}'.");
            }
        }

        if (!string.Equals(
                identityManifest,
                CombatDummyDefinition.IdentityManifest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The server training-dummy identity manifest does not " +
                "match the host's immutable identity manifest.");
        }

        return new CombatDummyHostOptions(
            address,
            gamePort,
            TimeSpan.FromSeconds(heartbeatSeconds),
            TimeSpan.FromSeconds(reconnectSeconds),
            exitAfterReady,
            readinessFile,
            singletonFile,
            ownerFile);
    }

    private static string Next(string[] args, ref int index)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException(
                $"Argument '{args[index - 1]}' requires a value.");
        }

        return args[index];
    }

    private static IPAddress ParseAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork ||
            !address.Equals(IPAddress.Parse("127.1.1.111")))
        {
            throw new ArgumentException(
                "Combat dummies are restricted to development host " +
                "127.1.1.111.");
        }

        return address;
    }

    private static int ParseRange(
        string value,
        string argument,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(value, out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentException(
                $"{argument} must be {minimum}..{maximum}.");
        }

        return parsed;
    }

    private static int ParseGamePort(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed != 7000)
        {
            throw new ArgumentException(
                "Combat dummies are restricted to development game port " +
                "7000.");
        }

        return parsed;
    }
}
