using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaPreparedWorldInstanceCreationChecks
{
    public const string CheckName =
        "Medusa prepared-before-active world-instance creation";

    private static readonly DateTimeOffset TestTime =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckEntryPreparationPublishesRosterAsync();
        await CheckPreparedBeforePublicationAsync();
        await CheckPreparationFailureIsUnpublishedAsync();
        await CheckRetainedContextsExpireAfterPublicationRejectionAsync();
        await CheckInvalidKindNeverInvokesPreparationAsync();
        await CheckRegistryResultIsImmutableAsync();
        CheckCapabilitySurfaceIsNarrow();
    }

    private static async Task CheckEntryPreparationPublishesRosterAsync()
    {
        await using var directory = CreateDirectory();
        var preparation = new MedusaWorldInstanceEntryPreparation(
            MedusaEncounterDifficulty.Enhanced,
            [101, 102]);

        var result = await directory.CreatePreparedInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            TestTime,
            preparation,
            CancellationToken.None);

        Check.True(
            result.Status == WorldInstanceRuntimeDirectoryStatus.Created &&
            result.Runtime is { } runtime &&
            runtime.Map.TryGetMedusaMonsterAttachmentSnapshot(
                out var attachment) &&
            attachment.MonsterCount ==
                MedusaIslandRosterPolicy.TotalSpawnCount +
                MedusaIslandAmbientSpawnPolicy.CountFor(
                    MedusaEncounterDifficulty.Enhanced) &&
            runtime.Map.SnapshotMonsters().Count ==
                MedusaIslandRosterPolicy.TotalSpawnCount +
                MedusaIslandAmbientSpawnPolicy.CountFor(
                    MedusaEncounterDifficulty.Enhanced) &&
            runtime.Map.SnapshotMonsters().All(monster =>
                WorldObjectIds.IsMonster(monster.ObjectId)) &&
            runtime.Map.SnapshotMonsters().Single(monster =>
                monster.ObjectId ==
                    WorldObjectIds.MedusaBabyRockElfObjectId) is
                {
                    HomeX: MedusaIslandAmbientSpawnPolicy.BabyRockElfX,
                    HomeZ: MedusaIslandAmbientSpawnPolicy.BabyRockElfZ,
                    Definition.TemplateKey:
                        MedusaIslandAmbientSpawnPolicy.BabyRockElfTemplateKey,
                    Definition.Tier: 1
                } &&
            runtime.Map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Run.AdmittedCharacterIds.SequenceEqual([101, 102]),
            "Medusa entry preparation publishes the complete roster and " +
            "exact admitted party before activation");
    }

    private static async Task CheckPreparedBeforePublicationAsync()
    {
        await using var directory = CreateDirectory();
        var preparation = new RecordingPreparation();

        var result = await directory.CreatePreparedInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            TestTime,
            preparation,
            CancellationToken.None);

        Check.True(
            result.Status == WorldInstanceRuntimeDirectoryStatus.Created &&
            result.Runtime is { } runtime &&
            runtime.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Active &&
            runtime.ContentMapId == new WorldMapId(200) &&
            preparation.PrepareCalls == 1 &&
            preparation.ValidateCalls == 1 &&
            preparation.StateSeenDuringPrepare ==
                WorldInstanceLifecycleState.Creating &&
            preparation.StateSeenDuringValidation ==
                WorldInstanceLifecycleState.Creating &&
            preparation.PreparedInstanceId == runtime.InstanceId &&
            directory.GetSnapshot().RuntimeCount == 1,
            "prepared dungeon content is complete while Creating and only then published Active");
        preparation.AssertRuntimeLeasesExpired(
            "successful prepared publication");
    }

    private static async Task CheckPreparationFailureIsUnpublishedAsync()
    {
        await using var directory = CreateDirectory();
        var preparation = new ThrowingPreparation();

        var threw = false;
        try
        {
            _ = await directory.CreatePreparedInstancedAsync(
                RealmId.Tempest,
                new WorldMapId(204),
                InstanceKind.Dungeon,
                playerCapacity: 5,
                TestTime,
                preparation,
                CancellationToken.None);
        }
        catch (InvalidOperationException error) when (
            error.Message == ThrowingPreparation.FailureMessage)
        {
            threw = true;
        }

        Check.True(
            threw &&
            preparation.PrepareCalls == 1 &&
            preparation.ValidateCalls == 0 &&
            directory.GetSnapshot().RuntimeCount == 0 &&
            directory.Snapshot().Count == 0,
            "failed preparation disposes the unpublished runtime without indexing it");
        preparation.AssertRuntimeLeaseExpired();
    }

    private static async Task
        CheckRetainedContextsExpireAfterPublicationRejectionAsync()
    {
        await using var directory = CreateDirectory(maximumInstances: 1);
        var existing = await directory.CreateInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(204),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            TestTime,
            CancellationToken.None);
        var preparation = new RecordingPreparation();

        var rejected = await directory.CreatePreparedInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            TestTime,
            preparation,
            CancellationToken.None);

        Check.True(
            existing.Status ==
                WorldInstanceRuntimeDirectoryStatus.Created &&
            rejected.Status ==
                WorldInstanceRuntimeDirectoryStatus.PlacementRejected &&
            rejected.PlacementStatus ==
                WorldInstancePlacementStatus.RegistryFull &&
            rejected.Runtime is null &&
            preparation.PrepareCalls == 1 &&
            preparation.ValidateCalls == 1 &&
            directory.GetSnapshot().RuntimeCount == 1,
            "placement rejection disposes the prepared runtime without publishing it");
        preparation.AssertRuntimeLeasesExpired(
            "placement-rejected prepared publication");
    }

    private static async Task CheckInvalidKindNeverInvokesPreparationAsync()
    {
        await using var directory = CreateDirectory();
        var preparation = new RecordingPreparation();

        var result = await directory.CreatePreparedInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.OpenWorld,
            playerCapacity: 5,
            TestTime,
            preparation,
            CancellationToken.None);

        Check.True(
            result.Status ==
                WorldInstanceRuntimeDirectoryStatus.InvalidInstanceKind &&
            preparation.PrepareCalls == 0 &&
            preparation.ValidateCalls == 0 &&
            directory.GetSnapshot().RuntimeCount == 0,
            "invalid prepared-instance kinds fail before executing content preparation");
    }

    private static async Task CheckRegistryResultIsImmutableAsync()
    {
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = RealmId.Tempest.Value,
                MaximumRuntimes = 4,
                MaximumPlayerAssignments = 20,
                MaximumRetiredInstanceIds = 16,
                DefaultOpenWorldPlayerCapacity = 20,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 2
            });
        var preparation = new RecordingPreparation(
            expectedPreparedAt: null);

        var result = await registry.CreatePreparedLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            preparation,
            CancellationToken.None);
        var descriptor = result.Descriptor;

        Check.True(
            result.Status == WorldInstanceRuntimeDirectoryStatus.Created &&
            result.Succeeded &&
            descriptor is not null &&
            descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Active &&
            descriptor.MapId == new WorldMapId(200) &&
            result.InstanceId == descriptor.InstanceId,
            "registry prepared creation returns only immutable published identity");
        preparation.AssertRuntimeLeasesExpired(
            "registry prepared publication");
    }

    private static void CheckCapabilitySurfaceIsNarrow()
    {
        var capabilityType =
            typeof(IWorldInstanceRuntimePreparationContext);
        var leaksMutableRuntime = capabilityType.GetMethods().Any(
            static method =>
                ContainsMutableRuntime(method.ReturnType) ||
                method.GetParameters().Any(parameter =>
                    ContainsMutableRuntime(parameter.ParameterType)));
        var registryProperties =
            typeof(PreparedWorldInstanceCreationResult)
                .GetProperties();
        var registryResultLeaksMutableRuntime = registryProperties.Any(
            static property =>
                property.Name == "Runtime" ||
                ContainsMutableRuntime(property.PropertyType));

        Check.True(
            !leaksMutableRuntime &&
            !registryResultLeaksMutableRuntime,
            "prepared creation exposes no mutable map or runtime handle");
    }

    private static bool ContainsMutableRuntime(Type type)
    {
        if (type == typeof(WorldInstanceRuntime) ||
            type == typeof(MapInstance))
        {
            return true;
        }
        if (type.HasElementType)
        {
            return ContainsMutableRuntime(type.GetElementType()!);
        }

        return type.IsGenericType &&
            type.GetGenericArguments().Any(ContainsMutableRuntime);
    }

    private static LocalWorldInstanceRuntimeDirectory CreateDirectory(
        int maximumInstances = 4) =>
        new(
            new LocalWorldInstancePlacementRegistry(
                ServerNodeId.Local,
                maximumInstances,
                maximumPlayerAssignments: 20,
                maximumRetiredInstanceIds: 16),
            new MapWorldInstanceRuntimeFactory(),
            ownerInvocationTimeout: TimeSpan.FromSeconds(2),
            ownerShutdownTimeout: TimeSpan.FromSeconds(2));

    private sealed class RecordingPreparation :
        IWorldInstanceRuntimePreparation
    {
        private readonly DateTimeOffset? _expectedPreparedAt;

        public RecordingPreparation()
            : this(TestTime)
        {
        }

        public RecordingPreparation(
            DateTimeOffset? expectedPreparedAt)
        {
            _expectedPreparedAt = expectedPreparedAt;
        }

        public int PrepareCalls { get; private set; }

        public int ValidateCalls { get; private set; }

        public WorldInstanceLifecycleState StateSeenDuringPrepare
        {
            get;
            private set;
        }

        public WorldInstanceLifecycleState StateSeenDuringValidation
        {
            get;
            private set;
        }

        public WorldInstanceId PreparedInstanceId { get; private set; }

        public IWorldInstanceRuntimePreparationContext? PrepareContext
        {
            get;
            private set;
        }

        public IWorldInstanceRuntimePreparationContext? ValidateContext
        {
            get;
            private set;
        }

        public void Prepare(
            IWorldInstanceRuntimePreparationContext context)
        {
            PrepareCalls++;
            PrepareContext = context;
            StateSeenDuringPrepare =
                context.Descriptor.LifecycleState;
            PreparedInstanceId = context.Descriptor.InstanceId;
            Check.True(
                (_expectedPreparedAt is null ||
                    context.PreparedAt == _expectedPreparedAt) &&
                context.PreparedAt == context.Descriptor.CreatedAt &&
                context.Descriptor.Kind == InstanceKind.Dungeon &&
                context.Population == 0,
                "preparation receives immutable identity and an empty-runtime capability");
        }

        public void ValidatePrepared(
            IWorldInstanceRuntimePreparationContext context)
        {
            ValidateCalls++;
            ValidateContext = context;
            StateSeenDuringValidation =
                context.Descriptor.LifecycleState;
            Check.Throws<ObjectDisposedException>(
                () => _ = PrepareContext!.Population,
                "the Prepare callback lease expires before validation");
            Check.True(
                context.Descriptor.InstanceId == PreparedInstanceId &&
                context.Population == 0,
                "prepared identity is stable through bounded validation");
        }

        public void AssertRuntimeLeasesExpired(string phase)
        {
            var prepareContext = PrepareContext ??
                throw new InvalidOperationException(
                    "Prepare context was not captured.");
            var validateContext = ValidateContext ??
                throw new InvalidOperationException(
                    "Validation context was not captured.");
            Check.Throws<ObjectDisposedException>(
                () => _ = prepareContext.Population,
                $"{phase} revokes the Prepare capability");
            Check.Throws<ObjectDisposedException>(
                () => _ = validateContext.Population,
                $"{phase} revokes the validation capability");
            Check.True(
                prepareContext.Descriptor.InstanceId ==
                    PreparedInstanceId &&
                validateContext.Descriptor.InstanceId ==
                    PreparedInstanceId,
                $"{phase} retains only safe immutable identity");
        }
    }

    private sealed class ThrowingPreparation :
        IWorldInstanceRuntimePreparation
    {
        public const string FailureMessage = "fixture preparation rejected";

        public int PrepareCalls { get; private set; }

        public int ValidateCalls { get; private set; }

        private IWorldInstanceRuntimePreparationContext? Context
        {
            get;
            set;
        }

        public void Prepare(
            IWorldInstanceRuntimePreparationContext context)
        {
            PrepareCalls++;
            Context = context;
            throw new InvalidOperationException(FailureMessage);
        }

        public void ValidatePrepared(
            IWorldInstanceRuntimePreparationContext context) =>
            ValidateCalls++;

        public void AssertRuntimeLeaseExpired()
        {
            Check.Throws<ObjectDisposedException>(
                () => _ = Context!.Population,
                "a throwing preparation revokes its retained capability");
        }
    }
}
