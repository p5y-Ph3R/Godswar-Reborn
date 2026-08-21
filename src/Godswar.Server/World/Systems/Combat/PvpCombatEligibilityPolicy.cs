using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal enum PvpEntitlementKind : byte
{
    None,
    MutualDuel,
    OpposingFaction,
    TrainingDummy
}

internal enum PvpEligibilityFailure : byte
{
    None,
    InvalidAuthenticatedIdentity,
    SelfTarget,
    DeadParticipant,
    DifferentMap,
    SafeZone,
    MissingEntitlement,
    InvalidEntitlement,
    EntitlementNotActive,
    EntitlementIdentityMismatch,
    EntitlementMapMismatch,
    FactionMismatch
}

internal readonly record struct PvpCombatParticipant(
    long AuthenticatedCharacterId,
    int MapId,
    bool IsAlive,
    bool IsInSafeZone,
    byte Faction);

// An entitlement is issued by a trusted duel/faction authority. It is bound to
// both authenticated identities and one map so a packet-level target ID can
// never act as PvP admission by itself.
internal readonly record struct PvpCombatEntitlement(
    Guid EntitlementId,
    PvpEntitlementKind Kind,
    long FirstCharacterId,
    long SecondCharacterId,
    int MapId,
    DateTimeOffset ValidFrom,
    DateTimeOffset ExpiresAt,
    byte FirstFaction,
    byte SecondFaction);

internal readonly record struct PvpCombatCaps(
    int MaximumElementalPotencyBasisPoints,
    int MaximumElementalResistanceBasisPoints,
    int MaximumElementalApplicationChanceBasisPoints,
    int MaximumElementalStatusDurationMilliseconds,
    int MaximumTriggeredDamageBasisPointsOfAppliedHit,
    int MaximumReflectionBasisPointsOfAttackerMaximumHealth,
    int MaximumResourceEffectBasisPointsOfMaximum)
{
    public const int BasisPointDenominator = 10_000;

    // These ceilings contain the currently authored loadout and resonance
    // maxima. They are server safety bounds, not claims about native balance.
    public static PvpCombatCaps Current { get; } = new(
        MaximumElementalPotencyBasisPoints: 1_000,
        MaximumElementalResistanceBasisPoints: 7_000,
        MaximumElementalApplicationChanceBasisPoints: 2_000,
        MaximumElementalStatusDurationMilliseconds: 10_000,
        MaximumTriggeredDamageBasisPointsOfAppliedHit: 1_500,
        MaximumReflectionBasisPointsOfAttackerMaximumHealth: 200,
        MaximumResourceEffectBasisPointsOfMaximum: 1_000);

    public bool IsValid =>
        IsBasisPointValue(MaximumElementalPotencyBasisPoints) &&
        IsBasisPointValue(MaximumElementalResistanceBasisPoints) &&
        IsBasisPointValue(MaximumElementalApplicationChanceBasisPoints) &&
        MaximumElementalStatusDurationMilliseconds is > 0 and <= 60_000 &&
        IsBasisPointValue(MaximumTriggeredDamageBasisPointsOfAppliedHit) &&
        IsBasisPointValue(
            MaximumReflectionBasisPointsOfAttackerMaximumHealth) &&
        IsBasisPointValue(MaximumResourceEffectBasisPointsOfMaximum);

    private static bool IsBasisPointValue(int value) =>
        value is >= 0 and <= BasisPointDenominator;
}

internal readonly record struct PvpEligibilityResult(
    bool Allowed,
    PvpEligibilityFailure Failure,
    PvpEntitlementKind EntitlementKind,
    PvpCombatCaps Caps,
    Guid EntitlementId,
    long AttackerCharacterId,
    long TargetCharacterId,
    int MapId)
{
    public static PvpEligibilityResult Denied(
        PvpEligibilityFailure failure) =>
        new(
            false,
            failure,
            PvpEntitlementKind.None,
            default,
            Guid.Empty,
            0,
            0,
            -1);

    public bool Admits(long attackerCharacterId, long targetCharacterId, int mapId) =>
        Allowed &&
        EntitlementId != Guid.Empty &&
        AttackerCharacterId == attackerCharacterId &&
        TargetCharacterId == targetCharacterId &&
        MapId == mapId;
}

internal static class PvpCombatEligibilityPolicy
{
    public static PvpEligibilityResult Evaluate(
        PvpCombatParticipant attacker,
        PvpCombatParticipant target,
        PvpCombatEntitlement? entitlement,
        DateTimeOffset now,
        PvpCombatCaps? caps = null)
    {
        if (attacker.AuthenticatedCharacterId <= 0 ||
            target.AuthenticatedCharacterId <= 0 ||
            attacker.MapId < 0 ||
            target.MapId < 0)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.InvalidAuthenticatedIdentity);
        }

        if (attacker.AuthenticatedCharacterId ==
            target.AuthenticatedCharacterId)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.SelfTarget);
        }

        if (!attacker.IsAlive || !target.IsAlive)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.DeadParticipant);
        }

        if (attacker.MapId != target.MapId)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.DifferentMap);
        }

        if (attacker.IsInSafeZone || target.IsInSafeZone)
        {
            return PvpEligibilityResult.Denied(PvpEligibilityFailure.SafeZone);
        }

        if (!entitlement.HasValue)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.MissingEntitlement);
        }

        var grant = entitlement.Value;
        if (grant.EntitlementId == Guid.Empty ||
            grant.Kind is not (
                PvpEntitlementKind.MutualDuel or
                PvpEntitlementKind.OpposingFaction or
                PvpEntitlementKind.TrainingDummy) ||
            grant.FirstCharacterId <= 0 ||
            grant.SecondCharacterId <= 0 ||
            grant.FirstCharacterId == grant.SecondCharacterId ||
            grant.MapId < 0 ||
            grant.ExpiresAt <= grant.ValidFrom)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.InvalidEntitlement);
        }

        if (now < grant.ValidFrom || now >= grant.ExpiresAt)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.EntitlementNotActive);
        }

        var forward =
            attacker.AuthenticatedCharacterId == grant.FirstCharacterId &&
            target.AuthenticatedCharacterId == grant.SecondCharacterId;
        var reverse =
            attacker.AuthenticatedCharacterId == grant.SecondCharacterId &&
            target.AuthenticatedCharacterId == grant.FirstCharacterId;
        if (!forward && !reverse)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.EntitlementIdentityMismatch);
        }

        if (attacker.MapId != grant.MapId)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.EntitlementMapMismatch);
        }

        if (grant.Kind == PvpEntitlementKind.OpposingFaction &&
            !HasMatchingOpposingFactions(
                attacker,
                target,
                grant,
                forward))
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.FactionMismatch);
        }

        var effectiveCaps = caps ?? PvpCombatCaps.Current;
        if (!effectiveCaps.IsValid)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.InvalidEntitlement);
        }

        return new PvpEligibilityResult(
            true,
            PvpEligibilityFailure.None,
            grant.Kind,
            effectiveCaps,
            grant.EntitlementId,
            attacker.AuthenticatedCharacterId,
            target.AuthenticatedCharacterId,
            attacker.MapId);
    }

    private static bool HasMatchingOpposingFactions(
        PvpCombatParticipant attacker,
        PvpCombatParticipant target,
        PvpCombatEntitlement entitlement,
        bool forward)
    {
        var attackerFaction = forward
            ? entitlement.FirstFaction
            : entitlement.SecondFaction;
        var targetFaction = forward
            ? entitlement.SecondFaction
            : entitlement.FirstFaction;
        return attacker.Faction == attackerFaction &&
            target.Faction == targetFaction &&
            attackerFaction is GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
            targetFaction is GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
            attackerFaction != targetFaction;
    }
}
