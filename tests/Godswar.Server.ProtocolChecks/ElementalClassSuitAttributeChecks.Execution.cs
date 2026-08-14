using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static void CheckElementalExecutionAndPvpGate()
    {
        Check.True(
            ElementalAttributeCatalog.GameplayExecutionEnabled,
            "all seven typed elemental effects have an enabled isolated executor");
        Check.True(
            DirectEvent(
                ulong.MaxValue,
                sourceId: 100,
                targetId: 200).IsValid,
            "unsigned core combat event IDs retain their high bit losslessly");
        CheckElementalStatusExecution();
        CheckDelayedBurnAndWindResistance();
        CheckMomentumSingleHitReservation();
        CheckPvpEligibilityGate();
    }

    private static void CheckMomentumSingleHitReservation()
    {
        var profile = ExecutionProfile(
            ElementKind.Wind,
            default,
            pieces: 6);
        var state = new ElementalResonanceState(100);
        state.Reconcile(profile);
        state.AcceptMovement(
            distanceMillimeters: 5_000,
            authoritativeTimeMilliseconds: 1,
            new MomentumParameters(5_000, 3_000, 1_000, true));

        const ulong areaScope = 7_700;
        var miss = DirectEvent(7_701, 100, 201) with
        {
            AuthoritativeTimeMilliseconds = 2
        };
        var missAdjustment = ElementalResonanceExecutionPolicy
            .AdjustOutgoingDirectDamage(
                miss,
                profile,
                state,
                originalDamage: 0,
                targetCurrentHealth: 1_000,
                targetMaximumHealth: 1_000,
                areaScope);
        var firstHit = DirectEvent(7_702, 100, 202) with
        {
            AuthoritativeTimeMilliseconds = 2
        };
        var firstAdjustment = ElementalResonanceExecutionPolicy
            .AdjustOutgoingDirectDamage(
                firstHit,
                profile,
                state,
                originalDamage: 1_000,
                targetCurrentHealth: 1_000,
                targetMaximumHealth: 1_000,
                areaScope);
        var laterHit = DirectEvent(7_703, 100, 203) with
        {
            AuthoritativeTimeMilliseconds = 2
        };
        var laterAdjustment = ElementalResonanceExecutionPolicy
            .AdjustOutgoingDirectDamage(
                laterHit,
                profile,
                state,
                originalDamage: 1_000,
                targetCurrentHealth: 1_000,
                targetMaximumHealth: 1_000,
                areaScope);
        Check.True(
            !missAdjustment.AeolusMomentumPendingCommit &&
            firstAdjustment.AeolusMomentumPendingCommit &&
            firstAdjustment.AdjustedDamage == 1_100 &&
            !laterAdjustment.AeolusMomentumPendingCommit &&
            laterAdjustment.AdjustedDamage == 1_000,
            "an area miss is skipped and Momentum reserves only the first deterministic hit");

        _ = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
            laterHit,
            profile,
            state,
            new ElementalStatusState(203),
            appliedDirectDamage: 1_000,
            sourceMaximumHealth: 10_000,
            primaryTargetIsBoss: false,
            []);
        Check.True(
            state.HasMomentum(2),
            "an unreserved area commit cannot consume another target's Momentum");

        _ = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
            firstHit,
            profile,
            state,
            new ElementalStatusState(202),
            appliedDirectDamage: 1_100,
            sourceMaximumHealth: 10_000,
            primaryTargetIsBoss: false,
            []);
        Check.True(
            !state.HasMomentum(2),
            "the matching committed hit consumes its Momentum reservation once");

        state.AcceptMovement(
            distanceMillimeters: 5_000,
            authoritativeTimeMilliseconds: 3,
            new MomentumParameters(5_000, 3_000, 1_000, true));
        var failedMutation = DirectEvent(7_704, 100, 204) with
        {
            AuthoritativeTimeMilliseconds = 4
        };
        var failedAdjustment = ElementalResonanceExecutionPolicy
            .AdjustOutgoingDirectDamage(
                failedMutation,
                profile,
                state,
                1_000,
                1_000,
                1_000,
                momentumReservationScopeId: 7_800);
        var nextTransaction = DirectEvent(7_705, 100, 205) with
        {
            AuthoritativeTimeMilliseconds = 5
        };
        var retriedAdjustment = ElementalResonanceExecutionPolicy
            .AdjustOutgoingDirectDamage(
                nextTransaction,
                profile,
                state,
                1_000,
                1_000,
                1_000,
                momentumReservationScopeId: 7_900);
        Check.True(
            failedAdjustment.AeolusMomentumPendingCommit &&
            retriedAdjustment.AeolusMomentumPendingCommit &&
            state.HasMomentum(5),
            "a failed mutation leaves Momentum unconsumed and a later transaction can reserve it");
    }

    private static void CheckElementalStatusExecution()
    {
        var tuning = AuthoredElementalCombatV1.EffectTuning;
        Check.True(
            AuthoredElementalCombatV1.Version == 1 &&
            tuning == new ElementalEffectExecutionTuning(
                4_000, 4, 4_000, 10_000,
                4_000, 4_000, 4_000, 4_000),
            "authored elemental V1 pins all status durations and Burn cadence");
        var selection = ExecutionProfile(
            ElementKind.Dark,
            new ElementalEffectTotals(900, 0, 2_000));
        selection = WithTotals(
            selection,
            ElementKind.Fire,
            new ElementalEffectTotals(900, 0, 2_000));
        selection = WithTotals(
            selection,
            ElementKind.Water,
            new ElementalEffectTotals(1_000, 0, 1_000));
        Check.True(
            AuthoredElementalCombatV1.TrySelectDirectHitElement(
                selection,
                out var selectedElement) &&
            selectedElement == ElementKind.Water,
            "authored V1 selects one strongest direct-hit element by potency/chance/ordinal");
        var tie = WithTotals(
            ExecutionProfile(
                ElementKind.Dark,
                new ElementalEffectTotals(900, 0, 2_000)),
            ElementKind.Fire,
            new ElementalEffectTotals(900, 0, 2_000));
        Check.True(
            AuthoredElementalCombatV1.TrySelectDirectHitElement(
                tie,
                out selectedElement) &&
            selectedElement == ElementKind.Fire,
            "authored direct-hit element ties use the stable enum ordinal");
        var targetProfile = ExecutionProfile(
            ElementKind.Fire,
            new ElementalEffectTotals(0, 2_000, 0));
        foreach (var element in new[]
                 {
                     ElementKind.Water,
                     ElementKind.Lightning,
                     ElementKind.Earth,
                     ElementKind.Light,
                     ElementKind.Dark
                 })
        {
            targetProfile = WithTotals(
                targetProfile,
                element,
                new ElementalEffectTotals(0, 2_000, 0));
        }

        var targetState = new ElementalStatusState(200);
        foreach (var element in new[]
                 {
                     ElementKind.Fire,
                     ElementKind.Water,
                     ElementKind.Lightning,
                     ElementKind.Earth,
                     ElementKind.Light,
                     ElementKind.Dark
                 })
        {
            var application = FindDirectApplication(
                element,
                ExecutionProfile(
                    element,
                    new ElementalEffectTotals(1_000, 0, 2_000)),
                targetProfile,
                tuning,
                sourceId: 100,
                targetId: 200,
                appliedDamage: 10_000);
            Check.True(
                application.EffectivePotencyBasisPoints == 800 &&
                targetState.TryApply(application),
                $"{element} applies deterministically after resistance");
        }

        var adjusted = targetState.ApplyAdjustments(
            100,
            movementSpeed: 1_000,
            physicalDefense: 1_000,
            magicDefense: 1_000,
            hitRating: 1_000,
            healingReceived: 1_000);
        Check.True(
            !adjusted.MovementAllowed &&
            adjusted.MovementSpeed == 920 &&
            adjusted.PhysicalDefense == 920 &&
            adjusted.MagicDefense == 920 &&
            adjusted.HitRating == 920 &&
            adjusted.HealingReceived == 920,
            "Drench, Shock, Fracture, Dazzle, and Wither expose typed adjustments");

        var ticks = targetState.CollectDuePeriodicDamage(4_000);
        Check.True(
            ticks.Count == 4 &&
            ticks.Sum(static value => value.Damage) == 800 &&
            ticks.All(static value =>
                value.Provenance == CombatEventProvenance.ElementalStatus),
            "Burn emits its exact total once through non-recursive periodic intents");

        var windProfile = ExecutionProfile(
            ElementKind.Wind,
            new ElementalEffectTotals(1_000, 7_000, 2_000));
        var movementApplication = FindMovementApplication(
            windProfile,
            tuning,
            characterId: 100);
        var selfState = new ElementalStatusState(100);
        Check.True(
            movementApplication.EffectivePotencyBasisPoints == 1_000 &&
            selfState.TryApply(movementApplication),
            "Gale rolls only after accepted movement and ignores self resistance");
        var gale = selfState.ApplyAdjustments(
            100,
            1_000,
            0,
            0,
            0,
            0);
        Check.True(
            gale.MovementAllowed && gale.MovementSpeed == 1_100,
            "Gale exposes its typed self movement-speed increase");
    }

    private static void CheckPvpEligibilityGate()
    {
        var now = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var attacker = new PvpCombatParticipant(100, 7, true, false, 0);
        var target = new PvpCombatParticipant(200, 7, true, false, 1);
        var duel = new PvpCombatEntitlement(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            PvpEntitlementKind.MutualDuel,
            100,
            200,
            7,
            now.AddMinutes(-1),
            now.AddMinutes(5),
            0,
            1);
        var allowed = PvpCombatEligibilityPolicy.Evaluate(
            attacker,
            target,
            duel,
            now);
        Check.True(
            allowed.Allowed &&
            allowed.Admits(100, 200, 7) &&
            allowed.Caps == PvpCombatCaps.Current &&
            allowed.Caps.MaximumElementalPotencyBasisPoints == 1_000 &&
            allowed.Caps.MaximumElementalResistanceBasisPoints == 7_000 &&
            allowed.Caps.MaximumElementalApplicationChanceBasisPoints == 2_000 &&
            allowed.Caps.MaximumTriggeredDamageBasisPointsOfAppliedHit == 1_500,
            "explicit duel admission is identity/map bound and carries exact PvP caps");

        ElementalEffectApplication cappedApplication = default;
        var capped = false;
        for (ulong eventId = 1; eventId <= 100_000; eventId++)
        {
            if (ElementalEffectExecutionPolicy.TryPlanDirectApplication(
                    DirectEvent(
                        eventId,
                        100,
                        200,
                        isPvp: true,
                        admission: allowed),
                    ElementKind.Fire,
                    ExecutionProfile(
                        ElementKind.Fire,
                        new ElementalEffectTotals(10_000, 0, 10_000)),
                    ExecutionProfile(ElementKind.Fire, default),
                    new ElementalEffectExecutionTuning(
                        1_000, 1, 1_000, 1_000,
                        1_000, 1_000, 1_000, 1_000),
                    10_000,
                    out cappedApplication))
            {
                capped = true;
                break;
            }
        }
        Check.True(
            capped &&
            cappedApplication.EffectivePotencyBasisPoints == 1_000 &&
            cappedApplication.ApplicationChanceBasisPoints == 2_000,
            "admitted PvP elemental execution enforces potency and chance caps");

        Check.True(
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target,
                null,
                now).Allowed &&
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker with { IsInSafeZone = true },
                target,
                duel,
                now).Allowed &&
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target with { MapId = 8 },
                duel,
                now).Allowed &&
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target,
                duel,
                now.AddMinutes(6)).Allowed &&
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target,
                duel with { Kind = (PvpEntitlementKind)byte.MaxValue },
                now).Allowed,
            "missing entitlement, safe zones, map mismatch, expiry, and unknown grant kinds fail closed");

        var faction = duel with
        {
            EntitlementId = Guid.Parse(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Kind = PvpEntitlementKind.OpposingFaction
        };
        Check.True(
            PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target,
                faction,
                now).Allowed &&
            !PvpCombatEligibilityPolicy.Evaluate(
                attacker,
                target with { Faction = 0 },
                faction,
                now).Allowed,
            "faction PvP requires an explicit grant and matching opposing factions");

        var deniedPvpEvent = DirectEvent(
            sequence: 1,
            sourceId: 100,
            targetId: 200,
            isPvp: true,
            admission: default);
        Check.True(
            !ElementalEffectExecutionPolicy.TryPlanDirectApplication(
                deniedPvpEvent,
                ElementKind.Fire,
                ExecutionProfile(
                    ElementKind.Fire,
                    new ElementalEffectTotals(1_000, 0, 2_000)),
                ExecutionProfile(ElementKind.Fire, default),
                new ElementalEffectExecutionTuning(
                    1_000, 1, 1_000, 1_000, 1_000, 1_000, 1_000, 1_000),
                10_000,
                out _),
            "elemental execution cannot turn an unadmitted player ID into a target");
    }

    private static ElementalEffectApplication FindDirectApplication(
        ElementKind element,
        ElementalEquipmentProfile source,
        ElementalEquipmentProfile target,
        ElementalEffectExecutionTuning tuning,
        long sourceId,
        long targetId,
        long appliedDamage)
    {
        for (ulong sequence = 1; sequence <= 100_000; sequence++)
        {
            if (ElementalEffectExecutionPolicy.TryPlanDirectApplication(
                    DirectEvent(sequence, sourceId, targetId),
                    element,
                    source,
                    target,
                    tuning,
                    appliedDamage,
                    out var application))
            {
                return application;
            }
        }

        throw new InvalidOperationException(
            $"No deterministic {element} application sample found.");
    }

    private static ElementalEffectApplication FindMovementApplication(
        ElementalEquipmentProfile source,
        ElementalEffectExecutionTuning tuning,
        long characterId)
    {
        for (ulong sequence = 1; sequence <= 100_000; sequence++)
        {
            var movement = new DeterministicCombatEventContext(
                sequence,
                7,
                characterId,
                characterId,
                0,
                CombatEventProvenance.AcceptedMovement,
                true,
                false,
                default);
            if (ElementalEffectExecutionPolicy.TryPlanMovementApplication(
                    movement,
                    source,
                    tuning,
                    out var application))
            {
                return application;
            }
        }

        throw new InvalidOperationException(
            "No deterministic Gale application sample found.");
    }

    private static DeterministicCombatEventContext DirectEvent(
        ulong sequence,
        long sourceId,
        long targetId,
        bool isPvp = false,
        PvpEligibilityResult admission = default) =>
        new(
            sequence,
            7,
            sourceId,
            targetId,
            0,
            CombatEventProvenance.DirectSkill,
            true,
            isPvp,
            admission);

    private static ElementalEquipmentProfile ExecutionProfile(
        ElementKind element,
        ElementalEffectTotals totals,
        int pieces = 0)
    {
        var raw = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => 0);
        raw[element] = totals;
        counts[element] = pieces;
        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(value, counts[value]));
        return new(raw, counts, active);
    }

    private static ElementalEquipmentProfile WithTotals(
        ElementalEquipmentProfile profile,
        ElementKind element,
        ElementalEffectTotals totals)
    {
        var raw = profile.RawEffects.ToDictionary();
        raw[element] = totals;
        return profile with { RawEffects = raw };
    }
}
