using Godswar.Server.Ecs;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Npcs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerNpcEcsHydrationChecks
{
    private const uint PlayerObjectId = 0x6401;

    public static Task RunAsync()
    {
        CheckPlayerProtocolSnapshotParity();
        CheckAuthoritativeNpcProtocolSnapshotParity();
        CheckHydrationBoundaryValidation();
        return Task.CompletedTask;
    }

    private static void CheckPlayerProtocolSnapshotParity()
    {
        var character = CreateCharacter();
        var effects = new[]
        {
            new ClientStatusEffect(1390, 743),
            new ClientStatusEffect(200, 599)
        };
        var aggregate = new ClientStatusAggregate(
            Hit: 12,
            CriticalAppend: 7,
            ExperienceBonus: 0.25f,
            MovementSpeedMultiplier: 1.6f,
            IsRiding: true);
        var status = new PlayerStatusSnapshot(
            effects,
            aggregate,
            "player-ecs-parity");

        var expectedSpawn = PacketBuilder.PlayerWorldSpawn(
            character,
            PlayerObjectId,
            effects);
        var expectedStatus = PacketBuilder.PlayerStatusEffects(
            character,
            PlayerObjectId,
            effects,
            aggregate);
        var expectedGameData = PacketBuilder.PlayerStatusUpdate(
            character,
            PlayerObjectId,
            aggregate.MovementSpeedMultiplier);
        var expectedAppearance = PacketBuilder.EquipmentVisualRefresh(
            character,
            PlayerObjectId);
        var expectedInspect = PacketBuilder.PlayerInspectEquipment(
            character,
            PlayerObjectId);

        var world = new EcsWorld();
        var entity = GameCharacterEcsHydrator.Hydrate(
            world,
            character,
            PlayerObjectId,
            worldRevision: 42,
            status);

        Check.Equal(1, world.EntityCount, "player ECS entity count");
        Check.Equal(
            11,
            world.RegisteredComponentCount,
            "player ECS component pool count");
        Check.Equal(
            entity,
            world.Query<PlayerIdentityComponent, PlayerTransformComponent>()
                .Single(),
            "player ECS typed query");

        // Hydration is a copy boundary. Mutating the mutable persistence/session
        // source and the caller-owned status array must not alter the ECS view.
        character.Name = "ChangedAfterHydration";
        character.PositionX = -999f;
        character.Equipment = string.Empty;
        character.CalculatedStats = new CharacterStats
        {
            PhysicalAttack = -1
        };
        effects[0] = new ClientStatusEffect(9999, 1);

        var snapshot = PlayerEcsSnapshotAdapter.Capture(world, entity);
        var protocolCharacter =
            PlayerEcsSnapshotAdapter.ToProtocolCharacter(snapshot);

        Check.Equal(
            42L,
            snapshot.Identity.WorldRevision,
            "player world revision survives hydration");
        Check.Equal(
            "player-ecs-parity",
            snapshot.StatusFingerprint,
            "player status fingerprint survives hydration");
        Check.Equal(
            1390u,
            snapshot.StatusEffects[0].StatusId,
            "player status list is copied at hydration");
        Check.True(
            expectedSpawn.SequenceEqual(
                PacketBuilder.PlayerWorldSpawn(
                    protocolCharacter,
                    snapshot.Identity.ObjectId,
                    snapshot.StatusEffects)),
            "player world-spawn bytes survive ECS snapshot adaptation");
        Check.True(
            expectedStatus.SequenceEqual(
                PacketBuilder.PlayerStatusEffects(
                    protocolCharacter,
                    snapshot.Identity.ObjectId,
                    snapshot.StatusEffects,
                    snapshot.StatusAggregate)),
            "player status-effect bytes survive ECS snapshot adaptation");
        Check.True(
            expectedGameData.SequenceEqual(
                PacketBuilder.PlayerStatusUpdate(
                    protocolCharacter,
                    snapshot.Identity.ObjectId,
                    snapshot.StatusAggregate.MovementSpeedMultiplier)),
            "player game-data bytes survive ECS snapshot adaptation");
        Check.True(
            expectedAppearance.SequenceEqual(
                PacketBuilder.EquipmentVisualRefresh(
                    protocolCharacter,
                    snapshot.Identity.ObjectId)),
            "player appearance bytes survive ECS snapshot adaptation");
        Check.True(
            expectedInspect.SequenceEqual(
                PacketBuilder.PlayerInspectEquipment(
                    protocolCharacter,
                    snapshot.Identity.ObjectId)),
            "player inspection bytes survive ECS snapshot adaptation");
        Check.Equal(
            string.Empty,
            protocolCharacter.KitBag,
            "database-owned kit bag is excluded from the ECS protocol projection");
    }

    private static void CheckAuthoritativeNpcProtocolSnapshotParity()
    {
        var gearMentor = NpcSpawnDefinitionFactory.Create(
                mapId: 0,
                capturedSpawns: [],
                capturedAppearanceFallbacks: [],
                referenceDefinitions: [])
            .Single(static definition =>
                definition.NpcKey == "Sparta_070");
        var detail10077 = new byte[] { 8, 0, 0x5D, 0x27, 1, 2, 3, 4 };
        var detail10080 = new byte[] { 8, 0, 0x60, 0x27, 5, 6, 7, 8 };
        var definition = gearMentor with
        {
            Detail10077 = detail10077,
            Detail10080 = detail10080
        };
        var expectedPacket = PacketBuilder.NpcSpawns([definition]);

        var world = new EcsWorld();
        var entity = NpcSpawnDefinitionEcsHydrator.Hydrate(
            world,
            definition);

        Check.Equal(1, world.EntityCount, "NPC ECS entity count");
        Check.Equal(
            5,
            world.RegisteredComponentCount,
            "NPC ECS component pool count");
        Check.Equal(
            entity,
            world.Query<NpcIdentityComponent, NpcTransformComponent>()
                .Single(),
            "NPC ECS typed query");

        detail10077[4] = byte.MaxValue;
        detail10080[4] = byte.MaxValue;

        var snapshot = NpcEcsSnapshotAdapter.Capture(world, entity);
        var projected = NpcEcsSnapshotAdapter.ToSpawnDefinition(snapshot);

        Check.Equal(5067u, projected.ObjectId, "Sparta Gear Mentor object ID");
        Check.Equal(
            5067u,
            projected.InteractionId,
            "Sparta Gear Mentor interaction ID");
        Check.Equal(142f, projected.X, "Sparta Gear Mentor authoritative X");
        Check.Equal(-165f, projected.Z, "Sparta Gear Mentor authoritative Z");
        Check.Equal(1.7f, projected.Facing, "Sparta Gear Mentor facing");
        Check.Equal(
            (byte)1,
            projected.Detail10077[4],
            "NPC detail 10077 is copied at hydration");
        Check.Equal(
            (byte)5,
            projected.Detail10080[4],
            "NPC detail 10080 is copied at hydration");
        Check.True(
            expectedPacket.SequenceEqual(PacketBuilder.NpcSpawns([projected])),
            "NPC spawn and dialog bytes survive ECS snapshot adaptation");
    }

    private static void CheckHydrationBoundaryValidation()
    {
        var invalidPlayer = CreateCharacter();
        invalidPlayer.PositionX = float.NaN;
        var neutralStatus = new PlayerStatusSnapshot(
            [],
            ClientStatusAggregate.Empty,
            "neutral");
        var playerWorld = new EcsWorld();

        Check.Throws<ArgumentException>(
            () => GameCharacterEcsHydrator.Hydrate(
                playerWorld,
                invalidPlayer,
                PlayerObjectId,
                worldRevision: 0,
                neutralStatus),
            "player hydration rejects non-finite coordinates");
        Check.Equal(
            0,
            playerWorld.EntityCount,
            "invalid player hydration leaves no partial entity");

        var invalidNpc = new NpcSpawnDefinition(
            MapId: 0,
            SceneKey: "Sparta",
            NpcKey: "Sparta_Invalid",
            TemplateKey: "Sparta_Invalid_Male1",
            ObjectId: 0,
            X: 1f,
            Z: 2f,
            InteractionId: 1,
            AppearanceType: NpcSpawnDefinitionFactory.DefaultAppearanceType,
            Facing: 1f,
            Detail10077: [],
            Detail10080: []);
        var npcWorld = new EcsWorld();

        Check.Throws<ArgumentException>(
            () => NpcSpawnDefinitionEcsHydrator.Hydrate(
                npcWorld,
                invalidNpc),
            "NPC hydration rejects a zero object ID");
        Check.Equal(
            0,
            npcWorld.EntityCount,
            "invalid NPC hydration leaves no partial entity");
    }

    private static GameCharacter CreateCharacter()
    {
        var equipment = Enumerable.Repeat("[]", 21).ToArray();
        equipment[0] = "[2443,24,90,60,250,,10,12,1,1,0]";
        equipment[3] = "[2261,13,103,133,33,40,10,12,1,1,0]";
        equipment[10] = "[1834,24,90,250,60,230,10,12,1,1,0]";
        equipment[15] = "[14504,374,414,,,,7,8,1,1,0]";
        equipment[20] = "[16184,,,,,,1,1,1,1,0]";

        return new GameCharacter
        {
            Id = 731,
            AccountId = 17,
            Name = "EcsProtocolHero",
            CreatedUtc = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc),
            Gender = 2,
            Camp = GameDefaults.SpartaCamp,
            Profession = 3,
            Hair = 7,
            Face = 4,
            Faith = 2,
            CurrentMap = 2,
            PositionX = 321.125f,
            PositionZ = -654.75f,
            Level = 89,
            Experience = 123_987,
            TalentPoints = 456,
            TalentExperience = 67,
            HolySuitPoints = 23,
            Silver = 10_000_000,
            Gold = 10,
            CurrentHp = 123_456,
            CurrentMp = 23_456,
            MaxHp = 234_567,
            MaxMp = 34_567,
            VitalsRevision = 19,
            WeaponRank = 10,
            WeaponAuraEffect = 6,
            ArmorRank = 14,
            ArmorAuraEffect = 8,
            Equipment = string.Join('#', equipment) + '#',
            KitBag = "[4000,,,,,,99,1,1,1,0]#",
            ZodiacType = 5,
            ZodiacLuckyStatus = 17,
            ZodiacLuckyExpiresAt =
                new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero),
            ZodiacLevel = 7,
            ZodiacEnergy = 1234,
            ZodiacEnergyRemainderX100 = 56,
            ZodiacOnlineDay = new DateOnly(2026, 7, 23),
            ZodiacOnlineDurationTicksToday = TimeSpan.FromHours(2).Ticks,
            ZodiacLastOnlineAt =
                new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero),
            ZodiacLastCompensationDay = new DateOnly(2026, 7, 22),
            ZodiacAccumulatedExperienceX100 = 789,
            ZodiacAccumulatedTalentExperienceX100 = 321,
            CalculatedStats = new CharacterStats
            {
                CharacterId = 731,
                AccountId = 17,
                Name = "EcsProtocolHero",
                Level = 89,
                MaxHp = 234_567,
                MaxMp = 34_567,
                CurrentHp = 123_456,
                CurrentMp = 23_456,
                PhysicalAttack = 91_001,
                PhysicalDefense = 82_002,
                MagicAttack = 73_003,
                MagicDefense = 64_004,
                Hit = 55_005,
                Dodge = 46_006,
                Critical = 37_007,
                CriticalResistance = 28_008,
                DamageAbsorb = 19_009,
                PhysicalDamageBonus = 1_234,
                MagicDamageBonus = 2_345,
                CureBonus = 4_567,
                BeCureBonus = 3_456,
                HpRecovery = 111,
                MpRecovery = 222,
                IgnorePhysicalDefense = 333,
                IgnoreMagicDefense = 444,
                PhysicalAppendDamage = 555,
                MagicAppendDamage = 666,
                CriticalDamagePercent = 777,
                CriticalDamageFlat = 888,
                WeaponScore = 999,
                WeaponRank = 10,
                WeaponAuraEffect = 6,
                ArmorScore = 1111,
                ArmorRank = 14,
                ArmorAuraEffect = 8,
                LearnedSkillCount = 12
            }
        };
    }
}
