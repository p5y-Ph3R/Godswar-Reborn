using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    public const string CheckName =
        "Medusa explicit world-instance ownership boundary";

    public static async Task RunAsync()
    {
        CheckConstructionAndCopiedInputs();
        CheckLateCharacterAdmission();
        await CheckBindingFencesAsync();
        CheckExplicitMapTwoHundredIdentity();
        CheckImmutableLookupAndDelegation();
        CheckDescriptorTimeAndCapacityAuthority();
        CheckCoupledOperationsAndPurePreview();
        CheckDeadlineCouplingAndMechanicsGate();
        CheckPeriodicOwnerCoupling();
        await CheckPeriodicFoundationEventAndEgressAsync();
        await CheckMedusaMonsterAttachmentAsync();
        await CheckPlayerMonsterDamageTransactionAsync();
        await CheckMonsterPlayerHitTransactionAsync();
        await CheckExactLifeAuthorityIntegrationAsync();
        await CheckCharacterEffectAuthorityAsync();
        await CheckStatusHandlerIntegrationAsync();
        CheckMedusaClientStatusProjectionPolicy();
        await CheckMedusaStatusPublicationAsync();
    }

    private static void CheckConstructionAndCopiedInputs()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var characters = new[] { 101, 102, 103, 104, 105 };
        var spawns = MedusaRunRuntimeCheckFixture.Spawns(
            MedusaEncounterDifficulty.Enhanced);
        var originalFirst = spawns[0];

        var result = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters,
            spawns);
        Check.True(
            result is
            {
                Outcome: MedusaInstanceBindOutcome.Bound,
                Snapshot: { } bound
            } &&
            result.IsBound &&
            bound.WorldInstanceId == map.WorldInstanceId &&
            bound.Difficulty == MedusaEncounterDifficulty.Enhanced &&
            bound.ContentMapId.Value == 200 &&
            bound.Run.Spawns.Count ==
                MedusaIslandRosterPolicy.TotalSpawnCount &&
            bound.MonsterBindings.Length ==
                MedusaIslandRosterPolicy.TotalSpawnCount &&
            bound.Mechanics.WorldInstanceId == map.WorldInstanceId,
            "a creating empty dungeon owns one explicit Enhanced aggregate");

        characters[0] = 999;
        spawns[0] = originalFirst with
        {
            ObjectId = uint.MaxValue,
            SpawnGeneration = uint.MaxValue
        };
        Check.True(
            map.TryGetMedusaOwnershipSnapshot(out var copied) &&
            copied.Run.AdmittedCharacterIds.Contains(101) &&
            !copied.Run.AdmittedCharacterIds.Contains(999) &&
            copied.Run.Spawns.Any(spawn =>
                spawn.ObjectId == originalFirst.ObjectId &&
                spawn.SpawnGeneration ==
                    originalFirst.SpawnGeneration) &&
            copied.Run.Spawns.All(spawn =>
                spawn.ObjectId != uint.MaxValue),
            "caller-owned participant and roster arrays cannot mutate ownership");
    }

    private static async Task CheckBindingFencesAsync()
    {
        var unbound = CreateMap(MedusaEncounterDifficulty.Enhanced);
        Check.True(
            !unbound.TryGetMedusaOwnershipSnapshot(out _) &&
            !unbound.TryGetMedusaMonsterBinding(1, 1, out _) &&
            !unbound.TryObserveMedusaTime(StartedAt, out _),
            "an unbound map exposes no Medusa mutable state");

        AssertRejectedWithoutOwnership(
            CreateMap(
                MedusaEncounterDifficulty.Enhanced,
                lifecycle: WorldInstanceLifecycleState.Active),
            MedusaEncounterDifficulty.Enhanced,
            MedusaInstanceBindOutcome.LifecycleNotCreating,
            "active runtime");
        AssertRejectedWithoutOwnership(
            CreateMap(
                MedusaEncounterDifficulty.Enhanced,
                kind: InstanceKind.OpenWorld),
            MedusaEncounterDifficulty.Enhanced,
            MedusaInstanceBindOutcome.WrongInstanceKind,
            "open-world runtime");
        AssertRejectedWithoutOwnership(
            CreateMap(MedusaEncounterDifficulty.Normal),
            MedusaEncounterDifficulty.Enhanced,
            MedusaInstanceBindOutcome.ContentMapMismatch,
            "wrong content map");
        AssertRejectedWithoutOwnership(
            CreateMap(MedusaEncounterDifficulty.Enhanced),
            (MedusaEncounterDifficulty)byte.MaxValue,
            MedusaInstanceBindOutcome.UnknownDifficulty,
            "unknown explicit difficulty");

        var initialized = CreateMap(
            MedusaEncounterDifficulty.Enhanced);
        _ = initialized.InitializeMonsters([], StartedAt);
        AssertRejectedWithoutOwnership(
            initialized,
            MedusaEncounterDifficulty.Enhanced,
            MedusaInstanceBindOutcome
                .MonsterRuntimeAlreadyInitialized,
            "initialized monster runtime");

        var invalidRoster = CreateMap(
            MedusaEncounterDifficulty.Enhanced);
        var mismatched = MedusaRunRuntimeCheckFixture.Spawns(
            MedusaEncounterDifficulty.Enhanced);
        mismatched[0] = mismatched[0] with
        {
            Role = MedusaEncounterEnemyRole.Medusa
        };
        var invalid = Bind(
            invalidRoster,
            MedusaEncounterDifficulty.Enhanced,
            spawns: mismatched);
        Check.True(
            invalid.Outcome ==
                MedusaInstanceBindOutcome.InvalidRunDefinition &&
            invalid.Snapshot is null &&
            !invalidRoster.TryGetMedusaOwnershipSnapshot(out _) &&
            Bind(
                invalidRoster,
                MedusaEncounterDifficulty.Enhanced).IsBound,
            "a mismatched roster rejects without consuming the one bind");

        var rebound = CreateMap(MedusaEncounterDifficulty.Enhanced);
        Check.True(
            Bind(rebound, MedusaEncounterDifficulty.Enhanced).IsBound,
            "first ownership bind succeeds");
        var rebind = Bind(rebound, MedusaEncounterDifficulty.Mythic);
        Check.True(
            rebind.Outcome == MedusaInstanceBindOutcome.AlreadyBound &&
            rebind.Snapshot is null &&
            rebound.TryGetMedusaOwnershipSnapshot(out var stillBound) &&
            stillBound.Difficulty ==
                MedusaEncounterDifficulty.Enhanced,
            "a rebind cannot replace explicit difficulty or owned state");

        var populated = CreateMap(MedusaEncounterDifficulty.Enhanced);
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        populated.AddOrUpdate(CreateContext(populated, socket.Session));
        var occupied = Bind(
            populated,
            MedusaEncounterDifficulty.Enhanced);
        Check.True(
            occupied.Outcome ==
                MedusaInstanceBindOutcome.RuntimeNotEmpty &&
            occupied.Snapshot is null &&
            !populated.TryGetMedusaOwnershipSnapshot(out _),
            "a populated runtime rejects ownership without mutation");
        Check.True(
            populated.Remove(socket.Session, out _) &&
            Bind(
                populated,
                MedusaEncounterDifficulty.Enhanced).IsBound,
            "clearing membership leaves the unconsumed bind available");
    }

    private static void CheckExplicitMapTwoHundredIdentity()
    {
        var enhancedMap = CreateMap(
            MedusaEncounterDifficulty.Enhanced);
        var mythicMap = CreateMap(MedusaEncounterDifficulty.Mythic);
        var enhanced = Bind(
            enhancedMap,
            MedusaEncounterDifficulty.Enhanced).Snapshot!;
        var mythic = Bind(
            mythicMap,
            MedusaEncounterDifficulty.Mythic).Snapshot!;

        Check.True(
            enhanced.WorldInstanceId != mythic.WorldInstanceId &&
            enhanced.ContentMapId.Value == 200 &&
            mythic.ContentMapId.Value == 200 &&
            enhanced.Difficulty ==
                MedusaEncounterDifficulty.Enhanced &&
            mythic.Difficulty == MedusaEncounterDifficulty.Mythic &&
            enhanced.MonsterBindings.All(binding =>
                binding.Difficulty ==
                    MedusaEncounterDifficulty.Enhanced) &&
            mythic.MonsterBindings.All(binding =>
                binding.Difficulty ==
                    MedusaEncounterDifficulty.Mythic) &&
            !MedusaIslandEncounterPolicy
                .TryGetUniqueDifficultyByContentMap(200, out _),
            "map 200 never infers or merges Enhanced and Mythic identity");
    }

    private static void CheckImmutableLookupAndDelegation()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var elite = Binding(bound, "E1-Elite");
        var copiedBindings = bound.MonsterBindings.ToArray();
        copiedBindings[0] = default;

        Check.True(
            map.TryGetMedusaMonsterBinding(
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                out var lookedUp) &&
            lookedUp == elite &&
            !map.TryGetMedusaMonsterBinding(
                elite.Identity.ObjectId,
                checked(elite.Identity.SpawnGeneration + 1),
                out _) &&
            map.TryGetMedusaOwnershipSnapshot(out var unchanged) &&
            unchanged.MonsterBindings[0] != default,
            "object and generation form an immutable exact roster lookup");

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                targetCharacterId: 101,
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out var hit) &&
            hit.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            hit.MechanicsResult is
            {
                Outcome: MedusaMechanicHitOutcome.Applied,
                Effect.Definition.Kind:
                    MedusaEncounterEffectKind.Stun
            },
            "the owner narrowly delegates a committed roster mechanic hit");
        Check.True(
            bound.Mechanics.Characters.Single().ActiveEffects.IsEmpty &&
            map.TryGetMedusaOwnershipSnapshot(out var afterHit) &&
            afterHit.Mechanics.Characters.Single().ActiveEffects.Length == 1,
            "an earlier ownership snapshot remains immutable after mechanics mutation");

        Check.True(
            map.TryObserveMedusaTime(
                StartedAt.AddSeconds(2),
                out var clock) &&
            clock.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            clock.RunOutcome == MedusaRunClockOutcome.Active &&
            clock.MechanicsResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced,
            "the owner advances both clocks through one explicit observation");

        Check.True(
            map.TryAbandonMedusaRun(
                101,
                StartedAt.AddSeconds(3),
                out var abandoned) &&
            abandoned.RunOutcome ==
                MedusaRunAbandonOutcome.Exited &&
            abandoned.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced &&
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                StartedAt.AddSeconds(4),
                out var terminalHit) &&
            terminalHit.GateOutcome ==
                MedusaOwnedOperationGateOutcome.RunNotActive &&
            terminalHit.MechanicsResult is null,
            "terminal run state gates mechanics without exposing either runtime");
    }

    private static void AssertRejectedWithoutOwnership(
        Godswar.Server.Game.MapInstance map,
        MedusaEncounterDifficulty difficulty,
        MedusaInstanceBindOutcome expected,
        string boundary)
    {
        var result = Bind(map, difficulty);
        Check.True(
            result.Outcome == expected &&
            result.Snapshot is null &&
            !map.TryGetMedusaOwnershipSnapshot(out _),
            $"{boundary} rejects without ownership mutation");
    }
}
