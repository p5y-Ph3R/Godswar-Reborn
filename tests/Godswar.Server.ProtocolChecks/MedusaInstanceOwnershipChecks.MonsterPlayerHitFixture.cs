using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private sealed class MonsterPlayerHitFixture : IAsyncDisposable
    {
        private MonsterPlayerHitFixture(
            GameSessionRegistry registry,
            WorldInstanceRuntime runtime,
            RuntimePolicySessionSocket socket,
            GameSessionContext context,
            GameCharacter character,
            uint playerObjectId,
            DamageAuthorityPreparation preparation,
            string rosterSpawnId,
            MonsterRuntimeSnapshot source)
        {
            Registry = registry;
            Runtime = runtime;
            Socket = socket;
            Context = context;
            Character = character;
            PlayerObjectId = playerObjectId;
            Preparation = preparation;
            RosterSpawnId = rosterSpawnId;
            Source = source;
        }

        public GameSessionRegistry Registry { get; }
        public WorldInstanceRuntime Runtime { get; }
        public MapInstance Map => Runtime.Map;
        public RuntimePolicySessionSocket Socket { get; }
        public GameSessionContext Context { get; set; }
        public GameCharacter Character { get; }
        public uint PlayerObjectId { get; }
        public PlayerOwnershipFence Ownership => Context.Ownership;
        public DamageAuthorityPreparation Preparation { get; }
        public string RosterSpawnId { get; }
        public MonsterRuntimeSnapshot Source { get; private set; }

        public MedusaEncounterCharacterMechanicsSnapshot Mechanics() =>
            (Map.TryGetMedusaOwnershipSnapshot(out var ownership)
                ? ownership.Mechanics.Characters.Single(character =>
                    character.CharacterId == Character.Id)
                : throw new InvalidOperationException(
                    "The production fixture owner disappeared."));

        public MedusaEncounterMechanicsSnapshot MechanicsSnapshot() =>
            (Map.TryGetMedusaOwnershipSnapshot(out var ownership)
                ? ownership.Mechanics
                : throw new InvalidOperationException(
                    "The production fixture owner disappeared."));

        public MonsterRuntimeUpdate CreateAttack(
            ulong attackEventId,
            MonsterRuntimeSnapshot? source = null,
            PlayerOwnershipFence? ownership = null,
            WorldInstanceId? worldInstanceId = null,
            long? worldRevision = null,
            uint? targetObjectId = null,
            long? targetLifeRevision = null,
            long? worldMembershipEpoch = null) =>
            new(
                MonsterRuntimeUpdateKind.Attacked,
                source ?? Source,
                TargetCharacterId: Character.Id,
                TargetX: Character.PositionX,
                TargetZ: Character.PositionZ,
                TargetObjectId: targetObjectId ?? Context.ObjectId,
                TargetLifeRevision: targetLifeRevision ??
                    Registry.GetPlayerLifeRevision(Socket.Session),
                TargetOwnership: ownership ?? Context.Ownership,
                TargetWorldInstanceId: worldInstanceId ??
                    Context.WorldInstanceId,
                TargetWorldRevision: worldRevision ??
                    Context.WorldRevision,
                TargetWorldMembershipEpoch: worldMembershipEpoch ??
                    Context.WorldMembershipEpoch,
                AttackEventId: attackEventId);

        public async Task<MonsterPlayerAttackObservation> AttackAsync(
            MonsterRuntimeUpdate attack)
        {
            int beforeHealth;
            long beforeVitalsRevision;
            lock (Character.VitalsSync)
            {
                beforeHealth = Character.CurrentHp;
                beforeVitalsRevision = Character.VitalsRevision;
            }

            await Registry.ProcessMonsterAttackForSessionAsync(
                Socket.Session,
                attack,
                CancellationToken.None);

            int afterHealth;
            long afterVitalsRevision;
            lock (Character.VitalsSync)
            {
                afterHealth = Character.CurrentHp;
                afterVitalsRevision = Character.VitalsRevision;
            }

            return new(
                beforeHealth,
                afterHealth,
                beforeVitalsRevision,
                afterVitalsRevision,
                Registry.GetPlayerLifeRevision(Socket.Session),
                MechanicsSnapshot());
        }

        public CombatResolution Resolve(ulong attackEventId) =>
            Resolve(Source, RosterSpawnId, attackEventId);

        public CombatResolution Resolve(
            MonsterRuntimeSnapshot source,
            string rosterSpawnId,
            ulong attackEventId)
        {
            var spawn = Preparation.Inputs.RunSpawns.Single(value =>
                value.RosterSpawnId == rosterSpawnId);
            var baseProfile = Registry.GameplayCatalogs
                .MonsterCombatProfiles.Resolve(source.Definition);
            var effective = MedusaIslandCombatOverride
                .ApplyMonsterAttackProfile(
                    Preparation.Inputs.Difficulty,
                    spawn.Role,
                    baseProfile);
            return MonsterIncomingCombatPolicy.ResolveAttack(
                effective,
                Character,
                default,
                attackEventId);
        }

        public async Task<MonsterRuntimeSnapshot> ActivateSourceAsync(
            string rosterSpawnId)
        {
            var initial = FindMonster(Map, rosterSpawnId);
            Check.True(
                Registry.TryCapturePlayerMonsterTarget(
                    Socket.Session,
                    mapId: 200,
                    initial.ObjectId,
                    out var target,
                    out var authority) &&
                Registry.TryCommitPlayerMonsterDamageGuarded(
                    Socket.Session,
                    mapId: 200,
                    target.ObjectId,
                    target.RuntimeInstanceId,
                    Character.Id,
                    target.SpawnGeneration,
                    target.HealthRevision,
                    authority,
                    DateTimeOffset.UtcNow,
                    Resolution(
                        CombatDamageChannel.Physical,
                        damage: 1),
                    out var aggro) &&
                aggro.DamageResult is { Killed: false },
                $"{rosterSpawnId} acquires production aggro");

            var advanceAt = DateTimeOffset.UtcNow;
            for (var index = 1; index <= 160; index++)
            {
                _ = Runtime.Owner.Invoke(
                    map => map.AdvanceMonsters(
                        advanceAt.AddMilliseconds(index * 100),
                        session =>
                            Registry.GetPlayerLifeRevision(session)),
                    TimeSpan.FromSeconds(3));
                var current = RequiredMonster(Map, initial.ObjectId);
                if (current.CombatPhase ==
                    MonsterCombatPhase.Attacking)
                {
                    return await Task.FromResult(current);
                }
            }

            throw new InvalidOperationException(
                $"{rosterSpawnId} did not reach its attack phase.");
        }

        public async Task ReconnectAndReacquireAsync()
        {
            Registry.Remove(Socket.Session);
            await Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Source = RequiredMonster(Map, Source.ObjectId);
            Character.PositionX = Source.X;
            Character.PositionZ = Source.Z;
            Character.CheckpointOwnerId = Guid.NewGuid();
            Character.CheckpointOwnerGeneration = checked(
                Character.CheckpointOwnerGeneration + 1);
            var ownership = new PlayerOwnershipFence(
                Character.CheckpointOwnerId,
                Character.CheckpointOwnerGeneration);
            Check.True(
                Registry.TryBindAccountSessionOwnership(
                    Character.AccountId,
                    Socket.Session,
                    ownership),
                "reconnected Medusa target advances ownership");
            Registry.JoinWorldInstance(
                Socket.Session,
                Character.AccountId,
                Character,
                PlayerObjectId,
                Runtime.InstanceId,
                worldReady: true,
                joinedAt: DateTimeOffset.UtcNow);
            Context = Map.Snapshot().Single(value =>
                ReferenceEquals(value.Session, Socket.Session));
            Source = await ActivateSourceAsync(RosterSpawnId);
        }

        public Task RejoinSameAuthorityAsync()
        {
            Registry.JoinWorldInstance(
                Socket.Session,
                Character.AccountId,
                Character,
                PlayerObjectId,
                Runtime.InstanceId,
                worldReady: true,
                joinedAt: DateTimeOffset.UtcNow);
            Context = Map.Snapshot().Single(value =>
                ReferenceEquals(value.Session, Socket.Session));
            Source = RequiredMonster(Map, Source.ObjectId);
            return Task.CompletedTask;
        }

        public ulong FindEvent(
            ulong start,
            Func<CombatResolution, bool> predicate)
        {
            for (var eventId = start;
                 eventId < start + 100_000;
                 eventId++)
            {
                if (predicate(Resolve(eventId)) &&
                    AuthoredEffectProcApplies(eventId))
                {
                    return eventId;
                }
            }

            throw new InvalidOperationException(
                $"No deterministic {RosterSpawnId} attack event matched.");
        }

        public bool AuthoredEffectProcApplies(ulong eventId)
        {
            if (!MedusaIslandRosterPolicy.TryGetSpawn(
                    RosterSpawnId,
                    out var roster) ||
                roster.Skill is not { } skill ||
                !skill.RequiresDeterministicRatingProc)
            {
                return true;
            }

            var spawn = Preparation.Inputs.RunSpawns.Single(value =>
                value.RosterSpawnId == RosterSpawnId);
            var baseProfile = Registry.GameplayCatalogs
                .MonsterCombatProfiles.Resolve(Source.Definition);
            var effective = MedusaIslandCombatOverride
                .ApplyMonsterAttackProfile(
                    Preparation.Inputs.Difficulty,
                    spawn.Role,
                    baseProfile);
            var targetStats = Character.CalculatedStats ??
                CharacterStats.FromCharacter(Character);
            return HostileStatusProcPolicy.Evaluate(
                    new(
                        effective.Level,
                        Character.Level,
                        effective.Hit,
                        targetStats.Dodge,
                        skill.NativeStatusOddsRating,
                        targetStats.StatusResistance),
                    eventId,
                    targetOrder: 0)
                .Applied;
        }

        public void SetHealth(int health)
        {
            lock (Character.VitalsSync)
            {
                Character.CurrentHp = Math.Clamp(
                    health,
                    1,
                    Character.MaxHp);
                Character.MarkVitalsChanged();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Socket.Session);
            await Registry.DisposeAsync();
            await Socket.DisposeAsync();
        }

        public static async Task<MonsterPlayerHitFixture> CreateAsync(
            string rosterSpawnId,
            params int[] additionalAdmittedCharacterIds)
        {
            return await CreateAsync(
                rosterSpawnId,
                PlayerRuntimeMode.Ecs,
                additionalAdmittedCharacterIds);
        }

        public static async Task<MonsterPlayerHitFixture> CreateAsync(
            string rosterSpawnId,
            PlayerRuntimeMode playerRuntimeMode,
            params int[] additionalAdmittedCharacterIds)
        {
            var registry = new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                MonsterRuntimeMode.Ecs,
                playerRuntimeMode,
                worldInstanceOptions: new WorldInstanceRuntimeOptions
                {
                    RealmId = RealmId.Tempest.Value,
                    MaximumRuntimes = 2,
                    MaximumPlayerAssignments = 5,
                    MaximumRetiredInstanceIds = 8,
                    DefaultOpenWorldPlayerCapacity = 5,
                    MailboxCapacity = 16,
                    OwnerInvocationTimeoutMilliseconds = 100,
                    ShutdownDrainTimeoutMilliseconds = 2_000,
                    MaximumFanoutConcurrency = 1
                },
                itemContent: TestItemContent.Content);
            var preparation = new DamageAuthorityPreparation(
                additionalAdmittedCharacterIds);
            var created = await registry.CreatePreparedLocalWorldInstanceAsync(
                RealmId.Tempest,
                new WorldMapId(200),
                InstanceKind.Dungeon,
                playerCapacity: 5,
                preparation,
                CancellationToken.None);
            var instanceId = created.InstanceId ??
                throw new InvalidOperationException(
                    "Production Medusa hit fixture was not created.");
            var runtime = RequiredRegistryRuntime(registry, instanceId);
            var initial = FindMonster(runtime.Map, rosterSpawnId);
            var socket = await RuntimePolicySessionSocket.CreateAsync();
            var character = CreateRegistryDamageCharacter(101, mapId: 200);
            character.MaxHp = 10_000_000;
            character.CurrentHp = character.MaxHp;
            character.PositionX = initial.X;
            character.PositionZ = initial.Z;
            character.CheckpointOwnerId = Guid.NewGuid();
            character.CheckpointOwnerGeneration = 1;
            var ownership = new PlayerOwnershipFence(
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration);
            var playerObjectId = WorldObjectIds.ForPlayer(character.Id);

            registry.ReplaceAccountSession(
                character.AccountId,
                socket.Session);
            Check.True(
                registry.TryBindAccountSessionOwnership(
                    character.AccountId,
                    socket.Session,
                    ownership),
                $"{rosterSpawnId} production fixture binds ownership");
            registry.JoinWorldInstance(
                socket.Session,
                character.AccountId,
                character,
                playerObjectId,
                instanceId,
                worldReady: true,
                joinedAt: DateTimeOffset.UtcNow);
            var context = runtime.Map.Snapshot().Single(value =>
                ReferenceEquals(value.Session, socket.Session));

            Check.True(
                registry.TryCapturePlayerMonsterTarget(
                    socket.Session,
                    mapId: 200,
                    initial.ObjectId,
                    out var target,
                    out var authority),
                $"{rosterSpawnId} production fixture captures source");
            Check.True(
                registry.TryCommitPlayerMonsterDamageGuarded(
                    socket.Session,
                    mapId: 200,
                    target.ObjectId,
                    target.RuntimeInstanceId,
                    character.Id,
                    target.SpawnGeneration,
                    target.HealthRevision,
                    authority,
                    DateTimeOffset.UtcNow,
                    Resolution(CombatDamageChannel.Physical, damage: 1),
                    out var aggro) &&
                aggro.DamageResult is { Killed: false },
                $"{rosterSpawnId} production fixture acquires aggro");

            await registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            var source = RequiredMonster(runtime.Map, initial.ObjectId);
            Check.True(
                source.CombatPhase == MonsterCombatPhase.Attacking,
                $"{rosterSpawnId} production fixture reaches attacking phase");
            return new(
                registry,
                runtime,
                socket,
                context,
                character,
                playerObjectId,
                preparation,
                rosterSpawnId,
                source);
        }
    }

    private readonly record struct MonsterPlayerAttackObservation(
        int BeforeHealth,
        int AfterHealth,
        long BeforeVitalsRevision,
        long AfterVitalsRevision,
        long LifeRevision,
        MedusaEncounterMechanicsSnapshot Mechanics);
}
