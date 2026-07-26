using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Game;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Operations;

internal static partial class ControlledHostValidationCommand
{
    internal static void ValidateAcceptanceOptions(
        ServerOptions options,
        string expectedCertificatePath,
        bool expectedAcceptanceFaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCertificatePath);
        var secure = options.Secure ??
            throw new InvalidDataException(
                "Secure options are required.");
        var listeners = ServerListenerProfile.Build(options);
        var target = secure.BuildGameTarget();
        var expectedCertificate =
            Path.GetFullPath(expectedCertificatePath);

        if (!secure.Enabled ||
            listeners.Transport != ServerListenerTransport.SecureTls ||
            listeners.Login.Host != "127.0.0.1" ||
            listeners.Login.Port != 6_599 ||
            secure.Login.DnsHost != "login.reborn.test" ||
            listeners.Game.Host != "127.0.0.1" ||
            listeners.Game.Port != 7_443 ||
            secure.Game.DnsHost != "game.reborn.test" ||
            target.RouteHost != "game.reborn.test" ||
            target.RoutePort != 7_000 ||
            target.TlsHost != "game.reborn.test" ||
            target.TlsPort != 7_443 ||
            target.Audience != "reborn-game" ||
            target.ServerId != 100 ||
            target.Permissions != SecureGamePermissions.EnterWorld ||
            !secure.Udp.Enabled ||
            !secure.Udp.GameplayMovementEnabled ||
            secure.Udp.BindHost != "127.0.0.1" ||
            secure.Udp.Port != 7_444 ||
            options.Authentication.AllowRegistration ||
            !options.Authentication.AllowPlaintextMigration ||
            secure.Phase4AcceptanceFaults.Enabled !=
                expectedAcceptanceFaults ||
            !Path.GetFullPath(secure.CertificatePath).Equals(
                expectedCertificate,
                StringComparison.OrdinalIgnoreCase) ||
            secure.AllowedOriginSha256.Length != 1 ||
            secure.AllowedOriginSha256[0] !=
                SecureNetworkOptions.PredecessorOriginSha256)
        {
            throw new InvalidDataException(
                "Server options do not match the controlled-host " +
                "acceptance policy.");
        }
        ValidateAcceptanceBudgets(options);
        ValidateAcceptanceRealtimePolicy();
    }

    private static void ValidateAcceptanceRealtimePolicy()
    {
        if (AuthoritativePlayerMovementPolicy.FixedStep !=
            TimeSpan.FromMilliseconds(50))
        {
            throw new InvalidDataException(
                "The controlled-host simulation fixed step is not exact.");
        }
        ValidateAcceptanceRealtimePolicyForChecks(
            AuthoritativePlayerMovementPolicy.SimulationTicksPerSecond,
            GameClientHandler.RealtimeSnapshotTicks,
            GameClientHandler.RealtimeKeyframeTicks,
            SecureUdpRuntimeCapabilities.Current.TlsFallbackVerified &&
                SecureUdpRuntimeCapabilities.Current.IsComplete);
    }

    internal static void ValidateAcceptanceRealtimePolicyForChecks(
        int simulationTicksPerSecond,
        int snapshotTicks,
        int keyframeTicks,
        bool tlsFallbackVerified)
    {
        if (simulationTicksPerSecond != 20 ||
            snapshotTicks != 2 ||
            keyframeTicks != 20 ||
            !tlsFallbackVerified)
        {
            throw new InvalidDataException(
                "Simulation, snapshot, keyframe, or fallback policy " +
                "does not match controlled-host acceptance.");
        }
    }

    private static void ValidateAcceptanceBudgets(
        ServerOptions options)
    {
        var authentication = options.Authentication;
        var network = options.Network;
        var tickets = options.Secure.Tickets;
        var udp = options.Secure.Udp;
        if (options.Storage.Provider != "postgres" ||
            string.IsNullOrWhiteSpace(
                options.Storage.PostgresConnectionString) ||
            options.Game.DeveloperCommands.Enabled ||
            options.Game.DeveloperCommands.AllowedAccountIds.Length != 0 ||
            options.Game.Monsters.Runtime != MonsterRuntimeMode.Ecs ||
            options.Game.Players.Runtime != PlayerRuntimeMode.Ecs ||

            authentication.Iterations != 600_000 ||
            authentication.MinimumStoredIterations != 100_000 ||
            authentication.MaximumStoredIterations != 2_000_000 ||
            authentication.MaximumConcurrentKdfs != 4 ||
            authentication.QueueCapacity != 64 ||
            authentication.QueueCredentialBytes != 8_192 ||
            authentication.QueueAdmissionTimeoutMilliseconds != 250 ||
            authentication.OperationTimeoutMilliseconds != 5_000 ||

            network.ListenBacklog != 512 ||
            network.MaxActiveConnections != 512 ||
            network.MaxConcurrentTlsHandshakes != 64 ||
            network.MaxUnauthenticatedConnections != 128 ||
            network.MaxUnauthenticatedConnectionsPerIp != 4 ||
            network.MaxUnauthenticatedConnectionsPerPrefix != 32 ||
            network.IngressQueueItems != 128 ||
            network.IngressQueueBytes != 524_288 ||
            network.ReliableEgressQueueItems != 128 ||
            network.ReliableEgressQueueBytes != 524_288 ||
            network.ReliableEgressPendingItems != 512 ||
            network.ReliableEgressPendingBytes != 2_097_152 ||
            network.ControlQueueItems != 32 ||
            network.ControlQueueBytes != 65_536 ||
            network.QueueAdmissionTimeoutMilliseconds != 2_000 ||
            network.TlsHandshakeTimeoutMilliseconds != 5_000 ||
            network.SecurePrefaceTimeoutMilliseconds != 2_000 ||
            network.GameBindTimeoutMilliseconds != 5_000 ||
            network.FirstPacketTimeoutMilliseconds != 10_000 ||
            network.PacketHeaderTimeoutMilliseconds != 5_000 ||
            network.PacketBodyTimeoutMilliseconds != 10_000 ||
            network.ReliableWriteTimeoutMilliseconds != 5_000 ||
            network.IdleTimeoutMilliseconds != 90_000 ||
            network.GracefulDrainTimeoutMilliseconds != 5_000 ||

            tickets.TtlSeconds != 60 ||
            tickets.Capacity != 1_024 ||

            udp.MaximumDatagramBytes != 1_200 ||
            udp.CookieLifetimeSeconds != 10 ||
            udp.CookieFutureSkewSeconds != 2 ||
            udp.CookieKeyRotationSeconds != 60 ||
            udp.SessionCapacity != 1_024 ||
            udp.BindingOfferTtlSeconds != 30 ||
            udp.GlobalPacketsPerSecond != 4_096 ||
            udp.UnvalidatedPacketsPerSecond != 3_072 ||
            udp.PrefixPacketsPerSecond != 256 ||
            udp.RateLimitPrefixCapacity != 1_024 ||
            udp.BindingProofPacketsPerSecond != 512 ||
            udp.BindingProofPrefixPacketsPerSecond != 64 ||
            udp.ProtectedCandidatePacketsPerSecond != 512 ||
            udp.ProtectedCandidatePrefixPacketsPerSecond != 256 ||
            udp.AuthenticatedSessionPacketsPerSecond != 256 ||
            udp.KeepAliveIntervalSeconds != 5 ||
            udp.BoundSessionIdleTimeoutSeconds != 30 ||
            udp.SessionCleanupIntervalSeconds != 5 ||
            udp.MinimumRebindIntervalMilliseconds != 2_000 ||
            udp.PreviousKeyEpochOverlapSeconds != 10 ||
            udp.KeyRotationSeconds != 300 ||
            udp.KeyRotationPacketLimit != 1_000_000)
        {
            throw new InvalidDataException(
                "Server resource, abuse, persistence, or ECS budgets " +
                "do not match the controlled-host acceptance policy.");
        }
    }
}
