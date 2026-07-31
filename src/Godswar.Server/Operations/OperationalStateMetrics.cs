using System.Diagnostics.Metrics;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.Operations;

internal sealed class OperationalStateMetrics : IDisposable
{
    public const string MeterName = "Godswar.Server.OperationalState";
    public const string StateTagName = "operational.state";

    public const string AdmissionInstrumentName =
        "godswar.server.operational.admission";
    public const string TicketInstrumentName =
        "godswar.server.operational.tickets";
    public const string UdpInstrumentName =
        "godswar.server.operational.udp";
    public const string CoordinationInstrumentName =
        "godswar.server.operational.coordination";

    private readonly IConnectionAdmission _admission;
    private readonly List<Instrument> _instruments = [];
    private readonly Meter _meter = new(MeterName);
    private readonly IGameTicketStoreSnapshotSource? _ticketSnapshots;
    private readonly Func<SecureUdpRuntimeSnapshot>? _udpSnapshot;
    private readonly IWorkerCoordinationReadinessSource?
        _coordinationSnapshots;
    private int _disposed;

    public OperationalStateMetrics(
        IConnectionAdmission admission,
        IGameTicketStoreSnapshotSource? ticketSnapshots = null,
        SecureUdpRuntime? secureUdpRuntime = null,
        IWorkerCoordinationReadinessSource?
            coordinationSnapshots = null)
        : this(
            admission,
            ticketSnapshots,
            secureUdpRuntime is null
                ? null
                : secureUdpRuntime.GetSnapshot,
            coordinationSnapshots)
    {
    }

    internal OperationalStateMetrics(
        IConnectionAdmission admission,
        IGameTicketStoreSnapshotSource? ticketSnapshots,
        Func<SecureUdpRuntimeSnapshot>? udpSnapshot,
        IWorkerCoordinationReadinessSource?
            coordinationSnapshots = null)
    {
        ArgumentNullException.ThrowIfNull(admission);

        _admission = admission;
        _ticketSnapshots = ticketSnapshots;
        _udpSnapshot = udpSnapshot;
        _coordinationSnapshots = coordinationSnapshots;

        AddAdmissionGauges();
        if (_ticketSnapshots is not null)
        {
            AddTicketGauges();
        }
        if (_udpSnapshot is not null)
        {
            AddUdpGauges();
        }
        if (_coordinationSnapshots is not null)
        {
            AddCoordinationGauges();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _meter.Dispose();
        _instruments.Clear();
    }

    private void AddAdmissionGauges()
    {
        _instruments.Add(
            _meter.CreateObservableGauge(
                AdmissionInstrumentName,
                ObserveAdmission,
                "{entry}",
                "Authoritative admission state by finite state kind."));
    }

    private void AddTicketGauges()
    {
        _instruments.Add(
            _meter.CreateObservableGauge(
                TicketInstrumentName,
                ObserveTickets,
                "{entry}",
                "Authoritative secure-ticket state by finite state kind."));
    }

    private void AddUdpGauges()
    {
        _instruments.Add(
            _meter.CreateObservableGauge(
                UdpInstrumentName,
                ObserveUdp,
                "{entry}",
                "Authoritative secure-UDP state by finite state kind."));
    }

    private void AddCoordinationGauges()
    {
        _instruments.Add(
            _meter.CreateObservableGauge(
                CoordinationInstrumentName,
                ObserveCoordination,
                "{entry}",
                "Cached, bounded cross-process coordination state."));
    }

    private IEnumerable<Measurement<long>> ObserveAdmission()
    {
        var snapshot = _admission.GetSnapshot();
        return
        [
            Measure(snapshot.ActiveConnections, "connections_active"),
            Measure(
                snapshot.UnauthenticatedConnections,
                "connections_unauthenticated"),
            Measure(
                snapshot.TrackedUnauthenticatedIpAddresses,
                "ip_entries"),
            Measure(
                snapshot.TrackedUnauthenticatedPrefixes,
                "prefix_entries")
        ];
    }

    private IEnumerable<Measurement<long>> ObserveTickets()
    {
        var snapshot = _ticketSnapshots!.GetCachedSnapshot();
        return
        [
            Measure(snapshot.OutstandingTickets, "outstanding"),
            Measure(snapshot.Capacity, "capacity")
        ];
    }

    private IEnumerable<Measurement<long>> ObserveUdp()
    {
        var snapshot = _udpSnapshot!();
        return
        [
            Measure(snapshot.IsReady ? 1 : 0, "ready"),
            Measure(
                snapshot.State == SecureUdpRuntimeState.Faulted ? 1 : 0,
                "faulted"),
            Measure(snapshot.Sessions.PendingSessions, "sessions_pending"),
            Measure(snapshot.Sessions.BoundSessions, "sessions_bound"),
            Measure(snapshot.Sessions.Capacity, "sessions_capacity"),
            Measure(
                snapshot.Admission.CurrentPackets,
                "limiter_packets_current"),
            Measure(
                snapshot.Admission.UnvalidatedPackets,
                "limiter_packets_unvalidated"),
            Measure(
                snapshot.Admission.BindingProofPackets,
                "limiter_packets_binding_proof"),
            Measure(
                snapshot.Admission.ProtectedCandidatePackets,
                "limiter_packets_protected_candidate"),
            Measure(
                snapshot.Admission.ActivePrefixes,
                "limiter_prefixes_active"),
            Measure(
                snapshot.Admission.ActiveBindingProofPrefixes,
                "limiter_prefixes_binding_proof"),
            Measure(
                snapshot.Admission.ActiveProtectedCandidatePrefixes,
                "limiter_prefixes_protected_candidate"),
            Measure(
                snapshot.Admission.ActiveAuthenticatedSessions,
                "limiter_sessions_authenticated")
        ];
    }

    private IEnumerable<Measurement<long>> ObserveCoordination()
    {
        var snapshot = _coordinationSnapshots!.GetSnapshot();
        return
        [
            Measure(snapshot.IsReady ? 1 : 0, "ready"),
            Measure(snapshot.InFlightOperations, "operations_in_flight"),
            Measure(snapshot.MaximumConcurrentOperations, "operations_limit"),
            Measure(snapshot.RegisteredRoutes, "routes"),
            Measure(snapshot.ActivePlayerLeases, "player_leases"),
            Measure(snapshot.Capacity, "capacity"),
            Measure(snapshot.AcceptedOperations, "operations_accepted"),
            Measure(snapshot.ConflictOperations, "operations_conflict"),
            Measure(snapshot.TimeoutOperations, "operations_timeout"),
            Measure(snapshot.UnavailableOperations, "operations_unavailable"),
            Measure(snapshot.OverloadRejections, "operations_overloaded"),
            Measure(
                snapshot.CircuitOpenRejections,
                "operations_circuit_open")
        ];
    }

    private static Measurement<long> Measure(
        long value,
        string state) =>
        new(
            value,
            new KeyValuePair<string, object?>(
                StateTagName,
                state));
}
