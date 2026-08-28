using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckSameMapTransferAuthorityAsync()
    {
        await CheckSameMapTransferCollisionAsync(
            destinationBound: true);
        await CheckSameMapTransferCollisionAsync(
            destinationBound: false);
    }

    private static async Task CheckSameMapTransferCollisionAsync(
        bool destinationBound)
    {
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = RealmId.Tempest.Value,
                MaximumRuntimes = 4,
                MaximumPlayerAssignments = 8,
                MaximumRetiredInstanceIds = 16,
                DefaultOpenWorldPlayerCapacity = 8,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 1
            },
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);
        var sourcePreparation = new DamageAuthorityPreparation();
        var source = await registry.CreatePreparedLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            sourcePreparation,
            CancellationToken.None);
        var sourceId = source.InstanceId ?? throw new InvalidOperationException(
            "Source Medusa instance was not created.");

        WorldInstanceId destinationId;
        if (destinationBound)
        {
            var destination = await registry
                .CreatePreparedLocalWorldInstanceAsync(
                    RealmId.Tempest,
                    new WorldMapId(200),
                    InstanceKind.Dungeon,
                    playerCapacity: 5,
                    new DamageAuthorityPreparation(),
                    CancellationToken.None);
            destinationId = destination.InstanceId ??
                throw new InvalidOperationException(
                    "Destination Medusa instance was not created.");
        }
        else
        {
            var destination = await registry.CreateLocalWorldInstanceAsync(
                RealmId.Tempest,
                new WorldMapId(200),
                InstanceKind.Dungeon,
                playerCapacity: 5);
            destinationId = destination.Runtime?.InstanceId ??
                throw new InvalidOperationException(
                    "Destination unbound instance was not created.");
            Check.Equal(
                sourcePreparation.Inputs.Definitions.Length,
                registry.InitializeWorldInstanceMonsters(
                    destinationId,
                    sourcePreparation.Inputs.Definitions,
                    StartedAt),
                "unbound collision destination initializes matching identities");
        }

        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var character = CreateRegistryDamageCharacter(101, mapId: 200);
        registry.JoinWorldInstance(
            session,
            character.AccountId,
            character,
            objectId: 0x7C01,
            sourceId,
            worldReady: true,
            joinedAt: StartedAt);
        Check.True(registry.TryCapturePlayerMonsterTarget(
                session,
                mapId: 200,
                objectId:
                    sourcePreparation.Inputs.RunSpawns[0].ObjectId,
                out var sourceTarget,
                out var sourceAuthority),
            "source intent atomically captures target and world authority");
        var sourceRuntime = RequiredRegistryRuntime(registry, sourceId);
        var destinationRuntime = RequiredRegistryRuntime(
            registry,
            destinationId);
        var sourceBefore = RequiredMonster(
            sourceRuntime.Map,
            sourceTarget.ObjectId);
        var sourceContext = registry
            .GetWorldInstanceSessions(sourceId)
            .Single(context => ReferenceEquals(context.Session, session));
        if (!destinationBound)
        {
            CheckCurrentBoundSecondaryDamageFences(
                registry,
                sourceRuntime,
                sourceContext,
                sourceTarget);
        }

        Check.True(registry.TryTransferWorldInstance(
                session,
                sourceId,
                destinationId,
                targetX: 2,
                targetZ: 2) &&
            registry.TryMarkWorldReady(
                session,
                new Dictionary<uint, long>(),
                out var unseen) &&
            unseen.Count == 0,
            "same-map transfer reaches a ready destination route");
        Check.True(registry.TryCapturePlayerMonsterTarget(
                session,
                mapId: 200,
                sourceTarget.ObjectId,
                out var destinationTarget,
                out var destinationAuthority),
            "destination exposes the colliding authored target identity");
        Check.True(
            sourceTarget.ObjectId == destinationTarget.ObjectId &&
            sourceTarget.SpawnGeneration ==
                destinationTarget.SpawnGeneration &&
            sourceTarget.HealthRevision ==
                destinationTarget.HealthRevision &&
            sourceAuthority.WorldInstanceId !=
                destinationAuthority.WorldInstanceId &&
            sourceTarget.RuntimeInstanceId !=
                destinationTarget.RuntimeInstanceId,
            "same-map instances collide on wire identity but retain explicit owner and runtime fences");
        var destinationBefore = RequiredMonster(
            destinationRuntime.Map,
            destinationTarget.ObjectId);

        var applied = registry.TryCommitPlayerMonsterDamageGuarded(
            session,
            mapId: 200,
            sourceTarget.ObjectId,
            sourceTarget.RuntimeInstanceId,
            character.Id,
            sourceTarget.SpawnGeneration,
            sourceTarget.HealthRevision,
            sourceAuthority,
            StartedAt.AddSeconds(1),
            Resolution(CombatDamageChannel.Physical, damage: 1),
            out var commit);
        var sourceAfter = RequiredMonster(
            sourceRuntime.Map,
            sourceTarget.ObjectId);
        var destinationAfter = RequiredMonster(
            destinationRuntime.Map,
            destinationTarget.ObjectId);
        Check.True(
            !applied &&
            commit == default &&
            SameMonsterHealth(sourceBefore, sourceAfter) &&
            SameMonsterHealth(destinationBefore, destinationAfter),
            destinationBound
                ? "a stale Medusa intent cannot damage a colliding Medusa destination"
                : "a stale Medusa intent cannot fall through to a colliding unbound map-200 destination");
        if (!destinationBound)
        {
            CheckStaleSecondaryDamageFences(
                registry,
                sourceRuntime,
                destinationRuntime,
                sourceContext,
                sourceTarget,
                destinationTarget);
        }
    }

    private static MonsterRuntimeSnapshot RequiredMonster(
        MapInstance map,
        uint objectId) => map.TryGetMonsterSnapshot(objectId, out var value)
        ? value
        : throw new InvalidOperationException(
            $"Monster {objectId} was not found.");

    private static WorldInstanceRuntime RequiredRegistryRuntime(
        GameSessionRegistry registry,
        WorldInstanceId instanceId) =>
        TryGetRegistryRuntime(registry, instanceId, out var runtime)
            ? runtime
            : throw new InvalidOperationException(
                $"Runtime {instanceId} was not found.");

    private static bool SameMonsterHealth(
        MonsterRuntimeSnapshot expected,
        MonsterRuntimeSnapshot actual) =>
        expected.CurrentHealth == actual.CurrentHealth &&
        expected.HealthRevision == actual.HealthRevision;

    private sealed class DamageAuthorityPreparation :
        IWorldInstanceRuntimePreparation
    {
        private readonly int[] _additionalAdmittedCharacterIds;

        public DamageAuthorityPreparation(
            params int[] additionalAdmittedCharacterIds)
        {
            _additionalAdmittedCharacterIds =
                additionalAdmittedCharacterIds ?? [];
        }

        public MedusaAttachmentInputs Inputs { get; private set; } = null!;

        private MedusaMonsterAttachmentResult Attachment { get; set; }

        public void Prepare(IWorldInstanceRuntimePreparationContext context)
        {
            var authoredContext = context as
                WorldInstanceRuntimePreparationContext ??
                throw new InvalidOperationException(
                    "Authored fixture requires the revocable concrete preparation capability.");
            Inputs = CreateAttachmentInputs(
                MedusaEncounterDifficulty.Enhanced,
                context.Descriptor.InstanceId,
                context.PreparedAt);
            if (_additionalAdmittedCharacterIds.Length > 0)
            {
                Inputs = Inputs with
                {
                    AdmittedCharacterIds = Inputs.AdmittedCharacterIds
                        .Concat(_additionalAdmittedCharacterIds)
                        .Distinct()
                        .ToArray()
                };
            }
            Attachment = authoredContext
                .PrepareAndAttachMedusaForAuthoredValidationTests(
                Inputs.Difficulty,
                Inputs.AdmittedCharacterIds,
                Inputs.RunSpawns,
                Inputs.Definitions);
        }

        public void ValidatePrepared(
            IWorldInstanceRuntimePreparationContext context)
        {
            Check.True(
                Attachment.IsAttached &&
                Attachment.Snapshot?.WorldInstanceId ==
                    context.Descriptor.InstanceId,
                "collision fixture publishes only an attached Medusa owner");
        }
    }
}
