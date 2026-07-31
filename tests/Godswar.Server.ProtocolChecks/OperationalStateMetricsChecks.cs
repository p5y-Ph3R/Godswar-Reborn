using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Net;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class OperationalStateMetricsChecks
{
    private static readonly HashSet<string> AdmissionStates =
    [
        "connections_active",
        "connections_unauthenticated",
        "ip_entries",
        "prefix_entries"
    ];

    private static readonly HashSet<string> TicketStates =
    [
        "outstanding",
        "capacity"
    ];

    private static readonly HashSet<string> UdpStates =
    [
        "ready",
        "faulted",
        "sessions_pending",
        "sessions_bound",
        "sessions_capacity",
        "limiter_packets_current",
        "limiter_packets_unvalidated",
        "limiter_packets_binding_proof",
        "limiter_packets_protected_candidate",
        "limiter_prefixes_active",
        "limiter_prefixes_binding_proof",
        "limiter_prefixes_protected_candidate",
        "limiter_sessions_authenticated"
    ];

    private static readonly HashSet<string> CoordinationStates =
    [
        "ready",
        "operations_in_flight",
        "operations_limit",
        "routes",
        "player_leases",
        "capacity",
        "operations_accepted",
        "operations_conflict",
        "operations_timeout",
        "operations_unavailable",
        "operations_overloaded",
        "operations_circuit_open"
    ];

    public static Task RunAsync()
    {
        CheckAuthoritativeSnapshotsAndDimensionContract();
        CheckOptionalFamiliesAreAbsent();
        CheckDisposalStopsObservation();
        return Task.CompletedTask;
    }

    private static void CheckAuthoritativeSnapshotsAndDimensionContract()
    {
        var admission = new StubConnectionAdmission(
            new ConnectionAdmissionSnapshot(
                ActiveConnections: 17,
                UnauthenticatedConnections: 6,
                LoginActiveConnections: 4,
                LoginUnauthenticatedConnections: 2,
                GameActiveConnections: 13,
                GameUnauthenticatedConnections: 4,
                TrackedUnauthenticatedIpAddresses: 5,
                TrackedUnauthenticatedPrefixes: 3));
        using var tickets = new StubGameTicketSnapshotSource(
            new SecureGameTicketStoreSnapshot(
                Capacity: 128,
                ActiveGenerations: 9,
                OutstandingTickets: 11));
        var udpSnapshot = CreateUdpSnapshot(
            SecureUdpRuntimeState.Ready);
        var udpSnapshotCalls = 0;
        SecureUdpRuntimeSnapshot ReadUdpSnapshot()
        {
            udpSnapshotCalls++;
            return udpSnapshot;
        }
        var coordination = new StubCoordinationSnapshots(
            new WorkerCoordinationSnapshot(
                IsReady: true,
                Capacity: 4_096,
                MaximumConcurrentOperations: 128,
                InFlightOperations: 3,
                RegisteredRoutes: 21,
                ActivePlayerLeases: 17,
                AcceptedOperations: 401,
                ConflictOperations: 7,
                TimeoutOperations: 5,
                UnavailableOperations: 3,
                OverloadRejections: 2,
                CircuitOpenRejections: 1,
                LastSuccessAtUtc: DateTimeOffset.UtcNow));
        using var metrics = new OperationalStateMetrics(
            admission,
            tickets,
            ReadUdpSnapshot,
            coordination);
        using var capture = new MetricCapture();

        capture.RecordObservableInstruments();
        var captured = capture.Measurements;
        var expectedNames = new HashSet<string>(
        [
            OperationalStateMetrics.AdmissionInstrumentName,
            OperationalStateMetrics.TicketInstrumentName,
            OperationalStateMetrics.UdpInstrumentName,
            OperationalStateMetrics.CoordinationInstrumentName
        ]);

        Check.True(
            captured.Select(static item => item.InstrumentName)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames),
            "operational state exposes the exact fixed gauge set");
        Check.True(
            captured.All(static item =>
                item.Tags is
                [
                    {
                        Key: OperationalStateMetrics.StateTagName,
                        Value: string
                    }
                ]),
            "operational state gauges carry one fixed state dimension");
        Check.True(
            captured.All(static item => IsAllowedState(
                item.InstrumentName,
                (string)item.Tags[0].Value!)),
            "operational state gauges expose only finite state values");
        Check.Equal(
            1,
            admission.SnapshotCalls,
            "one admission snapshot serves one metric collection");
        Check.Equal(
            1,
            tickets.SnapshotCalls,
            "one ticket snapshot serves one metric collection");
        Check.Equal(
            1,
            udpSnapshotCalls,
            "one UDP snapshot serves one metric collection");
        Check.Equal(
            1,
            coordination.SnapshotCalls,
            "one cached coordination snapshot serves one collection");
        CheckMeasurement(
            captured,
            OperationalStateMetrics.AdmissionInstrumentName,
            "connections_active",
            17);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.AdmissionInstrumentName,
            "connections_unauthenticated",
            6);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.AdmissionInstrumentName,
            "ip_entries",
            5);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.AdmissionInstrumentName,
            "prefix_entries",
            3);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.TicketInstrumentName,
            "outstanding",
            11);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.TicketInstrumentName,
            "capacity",
            128);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.UdpInstrumentName,
            "ready",
            1);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.UdpInstrumentName,
            "faulted",
            0);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.UdpInstrumentName,
            "sessions_pending",
            7);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.UdpInstrumentName,
            "sessions_bound",
            19);
        CheckMeasurement(
            captured,
            OperationalStateMetrics.UdpInstrumentName,
            "sessions_capacity",
            64);

        var limiterValues = new Dictionary<string, long>
        {
            ["limiter_packets_current"] = 101,
            ["limiter_packets_unvalidated"] = 23,
            ["limiter_packets_binding_proof"] = 17,
            ["limiter_packets_protected_candidate"] = 13,
            ["limiter_prefixes_active"] = 29,
            ["limiter_prefixes_binding_proof"] = 5,
            ["limiter_prefixes_protected_candidate"] = 3,
            ["limiter_sessions_authenticated"] = 19
        };
        foreach (var expected in limiterValues)
        {
            CheckMeasurement(
                captured,
                OperationalStateMetrics.UdpInstrumentName,
                expected.Key,
                expected.Value);
        }
        var coordinationValues = new Dictionary<string, long>
        {
            ["ready"] = 1,
            ["operations_in_flight"] = 3,
            ["operations_limit"] = 128,
            ["routes"] = 21,
            ["player_leases"] = 17,
            ["capacity"] = 4_096,
            ["operations_accepted"] = 401,
            ["operations_conflict"] = 7,
            ["operations_timeout"] = 5,
            ["operations_unavailable"] = 3,
            ["operations_overloaded"] = 2,
            ["operations_circuit_open"] = 1
        };
        foreach (var expected in coordinationValues)
        {
            CheckMeasurement(
                captured,
                OperationalStateMetrics.CoordinationInstrumentName,
                expected.Key,
                expected.Value);
        }

        capture.Clear();
        udpSnapshot = CreateUdpSnapshot(
            SecureUdpRuntimeState.Faulted);
        capture.RecordObservableInstruments();
        CheckMeasurement(
            capture.Measurements,
            OperationalStateMetrics.UdpInstrumentName,
            "ready",
            0);
        CheckMeasurement(
            capture.Measurements,
            OperationalStateMetrics.UdpInstrumentName,
            "faulted",
            1);
        Check.Equal(
            2,
            admission.SnapshotCalls,
            "each collection takes one fresh admission snapshot");
        Check.Equal(
            2,
            tickets.SnapshotCalls,
            "each collection takes one fresh ticket snapshot");
        Check.Equal(
            2,
            udpSnapshotCalls,
            "each collection takes one fresh UDP snapshot");
        Check.Equal(
            2,
            coordination.SnapshotCalls,
            "each collection takes one cached coordination snapshot");
    }

    private static void CheckOptionalFamiliesAreAbsent()
    {
        var admission = new StubConnectionAdmission(default);
        using var metrics = new OperationalStateMetrics(admission);
        using var capture = new MetricCapture();

        capture.RecordObservableInstruments();
        var names = capture.Measurements
            .Select(static item => item.InstrumentName)
            .ToHashSet(StringComparer.Ordinal);
        Check.True(
            names.SetEquals(
            [
                OperationalStateMetrics.AdmissionInstrumentName
            ]),
            "disabled optional runtimes publish no misleading zero series");
        Check.Equal(
            1,
            admission.SnapshotCalls,
            "admission-only collection takes one snapshot");
    }

    private static void CheckDisposalStopsObservation()
    {
        var admission = new StubConnectionAdmission(default);
        var metrics = new OperationalStateMetrics(admission);
        using var capture = new MetricCapture();

        capture.RecordObservableInstruments();
        Check.True(
            capture.Measurements.Count > 0,
            "live operational meter is observable");
        Check.Equal(
            1,
            admission.SnapshotCalls,
            "live meter takes one snapshot");
        capture.Clear();
        metrics.Dispose();
        metrics.Dispose();
        capture.RecordObservableInstruments();
        Check.Equal(
            0,
            capture.Measurements.Count,
            "disposing the owned meter removes its callbacks");
        Check.Equal(
            1,
            admission.SnapshotCalls,
            "disposed meter takes no further snapshots");
    }

    private static SecureUdpRuntimeSnapshot CreateUdpSnapshot(
        SecureUdpRuntimeState state)
    {
        return new SecureUdpRuntimeSnapshot(
            state,
            state == SecureUdpRuntimeState.Ready
                ? new IPEndPoint(IPAddress.Loopback, 7444)
                : null,
            new SecureUdpSessionAuthoritySnapshot(
                Capacity: 64,
                PendingSessions: 7,
                BoundSessions: 19),
            new SecureUdpRateLimiterSnapshot(
                CurrentPackets: 101,
                UnvalidatedPackets: 23,
                BindingProofPackets: 17,
                ProtectedCandidatePackets: 13,
                ActivePrefixes: 29,
                ActiveBindingProofPrefixes: 5,
                ActiveProtectedCandidatePrefixes: 3,
                ActiveAuthenticatedSessions: 19,
                GlobalLimit: 4096,
                UnvalidatedLimit: 3072,
                BindingProofLimit: 512,
                ProtectedCandidateLimit: 512,
                PrefixCapacity: 1024),
            state == SecureUdpRuntimeState.Faulted
                ? nameof(InvalidOperationException)
                : null);
    }

    private static void CheckMeasurement(
        IReadOnlyCollection<CapturedMeasurement> captured,
        string instrumentName,
        string state,
        long expectedValue)
    {
        Check.True(
            captured.Any(item =>
                item.InstrumentName == instrumentName
                && item.Tags.Any(tag =>
                    tag.Key == OperationalStateMetrics.StateTagName
                    && Equals(tag.Value, state))
                && item.Value == expectedValue),
            $"{instrumentName}/{state} exposes authoritative value {expectedValue}");
    }

    private static bool IsAllowedState(
        string instrumentName,
        string state) =>
        instrumentName switch
        {
            OperationalStateMetrics.AdmissionInstrumentName =>
                AdmissionStates.Contains(state),
            OperationalStateMetrics.TicketInstrumentName =>
                TicketStates.Contains(state),
            OperationalStateMetrics.UdpInstrumentName =>
                UdpStates.Contains(state),
            OperationalStateMetrics.CoordinationInstrumentName =>
                CoordinationStates.Contains(state),
            _ => false
        };

    private sealed class MetricCapture : IDisposable
    {
        private readonly ConcurrentQueue<CapturedMeasurement> _measurements =
            new();
        private readonly MeterListener _listener = new();

        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, candidate) =>
            {
                if (instrument.Meter.Name ==
                    OperationalStateMetrics.MeterName)
                {
                    candidate.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                {
                    _measurements.Enqueue(
                        new CapturedMeasurement(
                            instrument.Name,
                            measurement,
                            tags.ToArray()));
                });
            _listener.Start();
        }

        public IReadOnlyCollection<CapturedMeasurement> Measurements =>
            _measurements.ToArray();

        public void Clear()
        {
            while (_measurements.TryDequeue(out _))
            {
            }
        }

        public void RecordObservableInstruments()
        {
            _listener.RecordObservableInstruments();
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed class StubConnectionAdmission(
        ConnectionAdmissionSnapshot snapshot)
        : IConnectionAdmission
    {
        public int SnapshotCalls { get; private set; }

        public bool TryAcquire(
            NetworkEndpointRole role,
            IPAddress? remoteAddress,
            [NotNullWhen(true)]
            out ConnectionAdmissionLease? lease,
            out ConnectionAdmissionRejection rejection)
        {
            lease = null;
            rejection = ConnectionAdmissionRejection.ActiveLimit;
            return false;
        }

        public ConnectionAdmissionSnapshot GetSnapshot()
        {
            SnapshotCalls++;
            return snapshot;
        }
    }

    private sealed class StubGameTicketSnapshotSource(
        SecureGameTicketStoreSnapshot snapshot)
        : IGameTicketStoreSnapshotSource, IDisposable
    {
        public int SnapshotCalls { get; private set; }

        public SecureGameTicketStoreSnapshot GetCachedSnapshot()
        {
            SnapshotCalls++;
            return snapshot;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubCoordinationSnapshots(
        WorkerCoordinationSnapshot snapshot)
        : IWorkerCoordinationReadinessSource
    {
        public int SnapshotCalls { get; private set; }

        public bool IsReady => snapshot.IsReady;

        public WorkerCoordinationSnapshot GetSnapshot()
        {
            SnapshotCalls++;
            return snapshot;
        }
    }

    private readonly record struct CapturedMeasurement(
        string InstrumentName,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
