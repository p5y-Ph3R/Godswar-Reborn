using System.Diagnostics.Metrics;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Operations;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecurePhase4AcceptanceFaultState : byte
{
    AwaitingCutover = 1,
    DroppingSnapshotAcknowledgments = 2,
    AwaitingTlsFallback = 3,
    CorrectionForced = 4,
    Complete = 5,
    Expired = 6
}

internal readonly record struct SecurePhase4AcceptanceFaultSnapshot(
    SecurePhase4AcceptanceFaultState State,
    int RecordedDroppedSnapshots,
    bool TlsFallbackObserved,
    int ForcedCorrections,
    bool TlsNoSwitchbackObserved,
    bool Expired);

internal enum SecurePhase4AcceptanceFaultEvidence : byte
{
    Enabled = 1,
    CampaignStarted = 2,
    SnapshotAcknowledgmentDropped = 3,
    SnapshotDropWindowCompleted = 4,
    TlsFallbackObserved = 5,
    CorrectionForced = 6,
    TlsNoSwitchbackObserved = 7,
    CampaignExpired = 8
}

internal sealed class SecurePhase4AcceptanceFaults
{
    internal static readonly TimeSpan SnapshotDropWindow =
        TimeSpan.FromMilliseconds(1_500);
    internal static readonly TimeSpan CampaignLifetime =
        TimeSpan.FromSeconds(15);
    internal const int MaximumRecordedDroppedSnapshots = 32;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private SecureUdpConnectionKey _selectedConnection;
    private DateTimeOffset _dropDeadline;
    private DateTimeOffset _campaignDeadline;
    private uint _triggerTransportEpoch;
    private ulong _triggerAcknowledgedInputId;
    private ulong _forcedInputId;
    private int _recordedDroppedSnapshots;
    private int _forcedCorrections;
    private bool _hasSelectedConnection;
    private bool _tlsFallbackObserved;
    private bool _tlsNoSwitchbackObserved;
    private bool _expired;
    private SecurePhase4AcceptanceFaultState _state =
        SecurePhase4AcceptanceFaultState.AwaitingCutover;

    internal SecurePhase4AcceptanceFaults(
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Record(SecurePhase4AcceptanceFaultEvidence.Enabled);
    }

    internal static SecurePhase4AcceptanceFaults? Create(
        SecurePhase4AcceptanceFaultOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled
            ? new SecurePhase4AcceptanceFaults(timeProvider)
            : null;
    }

    internal bool ShouldDropSnapshot(
        in SecureRealtimeSnapshotDispatch dispatch)
    {
        var primary =
            (SecurePhase4AcceptanceFaultEvidence?)null;
        var secondary =
            (SecurePhase4AcceptanceFaultEvidence?)null;
        var drop = false;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (TryExpireLocked(now))
            {
                primary =
                    SecurePhase4AcceptanceFaultEvidence
                        .CampaignExpired;
            }
            else if (_state ==
                         SecurePhase4AcceptanceFaultState
                             .AwaitingCutover &&
                     IsEligibleTrigger(dispatch.Snapshot))
            {
                _selectedConnection = dispatch.ConnectionId;
                _hasSelectedConnection = true;
                _triggerTransportEpoch =
                    dispatch.Snapshot.TransportEpoch;
                _triggerAcknowledgedInputId =
                    dispatch.Snapshot.AcknowledgedInputId;
                _dropDeadline = now + SnapshotDropWindow;
                _campaignDeadline = now + CampaignLifetime;
                _state =
                    SecurePhase4AcceptanceFaultState
                        .DroppingSnapshotAcknowledgments;
                _recordedDroppedSnapshots = 1;
                drop = true;
                primary =
                    SecurePhase4AcceptanceFaultEvidence
                        .CampaignStarted;
                secondary =
                    SecurePhase4AcceptanceFaultEvidence
                        .SnapshotAcknowledgmentDropped;
            }
            else if (_state !=
                         SecurePhase4AcceptanceFaultState.Expired &&
                     MatchesSelected(dispatch.ConnectionId))
            {
                if (now >= _dropDeadline)
                {
                    if (_state ==
                        SecurePhase4AcceptanceFaultState
                            .DroppingSnapshotAcknowledgments)
                    {
                        _state =
                            SecurePhase4AcceptanceFaultState
                                .AwaitingTlsFallback;
                        primary =
                            SecurePhase4AcceptanceFaultEvidence
                                .SnapshotDropWindowCompleted;
                    }
                }
                else if (dispatch.Snapshot.TransportEpoch ==
                             _triggerTransportEpoch &&
                         dispatch.Snapshot
                                 .AcknowledgedInputId >=
                             _triggerAcknowledgedInputId)
                {
                    drop = true;
                    if (_recordedDroppedSnapshots <
                        MaximumRecordedDroppedSnapshots)
                    {
                        _recordedDroppedSnapshots++;
                        primary =
                            SecurePhase4AcceptanceFaultEvidence
                                .SnapshotAcknowledgmentDropped;
                    }
                }
            }
        }

        Record(primary);
        Record(secondary);
        return drop;
    }

    internal bool ShouldForceCorrection(
        SecureUdpConnectionKey connectionId,
        in SecureRealtimeMovementIngress ingress)
    {
        if (ingress.TransportSource !=
                SecureRealtimeTransportSource.Tls ||
            ingress.Kind !=
                SecureRealtimeMovementIngressKind.Input)
        {
            return false;
        }

        var primary =
            (SecurePhase4AcceptanceFaultEvidence?)null;
        var secondary =
            (SecurePhase4AcceptanceFaultEvidence?)null;
        var force = false;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (TryExpireLocked(now))
            {
                primary =
                    SecurePhase4AcceptanceFaultEvidence
                        .CampaignExpired;
            }
            else if (_state is not
                         SecurePhase4AcceptanceFaultState.Complete and
                         not SecurePhase4AcceptanceFaultState.Expired &&
                     MatchesSelected(connectionId) &&
                     _triggerTransportEpoch != uint.MaxValue &&
                     ingress.Input.TransportEpoch ==
                         _triggerTransportEpoch + 1)
            {
                if (!_tlsFallbackObserved)
                {
                    _tlsFallbackObserved = true;
                    _state =
                        SecurePhase4AcceptanceFaultState
                            .AwaitingTlsFallback;
                    primary =
                        SecurePhase4AcceptanceFaultEvidence
                            .TlsFallbackObserved;
                }

                if (_forcedCorrections == 0 &&
                    ingress.Input.InputId >
                        _triggerAcknowledgedInputId)
                {
                    _forcedCorrections = 1;
                    _forcedInputId = ingress.Input.InputId;
                    _state =
                        SecurePhase4AcceptanceFaultState
                            .CorrectionForced;
                    force = true;
                    secondary =
                        SecurePhase4AcceptanceFaultEvidence
                            .CorrectionForced;
                }
                else if (_forcedCorrections == 1 &&
                         !_tlsNoSwitchbackObserved &&
                         ingress.Input.InputId >
                             _forcedInputId)
                {
                    _tlsNoSwitchbackObserved = true;
                    _state =
                        SecurePhase4AcceptanceFaultState
                            .Complete;
                    secondary =
                        SecurePhase4AcceptanceFaultEvidence
                            .TlsNoSwitchbackObserved;
                }
            }
        }

        Record(primary);
        Record(secondary);
        return force;
    }

    internal SecurePhase4AcceptanceFaultSnapshot GetSnapshot()
    {
        SecurePhase4AcceptanceFaultEvidence? evidence = null;
        SecurePhase4AcceptanceFaultSnapshot snapshot;
        lock (_gate)
        {
            if (TryExpireLocked(_timeProvider.GetUtcNow()))
            {
                evidence =
                    SecurePhase4AcceptanceFaultEvidence
                        .CampaignExpired;
            }
            snapshot = new SecurePhase4AcceptanceFaultSnapshot(
                _state,
                _recordedDroppedSnapshots,
                _tlsFallbackObserved,
                _forcedCorrections,
                _tlsNoSwitchbackObserved,
                _expired);
        }
        Record(evidence);
        return snapshot;
    }

    private bool MatchesSelected(
        SecureUdpConnectionKey connectionId) =>
        _hasSelectedConnection &&
        connectionId == _selectedConnection;

    private bool TryExpireLocked(DateTimeOffset now)
    {
        if (_state is
                SecurePhase4AcceptanceFaultState.Complete or
                SecurePhase4AcceptanceFaultState.Expired ||
            _campaignDeadline == default ||
            now < _campaignDeadline)
        {
            return false;
        }

        _expired = true;
        _state = SecurePhase4AcceptanceFaultState.Expired;
        return true;
    }

    private static bool IsEligibleTrigger(
        in SecureRealtimePositionSnapshot snapshot) =>
        snapshot.TransportEpoch == 1 &&
        snapshot.AcknowledgedInputId != 0 &&
        snapshot.Rejection ==
            SecureRealtimeMovementRejection.None &&
        (snapshot.Flags &
            SecureRealtimeSnapshotFlags.Correction) == 0;

    private static void Record(
        SecurePhase4AcceptanceFaultEvidence? evidence)
    {
        if (evidence is null)
        {
            return;
        }

        SecurePhase4AcceptanceFaultMetrics.Record(evidence.Value);
        var controlledHostEvent = evidence.Value switch
        {
            SecurePhase4AcceptanceFaultEvidence.Enabled =>
                ControlledHostEvidenceEvent.Phase4FaultCampaignEnabled,
            SecurePhase4AcceptanceFaultEvidence.CampaignStarted =>
                ControlledHostEvidenceEvent.Phase4SnapshotDropStarted,
            SecurePhase4AcceptanceFaultEvidence
                    .SnapshotDropWindowCompleted =>
                ControlledHostEvidenceEvent
                    .Phase4SnapshotDropWindowCompleted,
            SecurePhase4AcceptanceFaultEvidence
                    .TlsFallbackObserved =>
                ControlledHostEvidenceEvent.Phase4TlsFallbackObserved,
            SecurePhase4AcceptanceFaultEvidence
                    .CorrectionForced =>
                ControlledHostEvidenceEvent.Phase4CorrectionForced,
            SecurePhase4AcceptanceFaultEvidence
                    .TlsNoSwitchbackObserved =>
                ControlledHostEvidenceEvent
                    .Phase4TlsNoSwitchbackObserved,
            SecurePhase4AcceptanceFaultEvidence
                    .CampaignExpired =>
                ControlledHostEvidenceEvent.Phase4FaultCampaignExpired,
            _ => (ControlledHostEvidenceEvent?)null
        };
        if (controlledHostEvent is not null)
        {
            ControlledHostPrivacyEvidence.Record(
                controlledHostEvent.Value);
        }
    }
}

internal static class SecurePhase4AcceptanceFaultMetrics
{
    private const string OutcomeTagName =
        "network.secure.acceptance.phase4.outcome";
    private static readonly Meter Meter =
        new(SecureNetworkMetrics.MeterName);
    private static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.acceptance.phase4",
            "{event}");

    internal static void Record(
        SecurePhase4AcceptanceFaultEvidence evidence)
    {
        Outcomes.Add(
            1,
            new KeyValuePair<string, object?>(
                OutcomeTagName,
                evidence.ToMetricTag()));
    }

    private static string ToMetricTag(
        this SecurePhase4AcceptanceFaultEvidence evidence) =>
        evidence switch
        {
            SecurePhase4AcceptanceFaultEvidence.Enabled =>
                "enabled",
            SecurePhase4AcceptanceFaultEvidence.CampaignStarted =>
                "campaign_started",
            SecurePhase4AcceptanceFaultEvidence
                    .SnapshotAcknowledgmentDropped =>
                "snapshot_ack_dropped",
            SecurePhase4AcceptanceFaultEvidence
                    .SnapshotDropWindowCompleted =>
                "snapshot_drop_window_completed",
            SecurePhase4AcceptanceFaultEvidence
                    .TlsFallbackObserved =>
                "tls_fallback_observed",
            SecurePhase4AcceptanceFaultEvidence.CorrectionForced =>
                "correction_forced",
            SecurePhase4AcceptanceFaultEvidence
                    .TlsNoSwitchbackObserved =>
                "tls_no_switchback_observed",
            SecurePhase4AcceptanceFaultEvidence.CampaignExpired =>
                "campaign_expired",
            _ => throw new ArgumentOutOfRangeException(
                nameof(evidence))
        };
}
