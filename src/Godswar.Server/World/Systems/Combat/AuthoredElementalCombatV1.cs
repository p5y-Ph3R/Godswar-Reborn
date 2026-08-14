using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Server-authored elemental combat tuning, version 1. These values are a
/// deliberate Reborn ruleset; they were not recovered from the native client
/// or original server binaries. Changing any value requires a new version.
/// </summary>
internal static class AuthoredElementalCombatV1
{
    public const int Version = 1;

    public const long MovementMillimetersPerWorldUnit = 1_000;

    public const long MovementMultiplierScale = 1_000_000;

    public static ElementalEffectExecutionTuning EffectTuning { get; } = new(
        BurnDurationMilliseconds: 4_000,
        BurnTickCount: 4,
        DrenchDurationMilliseconds: 4_000,
        ShockMaximumDurationMilliseconds: 10_000,
        FractureDurationMilliseconds: 4_000,
        GaleDurationMilliseconds: 4_000,
        DazzleDurationMilliseconds: 4_000,
        WitherDurationMilliseconds: 4_000);

    public static DeterministicCombatEventContext AcceptedMovementEvent(
        int characterId,
        byte mapId,
        long acceptedPositionRevision,
        DateTimeOffset acceptedAt)
    {
        ValidateRevisionEvent(
            characterId,
            acceptedPositionRevision,
            acceptedAt);
        return new(
            EventId(
                CombatEventProvenance.AcceptedMovement,
                characterId,
                mapId,
                acceptedPositionRevision),
            mapId,
            characterId,
            characterId,
            acceptedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.AcceptedMovement,
            Committed: true,
            IsPvp: false,
            default);
    }

    public static DeterministicCombatEventContext RecoveryEvent(
        int characterId,
        byte mapId,
        long acceptedRecoveryRevision,
        DateTimeOffset acceptedAt)
    {
        ValidateRevisionEvent(
            characterId,
            acceptedRecoveryRevision,
            acceptedAt);
        return new(
            EventId(
                CombatEventProvenance.Recovery,
                characterId,
                mapId,
                acceptedRecoveryRevision),
            mapId,
            characterId,
            characterId,
            acceptedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.Recovery,
            Committed: true,
            IsPvp: false,
            default);
    }

    public static DeterministicCombatEventContext CreditedKillEvent(
        ulong committedHitEventId,
        int sourceCharacterId,
        int targetCharacterId,
        byte mapId,
        int killOrdinal,
        DateTimeOffset committedAt,
        PvpEligibilityResult pvpEligibility)
    {
        if (committedHitEventId == 0 ||
            sourceCharacterId <= 0 ||
            targetCharacterId <= 0 ||
            sourceCharacterId == targetCharacterId ||
            killOrdinal <= 0 ||
            committedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(killOrdinal));
        }

        var eventId = 14695981039346656037UL;
        Mix(ref eventId, Version);
        Mix(ref eventId, (byte)CombatEventProvenance.CreditedKill);
        Mix(ref eventId, unchecked((long)committedHitEventId));
        Mix(ref eventId, sourceCharacterId);
        Mix(ref eventId, targetCharacterId);
        Mix(ref eventId, mapId);
        Mix(ref eventId, killOrdinal);
        return new(
            eventId == 0 ? 1UL : eventId,
            mapId,
            sourceCharacterId,
            targetCharacterId,
            committedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.CreditedKill,
            Committed: true,
            IsPvp: true,
            pvpEligibility);
    }

    public static DeterministicCombatEventContext CreditedPveKillEvent(
        ulong committedHitEventId,
        int sourceCharacterId,
        uint targetObjectId,
        byte mapId,
        int killOrdinal,
        DateTimeOffset committedAt)
    {
        if (committedHitEventId == 0 ||
            sourceCharacterId <= 0 ||
            targetObjectId == 0 ||
            killOrdinal <= 0 ||
            committedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(killOrdinal));
        }

        var eventId = 14695981039346656037UL;
        Mix(ref eventId, Version);
        Mix(ref eventId, (byte)CombatEventProvenance.CreditedKill);
        Mix(ref eventId, unchecked((long)committedHitEventId));
        Mix(ref eventId, sourceCharacterId);
        Mix(ref eventId, targetObjectId);
        Mix(ref eventId, mapId);
        Mix(ref eventId, killOrdinal);
        return new(
            eventId == 0 ? 1UL : eventId,
            mapId,
            sourceCharacterId,
            targetObjectId,
            committedAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.CreditedKill,
            Committed: true,
            IsPvp: false,
            default);
    }

    public static long AcceptedDistanceMillimeters(
        float previousX,
        float previousZ,
        float currentX,
        float currentZ)
    {
        if (!float.IsFinite(previousX) ||
            !float.IsFinite(previousZ) ||
            !float.IsFinite(currentX) ||
            !float.IsFinite(currentZ))
        {
            throw new ArgumentOutOfRangeException(nameof(currentX));
        }

        var deltaX = (double)currentX - previousX;
        var deltaZ = (double)currentZ - previousZ;
        var distance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        return checked((long)Math.Round(
            distance * MovementMillimetersPerWorldUnit,
            MidpointRounding.AwayFromZero));
    }

    public static long EncodeMovementMultiplier(float multiplier)
    {
        if (!float.IsFinite(multiplier) || multiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }

        return checked((long)Math.Round(
            multiplier * MovementMultiplierScale,
            MidpointRounding.AwayFromZero));
    }

    public static float DecodeMovementMultiplier(long encoded)
    {
        if (encoded <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encoded));
        }

        return (float)((double)encoded / MovementMultiplierScale);
    }

    /// <summary>
    /// Native attack packets expose no trustworthy element selector. V1
    /// therefore selects at most one non-Gale source element: highest potency,
    /// then highest application chance, then the lowest stable enum ordinal.
    /// An element missing either offensive channel is ineligible.
    /// </summary>
    public static bool TrySelectDirectHitElement(
        ElementalEquipmentProfile profile,
        out ElementKind element)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var selected = Enum.GetValues<ElementKind>()
            .Where(static value => value != ElementKind.Wind)
            .Select(value => (Element: value, Totals: profile.EffectsFor(value)))
            .Where(static value =>
                value.Totals.EffectPotencyBasisPoints > 0 &&
                value.Totals.ApplicationChanceBasisPoints > 0)
            .OrderByDescending(static value =>
                value.Totals.EffectPotencyBasisPoints)
            .ThenByDescending(static value =>
                value.Totals.ApplicationChanceBasisPoints)
            .ThenBy(static value => value.Element)
            .FirstOrDefault();
        element = selected.Element;
        return selected.Totals.EffectPotencyBasisPoints > 0;
    }

    private static void ValidateRevisionEvent(
        int characterId,
        long revision,
        DateTimeOffset acceptedAt)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (acceptedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedAt));
        }
    }

    private static ulong EventId(
        CombatEventProvenance provenance,
        int characterId,
        byte mapId,
        long revision)
    {
        var hash = 14695981039346656037UL;
        Mix(ref hash, Version);
        Mix(ref hash, (byte)provenance);
        Mix(ref hash, characterId);
        Mix(ref hash, mapId);
        Mix(ref hash, revision);
        return hash == 0 ? 1UL : hash;
    }

    private static void Mix(ref ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        hash *= 1099511628211UL;
    }
}
