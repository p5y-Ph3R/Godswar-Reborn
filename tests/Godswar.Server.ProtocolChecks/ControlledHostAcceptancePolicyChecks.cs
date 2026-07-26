using Godswar.Server.Networking.Secure;
using Godswar.Server.Operations;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class ControlledHostAcceptancePolicyChecks
{
    private const string CertificatePath =
        @"C:\ProgramData\RebornSecureNetworkRuntime\fixture\tls\server.pfx";

    internal static Task RunAsync()
    {
        ControlledHostValidationCommand.ValidateAcceptanceOptions(
            Valid(),
            CertificatePath,
            expectedAcceptanceFaults: false);
        ControlledHostValidationCommand.ValidateAcceptanceOptions(
            Valid(acceptanceFaults: true),
            CertificatePath,
            expectedAcceptanceFaults: true);
        ControlledHostValidationCommand
            .ValidateAcceptanceRealtimePolicyForChecks(
                20,
                2,
                20,
                tlsFallbackVerified: true);
        RejectRealtime(19, 2, 20, true, "simulation tick rate");
        RejectRealtime(20, 3, 20, true, "snapshot cadence");
        RejectRealtime(20, 2, 19, true, "keyframe cadence");
        RejectRealtime(20, 2, 20, false, "TLS fallback capability");

        Reject(options => options.Secure.Enabled = false, "raw listener");
        Reject(
            options => options.Secure.Login.BindHost = "127.0.0.2",
            "login bind");
        Reject(
            options => options.Secure.Login.Port = 6600,
            "login port");
        Reject(
            options => options.Secure.Login.DnsHost =
                "login.reborn.test.evil",
            "login DNS");
        Reject(
            options => options.Secure.Game.BindHost = "::1",
            "game bind");
        Reject(
            options => options.Secure.Game.Port = 7442,
            "game TLS port");
        Reject(
            options => options.Secure.Game.DnsHost =
                "other.reborn.test",
            "game DNS");
        Reject(
            options => options.Secure.Tickets.RouteHost =
                "other.reborn.test",
            "route host");
        Reject(
            options => options.Secure.Tickets.RoutePort = 7001,
            "route port");
        Reject(
            options => options.Secure.Tickets.Audience = "other",
            "audience");
        Reject(
            options => options.Secure.Tickets.TargetServerId = 101,
            "server ID");
        Reject(
            options => options.Secure.Tickets.Permissions = 2,
            "permissions");
        Reject(
            options => options.Secure.Udp.Enabled = false,
            "UDP disabled");
        Reject(
            options =>
                options.Secure.Udp.GameplayMovementEnabled = false,
            "UDP movement disabled");
        Reject(
            options => options.Secure.Udp.BindHost = "127.0.0.2",
            "UDP bind");
        Reject(
            options => options.Secure.Udp.Port = 7445,
            "UDP port");
        Reject(
            options =>
                options.Authentication.AllowRegistration = true,
            "registration");
        Reject(
            options =>
                options.Authentication.AllowPlaintextMigration = false,
            "plaintext migration");
        Reject(
            options => options.Secure.CertificatePath =
                @"C:\other.pfx",
            "certificate path");
        Reject(
            options => options.Secure.AllowedOriginSha256 =
                [
                    SecureNetworkOptions.PredecessorOriginSha256,
                    new string('A', 64)
                ],
            "extra allowed build");
        Reject(
            options => options.Storage.Provider = "json",
            "non-PostgreSQL storage");
        Reject(
            options => options.Authentication.Iterations = 600_001,
            "authentication KDF budget");
        Reject(
            options => options.Authentication.MaximumConcurrentKdfs = 5,
            "authentication concurrency budget");
        Reject(
            options => options.Authentication.QueueCapacity = 65,
            "authentication queue budget");
        Reject(
            options => options.Network.MaxActiveConnections = 513,
            "TCP connection budget");
        Reject(
            options => options.Network.IngressQueueBytes = 524_289,
            "TCP queue budget");
        Reject(
            options =>
                options.Network.TlsHandshakeTimeoutMilliseconds = 5_001,
            "TCP timeout budget");
        Reject(
            options => options.Secure.Tickets.TtlSeconds = 61,
            "ticket lifetime");
        Reject(
            options => options.Secure.Tickets.Capacity = 1_025,
            "ticket capacity");
        Reject(
            options => options.Secure.Udp.MaximumDatagramBytes = 1_199,
            "UDP MTU budget");
        Reject(
            options => options.Secure.Udp.CookieLifetimeSeconds = 11,
            "UDP cookie lifetime");
        Reject(
            options =>
                options.Secure.Udp.GlobalPacketsPerSecond = 4_097,
            "UDP global admission budget");
        Reject(
            options => options.Secure.Udp.SessionCapacity = 1_025,
            "UDP session capacity");
        Reject(
            options =>
                options.Secure.Udp.MinimumRebindIntervalMilliseconds =
                    2_001,
            "UDP rebinding budget");
        Reject(
            options => options.Secure.Udp.KeyRotationSeconds = 301,
            "UDP key rotation budget");
        Reject(
            options => options.Game.DeveloperCommands.Enabled = true,
            "developer commands enabled");
        Reject(
            options =>
                options.Game.DeveloperCommands.AllowedAccountIds = [7],
            "developer account allowlist");
        Reject(
            options => options.Game.Monsters.Runtime =
                MonsterRuntimeMode.Legacy,
            "monster ECS runtime");
        Reject(
            options => options.Game.Players.Runtime =
                PlayerRuntimeMode.Legacy,
            "player ECS runtime");

        var unexpectedFaults = Valid(acceptanceFaults: true);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateAcceptanceOptions(
                    unexpectedFaults,
                    CertificatePath,
                    expectedAcceptanceFaults: false),
            "unexpected acceptance-fault state");
        return Task.CompletedTask;
    }

    private static void Reject(
        Action<ServerOptions> mutate,
        string description)
    {
        var options = Valid();
        mutate(options);
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateAcceptanceOptions(
                    options,
                    CertificatePath,
                    expectedAcceptanceFaults: false),
            description);
    }

    private static void RejectRealtime(
        int simulationTicksPerSecond,
        int snapshotTicks,
        int keyframeTicks,
        bool tlsFallbackVerified,
        string description)
    {
        Check.Throws<Exception>(
            () => ControlledHostValidationCommand
                .ValidateAcceptanceRealtimePolicyForChecks(
                    simulationTicksPerSecond,
                    snapshotTicks,
                    keyframeTicks,
                    tlsFallbackVerified),
            description);
    }

    private static ServerOptions Valid(
        bool acceptanceFaults = false)
    {
        var options = new ServerOptions();
        options.Secure.Enabled = true;
        options.Secure.Udp.Enabled = true;
        options.Secure.Udp.GameplayMovementEnabled = true;
        options.Secure.CertificatePath = CertificatePath;
        options.Storage.Provider = "postgres";
        options.Storage.PostgresConnectionString =
            "Host=127.0.0.1;Database=acceptance";
        options.Authentication.MaximumConcurrentKdfs = 4;
        var variable =
            "GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED";
        var previous =
            Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(
                variable,
                acceptanceFaults ? "true" : "false");
            options.Secure.Phase4AcceptanceFaults.ApplyEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
        return options;
    }
}
