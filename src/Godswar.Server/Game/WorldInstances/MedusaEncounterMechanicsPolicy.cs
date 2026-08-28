using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Converts the authored roster bindings into server-owned encounter effects.
/// Stock StatusOdds remains a combat rating and is never treated as a percent.
/// </summary>
internal static class MedusaEncounterMechanicsPolicy
{
    private const uint BleedDamagePerTick = 200;
    private static readonly TimeSpan BleedTickInterval =
        TimeSpan.FromSeconds(2);

    public static bool TryGetEffectDefinition(
        MedusaIslandRosterMechanic mechanic,
        short contentMapId,
        out MedusaEncounterEffectDefinition definition)
    {
        if (contentMapId is not 200 and not 204 ||
            !MedusaIslandRosterPolicy.TryGetSkillBinding(
                mechanic,
                out var binding) ||
            binding.ApplicationRule is not (
                MedusaIslandStatusApplicationRule.GuaranteedOnCommittedHit or
                MedusaIslandStatusApplicationRule
                    .DeterministicRatingProcOnCommittedHit))
        {
            definition = default;
            return false;
        }

        var kind = mechanic switch
        {
            MedusaIslandRosterMechanic.Stun =>
                MedusaEncounterEffectKind.Stun,
            MedusaIslandRosterMechanic.Freeze =>
                MedusaEncounterEffectKind.Freeze,
            MedusaIslandRosterMechanic.Bleed =>
                MedusaEncounterEffectKind.Bleed,
            MedusaIslandRosterMechanic.Shackle =>
                MedusaEncounterEffectKind.Shackle,
            MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier =>
                MedusaEncounterEffectKind.OutgoingPhysicalAmplifier,
            MedusaIslandRosterMechanic.OutgoingMagicalAmplifier =>
                MedusaEncounterEffectKind.OutgoingMagicalAmplifier,
            _ => default
        };
        if (kind == default)
        {
            definition = default;
            return false;
        }

        var control = kind is MedusaEncounterEffectKind.Stun or
            MedusaEncounterEffectKind.Freeze or
            MedusaEncounterEffectKind.Shackle
                ? MedusaEncounterControlRestriction.AllActions
                : MedusaEncounterControlRestriction.None;

        MedusaDamageChannel? outgoingChannel = kind switch
        {
            MedusaEncounterEffectKind.OutgoingPhysicalAmplifier =>
                MedusaDamageChannel.Physical,
            MedusaEncounterEffectKind.OutgoingMagicalAmplifier =>
                MedusaDamageChannel.Magical,
            _ => null
        };

        MedusaBleedProfile? bleed = null;
        if (kind == MedusaEncounterEffectKind.Bleed)
        {
            // Status 18: Values=200, Interval=2, Time=15. The numeric
            // interval is used instead of interpreting the prose note as a
            // one-second cadence.
            bleed = new(
                MedusaPeriodicDamageKind.DirectHealthLoss,
                BleedDamagePerTick,
                BleedTickInterval,
                MaximumTicks: 7,
                TicksImmediately: false,
                TicksAtExpiration: false);
        }

        var nativeMaps = binding.NativeAffectedClientSceneIds.IsDefault
            ? ImmutableArray<short>.Empty
            : binding.NativeAffectedClientSceneIds;
        // Stock AffectMap uses the secondary scene ID, as also evidenced by
        // WarField 38->216 and Troy 44->231. Medusa content map 200 maps to
        // scene 209; content map 204 maps to scene 223.
        if (!MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                contentMapId,
                out var nativeClientSceneId))
        {
            definition = default;
            return false;
        }
        var matchedClientSceneId =
            kind != MedusaEncounterEffectKind.Bleed &&
            binding.HasNativeClientSceneRestriction &&
                                   binding
                                       .CanUseUnmodifiedNativeStatusInClientScene(
                                           nativeClientSceneId)
            ? nativeClientSceneId
            : (short?)null;
        // Status 18/skill 2041 is stock authorship evidence for the server
        // cadence, but its native periodic-HP reconciliation is not proven.
        // Keep Bleed off the wire until that client behavior is certified.
        var mayUseNative = kind != MedusaEncounterEffectKind.Bleed &&
            binding.CanUseUnmodifiedNativeStatusInClientScene(
                nativeClientSceneId);
        var projectionMode = mayUseNative
            ? MedusaEncounterClientProjectionMode.NativeProjectionSupported
            : MedusaEncounterClientProjectionMode.CompatibilityUnresolved;
        var projection = new MedusaEncounterClientProjection(
            projectionMode,
            binding.StatusId,
            mayUseNative ? binding.StatusId : null,
            matchedClientSceneId,
            nativeMaps);

        definition = new(
            kind,
            mechanic,
            binding.Duration,
            control,
            outgoingChannel,
            binding.OutgoingDamageMultiplier,
            bleed,
            projection);
        return true;
    }
}
