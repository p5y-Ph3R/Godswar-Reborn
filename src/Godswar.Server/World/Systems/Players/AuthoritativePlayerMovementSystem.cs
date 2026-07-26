namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Pure single-owner movement policy for a fixed 20 Hz simulation.
/// The caller supplies the latest authenticated input and a monotonic,
/// server-authored receive timestamp on each simulation tick.
/// </summary>
internal sealed class AuthoritativePlayerMovementSystem
{
    private const double WorldCellSize = 32d;

    private readonly AuthoritativePlayerMovementPolicy _policy;
    private uint _transportEpoch;
    private readonly uint _worldGeneration;
    private readonly byte _mapId;
    private readonly uint _sourceObjectId;
    private readonly TimeSpan _baselineTimestamp;

    private TimeSpan _lastAcceptedTimestamp;
    private TimeSpan _lastObservedTimestamp;
    private bool _hasObservedTimestamp;
    private ulong _lastObservedInputId;
    private ulong _acknowledgedInputId;
    private ulong _simulationTick;
    private ulong _revision;
    private uint _opaqueState;
    private float _currentX;
    private float _currentZ;
    private float _auxiliary;

    public AuthoritativePlayerMovementSystem(
        in AuthoritativePlayerMovementBaseline baseline,
        AuthoritativePlayerMovementPolicy? policy = null)
    {
        if (!HasRepresentableCoordinates(
                baseline.CurrentX,
                baseline.CurrentZ) ||
            !float.IsFinite(baseline.Auxiliary))
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseline),
                "The authoritative baseline must be finite and representable.");
        }

        _policy = policy ?? new AuthoritativePlayerMovementPolicy();
        _transportEpoch = baseline.TransportEpoch;
        _worldGeneration = baseline.WorldGeneration;
        _mapId = baseline.MapId;
        _sourceObjectId = baseline.SourceObjectId;
        _opaqueState = baseline.OpaqueState;
        _currentX = baseline.CurrentX;
        _currentZ = baseline.CurrentZ;
        _auxiliary = baseline.Auxiliary;
        _baselineTimestamp = baseline.ServerTimestamp;
        _lastAcceptedTimestamp = baseline.ServerTimestamp;
        _lastObservedTimestamp = baseline.ServerTimestamp;
        _hasObservedTimestamp =
            baseline.AcknowledgedInputId != 0;
        _lastObservedInputId =
            baseline.AcknowledgedInputId;
        _acknowledgedInputId =
            baseline.AcknowledgedInputId;
        _revision = baseline.PositionRevision;
        _simulationTick = baseline.SimulationTick;
    }

    public AuthoritativePlayerMovementSnapshot Snapshot =>
        new(
            _simulationTick,
            _revision,
            _acknowledgedInputId,
            _transportEpoch,
            _worldGeneration,
            _mapId,
            _opaqueState,
            _currentX,
            _currentZ,
            _auxiliary);

    /// <summary>
    /// Advances an empty fixed step. The single-owner simulation calls either
    /// this method or <see cref="ProcessLatest"/> exactly once per 50 ms tick.
    /// </summary>
    public AuthoritativePlayerMovementSnapshot AdvanceWithoutInput()
    {
        _simulationTick = checked(_simulationTick + 1);
        return Snapshot;
    }

    /// <summary>
    /// Advances an authenticated TLS/UDP handoff by exactly one epoch.
    /// All authority, global input acknowledgement, and cadence state are
    /// preserved; only the authenticated transport epoch changes.
    /// </summary>
    public bool TryAdvanceTransportEpoch(uint nextEpoch)
    {
        if (_transportEpoch == uint.MaxValue ||
            nextEpoch != _transportEpoch + 1)
        {
            return false;
        }

        _transportEpoch = nextEpoch;
        return true;
    }

    public AuthoritativePlayerMovementDecision ProcessLatest(
        in AuthoritativePlayerMovementInput input,
        in AuthoritativePlayerMovementWorldContext world,
        TimeSpan serverReceivedAt)
    {
        _simulationTick = checked(_simulationTick + 1);

        if (!IsCurrentWorld(world))
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.MapTransition);
        }

        var transportRejection =
            ValidateTransportSemantics(input, world);
        if (transportRejection !=
            AuthoritativePlayerMovementRejectionReason.None)
        {
            return Reject(input, transportRejection);
        }

        if (input.InputId == 0)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.Malformed);
        }

        if (input.InputId <= _lastObservedInputId ||
            serverReceivedAt < _baselineTimestamp ||
            (_hasObservedTimestamp &&
             serverReceivedAt < _lastObservedTimestamp))
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.StaleInput);
        }

        var previousObservedTimestamp = _lastObservedTimestamp;
        var hadObservedTimestamp = _hasObservedTimestamp;
        _lastObservedInputId = input.InputId;
        _acknowledgedInputId = input.InputId;
        _lastObservedTimestamp = serverReceivedAt;
        _hasObservedTimestamp = true;

        var targetRejection = ValidateTargetSemantics(input);
        if (targetRejection !=
            AuthoritativePlayerMovementRejectionReason.None)
        {
            return Reject(input, targetRejection);
        }

        if (!world.IsReady)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.NotReady);
        }

        if (!world.IsAlive)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.Dead);
        }

        if (hadObservedTimestamp &&
            GetElapsedTicks(
                serverReceivedAt,
                previousObservedTimestamp) <
            _policy.MinimumInputCadence.Ticks)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.Cadence);
        }

        if (!HasRepresentableCoordinates(
                input.TargetX,
                input.TargetZ) ||
            !float.IsFinite(input.Auxiliary))
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason
                    .InvalidCoordinates);
        }

        if (!HasValidMovementMultiplier(world.MovementMultiplier))
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason
                    .Malformed);
        }

        var distance = Distance(
            _currentX,
            _currentZ,
            input.TargetX,
            input.TargetZ);
        var maximumDistance = GetMaximumDistance(
            world.MovementMultiplier,
            _policy.ElapsedCreditCap.TotalSeconds);
        if (distance > maximumDistance)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.Distance);
        }

        var elapsedSeconds = GetElapsedSeconds(
            serverReceivedAt,
            _lastAcceptedTimestamp);
        if (elapsedSeconds < 0d)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.StaleInput);
        }

        elapsedSeconds = Math.Min(
            elapsedSeconds,
            _policy.ElapsedCreditCap.TotalSeconds);

        var permittedDistance = GetMaximumDistance(
            world.MovementMultiplier,
            elapsedSeconds);
        if (distance > permittedDistance)
        {
            return Reject(
                input,
                AuthoritativePlayerMovementRejectionReason.Speed);
        }

        _currentX = input.TargetX;
        _currentZ = input.TargetZ;
        _auxiliary = input.Auxiliary;
        _opaqueState = input.OpaqueState;
        _lastAcceptedTimestamp = serverReceivedAt;
        _acknowledgedInputId = input.InputId;
        _revision = checked(_revision + 1);

        return new AuthoritativePlayerMovementDecision(
            Accepted: true,
            AuthoritativePlayerMovementRejectionReason.None,
            _simulationTick,
            _revision,
            input.InputId,
            _acknowledgedInputId,
            _transportEpoch,
            _worldGeneration,
            _mapId,
            _opaqueState,
            _currentX,
            _currentZ,
            _auxiliary,
            input.Source);
    }

    internal static bool HasRepresentableCoordinates(
        float x,
        float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        var cellX = Math.Floor((double)x / WorldCellSize);
        var cellZ = Math.Floor((double)z / WorldCellSize);
        return cellX is >= int.MinValue and <= int.MaxValue &&
               cellZ is >= int.MinValue and <= int.MaxValue;
    }

    private bool IsCurrentWorld(
        in AuthoritativePlayerMovementWorldContext world) =>
        world.TransportEpoch == _transportEpoch &&
        world.WorldGeneration == _worldGeneration &&
        world.MapId == _mapId &&
        world.SourceObjectId == _sourceObjectId;

    private AuthoritativePlayerMovementRejectionReason
        ValidateTransportSemantics(
            in AuthoritativePlayerMovementInput input,
            in AuthoritativePlayerMovementWorldContext world)
    {
        if (input.TransportEpoch != _transportEpoch)
        {
            return AuthoritativePlayerMovementRejectionReason
                .TransportEpoch;
        }

        if (input.SourceObjectId != _sourceObjectId)
        {
            return AuthoritativePlayerMovementRejectionReason
                .TransportSource;
        }

        if (input.Source is not (
                AuthoritativePlayerMovementSource.Tls or
                AuthoritativePlayerMovementSource.Udp) ||
            (world.AllowedSources & input.Source) == 0)
        {
            return AuthoritativePlayerMovementRejectionReason
                .TransportSource;
        }

        return AuthoritativePlayerMovementRejectionReason.None;
    }

    private AuthoritativePlayerMovementRejectionReason
        ValidateTargetSemantics(
            in AuthoritativePlayerMovementInput input)
    {
        if (!input.TargetsCurrentWorld ||
            input.WorldGeneration < _worldGeneration)
        {
            return AuthoritativePlayerMovementRejectionReason.StaleInput;
        }

        if (input.WorldGeneration != _worldGeneration ||
            input.MapId != _mapId)
        {
            return AuthoritativePlayerMovementRejectionReason.MapTransition;
        }

        return AuthoritativePlayerMovementRejectionReason.None;
    }

    private bool HasValidMovementMultiplier(float multiplier) =>
        float.IsFinite(multiplier) &&
        multiplier > 0f &&
        multiplier <= _policy.MaximumMovementMultiplier;

    private double GetMaximumDistance(
        float multiplier,
        double elapsedSeconds) =>
        (double)_policy.BaseMaximumSpeed *
        multiplier *
        elapsedSeconds +
        _policy.PositionTolerance;

    private static double GetElapsedSeconds(
        TimeSpan later,
        TimeSpan earlier) =>
        (double)(GetElapsedTicks(later, earlier) /
            TimeSpan.TicksPerSecond);

    private static decimal GetElapsedTicks(
        TimeSpan later,
        TimeSpan earlier) =>
        (decimal)later.Ticks - earlier.Ticks;

    private AuthoritativePlayerMovementDecision Reject(
        in AuthoritativePlayerMovementInput input,
        AuthoritativePlayerMovementRejectionReason reason) =>
        new(
            Accepted: false,
            reason,
            _simulationTick,
            _revision,
            input.InputId,
            _acknowledgedInputId,
            _transportEpoch,
            _worldGeneration,
            _mapId,
            _opaqueState,
            _currentX,
            _currentZ,
            _auxiliary,
            input.Source);

    private static double Distance(
        float fromX,
        float fromZ,
        float toX,
        float toZ)
    {
        var x = (double)toX - fromX;
        var z = (double)toZ - fromZ;
        return Math.Sqrt(x * x + z * z);
    }
}
