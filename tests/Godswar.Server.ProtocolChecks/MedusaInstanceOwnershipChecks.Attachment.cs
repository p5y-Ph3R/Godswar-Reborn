using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckMedusaMonsterAttachmentAsync()
    {
        await CheckProductionAttachmentAsync();
        CheckExactAuthoredAttachmentAndNeverLifecycle();
        CheckGenericInitializationCannotBypassOwnership();
        CheckExactRetryAndExplicitMapTwoHundredConflict();
        CheckAttachmentRollbackAndModeFence();
        await CheckLegacyStagedTransferFencesAsync();
    }

    private static async Task CheckProductionAttachmentAsync()
    {
        var direct = CreateAttachmentFixture();
        var rejected = direct.Map.PrepareAndAttachMedusaProductionLive(
            direct.Inputs.Difficulty,
            direct.Inputs.AdmittedCharacterIds,
            direct.Inputs.RunSpawns,
            direct.Inputs.Definitions);
        Check.True(
            rejected.IsAttached &&
            rejected.Outcome == MedusaMonsterAttachmentOutcome.Attached &&
            rejected.BootstrapOutcome ==
                MedusaMonsterBootstrapValidationOutcome.Prepared &&
            direct.Map.SnapshotMonsters().Count ==
                direct.Inputs.Definitions.Length,
            "direct production attachment accepts decoded unblocked spawns");

        var prepared = CreateAttachmentFixture();
        await using var runtime = new WorldInstanceRuntime(prepared.Map);
        var concreteContext = new WorldInstanceRuntimePreparationContext(
            runtime,
            prepared.Map.Descriptor);
        IWorldInstanceRuntimePreparationContext context = concreteContext;
        var capabilityResult =
            context.PrepareAndAttachMedusaProductionLive(
                prepared.Inputs.Difficulty,
                prepared.Inputs.AdmittedCharacterIds,
                prepared.Inputs.RunSpawns,
                prepared.Inputs.Definitions);
        concreteContext.Invalidate();
        Check.True(
            capabilityResult.IsAttached &&
            capabilityResult.Outcome ==
                MedusaMonsterAttachmentOutcome.Attached &&
            capabilityResult.BootstrapOutcome ==
                MedusaMonsterBootstrapValidationOutcome.Prepared &&
            prepared.Map.SnapshotMonsters().Count ==
                prepared.Inputs.Definitions.Length,
            "prepared production capability attaches all Medusa spawns");

        var medusaMethods = typeof(
                IWorldInstanceRuntimePreparationContext)
            .GetMethods()
            .Where(static method =>
                method.Name.Contains(
                    "Medusa",
                    StringComparison.Ordinal))
            .ToArray();
        Check.True(
            medusaMethods is
            [
                {
                    Name: "PrepareAndAttachMedusaProductionLive",
                    ReturnType:
                        { } returnType
                }
            ] &&
            returnType == typeof(MedusaMonsterAttachmentResult) &&
            medusaMethods.All(static method =>
                !method.Name.Contains(
                    "Authored",
                    StringComparison.Ordinal) &&
                !method.Name.Contains(
                    "Bind",
                    StringComparison.Ordinal)),
            "prepared production exposes one combined Medusa capability and no authored or standalone bind seam");
    }

    private static void CheckExactAuthoredAttachmentAndNeverLifecycle()
    {
        var fixture = CreateAttachmentFixture();
        var result = AttachAuthored(fixture);
        var attached = result.Snapshot ??
            throw new InvalidOperationException(
                "Authored attachment did not publish a snapshot.");
        Check.True(
            result is
            {
                Outcome: MedusaMonsterAttachmentOutcome.Attached,
                OwnershipOutcome: null,
                BootstrapOutcome:
                    MedusaMonsterBootstrapValidationOutcome.Prepared,
                Snapshot: not null
            } &&
            result.IsAttached &&
            fixture.Map.TryGetMedusaMonsterAttachmentSnapshot(
                out var observedAttachment) &&
            observedAttachment == attached &&
            attached.WorldInstanceId == fixture.Map.WorldInstanceId &&
            attached.Difficulty ==
                MedusaEncounterDifficulty.Enhanced &&
            attached.ContentMapId.Value == 200 &&
            attached.StartedAt == fixture.Map.Descriptor.CreatedAt &&
            attached.RuntimeMode == MonsterRuntimeMode.Ecs &&
            attached.RespawnPolicy == MonsterRespawnPolicy.Never &&
            attached.MonsterCount == fixture.Inputs.Definitions.Length &&
            attached.RuntimeInstanceId != Guid.Empty &&
            attached.Fingerprint.Length == 64,
            "authored validation atomically publishes explicit ownership and one Ecs/Never attachment");

        Check.True(
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Difficulty == fixture.Inputs.Difficulty,
            "attachment publishes the matching owner at the same boundary");
        var preparation = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            ownership,
            fixture.Inputs.Definitions);
        Check.True(
            preparation.IsPrepared,
            "published ownership revalidates the exact authored bootstrap");
        var expected = preparation.Preparation!
            .CreateCapturedDefinitions()
            .ToDictionary(static spawn => spawn.ObjectId);
        var expectedMaximumHealth = preparation.Preparation.Spawns
            .Select(static spawn =>
                (spawn.ObjectId, spawn.MaximumHealth))
            .Concat(preparation.Preparation.AmbientSpawns.Select(
                static spawn =>
                    (spawn.ObjectId, spawn.MaximumHealth)))
            .ToDictionary(
                static spawn => spawn.ObjectId,
                static spawn => spawn.MaximumHealth);
        var monsters = fixture.Map.SnapshotMonsters();
        Check.True(
            monsters.Count == fixture.Inputs.Definitions.Length &&
            monsters.All(monster =>
                expected.TryGetValue(monster.ObjectId, out var spawn) &&
                expectedMaximumHealth.TryGetValue(
                    monster.ObjectId,
                    out var maximumHealth) &&
                monster.SpawnGeneration == 1 &&
                monster.HealthRevision == 0 &&
                monster.CurrentHealth == maximumHealth &&
                monster.MaximumHealth == maximumHealth &&
                monster.IsAlive &&
                monster.IsSpawned &&
                monster.RespawnAt is null &&
                monster.RuntimeInstanceId == attached.RuntimeInstanceId &&
                monster.Definition.Packet.AsSpan()
                    .SequenceEqual(spawn.Packet.AsSpan())),
            "all runtime monsters attach as pristine generation-one authored snapshots");

        var doomed = monsters[0];
        var killedAt = StartedAt.AddSeconds(1);
        var lethal = CommitTypedDamage(
            fixture.Map,
            doomed,
            attackerCharacterId: 101,
            Godswar.Server.World.Systems.Combat
                .CombatDamageChannel.Physical,
            uint.MaxValue,
            killedAt);
        var death = lethal.DamageResult!;
        Check.True(
            lethal.Applied &&
            death.Killed &&
            death.Monster.RespawnAt is null,
            "attached monster death has no respawn timer");
    }

    private static void CheckGenericInitializationCannotBypassOwnership()
    {
        var bound = CreateAttachmentFixture();
        Check.True(
            bound.Map.BindMedusaEncounter(
                bound.Inputs.Difficulty,
                bound.Inputs.AdmittedCharacterIds,
                bound.Inputs.RunSpawns).IsBound,
            "generic bypass fixture binds ownership");
        Check.Throws<InvalidOperationException>(
            () => bound.Map.InitializeMonsters(
                bound.Inputs.Definitions,
                StartedAt,
                respawnPolicy: MonsterRespawnPolicy.Timed),
            "bound Medusa map rejects generic Timed initialization");
        Check.Throws<InvalidOperationException>(
            () => bound.Map.InitializeMonsters(
                bound.Inputs.Definitions,
                StartedAt,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "bound Medusa map rejects generic Never initialization");
        Check.True(
            bound.Map.SnapshotMonsters().Count == 0 &&
            bound.Map.TryGetMedusaOwnershipSnapshot(out _) &&
            !bound.Map.TryGetMedusaMonsterAttachmentSnapshot(out _),
            "generic bound-map rejection leaves ownership and runtime unchanged");

        var attached = CreateAttachmentFixture();
        Check.True(AttachAuthored(attached).IsAttached,
            "attached generic-bypass fixture initializes");
        var before = SnapshotMonsterValues(attached.Map);
        Check.Throws<InvalidOperationException>(
            () => attached.Map.InitializeMonsters(
                attached.Inputs.Definitions,
                StartedAt,
                respawnPolicy: MonsterRespawnPolicy.Never),
            "attached map rejects generic exact Never definitions");
        Check.Throws<InvalidOperationException>(
            () => attached.Map.InitializeMonsters(
                [],
                StartedAt,
                respawnPolicy: MonsterRespawnPolicy.Timed),
            "attached map rejects generic mismatched Timed definitions");
        Check.True(
            SnapshotMonsterValues(attached.Map) == before,
            "attached generic rejection cannot replace or mutate runtime state");

        var ordinary = CreateAttachmentFixture();
        var first = ordinary.Map.InitializeMonsters(
            ordinary.Inputs.Definitions,
            StartedAt,
            respawnPolicy: MonsterRespawnPolicy.Timed);
        var retry = ordinary.Map.InitializeMonsters(
            ordinary.Inputs.Definitions,
            StartedAt,
            respawnPolicy: MonsterRespawnPolicy.Timed);
        Check.True(
            ReferenceEquals(first, retry) &&
            ordinary.Map.SnapshotMonsters().Count ==
                ordinary.Inputs.Definitions.Length &&
            !ordinary.Map.TryGetMedusaOwnershipSnapshot(out _),
            "ordinary unowned map retains generic Timed idempotence");
    }

    private static void CheckExactRetryAndExplicitMapTwoHundredConflict()
    {
        var fixture = CreateAttachmentFixture();
        var first = AttachAuthored(fixture);
        var firstState = SnapshotMonsterValues(fixture.Map);
        var permuted =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Inputs.Definitions)
                .Reverse()
                .ToArray();
        var exactRetry = AttachAuthored(fixture, permuted);
        Check.True(
            first.IsAttached &&
            exactRetry.Outcome ==
                MedusaMonsterAttachmentOutcome.AlreadyAttached &&
            exactRetry.Snapshot == first.Snapshot &&
            SnapshotMonsterValues(fixture.Map) == firstState,
            "exact canonical fingerprint retry is idempotent only after complete publication");

        var owned = fixture.Map.TryGetMedusaOwnershipSnapshot(
            out var ownership)
            ? ownership
            : throw new InvalidOperationException(
                "Attached retry fixture lost ownership.");
        var defeated = owned.MonsterBindings[0];
        var defeatedTarget = FindMonster(
            fixture.Map,
            defeated.RosterSpawnId);
        var committedDefeat = CommitTypedDamage(
            fixture.Map,
            defeatedTarget,
            101,
            CombatDamageChannel.Physical,
            uint.MaxValue,
            StartedAt.AddSeconds(1));
        var defeatedState = SnapshotMonsterValues(fixture.Map);
        Check.True(
            committedDefeat.Defeat?.Claim is
                { Outcome: MedusaDefeatClaimOutcome.Applied } &&
            AttachAuthored(fixture).Outcome ==
                MedusaMonsterAttachmentOutcome.AlreadyAttached,
            "a later typed lethal commit does not alter immutable initialization identity");

        var drift =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Inputs.Definitions);
        drift[0].Packet[^1] ^= 0x5A;
        var conflict = AttachAuthored(fixture, drift);
        var mythicInputs = CreateAttachmentInputs(
            MedusaEncounterDifficulty.Mythic,
            fixture.Map.WorldInstanceId,
            fixture.Map.Descriptor.CreatedAt);
        var mythicConflict =
            fixture.Map.PrepareAndAttachMedusaForAuthoredValidationTests(
                mythicInputs.Difficulty,
                mythicInputs.AdmittedCharacterIds,
                mythicInputs.RunSpawns,
                mythicInputs.Definitions);
        Check.True(
            conflict.Outcome ==
                MedusaMonsterAttachmentOutcome.FingerprintConflict &&
            mythicConflict.Outcome ==
                MedusaMonsterAttachmentOutcome.FingerprintConflict &&
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var unchanged) &&
            unchanged.Difficulty ==
                MedusaEncounterDifficulty.Enhanced &&
            fixture.Map.TryGetMedusaMonsterAttachmentSnapshot(
                out var unchangedAttachment) &&
            unchangedAttachment == first.Snapshot &&
            SnapshotMonsterValues(fixture.Map) == defeatedState,
            "packet drift and explicit Mythic identity conflict without map-200 inference or mutation");
    }

    private static void CheckAttachmentRollbackAndModeFence()
    {
        var preinitialized = CreateAttachmentFixture();
        _ = preinitialized.Map.InitializeMonsters(
            preinitialized.Inputs.Definitions,
            StartedAt,
            respawnPolicy: MonsterRespawnPolicy.Timed);
        var preinitializedState =
            SnapshotMonsterValues(preinitialized.Map);
        var existingConflict = AttachAuthored(preinitialized);
        Check.True(
            existingConflict.Outcome ==
                MedusaMonsterAttachmentOutcome.ExistingStateConflict &&
            existingConflict.Snapshot is null &&
            !preinitialized.Map.TryGetMedusaOwnershipSnapshot(out _) &&
            !preinitialized.Map.TryGetMedusaMonsterAttachmentSnapshot(
                out _) &&
            SnapshotMonsterValues(preinitialized.Map) ==
                preinitializedState,
            "a generic pre-existing runtime is conflict, never an idempotent Medusa attachment");

        var rejecting = CreateAttachmentFixture(
            rejectNeverWorldBoss: true);
        var creationFailed = AttachAuthored(rejecting);
        AssertUnpublishedAttachment(
            rejecting.Map,
            creationFailed,
            MedusaMonsterAttachmentOutcome.RuntimeCreationFailed,
            MedusaMonsterBootstrapValidationOutcome.Prepared,
            "runtime factory rollback");
        Check.True(
            rejecting.Map.BindMedusaEncounter(
                rejecting.Inputs.Difficulty,
                rejecting.Inputs.AdmittedCharacterIds,
                rejecting.Inputs.RunSpawns).IsBound,
            "runtime creation rollback leaves the owner publication slot unconsumed");

        var legacyMonsters = CreateAttachmentFixture(
            monsterRuntimeMode: MonsterRuntimeMode.Legacy);
        var unsupported = AttachAuthored(legacyMonsters);
        AssertUnpublishedAttachment(
            legacyMonsters.Map,
            unsupported,
            MedusaMonsterAttachmentOutcome
                .MonsterRuntimeModeUnsupported,
            bootstrapOutcome: null,
            "non-ECS attachment mode rejection");
    }

    private static async Task CheckLegacyStagedTransferFencesAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attachment = CreateAttachmentFixture(
            playerRuntimeMode: PlayerRuntimeMode.Legacy);
        using var stagedAttachment = attachment.Map.StagePlayerTransfer(
            CreateContext(attachment.Map, socket.Session));
        Check.True(
            attachment.Map.Population == 0,
            "Legacy staged transfer remains invisible to session population");
        var rejectedAttachment = AttachAuthored(attachment);
        AssertUnpublishedAttachment(
            attachment.Map,
            rejectedAttachment,
            MedusaMonsterAttachmentOutcome.RuntimeNotEmpty,
            bootstrapOutcome: null,
            "Legacy staged-transfer attachment reservation");
        stagedAttachment.Dispose();
        Check.True(
            AttachAuthored(attachment).Outcome ==
                MedusaMonsterAttachmentOutcome.Attached,
            "rolling back staged attachment reservation restores availability");

        var binding = CreateAttachmentFixture(
            playerRuntimeMode: PlayerRuntimeMode.Legacy);
        using var stagedBinding = binding.Map.StagePlayerTransfer(
            CreateContext(binding.Map, socket.Session));
        var bindRejected = binding.Map.BindMedusaEncounter(
            binding.Inputs.Difficulty,
            binding.Inputs.AdmittedCharacterIds,
            binding.Inputs.RunSpawns);
        Check.True(
            binding.Map.Population == 0 &&
            bindRejected.Outcome ==
                MedusaInstanceBindOutcome.RuntimeNotEmpty &&
            !binding.Map.TryGetMedusaOwnershipSnapshot(out _),
            "standalone bind sees the Legacy ECS-shadow transfer reservation");
        stagedBinding.Dispose();
        Check.True(
            binding.Map.BindMedusaEncounter(
                binding.Inputs.Difficulty,
                binding.Inputs.AdmittedCharacterIds,
                binding.Inputs.RunSpawns).IsBound,
            "standalone bind remains available after staged rollback");
    }

    private static void AssertUnpublishedAttachment(
        MapInstance map,
        MedusaMonsterAttachmentResult result,
        MedusaMonsterAttachmentOutcome outcome,
        MedusaMonsterBootstrapValidationOutcome? bootstrapOutcome,
        string boundary)
    {
        Check.True(
            result.Outcome == outcome &&
            result.BootstrapOutcome == bootstrapOutcome &&
            result.Snapshot is null &&
            !result.IsAttached &&
            !map.TryGetMedusaOwnershipSnapshot(out _) &&
            !map.TryGetMedusaMonsterAttachmentSnapshot(out _) &&
            map.SnapshotMonsters().Count == 0,
            $"{boundary} leaves owner, attachment, and monster runtime unpublished");
    }
}
