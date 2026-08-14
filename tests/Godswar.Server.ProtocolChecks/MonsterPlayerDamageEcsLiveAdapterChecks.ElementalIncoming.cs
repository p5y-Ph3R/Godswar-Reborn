using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task CheckIncomingElementalMonsterParityAsync()
    {
        var legacyGaia = await ObserveGaiaIncomingAsync(
            PlayerRuntimeMode.Legacy,
            9_120);
        var ecsGaia = await ObserveGaiaIncomingAsync(
            PlayerRuntimeMode.Ecs,
            9_121);
        Check.True(
            legacyGaia == ecsGaia,
            "legacy and ECS Gaia mitigation/reflection are identical");

        var legacyPoseidon = await ObservePoseidonIncomingAsync(
            PlayerRuntimeMode.Legacy,
            9_122);
        var ecsPoseidon = await ObservePoseidonIncomingAsync(
            PlayerRuntimeMode.Ecs,
            9_123);
        Check.True(
            legacyPoseidon == ecsPoseidon,
            "legacy and ECS Poseidon guard/recovery are identical");

        var legacyAeolus = await ObserveAeolusIncomingAsync(
            PlayerRuntimeMode.Legacy,
            9_124);
        var ecsAeolus = await ObserveAeolusIncomingAsync(
            PlayerRuntimeMode.Ecs,
            9_125);
        Check.True(
            legacyAeolus == ecsAeolus,
            "legacy and ECS Aeolus sixth-hit evade are identical");

        var legacyApollo = await ObserveApolloIncomingAsync(
            PlayerRuntimeMode.Legacy,
            9_126);
        var ecsApollo = await ObserveApolloIncomingAsync(
            PlayerRuntimeMode.Ecs,
            9_127);
        Check.True(
            legacyApollo == ecsApollo,
            "legacy and ECS Apollo lethal protection are identical");
    }

    private static async Task<GaiaIncomingObservation>
        ObserveGaiaIncomingAsync(
            PlayerRuntimeMode mode,
            uint monsterObjectId)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateElementalIncomingRegistry(mode);
        var character = CreateElementalIncomingCharacter(
            monsterObjectId,
            ElementKind.Earth,
            pieces: 10,
            currentHealth: 10_000,
            maximumHealth: 10_000,
            currentMana: 0,
            maximumMana: 1_000);
        var playerObjectId = await JoinElementalIncomingFixtureAsync(
            registry,
            socket.Session,
            character,
            monsterObjectId,
            activeAt);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monster),
            $"{mode} Gaia monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monster.Definition);
        var eventId = FindMonsterHitEventIds(
            profile,
            character,
            count: 1)[0];
        var baseResolution = MonsterIncomingCombatPolicy.ResolveAttack(
            profile,
            character,
            default,
            eventId);
        var combatEvent = IncomingElementalEvent(
            eventId,
            monsterObjectId,
            character,
            activeAt);
        var expectedState = new ElementalResonanceState(character.Id);
        var expectedAdjustment = ElementalResonanceExecutionPolicy
            .AdjustIncomingDirectDamage(
                combatEvent,
                character.ElementalEquipment,
                expectedState,
                baseResolution.Damage,
                character.CurrentHp,
                character.MaxHp,
                character.MaxMp);
        var expectedReflection = ElementalResonanceExecutionPolicy
            .PlanCommittedReflection(
                combatEvent with { Committed = true },
                character.ElementalEquipment,
                expectedState,
                expectedAdjustment.AdjustedDamage,
                monster.MaximumHealth);
        var update = IncomingMonsterUpdate(
            monster,
            character,
            playerObjectId,
            registry.GetPlayerLifeRevision(socket.Session),
            eventId);
        var beforeHealth = character.CurrentHp;
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            update,
            CancellationToken.None);

        var impact = await socket.ReadPacketAsync(24);
        var primary = await socket.ReadPacketAsync(30);
        var reflection = await socket.ReadPacketAsync(30);
        var reported = BinaryPrimitives.ReadUInt32LittleEndian(
            primary.AsSpan(24, 4));
        var reflected = BinaryPrimitives.ReadUInt32LittleEndian(
            reflection.AsSpan(24, 4));
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                impact.AsSpan(2, 2)) == 10046 &&
            reported == expectedAdjustment.AdjustedDamage &&
            beforeHealth - character.CurrentHp ==
                expectedAdjustment.AdjustedDamage,
            $"{mode} Gaia mitigation changes authoritative and wire damage");
        Check.True(
            expectedReflection is { } planned &&
            reflected == planned.Damage &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                reflection.AsSpan(4, 4)) == 0x1448u &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                reflection.AsSpan(20, 4)) == monsterObjectId,
            $"{mode} Gaia reflection is terminal player-to-monster damage");
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var afterReflection) &&
            monster.CurrentHealth - afterReflection.CurrentHealth == reflected,
            $"{mode} Gaia reflection mutates the monster by applied damage");

        var playerAfter = character.CurrentHp;
        var monsterAfter = afterReflection.CurrentHealth;
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            update,
            CancellationToken.None);
        Check.True(
            character.CurrentHp == playerAfter &&
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var afterReplay) &&
            afterReplay.CurrentHealth == monsterAfter &&
            socket.Available == 0,
            $"{mode} replay cannot repeat direct damage or reflection");
        registry.Remove(socket.Session);
        return new(
            checked((uint)(beforeHealth - character.CurrentHp)),
            reflected,
            reported,
            primary[29]);
    }

    private static async Task<ApolloIncomingObservation>
        ObserveApolloIncomingAsync(
            PlayerRuntimeMode mode,
            uint monsterObjectId)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateElementalIncomingRegistry(mode);
        var character = CreateElementalIncomingCharacter(
            monsterObjectId,
            ElementKind.Light,
            pieces: 10,
            currentHealth: 1_000,
            maximumHealth: 1_000,
            currentMana: 100,
            maximumMana: 100);
        var playerObjectId = await JoinElementalIncomingFixtureAsync(
            registry,
            socket.Session,
            character,
            monsterObjectId,
            activeAt);
        var fence = new ElementalCombatSessionFence(
            character.Id,
            character.CurrentMap,
            new(
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration));
        Check.True(
            registry.TryProcessElementalRecoveryPulse(
                socket.Session,
                fence,
                AuthoredElementalCombatV1.RecoveryEvent(
                    character.Id,
                    character.CurrentMap,
                    acceptedRecoveryRevision: 1,
                    activeAt),
                character.ElementalEquipment,
                requestedHealth: 1_000,
                requestedMana: 0,
                currentHealth: character.MaxHp,
                currentMana: character.CurrentMp,
                maximumHealth: character.MaxHp,
                maximumMana: character.MaxMp,
                out var barrier) &&
            barrier.BarrierTotal > 0,
            $"{mode} Apollo fixture creates an authoritative barrier");
        lock (character.VitalsSync)
        {
            character.CurrentHp = 100;
            character.MarkVitalsChanged();
        }

        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                monsterObjectId,
                out var monster),
            $"{mode} Apollo monster is queryable");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(monster.Definition);
        var eventId = FindMonsterHitEventIds(
            profile,
            character,
            count: 1)[0];
        var baseResolution = MonsterIncomingCombatPolicy.ResolveAttack(
            profile,
            character,
            default,
            eventId);
        Check.True(
            baseResolution.Damage >= character.CurrentHp,
            $"{mode} Apollo fixture starts with lethal resolved damage");
        await registry.ProcessMonsterAttackForSessionAsync(
            socket.Session,
            IncomingMonsterUpdate(
                monster,
                character,
                playerObjectId,
                registry.GetPlayerLifeRevision(socket.Session),
                eventId),
            CancellationToken.None);
        await socket.ReadPacketAsync(24);
        var damage = await socket.ReadPacketAsync(30);
        var reported = BinaryPrimitives.ReadUInt32LittleEndian(
            damage.AsSpan(24, 4));
        Check.True(
            character.CurrentHp == 1 &&
            reported == 99 &&
            registry.GetPlayerLifeRevision(socket.Session) == 0 &&
            socket.Available == 0,
            $"{mode} Apollo consumes barrier and prevents death at one HP");
        registry.Remove(socket.Session);
        return new(character.CurrentHp, reported, damage[29]);
    }

    private static GameSessionRegistry CreateElementalIncomingRegistry(
        PlayerRuntimeMode mode) =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            mode,
            gameplayCatalogs: CreateMonsterCombatCatalog(
                MonsterAttackDamageKind.Physical));

    private static GameCharacter CreateElementalIncomingCharacter(
        uint monsterObjectId,
        ElementKind element,
        int pieces,
        int currentHealth,
        int maximumHealth,
        int currentMana,
        int maximumMana)
    {
        var character = CreateCharacter();
        character.Id = checked(10_000 + (int)monsterObjectId);
        character.AccountId = checked(2_000 + (int)monsterObjectId);
        character.Name = $"Incoming{element}{monsterObjectId}";
        character.CurrentHp = currentHealth;
        character.MaxHp = maximumHealth;
        character.CurrentMp = currentMana;
        character.MaxMp = maximumMana;
        character.CheckpointOwnerId = Guid.NewGuid();
        character.CheckpointOwnerGeneration = 1;
        SetIncomingElementalProfile(
            character,
            CreateIncomingElementalProfile(element, pieces));
        return character;
    }

    private static async Task<uint> JoinElementalIncomingFixtureAsync(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint monsterObjectId,
        DateTimeOffset activeAt)
    {
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        var playerObjectId = WorldObjectIds.ForPlayer(character.Id);
        registry.ReplaceAccountSession(character.AccountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                session,
                new(
                    character.CheckpointOwnerId,
                    character.CheckpointOwnerGeneration)),
            "incoming elemental fixture binds player ownership");
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            playerObjectId,
            joinedAt: activeAt);
        await using var visibility =
            await registry.BeginMonsterVisibilityTransitionAsync(
                session,
                character.CurrentMap,
                character.PositionX,
                character.PositionZ,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "Incoming elemental visibility transition was unavailable.");
        visibility.Commit();
        return playerObjectId;
    }

    private static MonsterRuntimeUpdate IncomingMonsterUpdate(
        MonsterRuntimeSnapshot monster,
        GameCharacter character,
        uint playerObjectId,
        long lifeRevision,
        ulong eventId) =>
        new(
            MonsterRuntimeUpdateKind.Attacked,
            monster,
            TargetCharacterId: character.Id,
            TargetX: character.PositionX,
            TargetZ: character.PositionZ,
            TargetObjectId: playerObjectId,
            TargetLifeRevision: lifeRevision,
            TargetVitalsRevision: character.VitalsRevision,
            AttackEventId: eventId);

    private static DeterministicCombatEventContext IncomingElementalEvent(
        ulong eventId,
        uint monsterObjectId,
        GameCharacter character,
        DateTimeOffset authoritativeAt) =>
        new(
            eventId,
            character.CurrentMap,
            monsterObjectId,
            character.Id,
            authoritativeAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectBasicAttack,
            Committed: false,
            IsPvp: false,
            default);

    private static IReadOnlyList<ulong> FindMonsterHitEventIds(
        in MonsterCombatProfile profile,
        GameCharacter character,
        int count)
    {
        var resolved = new List<ulong>(count);
        for (ulong eventId = 1;
             eventId <= 100_000 && resolved.Count < count;
             eventId++)
        {
            if (MonsterIncomingCombatPolicy.ResolveAttack(
                    profile,
                    character,
                    default,
                    eventId).Hit)
            {
                resolved.Add(eventId);
            }
        }

        return resolved.Count == count
            ? resolved.AsReadOnly()
            : throw new InvalidOperationException(
                "Insufficient deterministic monster hit events.");
    }

    private static ElementalEquipmentProfile CreateIncomingElementalProfile(
        ElementKind element,
        int pieces)
    {
        var totals = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => 0);
        counts[element] = pieces;
        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(
                value,
                counts[value]));
        return new(totals, counts, active);
    }

    private static void SetIncomingElementalProfile(
        GameCharacter character,
        ElementalEquipmentProfile profile)
    {
        var property = typeof(GameCharacter).GetProperty(
            nameof(GameCharacter.ElementalEquipment),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "GameCharacter.ElementalEquipment was not found.");
        property.SetValue(character, profile);
    }

}
