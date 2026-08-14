using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static void CheckElementalRuntimeRegistry()
    {
        var transport = new ControlledLegacyByteTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry();
        var ownership = new PlayerOwnershipFence(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            1);
        var character = new GameCharacter
        {
            Id = 100,
            AccountId = 10,
            Name = "ElementalRuntime",
            CurrentMap = 7,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            CheckpointOwnerId = ownership.OwnerId,
            CheckpointOwnerGeneration = ownership.Generation
        };
        try
        {
            registry.ReplaceAccountSession(character.AccountId, session);
            Check.True(
                registry.TryBindAccountSessionOwnership(
                    character.AccountId,
                    session,
                    ownership),
                "elemental runtime fixture binds current ownership");
            registry.JoinMap(
                session,
                character.AccountId,
                character,
                0x7001);
            var fence = new ElementalCombatSessionFence(
                character.Id,
                character.CurrentMap,
                ownership);

            CheckRuntimeOwnershipAndLifeReset(
                registry,
                session,
                fence);
            CheckRuntimeMovementAndHitHooks(
                registry,
                session,
                fence);
            CheckRuntimeRecoveryAndKillHooks(
                registry,
                session,
                fence);

            Check.True(
                registry.HasElementalCombatSession(session),
                "elemental state is lazily registry-owned for the live session");
            registry.Remove(session);
            Check.True(
                !registry.HasElementalCombatSession(session),
                "disconnect removes all per-session elemental state");
        }
        finally
        {
            registry.Remove(session);
            registry.RemoveAccountSession(character.AccountId, session);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckRuntimeOwnershipAndLifeReset(
        GameSessionRegistry registry,
        ClientSession session,
        ElementalCombatSessionFence fence)
    {
        var shockEvent = RuntimeDirectEvent(
            900,
            sourceId: 999,
            targetId: fence.CharacterId,
            time: 0);
        var shock = new ElementalEffectApplication(
            ElementKind.Lightning,
            ElementalEffectKind.Shock,
            999,
            fence.CharacterId,
            shockEvent.EventId,
            0,
            1_000,
            1_000,
            1_000,
            0,
            0,
            0,
            CombatEventProvenance.ElementalStatus);
        Check.True(
            registry.TryApplyElementalApplication(
                session,
                fence,
                shockEvent,
                shock) &&
            registry.TryGetElementalStatusAdjustment(
                session,
                fence,
                100,
                1_000,
                1_000,
                1_000,
                1_000,
                1_000,
                out var shocked) &&
            !shocked.MovementAllowed,
            "registry accepts only target-bound application and exposes Shock");

        var staleOwnership = fence with
        {
            Ownership = fence.Ownership with { Generation = 2 }
        };
        Check.True(
            !registry.TryGetElementalStatusAdjustment(
                session,
                staleOwnership,
                100,
                1_000,
                0,
                0,
                0,
                0,
                out _) &&
            !registry.TryGetElementalStatusAdjustment(
                session,
                fence with { MapId = 8 },
                100,
                1_000,
                0,
                0,
                0,
                0,
                out _),
            "stale ownership and map fences cannot reach elemental state");

        registry.AdvancePlayerLifeRevision(session);
        Check.True(
            registry.TryGetElementalStatusAdjustment(
                session,
                fence,
                100,
                1_000,
                0,
                0,
                0,
                0,
                out var cleared) &&
            cleared.MovementAllowed,
            "life-revision boundary clears transient elemental statuses");
    }

    private static void CheckRuntimeMovementAndHitHooks(
        GameSessionRegistry registry,
        ClientSession session,
        ElementalCombatSessionFence fence)
    {
        var profile = RuntimeProfile(
            ElementKind.Wind,
            pieces: 10,
            new ElementalEffectTotals(1_000, 0, 2_000));
        var tuning = RuntimeTuning();
        var movement = FindRuntimeMovementEvent(fence.CharacterId);
        Check.True(
            registry.TryProcessAcceptedElementalMovement(
                session,
                fence,
                movement,
                profile,
                tuning,
                5_000,
                1_000,
                out var moved) &&
            moved.Accepted &&
            moved.GaleApplied &&
            moved.StatusAdjustment.MovementSpeed == 1_100 &&
            moved.Resonance.MomentumReady,
            "accepted movement atomically applies Gale and advances Momentum");

        var direct = RuntimeDirectEvent(
            movement.EventId + 1,
            fence.CharacterId,
            200,
            movement.AuthoritativeTimeMilliseconds + 1);
        Check.True(
            registry.TryAdjustElementalOutgoingHit(
                session,
                fence,
                direct,
                profile,
                1_000,
                10_000,
                10_000,
                out var pre) &&
            pre.AeolusMomentumPendingCommit &&
            pre.AdjustedDamage == 1_100,
            "registry exposes pre-hit outgoing resonance adjustment");

        var targetStatuses = new ElementalStatusState(200);
        Check.True(
            registry.TryProcessCommittedElementalHitOnTargetOwnerLane(
                session,
                fence,
                direct,
                profile,
                RuntimeProfile(ElementKind.Wind, 0, default),
                targetStatuses,
                authoredElement: null,
                tuning,
                1_100,
                10_000,
                false,
                [],
                out var committed) &&
            committed.Resonance.DamageIntents.All(static value =>
                !value.CanTriggerSecondaryCombatEffects),
            "committed-hit hook emits terminal post-effects on the target owner lane");

        var after = RuntimeDirectEvent(
            direct.EventId + 1,
            fence.CharacterId,
            200,
            direct.AuthoritativeTimeMilliseconds + 1);
        Check.True(
            registry.TryAdjustElementalOutgoingHit(
                session,
                fence,
                after,
                profile,
                1_000,
                10_000,
                10_000,
                out var consumed) &&
            !consumed.AeolusMomentumPendingCommit &&
            consumed.AdjustedDamage == 1_000,
            "Momentum is consumed only by a committed direct hit");
    }

    private static void CheckRuntimeRecoveryAndKillHooks(
        GameSessionRegistry registry,
        ClientSession session,
        ElementalCombatSessionFence fence)
    {
        var light = RuntimeProfile(ElementKind.Light, 10, default);
        var recovery = RuntimeSelfEvent(
            2_000,
            fence.CharacterId,
            0,
            CombatEventProvenance.Recovery);
        Check.True(
            registry.TryProcessElementalRecoveryPulse(
                session,
                fence,
                recovery,
                light,
                200,
                0,
                10_000,
                1_000,
                10_000,
                1_000,
                out var recovered) &&
            recovered.RequestedHealth == 220 &&
            recovered.BarrierAdded == 110,
            "recovery hook applies Apollo amplification and barrier state");

        var incoming = RuntimeDirectEvent(
            2_001,
            sourceId: 200,
            targetId: fence.CharacterId,
            time: 1);
        Check.True(
            registry.TryAdjustElementalIncomingHit(
                session,
                fence,
                incoming,
                light,
                1_000,
                500,
                10_000,
                1_000,
                out var protectedHit) &&
            protectedHit.ApolloLethalProtectionApplied &&
            protectedHit.RemainingHealth == 1,
            "incoming hook consumes Apollo barrier before the HP commit seam");

        var dark = RuntimeProfile(ElementKind.Dark, 10, default);
        var kill = RuntimeDirectEvent(
            2_002,
            fence.CharacterId,
            300,
            2) with
        {
            Provenance = CombatEventProvenance.CreditedKill
        };
        Check.True(
            registry.TryProcessElementalCreditedKill(
                session,
                fence,
                kill,
                dark,
                9_000,
                900,
                10_000,
                1_000,
                out var restored) &&
            restored.AppliedHealth == 800 &&
            restored.AppliedMana == 80,
            "credited-kill hook restores Hades resources once");
        Check.True(
            registry.TryProcessElementalCreditedKill(
                session,
                fence,
                kill,
                dark,
                9_000,
                900,
                10_000,
                1_000,
                out var replay) &&
            replay.AppliedHealth == 0 && replay.AppliedMana == 0,
            "credited-kill replay cannot restore resources twice");
    }

    private static ElementalEquipmentProfile RuntimeProfile(
        ElementKind element,
        int pieces,
        ElementalEffectTotals totals)
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

    private static ElementalEffectExecutionTuning RuntimeTuning() =>
        AuthoredElementalCombatV1.EffectTuning;

    private static DeterministicCombatEventContext FindRuntimeMovementEvent(
        long characterId)
    {
        for (ulong sequence = 1_000; sequence < 100_000; sequence++)
        {
            var candidate = RuntimeSelfEvent(
                sequence,
                characterId,
                2_000,
                CombatEventProvenance.AcceptedMovement);
            if (ElementalEffectExecutionPolicy.DeterministicRollBasisPoints(
                    candidate,
                    ElementKind.Wind) < 2_000)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No deterministic runtime Gale sample found.");
    }

    private static DeterministicCombatEventContext RuntimeDirectEvent(
        ulong sequence,
        long sourceId,
        long targetId,
        long time) =>
        new(
            sequence, 7, sourceId, targetId, time,
            CombatEventProvenance.DirectSkill,
            true, false, default);

    private static DeterministicCombatEventContext RuntimeSelfEvent(
        ulong sequence,
        long characterId,
        long time,
        CombatEventProvenance provenance) =>
        new(
            sequence, 7, characterId, characterId, time,
            provenance,
            true, false, default);
}
