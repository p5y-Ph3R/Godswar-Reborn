using System.Diagnostics.Metrics;
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

    private readonly IConnectionAdmission _admission;
    private readonly List<Instrument> _instruments = [];
    private readonly Meter _meter = new(MeterName);
    private readonly IGameTicketStore? _ticketStore;
    private readonly Func<SecureUdpRuntimeSnapshot>? _udpSnapshot;
    private int _disposed;

    public OperationalStateMetrics(
        IConnectionAdmission admission,
        IGameTicketStore? ticketStore = null,
        SecureUdpRuntime? secureUdpRuntime = null)
        : this(
            admission,
            ticketStore,
            secureUdpRuntime is null
                ? null
                : secureUdpRuntime.GetSnapshot)
    {
    }

    internal OperationalStateMetrics(
        IConnectionAdmission admission,
        IGameTicketStore? ticketStore,
        Func<SecureUdpRuntimeSnapshot>? udpSnapshot)
    {
        ArgumentNullException.ThrowIfNull(admission);

        _admission = admission;
        _ticketStore = ticketStore;
        _udpSnapshot = udpSnapshot;

        AddAdmissionGauges();
        if (_ticketStore is not null)
        {
            AddTicketGauges();
        }
        if (_udpSnapshot is not null)
        {
            AddUdpGauges();
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
        var snapshot = _ticketStore!.GetSnapshot();
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

    private static Measurement<long> Measure(
        long value,
        string state) =>
        new(
            value,
            new KeyValuePair<string, object?>(
                StateTagName,
                state));
}
