using System.Net;
using System.Text.Json;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecurePhase4AcceptanceFaultChecks
{
    private static readonly IPEndPoint LoopbackEndpoint =
        new(IPAddress.Loopback, 7444);

    public static Task RunAsync()
    {
        CheckActivationGuards();
        CheckOneShotFallbackStateMachine();
        CheckTimeAndEvidenceBounds();
        CheckLowCardinalityEvidence();
        return Task.CompletedTask;
    }

    private static void CheckActivationGuards()
    {
        using var environment = new EnvironmentVariableScope(
            SecurePhase4AcceptanceFaultOptions
                .EnabledEnvironmentVariable,
            SecurePhase4AcceptanceFaultOptions
                .RuntimeEnvironmentVariable,
            SecurePhase4AcceptanceFaultOptions
                .AspNetRuntimeEnvironmentVariable);
        environment.Clear();

        var defaults = new SecurePhase4AcceptanceFaultOptions();
        defaults.ApplyEnvironment();
        defaults.Validate(CreateSecureProfile());
        Check.True(
            !defaults.Enabled &&
            SecurePhase4AcceptanceFaults.Create(defaults) is null,
            "acceptance faults are inert without the explicit environment flag");

        var serialized = JsonSerializer.Serialize(
            CreateSecureProfile(),
            JsonDefaults.Indented);
        Check.True(
            !serialized.Contains(
                "phase4AcceptanceFaults",
                StringComparison.OrdinalIgnoreCase),
            "acceptance fault activation is excluded from JSON");
        var jsonProfile = JsonSerializer.Deserialize<SecureNetworkOptions>(
            """
            {
              "phase4AcceptanceFaults": {
                "enabled": true
              }
            }
            """,
            JsonDefaults.Indented)
            ?? throw new InvalidOperationException(
                "Secure options JSON did not deserialize.");
        Check.True(
            !jsonProfile.Phase4AcceptanceFaults.Enabled,
            "JSON cannot arm the acceptance fault hook");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .EnabledEnvironmentVariable,
            "not-a-boolean");
        Check.Throws<InvalidDataException>(
            () => new SecurePhase4AcceptanceFaultOptions()
                .ApplyEnvironment(),
            "malformed acceptance activation fails closed");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .EnabledEnvironmentVariable,
            "false");
        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .RuntimeEnvironmentVariable,
            "Production");
        var explicitlyDisabled =
            new SecurePhase4AcceptanceFaultOptions();
        explicitlyDisabled.ApplyEnvironment();
        explicitlyDisabled.Validate(CreateSecureProfile());
        Check.True(
            SecurePhase4AcceptanceFaults.Create(
                explicitlyDisabled) is null,
            "an explicit false flag stays inert in production");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .EnabledEnvironmentVariable,
            "true");
        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .RuntimeEnvironmentVariable,
            null);
        var ambiguousRuntime = ApplyAcceptanceEnvironment();
        Check.Throws<InvalidDataException>(
            () => ambiguousRuntime.Validate(
                CreateSecureProfile()),
            "activation requires DOTNET_ENVIRONMENT Development");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .RuntimeEnvironmentVariable,
            "Production");
        var productionRuntime = ApplyAcceptanceEnvironment();
        Check.Throws<InvalidDataException>(
            () => productionRuntime.Validate(
                CreateSecureProfile()),
            "production runtime cannot activate acceptance faults");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .RuntimeEnvironmentVariable,
            "Development");
        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .AspNetRuntimeEnvironmentVariable,
            "Production");
        var conflictingRuntime = ApplyAcceptanceEnvironment();
        Check.Throws<InvalidDataException>(
            () => conflictingRuntime.Validate(
                CreateSecureProfile()),
            "conflicting ASP.NET production runtime cannot activate faults");

        environment.Set(
            SecurePhase4AcceptanceFaultOptions
                .AspNetRuntimeEnvironmentVariable,
            null);
        var enabled = ApplyAcceptanceEnvironment();
        var validProfile = CreateSecureProfile();
        enabled.Validate(validProfile);
        Check.True(
            enabled.Enabled &&
            SecurePhase4AcceptanceFaults.Create(enabled) is not null,
            "explicit Development loopback activation creates the hook");

        var ipv6Profile = CreateSecureProfile();
        ipv6Profile.Login.BindHost = "::1";
        ipv6Profile.Game.BindHost = "::1";
        ipv6Profile.Udp.BindHost = "::1";
        enabled.Validate(ipv6Profile);

        AssertProfileRejected(
            enabled,
            profile => profile.Enabled = false,
            "disabled TLS profile");
        AssertProfileRejected(
            enabled,
            profile => profile.Udp.Enabled = false,
            "disabled UDP profile");
        AssertProfileRejected(
            enabled,
            profile =>
                profile.Udp.GameplayMovementEnabled = false,
            "disabled authoritative movement profile");
        AssertProfileRejected(
            enabled,
            profile => profile.Login.BindHost = "0.0.0.0",
            "wildcard TLS login bind");
        AssertProfileRejected(
            enabled,
            profile => profile.Game.BindHost = "192.168.1.25",
            "private non-loopback TLS game bind");
        AssertProfileRejected(
            enabled,
            profile => profile.Udp.BindHost = "localhost",
            "non-literal UDP hostname bind");
    }

    private static void CheckOneShotFallbackStateMachine()
    {
        var time = new ManualTimeProvider();
        var faults = new SecurePhase4AcceptanceFaults(time);
        var selected = new SecureUdpConnectionKey(1, 2);
        var other = new SecureUdpConnectionKey(3, 4);
        var eligible = CreateSnapshot();

        Check.True(
            !faults.ShouldDropSnapshot(
                Dispatch(
                    selected,
                    eligible with
                    {
                        AcknowledgedInputId = 0
                    })),
            "zero-ack baseline cannot start the campaign");
        Check.True(
            !faults.ShouldDropSnapshot(
                Dispatch(
                    selected,
                    eligible with
                    {
                        Flags =
                            SecureRealtimeSnapshotFlags.Correction
                    })),
            "correction snapshot cannot start the campaign");
        Check.True(
            faults.ShouldDropSnapshot(
                Dispatch(selected, eligible)),
            "first eligible epoch-one ACK starts and selects the campaign");
        Check.True(
            !faults.ShouldDropSnapshot(
                Dispatch(other, eligible)),
            "process-global campaign does not affect another connection");

        Check.True(
            !faults.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Udp,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 1,
                    inputId: 2)),
            "UDP input cannot force the acceptance correction");
        Check.True(
            !faults.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind
                        .TransportTransition,
                    epoch: 2,
                    inputId: 1)),
            "TLS transition retry cannot force a correction");
        Check.True(
            !faults.ShouldForceCorrection(
                other,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 2,
                    inputId: 2)),
            "another connection cannot consume the selected campaign");
        Check.True(
            !faults.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 3,
                    inputId: 2)),
            "non-adjacent TLS epoch cannot consume the campaign");

        Check.True(
            !faults.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 2,
                    inputId: 1)),
            "fallback retry at the trigger ACK is observed but not corrected");
        var fallback = faults.GetSnapshot();
        Check.True(
            fallback.TlsFallbackObserved &&
            fallback.ForcedCorrections == 0,
            "adjacent-epoch TLS fallback is recorded before correction");

        var correctionIngress = CreateIngress(
            SecureRealtimeTransportSource.Tls,
            SecureRealtimeMovementIngressKind.Input,
            epoch: 2,
            inputId: 2);
        var forcedCalls = 0;
        Parallel.For(
            0,
            64,
            _ =>
            {
                if (faults.ShouldForceCorrection(
                        selected,
                        correctionIngress))
                {
                    Interlocked.Increment(ref forcedCalls);
                }
            });
        Check.Equal(
            1,
            forcedCalls,
            "concurrent fallback delivery forces exactly one correction");

        Check.True(
            !faults.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 2,
                    inputId: 3)),
            "later TLS movement is observed without another correction");
        var complete = faults.GetSnapshot();
        Check.True(
            complete.State ==
                SecurePhase4AcceptanceFaultState.Complete &&
            complete.ForcedCorrections == 1 &&
            complete.TlsNoSwitchbackObserved,
            "one-way TLS continuation completes the one-shot campaign");
        Check.True(
            faults.ShouldDropSnapshot(
                Dispatch(selected, eligible)),
            "completed campaign still suppresses old UDP ACKs until the deadline");
        time.Advance(
            SecurePhase4AcceptanceFaults.SnapshotDropWindow);
        Check.True(
            !faults.ShouldDropSnapshot(
                Dispatch(selected, eligible)) &&
            !faults.ShouldDropSnapshot(
                Dispatch(other, eligible)),
            "completed campaign stops at its deadline and cannot rearm");
    }

    private static void CheckTimeAndEvidenceBounds()
    {
        var time = new ManualTimeProvider();
        var faults = new SecurePhase4AcceptanceFaults(time);
        var selected = new SecureUdpConnectionKey(10, 20);
        var dispatch = Dispatch(selected, CreateSnapshot());

        for (var index = 0; index < 128; index++)
        {
            Check.True(
                faults.ShouldDropSnapshot(dispatch),
                "high-rate ACK snapshots remain suppressed for the full window");
        }
        var saturated = faults.GetSnapshot();
        Check.Equal(
            SecurePhase4AcceptanceFaults
                .MaximumRecordedDroppedSnapshots,
            saturated.RecordedDroppedSnapshots,
            "drop evidence count saturates under high-rate traffic");

        time.Advance(
            SecurePhase4AcceptanceFaults.SnapshotDropWindow -
            TimeSpan.FromMilliseconds(1));
        Check.True(
            faults.ShouldDropSnapshot(dispatch),
            "selected ACK remains suppressed just before the deadline");
        Check.Equal(
            SecurePhase4AcceptanceFaults
                .MaximumRecordedDroppedSnapshots,
            faults.GetSnapshot().RecordedDroppedSnapshots,
            "saturated evidence does not grow while suppression continues");

        time.Advance(TimeSpan.FromMilliseconds(1));
        Check.True(
            !faults.ShouldDropSnapshot(dispatch),
            "ACK suppression ends at the exact 1.5-second deadline");
        Check.True(
            faults.GetSnapshot().State ==
                SecurePhase4AcceptanceFaultState
                    .AwaitingTlsFallback,
            "deadline transitions the campaign to TLS fallback observation");

        var expiryTime = new ManualTimeProvider();
        var expiring = new SecurePhase4AcceptanceFaults(
            expiryTime);
        Check.True(
            expiring.ShouldDropSnapshot(dispatch),
            "expiry campaign starts from an eligible ACK");
        expiryTime.Advance(
            SecurePhase4AcceptanceFaults.CampaignLifetime);
        var expired = expiring.GetSnapshot();
        Check.True(
            expired.State ==
                SecurePhase4AcceptanceFaultState.Expired &&
            expired.Expired,
            "campaign expires at its exact lifetime bound");
        Check.True(
            !expiring.ShouldForceCorrection(
                selected,
                CreateIngress(
                    SecureRealtimeTransportSource.Tls,
                    SecureRealtimeMovementIngressKind.Input,
                    epoch: 2,
                    inputId: 2)) &&
            !expiring.ShouldDropSnapshot(dispatch),
            "expired campaign cannot drop or force");
    }

    private static SecurePhase4AcceptanceFaultOptions
        ApplyAcceptanceEnvironment()
    {
        var options = new SecurePhase4AcceptanceFaultOptions();
        options.ApplyEnvironment();
        return options;
    }

    private static SecureNetworkOptions CreateSecureProfile()
    {
        var options = new SecureNetworkOptions
        {
            Enabled = true
        };
        options.Login.BindHost = "127.0.0.1";
        options.Game.BindHost = "127.0.0.1";
        options.Udp.Enabled = true;
        options.Udp.GameplayMovementEnabled = true;
        options.Udp.BindHost = "127.0.0.1";
        return options;
    }

    private static void AssertProfileRejected(
        SecurePhase4AcceptanceFaultOptions acceptance,
        Action<SecureNetworkOptions> mutate,
        string description)
    {
        var profile = CreateSecureProfile();
        mutate(profile);
        Check.Throws<InvalidDataException>(
            () => acceptance.Validate(profile),
            $"{description} is rejected");
    }

    private static SecureRealtimePositionSnapshot
        CreateSnapshot() =>
        SecureRealtimeMovementProtocolChecks.CreateSnapshot(
            SecureRealtimeSnapshotFlags.None,
            SecureRealtimeMovementRejection.None);

    private static SecureRealtimeSnapshotDispatch Dispatch(
        SecureUdpConnectionKey connectionId,
        SecureRealtimePositionSnapshot snapshot) =>
        new(
            connectionId,
            LoopbackEndpoint,
            BindingRevision: 1,
            snapshot);

    private static SecureRealtimeMovementIngress CreateIngress(
        SecureRealtimeTransportSource source,
        SecureRealtimeMovementIngressKind kind,
        uint epoch,
        ulong inputId) =>
        new(
            SecureRealtimeMovementProtocolChecks.CreateInput(
                source == SecureRealtimeTransportSource.Tls
                    ? SecureRealtimeMovementFlags.CurrentWorld
                    : SecureRealtimeMovementFlags.None,
                epoch,
                inputId),
            source,
            TimeSpan.FromMilliseconds(
                checked((long)inputId * 50)),
            kind);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public EnvironmentVariableScope(params string[] names)
        {
            _original = names.ToDictionary(
                static name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        }

        public void Clear()
        {
            foreach (var name in _original.Keys)
            {
                Set(name, null);
            }
        }

        public void Set(string name, string? value) =>
            Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(
                    name,
                    value);
            }
        }
    }
}
