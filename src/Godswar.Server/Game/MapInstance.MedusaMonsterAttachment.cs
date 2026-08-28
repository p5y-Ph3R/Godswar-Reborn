using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private enum MedusaAttachmentValidationAuthority : byte
    {
        ProductionLive = 1,
        AuthoredValidationTests = 2
    }

    private MedusaMonsterAttachmentSnapshot? _medusaMonsterAttachment;

    internal MedusaMonsterAttachmentResult
        PrepareAndAttachMedusaProductionLive(
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int> admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition> runSpawns,
            IReadOnlyList<CapturedMonsterSpawn> definitions) =>
        PrepareAndAttachMedusaMonsters(
            difficulty,
            admittedCharacterIds,
            runSpawns,
            definitions,
            MedusaAttachmentValidationAuthority.ProductionLive);

    /// <summary>
    /// Test-only success seam for validating authored attachment mechanics
    /// while live placements remain uncertified. Prepared production context
    /// deliberately exposes only PrepareAndAttachMedusaProductionLive.
    /// </summary>
    internal MedusaMonsterAttachmentResult
        PrepareAndAttachMedusaForAuthoredValidationTests(
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int> admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition> runSpawns,
            IReadOnlyList<CapturedMonsterSpawn> definitions) =>
        PrepareAndAttachMedusaMonsters(
            difficulty,
            admittedCharacterIds,
            runSpawns,
            definitions,
            MedusaAttachmentValidationAuthority
                .AuthoredValidationTests);

    internal bool TryGetMedusaMonsterAttachmentSnapshot(
        out MedusaMonsterAttachmentSnapshot snapshot)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaMonsterAttachment is { } attached)
            {
                snapshot = attached;
                return true;
            }

            snapshot = null!;
            return false;
        }
    }

    private MedusaMonsterAttachmentResult PrepareAndAttachMedusaMonsters(
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int>? admittedCharacterIds,
        IReadOnlyCollection<MedusaRunSpawnDefinition>? runSpawns,
        IReadOnlyList<CapturedMonsterSpawn>? definitions,
        MedusaAttachmentValidationAuthority authority)
    {
        lock (_medusaOwnershipGate)
        {
            lock (_descriptorGate)
            {
                var descriptor = _descriptor;
                var ownershipOutcome = TryCreateMedusaOwnerCandidate(
                    descriptor,
                    difficulty,
                    admittedCharacterIds,
                    runSpawns,
                    out var candidate);
                if (ownershipOutcome != MedusaInstanceBindOutcome.Bound)
                {
                    return RejectedAttachment(
                        MedusaMonsterAttachmentOutcome.OwnershipRejected,
                        ownershipOutcome: ownershipOutcome);
                }

                lock (_membershipGate)
                {
                    if (HasMedusaPlayerMembershipOrStaging())
                    {
                        return RejectedAttachment(
                            MedusaMonsterAttachmentOutcome.RuntimeNotEmpty);
                    }

                    lock (_monsterRuntimeGate)
                    {
                        return PrepareAndAttachMedusaMonstersLocked(
                            candidate,
                            definitions,
                            authority);
                    }
                }
            }
        }
    }

    private MedusaMonsterAttachmentResult
        PrepareAndAttachMedusaMonstersLocked(
            MedusaInstanceOwnerBoundAggregate candidate,
            IReadOnlyList<CapturedMonsterSpawn>? definitions,
            MedusaAttachmentValidationAuthority authority)
    {
        if (_monsterRuntimeMode != MonsterRuntimeMode.Ecs)
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome
                    .MonsterRuntimeModeUnsupported);
        }

        var ownership = candidate.Snapshot();
        var validation = authority switch
        {
            MedusaAttachmentValidationAuthority.ProductionLive =>
                MedusaMonsterBootstrapPolicy.PrepareProductionLive(
                    ownership,
                    definitions),
            MedusaAttachmentValidationAuthority.AuthoredValidationTests =>
                MedusaMonsterBootstrapPolicy.PrepareAuthored(
                    ownership,
                    definitions),
            _ => throw new InvalidOperationException(
                $"Unknown Medusa attachment authority {authority}.")
        };
        if (!validation.IsPrepared)
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.BootstrapRejected,
                bootstrapOutcome: validation.Outcome);
        }

        var preparation = validation.Preparation!;
        if (!IsExactNeverPreparation(ownership, preparation))
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.RuntimeVerificationFailed,
                bootstrapOutcome: validation.Outcome);
        }

        var ownsState = _medusaInstanceOwner is not null;
        var hasRuntime = _monsterRuntime is not null;
        var hasPolicy = _monsterRespawnPolicy is not null;
        var hasAttachment = _medusaMonsterAttachment is not null;
        var hasAllPublishedState = ownsState &&
                                   hasRuntime &&
                                   hasPolicy &&
                                   hasAttachment;
        if (hasAllPublishedState)
        {
            var existingOwnership =
                _medusaInstanceOwner!.Snapshot();
            if (!IsCompletePublishedMedusaAttachment(
                    existingOwnership,
                    _medusaMonsterAttachment!))
            {
                return RejectedAttachment(
                    MedusaMonsterAttachmentOutcome.ExistingStateConflict,
                    bootstrapOutcome: validation.Outcome);
            }

            if (OwnerRequestsMatch(existingOwnership, ownership) &&
                string.Equals(
                    _medusaMonsterAttachment!.Fingerprint,
                    preparation.Fingerprint,
                    StringComparison.Ordinal))
            {
                return new(
                    MedusaMonsterAttachmentOutcome.AlreadyAttached,
                    OwnershipOutcome: null,
                    validation.Outcome,
                    _medusaMonsterAttachment);
            }

            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.FingerprintConflict,
                bootstrapOutcome: validation.Outcome);
        }
        if (ownsState || hasRuntime || hasPolicy || hasAttachment)
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.ExistingStateConflict,
                bootstrapOutcome: validation.Outcome);
        }

        var runtimeDefinitions = preparation.CreateCapturedDefinitions();
        IMonsterMapRuntime stagedRuntime;
        try
        {
            EnsureMonsterObjectIdsDoNotCollideWithNpcs(runtimeDefinitions);
            stagedRuntime = MonsterMapRuntimeFactory.Create(
                MonsterRuntimeMode.Ecs,
                MapId,
                runtimeDefinitions,
                preparation.StartedAt,
                corpseDespawnDelay:
                    MedusaMonsterPresentationPolicy.CorpseRemovalDelay,
                activeWorldBossRespawn: null,
                worldBossCatalog: _worldBossCatalog,
                respawnPolicy: MonsterRespawnPolicy.Never,
                monsterCombatProfiles: _monsterCombatProfiles);
        }
        catch (Exception error) when (
            error is ArgumentException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.RuntimeCreationFailed,
                bootstrapOutcome: validation.Outcome);
        }

        if (!TryVerifyStagedMedusaRuntime(
                stagedRuntime,
                preparation,
                out var runtimeInstanceId))
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.RuntimeVerificationFailed,
                bootstrapOutcome: validation.Outcome);
        }
        if (!TryApplyConfiguredMedusaMovement(
                stagedRuntime,
                preparation))
        {
            return RejectedAttachment(
                MedusaMonsterAttachmentOutcome.RuntimeVerificationFailed,
                bootstrapOutcome: validation.Outcome);
        }

        var attachment = new MedusaMonsterAttachmentSnapshot(
            ownership.WorldInstanceId,
            ownership.Difficulty,
            ownership.ContentMapId,
            preparation.StartedAt,
            MonsterRuntimeMode.Ecs,
            MonsterRespawnPolicy.Never,
            preparation.RuntimeSpawnCount,
            runtimeInstanceId,
            preparation.Fingerprint);

        // Every fallible operation has completed. These reference/value
        // assignments are the one publication point under all owner gates.
        _medusaInstanceOwner = candidate;
        _monsterRuntime = stagedRuntime;
        _monsterRespawnPolicy = MonsterRespawnPolicy.Never;
        _medusaMonsterAttachment = attachment;
        return new(
            MedusaMonsterAttachmentOutcome.Attached,
            OwnershipOutcome: null,
            validation.Outcome,
            attachment);
    }

    private static bool IsExactNeverPreparation(
        MedusaInstanceOwnershipSnapshot ownership,
        MedusaMonsterBootstrapPreparation preparation) =>
        preparation.WorldInstanceId == ownership.WorldInstanceId &&
        preparation.Difficulty == ownership.Difficulty &&
        preparation.ContentMapId == ownership.ContentMapId &&
        preparation.StartedAt == ownership.Run.StartedAt &&
        preparation.RespawnPolicy == MonsterRespawnPolicy.Never &&
        preparation.Spawns.Length ==
            MedusaIslandRosterPolicy.TotalSpawnCount &&
        preparation.AmbientSpawns.Length ==
            MedusaIslandAmbientSpawnPolicy.CountFor(
                ownership.Difficulty) &&
        preparation.RuntimeSpawnCount ==
            MedusaIslandRosterPolicy.TotalSpawnCount +
            MedusaIslandAmbientSpawnPolicy.CountFor(
                ownership.Difficulty) &&
        preparation.Spawns.All(static spawn =>
            spawn.SpawnGeneration == 1) &&
        preparation.AmbientSpawns.All(static spawn =>
            spawn.SpawnGeneration == 1) &&
        preparation.Fingerprint.Length == 64;

    private static bool OwnerRequestsMatch(
        MedusaInstanceOwnershipSnapshot left,
        MedusaInstanceOwnershipSnapshot right) =>
        left.WorldInstanceId == right.WorldInstanceId &&
        left.Difficulty == right.Difficulty &&
        left.ContentMapId == right.ContentMapId &&
        left.Run.StartedAt == right.Run.StartedAt &&
        left.Run.Deadline == right.Run.Deadline &&
        left.Run.AdmittedCharacterIds.SequenceEqual(
            right.Run.AdmittedCharacterIds) &&
        left.Run.Spawns.Count == right.Run.Spawns.Count &&
        left.Run.Spawns.Zip(right.Run.Spawns).All(pair =>
            SameRunSpawnInitialization(
                pair.First,
                pair.Second)) &&
        left.MonsterBindings.SequenceEqual(right.MonsterBindings);

    private static bool SameRunSpawnInitialization(
        MedusaRunSpawnSnapshot left,
        MedusaRunSpawnSnapshot right) =>
        string.Equals(
            left.RosterSpawnId,
            right.RosterSpawnId,
            StringComparison.Ordinal) &&
        left.ObjectId == right.ObjectId &&
        left.SpawnGeneration == right.SpawnGeneration &&
        string.Equals(
            left.TemplateKey,
            right.TemplateKey,
            StringComparison.Ordinal) &&
        left.Role == right.Role &&
        left.Rank == right.Rank &&
        left.ScoreValue == right.ScoreValue;

    private static MedusaMonsterAttachmentResult RejectedAttachment(
        MedusaMonsterAttachmentOutcome outcome,
        MedusaInstanceBindOutcome? ownershipOutcome = null,
        MedusaMonsterBootstrapValidationOutcome? bootstrapOutcome = null) =>
        new(
            outcome,
            ownershipOutcome,
            bootstrapOutcome,
            Snapshot: null);
}
