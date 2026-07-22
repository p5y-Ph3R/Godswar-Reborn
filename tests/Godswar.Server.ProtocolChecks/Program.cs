using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class Program
{
    private const int ConcurrentPacketCount = 512;
    private const int ConcurrentPacketLength = 37;
    private const ushort ConcurrentPacketOpcode = 0x6F6F;

    public static async Task<int> Main()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("Character camp starting location", CheckCharacterCampStartingLocationAsync),
            ("Saved character location persistence", CheckSavedCharacterLocationPersistenceAsync),
            ("Persistent monster-kill progression", CheckMonsterKillProgressionAsync),
            ("Additive fighter EXP boost stacking", CheckExperienceBoostStackingAsync),
            ("Online-only EXP and Talent boost duration", CheckOnlineProgressionBoostDurationAsync),
            ("World-session owned boost clock", CheckWorldSessionOwnedBoostClockAsync),
            ("Working-original login bootstrap manifest", CheckAfterLoginManifestAsync),
            ("Working-original character preview layout", CheckCharacterPreviewAsync),
            ("EnterMain character identity and saved location", CheckEnterMainCharacterIdentityAsync),
            ("Warrior talent ID-zero upgrade protocol", CheckWarriorTalentIdZeroUpgradeAsync),
            ("JSON warrior talent persistence", CheckJsonWarriorTalentPersistenceAsync),
            ("Warrior starter skill packets", CheckWarriorStarterSkillPacketsAsync),
            ("JSON provider starter skill", CheckJsonProviderStarterSkillAsync),
            ("Skill combat catalog", CheckSkillCombatCatalogAsync),
            ("Sacred Zeal runtime-status composition", CheckSacredZealStatusCompositionAsync),
            ("Holy Ward runtime-status mitigation", CheckHolyWardStatusCompositionAsync),
            ("Skill cast target and impact layout", CheckSkillCastTargetAndImpactAsync),
            ("Basic and monster attack packet layouts", CheckAttackPacketLayoutsAsync),
            ("Dynamic original-server time response", CheckServerTimePacketAsync),
            ("Zodiac full-sync and accumulation protocol", CheckZodiacProtocolAsync),
            ("Zodiac online-energy cadence and day policy", CheckZodiacOnlineEnergyPolicyAsync),
            ("JSON Zodiac creation persistence", CheckJsonZodiacPersistenceAsync),
            ("Player passive recovery protocol", CheckPlayerRecoveryProtocolAsync),
            ("PlayerWorldSpawn layout", CheckPlayerWorldSpawnAsync),
            ("PlayerWorldSpawn captured appearance", CheckPlayerWorldAppearanceAsync),
            ("PlayerWorldSpawn full quality/grade extension", CheckPlayerWorldExtendedAppearanceAsync),
            ("Player auxiliary appearance packets", CheckPlayerAuxiliaryAppearanceAsync),
            ("PlayerInspectEquipment packed slots and details", CheckPlayerInspectExtendedSlotsAsync),
            ("PlayerDetail vitals and wallet layout", CheckPlayerDetailAsync),
            ("PlayerStatusUpdate layout", CheckPlayerStatusUpdateAsync),
            ("Native status-effect sync layout", CheckPlayerStatusEffectsAsync),
            ("Post-enter UI-ready bootstrap gate", CheckPostEnterBootstrapGateAsync),
            ("Captured accepted-quest replay exclusion", CheckCapturedAcceptedQuestReplayExclusionAsync),
            ("Guarded bag-to-equipment persistence and snapshot", CheckGuardedEquipmentMoveAsync),
            ("Genuine equipment-kind persistence guard", EquipmentKindGuardChecks.RunAsync),
            ("Holy-stone targeted authoritative-item preservation", CheckHolyStoneAuthoritativePersistencePlanAsync),
            ("Occupied ghost-slot bag move parsing", CheckOccupiedGhostSlotBagMoveParsingAsync),
            ("Confirmed bag-item deletion protocol and persistence", CheckBagItemDeletionAsync),
            ("Developer material item command", CheckDeveloperForgingMaterialCommandAsync),
            ("PostgreSQL developer clear-bag scope and audit", PostgresKitBagClearIntegrationChecks.RunAsync),
            ("Equipment forging packet protocol", ForgeProtocolChecks.RunAsync),
            ("Equipment forging rule catalog and calculator", EquipmentForgeCatalogChecks.RunAsync),
            ("Atomic equipment-forge persistence", ForgeTransactionChecks.RunAsync),
            ("PostgreSQL equipment-forge race and preservation", PostgresForgeIntegrationChecks.RunAsync),
            ("Gear-enhancement material catalog and planner", GearEnhancementStateChecks.RunAsync),
            ("Gear Mentor material, planner, and protocol", GearMentorStateChecks.RunAsync),
            ("PostgreSQL Gear Mentor race and preservation", PostgresGearMentorIntegrationChecks.RunAsync),
            ("Atomic gear-enhancement persistence", GearEnhancementTransactionChecks.RunAsync),
            ("PostgreSQL gear-enhancement race and preservation", PostgresGearEnhancementIntegrationChecks.RunAsync),
            ("Gear-enhancer initial NPC protocol", CheckGearEnhancerInitialProtocolAsync),
            ("Holy-suit design original NPC protocol", CheckHolySuitDesignProtocolAsync),
            ("NPC definitions and spawn layout", CheckNpcDefinitionsAndSpawnLayoutAsync),
            ("NPC movement-cell visibility", CheckNpcMovementCellVisibilityAsync),
            ("Monster movement-cell visibility and spawn layout", CheckMonsterMovementCellVisibilityAsync),
            ("World boss outdoor-area catalog", WorldBossCatalogChecks.RunAsync),
            ("Persisted world-boss respawn across restart", CheckPersistedWorldBossRespawnAsync),
            ("Monster movement and lifecycle packet layouts", CheckMonsterMovementPacketLayoutsAsync),
            ("Monster runtime appearance patch", CheckMonsterRuntimeAppearancePatchAsync),
            ("Shared bounded monster runtime and lifecycle", CheckSharedBoundedMonsterRuntimeAsync),
            ("Warrior stun monster-control runtime", MonsterStunChecks.RunAsync),
            ("Passive monster retaliation state machine", CheckMonsterRetaliationRuntimeAsync),
            ("Monster smooth leash return and full-health replacement", CheckMonsterLeashReturnAsync),
            ("Monster return/replacement socket lifecycle", CheckMonsterReturnViewerPacketOrderAsync),
            ("Monster generation reconciliation across bootstrap", CheckMonsterGenerationReconciliationAsync),
            ("Monster old-generation event packet suppression", CheckMonsterOldGenerationEventSuppressionAsync),
            ("Monster same-generation activation refresh", CheckMonsterSameGenerationActivationRefreshAsync),
            ("Monster entering-viewer damage delivery lease", CheckMonsterEnteringViewerDamageLeaseAsync),
            ("Monster health-revision inverse and gap ordering", CheckMonsterHealthRevisionOrderingAsync),
            ("Monster self-viewer inverse damage ordering", CheckMonsterSelfViewerDamageOrderingAsync),
            ("Monster area-damage AOI revision delivery", CheckMonsterAreaDamageDeliveryAsync),
            ("Monster viewer registry AOI scoping", CheckMonsterViewerRegistryAsync),
            ("Map registry world-readiness gate", CheckMapRegistryWorldReadinessAsync),
            ("ClientSession concurrent send ordering", CheckConcurrentSendOrderingAsync)
        };

        var failures = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Protocol checks: {checks.Length - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static Task CheckCharacterCampStartingLocationAsync()
    {
        var sparta = new GameCharacter
        {
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = GameDefaults.AthensCapitalMap,
            PositionX = 10f,
            PositionZ = 20f
        };
        GameDefaults.InitializeStartingLocation(sparta);

        Check.Equal(GameDefaults.SpartaCamp, sparta.Camp, "Sparta camp is preserved");
        Check.Equal(GameDefaults.SpartaCapitalMap, sparta.CurrentMap, "Sparta starts on map 0");
        Check.Equal(GameDefaults.StartingPositionX, sparta.PositionX, "Sparta starting X");
        Check.Equal(GameDefaults.StartingPositionZ, sparta.PositionZ, "Sparta starting Z");

        var athens = new GameCharacter
        {
            Camp = GameDefaults.AthensCamp,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            PositionX = 10f,
            PositionZ = 20f
        };
        GameDefaults.InitializeStartingLocation(athens);

        Check.Equal(GameDefaults.AthensCamp, athens.Camp, "Athens camp is preserved");
        Check.Equal(GameDefaults.AthensCapitalMap, athens.CurrentMap, "Athens starts on map 1");
        Check.Equal(GameDefaults.StartingPositionX, athens.PositionX, "Athens starting X");
        Check.Equal(GameDefaults.StartingPositionZ, athens.PositionZ, "Athens starting Z");

        var invalid = new GameCharacter { Camp = byte.MaxValue };
        GameDefaults.InitializeStartingLocation(invalid);

        Check.Equal(GameDefaults.AthensCamp, invalid.Camp, "invalid camp uses the safe Athens default");
        Check.Equal(GameDefaults.AthensCapitalMap, invalid.CurrentMap, "invalid camp uses the Athens capital");
        return Task.CompletedTask;
    }

    private static async Task CheckSavedCharacterLocationPersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-location-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("location-check", "");
            var created = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "LocationHero",
                    Camp = GameDefaults.SpartaCamp
                });

            Check.Equal(GameDefaults.SpartaCapitalMap, created.CurrentMap, "camp selects the capital at creation");

            const byte travelledMap = 17;
            const float travelledX = -412.75f;
            const float travelledZ = 903.125f;
            await store.SaveCharacterPositionAsync(
                account.Id,
                created.Id,
                travelledMap,
                travelledX,
                travelledZ);
            await store.SaveCharacterVitalsAsync(
                account.Id,
                created.Id,
                currentHp: 777,
                currentMp: 123,
                vitalsRevision: 2);
            await store.SaveCharacterVitalsAsync(
                account.Id,
                created.Id,
                currentHp: 1,
                currentMp: 2,
                vitalsRevision: 1);

            var reloaded = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidOperationException("saved character was not reloaded");
            Check.Equal(GameDefaults.SpartaCamp, reloaded.Camp, "saved camp is retained after travel");
            Check.Equal(travelledMap, reloaded.CurrentMap, "login loads the saved non-capital map");
            Check.Equal(travelledX, reloaded.PositionX, "login loads saved X");
            Check.Equal(travelledZ, reloaded.PositionZ, "login loads saved Z");
            Check.Equal(777, reloaded.CurrentHp, "login loads saved current HP");
            Check.Equal(123, reloaded.CurrentMp, "login loads saved current MP");
            Check.Equal(2L, reloaded.VitalsRevision, "stale vitals snapshots cannot overwrite newer state");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckCharacterPreviewAsync()
    {
        var character = CreateCharacter();
        character.Profession = 0;
        character.Faith = 0;
        character.Equipment = GameDefaults.DefaultEquipment(character.Profession);

        var packet = PacketBuilder.CharacterPreview(character);
        Check.Equal(188, packet.Length, "character preview packet length");
        Check.Equal((ushort)188, ReadUInt16(packet, 0), "character preview declared length");
        Check.Equal((ushort)10002, ReadUInt16(packet, 2), "character preview opcode");
        Check.Equal((byte)1, packet[4], "character preview record count");
        Check.Equal((byte)1, packet[43], "character preview final metadata control byte");
        Check.Equal(2100u, ReadUInt32(packet, 56), "character preview armor slot");
        Check.Equal(2900u, ReadUInt32(packet, 68), "character preview shoes slot");
        Check.Equal(1000u, ReadUInt32(packet, 84), "character preview weapon slot");
        Check.Equal(2000u, ReadUInt32(packet, 88), "character preview shield slot");
        Check.True(
            packet.AsSpan(140, 48).IndexOfAnyExcept((byte)0) < 0,
            "character preview reserved tail remains zero");
        return Task.CompletedTask;
    }

    private static Task CheckAfterLoginManifestAsync()
    {
        int[] expectedIds =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
            10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
            20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
            40, 41, 42, 43, 44, 45, 46, 56, 57, 68,
            69, 200, 201, 202, 203, 204, 205, 206, 207, 208,
            209, 210, 210
        ];

        var packet = PacketBuilder.AfterLogin();
        Check.Equal(2772, packet.Length, "login bootstrap stream length");
        Check.Equal(
            "AD4125D3F759C969487EC5C89EA3AB2D41646D073AB4BBAA88D1B3082C95EB84",
            Convert.ToHexString(SHA256.HashData(packet)),
            "login bootstrap captured-byte hash");

        for (var recordIndex = 0; recordIndex < expectedIds.Length; recordIndex++)
        {
            var record = packet.AsSpan(recordIndex * 44, 44);
            Check.Equal((ushort)44, BinaryPrimitives.ReadUInt16LittleEndian(record), $"bootstrap record {recordIndex} length");
            Check.Equal((ushort)10358, BinaryPrimitives.ReadUInt16LittleEndian(record[2..]), $"bootstrap record {recordIndex} opcode");
            Check.Equal(expectedIds[recordIndex], BinaryPrimitives.ReadInt32LittleEndian(record[4..]), $"bootstrap record {recordIndex} id");
            Check.Equal((byte)0, record[40], $"bootstrap record {recordIndex} hash terminator");
            Check.Equal((byte)'8', record[41], $"bootstrap record {recordIndex} version digit one");
            Check.Equal((byte)'8', record[42], $"bootstrap record {recordIndex} version digit two");
            Check.Equal((byte)0, record[43], $"bootstrap record {recordIndex} version terminator");
        }

        return Task.CompletedTask;
    }

    private static Task CheckEnterMainCharacterIdentityAsync()
    {
        var character = CreateCharacter();
        var packet = PacketBuilder.EnterMain(character);

        Check.Equal((uint)character.Id, ReadUInt32(packet, 4), "EnterMain persistent character key");
        Check.Equal(0x00001448u, ReadUInt32(packet, 52), "EnterMain local world object ID");
        Check.Equal(character.CurrentMap, packet[46], "EnterMain saved map");
        Check.Equal(character.PositionX, ReadSingle(packet, 56), "EnterMain saved X");
        Check.Equal(character.PositionZ, ReadSingle(packet, 64), "EnterMain saved Z");
        Check.Equal(character.Experience, ReadInt32(packet, 84), "EnterMain saved fighter EXP");
        Check.Equal(
            PlayerExperienceCatalog.GetNextLevelExperience(character.Level),
            ReadInt32(packet, 88),
            "EnterMain next-level EXP threshold");
        Check.Equal(character.TalentPoints, ReadInt32(packet, 92), "EnterMain saved Talent Points");
        Check.Equal(character.TalentExperience, ReadInt32(packet, 96), "EnterMain saved Talent EXP");

        var secondCharacter = CreateCharacter();
        secondCharacter.Id = character.Id + 1;
        var secondPacket = PacketBuilder.EnterMain(secondCharacter);
        Check.Equal((uint)secondCharacter.Id, ReadUInt32(secondPacket, 4), "second character has an isolated UI key");
        Check.Equal(0x00001448u, ReadUInt32(secondPacket, 52), "local world object ID remains session-local");
        return Task.CompletedTask;
    }

    private static Task CheckWarriorTalentIdZeroUpgradeAsync()
    {
        var request = Convert.FromHexString(
            "1C004127481400000000000000000000000000000A00000000000000");
        Check.True(
            GameClientHandler.TryReadTalentUpgrade(
                request.AsSpan(4),
                out var talentId,
                out var clientRank,
                out var clientTalentPoints),
            "live warrior talent ID zero request parses");
        Check.Equal(0, talentId, "warrior talent ID zero is valid");
        Check.Equal(0, clientRank, "warrior talent request rank");
        Check.Equal(10, clientTalentPoints, "warrior talent request point echo");

        var character = CreateCharacter();
        character.TalentPoints = 9;
        var acknowledgement = PacketBuilder.TalentUpgradeAck(new TalentUpgradeResult
        {
            Character = character,
            TalentId = 0,
            NewRank = 1,
            Cost = 1,
            RemainingTalentPoints = 9,
            DisplayValue = 4
        });
        Check.True(
            acknowledgement.SequenceEqual(
                Convert.FromHexString("1C004127481400000000000001000000010000000900000004000000")),
            "live warrior talent ID zero acknowledgement");
        return Task.CompletedTask;
    }

    private static async Task CheckJsonWarriorTalentPersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-talent-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync("talent-check", "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "JsonTalentWarrior",
                        Profession = 0,
                        TalentPoints = 10
                    });
                accountId = account.Id;
                characterId = character.Id;
            }

            // Existing JSON saves predate characterTalents. Removing the new
            // collection verifies that those files still load as rank zero.
            var statePath = Path.Combine(dataPath, "state.json");
            var legacyState = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new InvalidOperationException("JSON talent test could not parse state.json");
            legacyState.Remove("characterTalents");
            await File.WriteAllTextAsync(statePath, legacyState.ToJsonString(JsonDefaults.Indented));

            await using (var store = new JsonGameStore(dataPath))
            {
                var legacyTalents = await store.GetTalentStatesAsync(accountId, characterId);
                var healthy = legacyTalents.Single(talent => talent.TalentId == 0);
                Check.Equal(0, healthy.Rank, "legacy JSON defaults warrior talent ID zero to rank zero");

                var wrongClass = await store.UpgradeTalentAsync(
                    accountId,
                    characterId,
                    talentId: 50,
                    clientRank: 0,
                    clientTalentPoints: 10);
                Check.True(wrongClass is null, "warrior cannot upgrade a Champion talent");

                var upgraded = await store.UpgradeTalentAsync(
                    accountId,
                    characterId,
                    talentId: 0,
                    clientRank: 99,
                    clientTalentPoints: int.MaxValue)
                    ?? throw new InvalidOperationException("JSON warrior talent ID zero was not upgraded");
                Check.Equal(1, upgraded.NewRank, "JSON upgrade derives rank from saved state");
                Check.Equal(1, upgraded.Cost, "JSON rank-one upgrade uses server-owned cost");
                Check.Equal(9, upgraded.RemainingTalentPoints, "JSON upgrade spends server-owned points");
            }

            await using (var reloadedStore = new JsonGameStore(dataPath))
            {
                var reloadedCharacter = await reloadedStore.GetFirstCharacterAsync(accountId)
                    ?? throw new InvalidOperationException("JSON talent character did not reload");
                Check.Equal(9, reloadedCharacter.TalentPoints, "talent points survive JSON store reload");

                var reloadedTalents = await reloadedStore.GetTalentStatesAsync(accountId, characterId);
                var healthy = reloadedTalents.Single(talent => talent.TalentId == 0);
                Check.Equal(1, healthy.Rank, "warrior talent ID zero rank survives JSON store reload");
                Check.Equal(4, healthy.DisplayValue, "reloaded warrior talent has its rank-one display value");
                Check.Equal(2, healthy.NextCost, "reloaded warrior talent has its server-owned next cost");
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckMonsterKillProgressionAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-progression-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("progression-check", "");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "ProgressionHero",
                    Camp = GameDefaults.SpartaCamp,
                    TalentPoints = 10,
                    TalentExperience = 0
                });

            var first = await store.ApplyMonsterKillRewardAsync(
                account.Id,
                character.Id,
                experience: 80,
                talentExperience: 2)
                ?? throw new InvalidOperationException("first progression update returned no character");
            Check.Equal(80, first.CurrentExperience, "first kill persists fighter EXP");
            Check.Equal(1, first.CurrentLevel, "first kill remains below the level-one threshold");
            Check.Equal(2, first.CurrentTalentExperience, "first kill persists Talent EXP");
            Check.Equal(0, first.TalentPointsGained, "first kill does not prematurely create a Talent Point");
            Check.Equal(10, first.CurrentTalentPoints, "first kill retains spendable Talent Points");

            var carry = await store.ApplyMonsterKillRewardAsync(
                account.Id,
                character.Id,
                experience: 160,
                talentExperience: 99)
                ?? throw new InvalidOperationException("carry progression update returned no character");
            Check.Equal(2, carry.CurrentLevel, "fighter EXP advances a level at the original threshold");
            Check.Equal(40, carry.CurrentExperience, "fighter EXP carries its remainder into the next level");
            Check.Equal(252, carry.NextLevelExperience, "level two uses the original next-level threshold");
            Check.Equal(1, carry.LevelUps.Count, "progression reports every crossed level");
            Check.Equal(40, carry.LevelUps[0].CurrentExperience, "level-up packet receives carried fighter EXP");
            Check.Equal(1, carry.CurrentTalentExperience, "Talent EXP carries its remainder at 100");
            Check.Equal(1, carry.TalentPointsGained, "Talent EXP carry creates one Talent Point");
            Check.Equal(11, carry.CurrentTalentPoints, "spendable Talent Point total increments");

            var reloaded = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidOperationException("progression character was not reloaded");
            Check.Equal(2, reloaded.Level, "fighter level survives relogin");
            Check.Equal(40, reloaded.Experience, "carried fighter EXP survives relogin");
            Check.Equal(1, reloaded.TalentExperience, "Talent EXP remainder survives relogin");
            Check.Equal(11, reloaded.TalentPoints, "converted Talent Point survives relogin");

            Check.Equal(200, PlayerExperienceCatalog.GetNextLevelExperience(1), "level-one EXP threshold");
            Check.Equal(252, PlayerExperienceCatalog.GetNextLevelExperience(2), "level-two EXP threshold");
            Check.Equal(584435250, PlayerExperienceCatalog.GetNextLevelExperience(200), "level-cap EXP table entry");
            Check.Equal(80, MonsterRewardCatalog.Resolve(1, 1).Experience, "captured tier-one reward");
            Check.Equal(120, MonsterRewardCatalog.Resolve(11, 1).Experience, "tier-eleven reward follows original curve");
            Check.Equal(8, MonsterRewardCatalog.Resolve(1, 10).Experience, "level-difference reward scales deterministically");
            Check.Equal(0, MonsterRewardCatalog.Resolve(1, 11).Experience, "ten-level reward falloff reaches zero");
            Check.Equal(0, MonsterRewardCatalog.Resolve(1, 12).TalentExperience, "over-level kills do not award Talent EXP");
            Check.Equal(0, MonsterRewardCatalog.Resolve(200, 200).TalentExperience, "level-cap kills award no progression");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckExperienceBoostStackingAsync()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var state = new ExperienceBoostState(
        [
            new(ExperienceStatusIds.MaxExperiencePotion, ExperienceBoostKinds.Consumable, 30_000, 11, expiresAt, "potion"),
            new(ExperienceStatusIds.Weekend, ExperienceBoostKinds.Weekend, 20_000, 1, expiresAt, "weekend"),
            new(ExperienceStatusIds.TrickOrTreat, ExperienceBoostKinds.TrickOrTreat, 1_000, 1, expiresAt, "event"),
            new(ExperienceStatusIds.GuildDoubleExperience16Hours, ExperienceBoostKinds.Guild, 10_000, 1, expiresAt, "guild"),
            new(ExperienceStatusIds.MaxTalentPotion400Percent, ExperienceBoostKinds.Talent, 40_000, 10, expiresAt, "talent"),
            new(ExperienceStatusIds.VipPlatinum, ExperienceBoostKinds.Vip, 2_000, 4, null, "vip:platinum"),
            new(ExperienceStatusIds.FactionAreaExperience, ExperienceBoostKinds.FactionArea, 2_500, 1, expiresAt, "world-boss")
        ]);

        Check.Equal(65_500, state.TotalBonusBasisPoints, "all six fighter EXP families add their bonus rates");
        Check.Equal(604, state.ApplyTo(80), "base 80 EXP receives the additive 7.55x total multiplier");
        Check.Equal(40_000, state.TotalTalentBonusBasisPoints, "Talent EXP boost is isolated from fighter EXP");
        Check.Equal(10, state.ApplyToTalent(2), "base 2 Talent EXP receives the 5x Talent-only multiplier");
        var statusSnapshot = PlayerStatusComposer.Compose(state, [], DateTimeOffset.UtcNow);
        Check.Equal(6.55f, statusSnapshot.Aggregate.ExperienceBonus, "Talent status does not inflate fighter EXP wire aggregate");
        Check.Equal(0, state.ApplyTo(0), "zero base reward remains zero");
        Check.Equal(2_000, VipExperienceBoosts.BonusBasisPoints(VipTier.Platinum), "Platinum VIP grants 20 percent");
        Check.Equal(ExperienceStatusIds.VipPlatinum, VipExperienceBoosts.StatusId(VipTier.Platinum), "Platinum VIP status ID");
        var finiteVip = new ActiveExperienceBoost(
            ExperienceStatusIds.VipGold,
            ExperienceBoostKinds.Vip,
            1_500,
            3,
            expiresAt.AddDays(30),
            "vip:gold");
        Check.Equal(
            uint.MaxValue,
            finiteVip.RemainingSeconds(DateTimeOffset.UtcNow),
            "finite VIP status remains permanent-looking until server reconciliation removes it");
        return Task.CompletedTask;
    }

    private static async Task CheckOnlineProgressionBoostDurationAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-online-boost-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync("online-boost-check", "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "OnlineBoostHero",
                        Camp = GameDefaults.SpartaCamp
                    });
                accountId = account.Id;
                characterId = character.Id;
            }

            var grantedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var statePath = Path.Combine(dataPath, "state.json");
            var legacyState = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new InvalidOperationException("JSON online-boost test could not parse state.json");
            legacyState["characterExperienceBoosts"] = new JsonArray
            {
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.GuildDoubleExperience16Hours,
                    ["kind"] = ExperienceBoostKinds.Guild,
                    ["bonusBasisPoints"] = 10_000,
                    ["priority"] = 1,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(16),
                    ["source"] = "legacy-exp"
                },
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.HighTalentBoost100Percent,
                    ["kind"] = ExperienceBoostKinds.Talent,
                    ["bonusBasisPoints"] = 10_000,
                    ["priority"] = 4,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(8),
                    ["source"] = "legacy-talent"
                },
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = ExperienceStatusIds.Weekend,
                    ["kind"] = ExperienceBoostKinds.Weekend,
                    ["bonusBasisPoints"] = 20_000,
                    ["priority"] = 1,
                    ["activatedAt"] = grantedAt,
                    ["expiresAt"] = grantedAt.AddHours(8),
                    ["source"] = "personal-weekend-grant"
                }
            };
            var accountNode = legacyState["accounts"]?.AsArray().Single()?.AsObject()
                ?? throw new InvalidOperationException("JSON online-boost account is missing");
            accountNode["vipTier"] = (short)VipTier.Platinum;
            accountNode["vipExpiresAt"] = grantedAt.AddDays(1);
            await File.WriteAllTextAsync(statePath, legacyState.ToJsonString(JsonDefaults.Indented));

            var firstOnlineAt = grantedAt.AddDays(3);
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var restored = await store.GetExperienceBoostStateAsync(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    mapId: 0,
                    firstOnlineAt);
                Check.Equal(3, restored.ActiveBoosts.Count, "legacy personal boosts restore after wall-clock expiry");
                Check.Equal(30_000, restored.TotalBonusBasisPoints, "personal EXP grants remain active after offline gap");
                Check.Equal(10_000, restored.TotalTalentBonusBasisPoints, "personal Talent grant remains active after offline gap");
                Check.True(
                    restored.ActiveBoosts.All(boost => boost.Kind != ExperienceBoostKinds.Vip),
                    "expired VIP membership remains calendar-based");
                Check.Equal(
                    57_600u,
                    restored.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Guild)
                        .RemainingSeconds(firstOnlineAt),
                    "legacy sixteen-hour EXP grant restores its original duration");
                Check.Equal(
                    28_800u,
                    restored.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Talent)
                        .RemainingSeconds(firstOnlineAt),
                    "legacy eight-hour Talent grant restores its original duration");

                await store.ConsumeCharacterBoostOnlineTimeAsync(
                    accountId,
                    characterId,
                    firstOnlineAt,
                    firstOnlineAt.AddSeconds(90));
            }

            // Reopening the provider and advancing wall time by a week models
            // logout plus server restart: no offline duration is consumed.
            var secondOnlineAt = firstOnlineAt.AddDays(7);
            await using (var restartedStore = new JsonGameStore(dataPath))
            {
                await restartedStore.EnsureSeedDataAsync();
                var resumed = await restartedStore.GetExperienceBoostStateAsync(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    mapId: 0,
                    secondOnlineAt);
                Check.Equal(
                    57_510u,
                    resumed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Guild)
                        .RemainingSeconds(secondOnlineAt),
                    "EXP duration pauses through logout and restart");
                Check.Equal(
                    28_710u,
                    resumed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Talent)
                        .RemainingSeconds(secondOnlineAt),
                    "Talent duration pauses through logout and restart");

                await restartedStore.ConsumeCharacterBoostOnlineTimeAsync(
                    accountId,
                    characterId,
                    secondOnlineAt,
                    secondOnlineAt.AddSeconds(10));
                var checkpointed = await restartedStore.GetExperienceBoostStateAsync(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    mapId: 0,
                    secondOnlineAt.AddSeconds(10));
                Check.Equal(
                    57_500u,
                    checkpointed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Guild)
                        .RemainingSeconds(secondOnlineAt.AddSeconds(10)),
                    "reconnected online interval resumes the EXP countdown exactly once");
                Check.Equal(
                    28_700u,
                    checkpointed.ActiveBoosts.Single(boost => boost.Kind == ExperienceBoostKinds.Talent)
                        .RemainingSeconds(secondOnlineAt.AddSeconds(10)),
                    "reconnected online interval resumes the Talent countdown exactly once");
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckWorldSessionOwnedBoostClockAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-world-boost-clock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int accountId;
            GameCharacter character;
            await using (var seedStore = new JsonGameStore(dataPath))
            {
                await seedStore.EnsureSeedDataAsync();
                var account = await seedStore.LoginOrCreateAccountAsync("world-boost-clock", "");
                character = await seedStore.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "WorldBoostHero",
                        Camp = GameDefaults.SpartaCamp
                    });
                accountId = account.Id;
            }

            var joinedAt = new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
            var statePath = Path.Combine(dataPath, "state.json");
            var stateJson = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new InvalidOperationException("JSON world boost-clock test could not parse state.json");
            stateJson["characterExperienceBoosts"] = new JsonArray
            {
                new JsonObject
                {
                    ["characterId"] = character.Id,
                    ["statusId"] = ExperienceStatusIds.MaxExperiencePotion,
                    ["kind"] = ExperienceBoostKinds.Consumable,
                    ["bonusBasisPoints"] = 30_000,
                    ["priority"] = 11,
                    ["activatedAt"] = joinedAt.AddDays(-1),
                    ["expiresAt"] = joinedAt.AddDays(-1).AddSeconds(1_000),
                    ["source"] = "world-clock"
                }
            };
            await File.WriteAllTextAsync(statePath, stateJson.ToJsonString(JsonDefaults.Indented));

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var firstOutbound = new TcpClient();
            var firstAccept = listener.AcceptTcpClientAsync();
            await firstOutbound.ConnectAsync(IPAddress.Loopback, port);
            using var firstInbound = await firstAccept;
            await using var firstSession = new ClientSession(firstOutbound);

            using var secondOutbound = new TcpClient();
            var secondAccept = listener.AcceptTcpClientAsync();
            await secondOutbound.ConnectAsync(IPAddress.Loopback, port);
            using var secondInbound = await secondAccept;
            await using var secondSession = new ClientSession(secondOutbound);

            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var registry = new GameSessionRegistry(store);

            var beforeWorld = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddHours(5),
                CancellationToken.None);
            Check.Equal(
                1_000u,
                beforeWorld.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddHours(5)),
                "account login and character selection do not start the boost clock");

            registry.ReplaceAccountSession(accountId, firstSession);
            registry.JoinMap(
                firstSession,
                accountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                worldReady: false,
                joinedAt: joinedAt);
            var whileWorldLoads = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(60),
                CancellationToken.None);
            Check.Equal(
                1_000u,
                whileWorldLoads.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(60)),
                "world loading does not start the online countdown");
            Check.True(
                registry.TryMarkWorldReady(
                    firstSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers,
                    joinedAt.AddSeconds(60)) &&
                unseenPlayers.Count == 0,
                "world-ready transition starts the authoritative boost session");
            var firstCheckpoint = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(120),
                CancellationToken.None);
            Check.Equal(
                940u,
                firstCheckpoint.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(120)),
                "world-ready play checkpoints the online countdown");

            var replaced = registry.ReplaceAccountSession(accountId, secondSession);
            Check.True(ReferenceEquals(firstSession, replaced), "second login identifies the prior account session");
            await registry.FinishProgressionBoostOnlineSessionAsync(
                firstSession,
                joinedAt.AddSeconds(150),
                CancellationToken.None);
            registry.Remove(firstSession);
            registry.JoinMap(
                secondSession,
                accountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                joinedAt: joinedAt.AddSeconds(150));
            var replacementCheckpoint = await registry.GetExperienceBoostStateAsync(
                secondSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(180),
                CancellationToken.None);
            Check.Equal(
                880u,
                replacementCheckpoint.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(180)),
                "session replacement consumes one continuous interval without overlap");

            var staleSessionRead = await registry.GetExperienceBoostStateAsync(
                firstSession,
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddSeconds(210),
                CancellationToken.None);
            Check.Equal(
                880u,
                staleSessionRead.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddSeconds(210)),
                "replaced session cannot consume the new owner's duration");

            await registry.FinishProgressionBoostOnlineSessionAsync(
                secondSession,
                joinedAt.AddSeconds(190),
                CancellationToken.None);
            registry.Remove(secondSession);
            var afterLogout = await store.GetExperienceBoostStateAsync(
                accountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                joinedAt.AddDays(30));
            Check.Equal(
                870u,
                afterLogout.ActiveBoosts.Single().RemainingSeconds(joinedAt.AddDays(30)),
                "logout saves the exact tail and the offline month consumes nothing");
        }
        finally
        {
            listener.Stop();
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckWarriorStarterSkillPacketsAsync()
    {
        SkillState[] warriorSkills = [new() { SkillId = 0, Level = 1 }];
        var skillList = PacketBuilder.SkillList(warriorSkills);
        Check.Equal(24, skillList.Length, "single-skill list length");
        Check.Equal((ushort)10196, ReadUInt16(skillList, 2), "skill-list opcode");
        Check.Equal(1u, ReadUInt32(skillList, 8), "skill-list count");
        Check.Equal(0u, ReadUInt32(skillList, 12), "Light Chop skill ID zero remains valid");
        Check.Equal(0x101u, ReadUInt32(skillList, 16), "Light Chop level flag");

        var unlocks = PacketBuilder.TalentSkillUnlockList(warriorSkills);
        Check.Equal((ushort)10041, ReadUInt16(unlocks, 2), "skill-unlock opcode");
        Check.Equal(1u, ReadUInt32(unlocks, 8), "skill-unlock count");
        Check.Equal(0u, ReadUInt32(unlocks, 12), "skill unlock preserves ID zero");
        var listedIds = Enumerable.Range(0, (int)ReadUInt32(skillList, 8))
            .Select(index => ReadUInt32(skillList, 12 + (index * 12)))
            .ToArray();
        Check.True(
            !listedIds.Any(value => value is >= 250 and <= 354),
            "Warrior skill list does not contain Champion skills");
        return Task.CompletedTask;
    }

    private static async Task CheckJsonProviderStarterSkillAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-skill-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("skill-check", "");
            var warrior = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "JsonWarrior",
                    Profession = 0,
                    Camp = GameDefaults.SpartaCamp
                });
            var skills = await store.GetSkillStatesAsync(account.Id, warrior.Id);
            Check.True(
                skills.Count == 1 && skills[0].SkillId == 0 && skills[0].Level == 1,
                "JSON warrior learns only Light Chop 1");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckSkillCombatCatalogAsync()
    {
        Check.True(SkillCombatCatalog.TryGet(0, out var lightChop), "Light Chop combat data exists");
        Check.Equal(44, lightChop.Target, "Light Chop target mode");
        Check.Equal(28, lightChop.AffectObj, "Light Chop affected-object mode");
        Check.Equal(3f, lightChop.Distance, "Light Chop distance");
        Check.Equal(0f, lightChop.Range, "Light Chop single-target range");
        Check.Equal(0, lightChop.Property, "Light Chop uses physical attack");
        Check.Equal(12, lightChop.Mp, "Light Chop mana cost");
        Check.Equal(-0.5m, lightChop.Power1, "Light Chop physical attack multiplier");
        Check.Equal(250m, lightChop.Power2, "Light Chop flat damage");

        var warrior = CreateCharacter();
        warrior.CalculatedStats = new CharacterStats { PhysicalAttack = 40 };
        Check.True(SkillCombatResolver.IsHostileMonsterSkill(lightChop), "Light Chop can target a hostile monster");
        Check.Equal(270u, SkillCombatResolver.CalculateDamage(warrior, lightChop), "Light Chop damage formula");
        Check.True(
            SkillCombatResolver.IsWithinRange(41.15f, 165.53f, 40.8691f, 162.7964f, lightChop),
            "captured account-13 cast is within Light Chop range");
        Check.True(
            !SkillCombatResolver.IsWithinRange(41.15f, 165.53f, 60f, 180f, lightChop),
            "distant monster cast is rejected");

        Check.True(SkillCombatCatalog.TryGet(334, out var meteorBlast), "Meteor Blast 5 combat data exists");
        Check.Equal(1, meteorBlast.Target, "Meteor Blast targets the caster");
        Check.Equal(28, meteorBlast.AffectObj, "Meteor Blast affected-object mode");
        Check.Equal(0f, meteorBlast.Distance, "Meteor Blast has no selected-target distance");
        Check.Equal(10f, meteorBlast.Range, "Meteor Blast area radius");
        Check.Equal(0, meteorBlast.Property, "Meteor Blast uses physical attack");
        Check.Equal(900, meteorBlast.Mp, "Meteor Blast mana cost");
        Check.Equal(0.88m, meteorBlast.Power1, "Meteor Blast physical attack multiplier");
        Check.Equal(1980m, meteorBlast.Power2, "Meteor Blast flat damage");
        Check.True(
            SkillCombatResolver.IsHostileMonsterAreaSkill(meteorBlast),
            "Meteor Blast is admitted as a hostile self-centred area skill");
        foreach (var championAreaSkillId in new[] { 304, 314, 324, 334 })
        {
            Check.True(
                SkillCombatCatalog.TryGet(championAreaSkillId, out var championAreaSkill) &&
                SkillCombatResolver.IsHostileMonsterAreaSkill(championAreaSkill),
                $"Champion area skill {championAreaSkillId} uses the shared AOE path");
        }

        Check.Equal(
            2055u,
            SkillCombatResolver.CalculateDamage(warrior, meteorBlast),
            "Meteor Blast damage formula");
        Check.True(
            SkillCombatResolver.IsWithinArea(10f, 10f, 19.99f, 10f, meteorBlast),
            "Meteor Blast includes monsters strictly inside its area");
        Check.True(
            !SkillCombatResolver.IsWithinArea(10f, 10f, 20f, 10f, meteorBlast),
            "Meteor Blast excludes monsters on its strict area boundary");
        return Task.CompletedTask;
    }

    private static Task CheckSacredZealStatusCompositionAsync()
    {
        var expected = new[]
        {
            (SkillId: 340, StatusId: 200u, Priority: 1, Mp: 50, Hit: 10, Critical: 4),
            (SkillId: 341, StatusId: 201u, Priority: 2, Mp: 90, Hit: 20, Critical: 8),
            (SkillId: 342, StatusId: 202u, Priority: 3, Mp: 130, Hit: 30, Critical: 12),
            (SkillId: 343, StatusId: 203u, Priority: 4, Mp: 200, Hit: 45, Critical: 18),
            (SkillId: 344, StatusId: 204u, Priority: 5, Mp: 300, Hit: 60, Critical: 24)
        };
        foreach (var item in expected)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(item.SkillId, out var definition),
                $"Sacred Zeal {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId, $"Sacred Zeal {item.SkillId} status ID");
            Check.Equal(7, definition.Kind, $"Sacred Zeal {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority, $"Sacred Zeal {item.SkillId} priority");
            Check.True(definition.Beneficial, $"Sacred Zeal {item.SkillId} is beneficial");
            Check.Equal(TimeSpan.FromSeconds(600), definition.Duration, $"Sacred Zeal {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(10), definition.Cooldown, $"Sacred Zeal {item.SkillId} cooldown");
            Check.Equal(item.Hit, definition.HitBonus, $"Sacred Zeal {item.SkillId} Hit bonus");
            Check.Equal(item.Critical, definition.CriticalAppendBonus, $"Sacred Zeal {item.SkillId} Critical bonus");
            Check.True(
                SkillCombatCatalog.TryGet(item.SkillId, out var combat),
                $"Sacred Zeal {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp, $"Sacred Zeal {item.SkillId} MP cost");
            Check.Equal(1, combat.Target, $"Sacred Zeal {item.SkillId} targets self");
            Check.Equal(1, combat.AffectObj, $"Sacred Zeal {item.SkillId} affects self");
        }

        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var boosts = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                ExperienceStatusIds.Weekend,
                ExperienceBoostKinds.Weekend,
                20_000,
                1,
                now.AddHours(8),
                "weekend"),
            new ActiveExperienceBoost(
                ExperienceStatusIds.VipPlatinum,
                ExperienceBoostKinds.Vip,
                2_000,
                4,
                null,
                "vip:platinum")
        ]);
        var runtime = new ActiveRuntimeStatus(
            204,
            7,
            5,
            true,
            now.AddSeconds(600),
            new ClientStatusAggregate(60, 24, 0f),
            1);
        var snapshot = PlayerStatusComposer.Compose(boosts, [runtime], now);

        Check.Equal(3, snapshot.Effects.Count, "EXP and Sacred Zeal status count");
        Check.Equal(204u, snapshot.Effects[0].StatusId, "Sacred Zeal remains in sorted full snapshot");
        Check.Equal(600u, snapshot.Effects[0].RemainingSeconds, "Sacred Zeal timer starts at 600 seconds");
        Check.Equal(511u, snapshot.Effects[1].StatusId, "weekend EXP status is preserved");
        Check.Equal(1503u, snapshot.Effects[2].StatusId, "VIP EXP status is preserved");
        Check.Equal(60, snapshot.Aggregate.Hit, "Sacred Zeal aggregate Hit bonus");
        Check.Equal(24, snapshot.Aggregate.CriticalAppend, "Sacred Zeal aggregate Critical bonus");
        Check.Equal(2.2f, snapshot.Aggregate.ExperienceBonus, "EXP aggregate is preserved");

        var character = CreateCharacter();
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(204u, ReadUInt32(packet, 12), "Sacred Zeal status packet ID");
        Check.Equal(600u, ReadUInt32(packet, 92), "Sacred Zeal status packet timer");
        Check.Equal(
            character.CalculatedStats!.Hit + 60,
            ReadInt32(packet, 204),
            "StatusData includes base and Sacred Zeal Hit");
        Check.Equal(
            character.CalculatedStats.Critical + 24,
            ReadInt32(packet, 212),
            "StatusData includes base and Sacred Zeal Critical");
        Check.Equal(2.2f, ReadSingle(packet, 300), "StatusData EXP wire offset");

        var oneSecondLater = PlayerStatusComposer.Compose(boosts, [runtime], now.AddSeconds(1));
        Check.Equal(
            snapshot.Fingerprint,
            oneSecondLater.Fingerprint,
            "status fingerprint excludes the changing countdown");
        Check.Equal(599u, oneSecondLater.Effects[0].RemainingSeconds, "status countdown still updates when republished");

        var expired = PlayerStatusComposer.Compose(boosts, [runtime], now.AddSeconds(601));
        Check.Equal(2, expired.Effects.Count, "Sacred Zeal expires without removing EXP statuses");
        Check.Equal(0, expired.Aggregate.Hit, "expired Sacred Zeal removes aggregate Hit");
        Check.Equal(0, expired.Aggregate.CriticalAppend, "expired Sacred Zeal removes aggregate Critical");
        Check.Equal(2.2f, expired.Aggregate.ExperienceBonus, "expired Sacred Zeal preserves aggregate EXP");

        return Task.CompletedTask;
    }

    private static Task CheckHolyWardStatusCompositionAsync()
    {
        var expected = new[]
        {
            (SkillId: 90, StatusId: 160u, Priority: 2, Mp: 35, Physical: 0.10m, Magical: 0m),
            (SkillId: 91, StatusId: 161u, Priority: 3, Mp: 45, Physical: 0.13m, Magical: 0m),
            (SkillId: 92, StatusId: 162u, Priority: 4, Mp: 60, Physical: 0.16m, Magical: 0.05m),
            (SkillId: 93, StatusId: 163u, Priority: 5, Mp: 90, Physical: 0.20m, Magical: 0.10m),
            (SkillId: 94, StatusId: 164u, Priority: 6, Mp: 120, Physical: 0.25m, Magical: 0.15m)
        };
        foreach (var item in expected)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(item.SkillId, out var definition),
                $"Holy Ward {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId, $"Holy Ward {item.SkillId} status ID");
            Check.Equal(6, definition.Kind, $"Holy Ward {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority, $"Holy Ward {item.SkillId} priority");
            Check.True(definition.Beneficial, $"Holy Ward {item.SkillId} is beneficial");
            Check.Equal(TimeSpan.FromSeconds(600), definition.Duration, $"Holy Ward {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(10), definition.Cooldown, $"Holy Ward {item.SkillId} cooldown");
            Check.Equal(item.Physical, definition.PhysicalDamageReduction, $"Holy Ward {item.SkillId} physical mitigation");
            Check.Equal(item.Magical, definition.MagicDamageReduction, $"Holy Ward {item.SkillId} magical mitigation");
            Check.True(
                SkillCombatCatalog.TryGet(item.SkillId, out var combat),
                $"Holy Ward {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp, $"Holy Ward {item.SkillId} MP cost");
            Check.Equal(1, combat.Target, $"Holy Ward {item.SkillId} targets self");
            Check.Equal(1, combat.AffectObj, $"Holy Ward {item.SkillId} affects self");
        }

        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var holyWard = new ActiveRuntimeStatus(
            160,
            6,
            2,
            true,
            now.AddSeconds(600),
            ClientStatusAggregate.Empty,
            1,
            PhysicalDamageReduction: 0.10m);
        var snapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [holyWard],
            now);
        Check.Equal(1, snapshot.Effects.Count, "Holy Ward publishes one status icon");
        Check.Equal(160u, snapshot.Effects[0].StatusId, "Holy Ward publishes Apollo's Shield status ID");
        Check.Equal(600u, snapshot.Effects[0].RemainingSeconds, "Holy Ward publishes the ten-minute timer");
        var strongerSnapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [holyWard with { PhysicalDamageReduction = 0.20m }],
            now);
        Check.True(
            !string.Equals(snapshot.Fingerprint, strongerSnapshot.Fingerprint, StringComparison.Ordinal),
            "Holy Ward mitigation participates in the full-status fingerprint");

        var character = CreateCharacter();
        character.CalculatedStats = new CharacterStats { PhysicalDefense = 0 };
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(160u, ReadUInt32(packet, 12), "Holy Ward status packet carries its icon ID");
        Check.Equal(600u, ReadUInt32(packet, 92), "Holy Ward status packet carries its timer");
        Check.Equal(
            21u,
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 1,
                character,
                holyWard.PhysicalDamageReduction),
            "Holy Ward 1 reduces a captured 24-damage monster hit by ten percent with native truncation");
        Check.Equal(
            18u,
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 1,
                character,
                receivedDamageReduction: 0.25m),
            "Holy Ward 5 reduces a captured monster hit by twenty-five percent");

        return Task.CompletedTask;
    }

    private static Task CheckSkillCastTargetAndImpactAsync()
    {
        const uint localObjectId = 0x1448;
        const uint remoteCasterId = 0x6002;
        const uint monsterId = 0x282C;
        var clientCast = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(clientCast.AsSpan(0, 2), (ushort)clientCast.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(clientCast.AsSpan(2, 2), 10040);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(4, 4), localObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(16, 4), monsterId);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(24, 4), 41.15f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(28, 4), 165.53f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(32, 4), 44.75f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(36, 4), 166.25f);

        Check.True(SkillCastRequest.TryParse(clientCast, out var parsed), "client skill cast parses");
        Check.Equal(localObjectId, parsed.CasterObjectId, "client skill cast caster");
        Check.Equal(0u, parsed.SkillId, "client skill cast supports skill ID zero");
        Check.Equal(monsterId, parsed.TargetObjectId, "client skill cast target at absolute offset 16");
        Check.Equal(41.15f, parsed.CasterX, "client skill cast caster X");
        Check.Equal(165.53f, parsed.CasterZ, "client skill cast caster Z");
        Check.Equal(44.75f, parsed.TargetX, "client skill cast target X");
        Check.Equal(166.25f, parsed.TargetZ, "client skill cast target Z");

        var visual = PacketBuilder.SkillCastVisual(clientCast, remoteCasterId);
        Check.Equal(remoteCasterId, ReadUInt32(visual, 4), "cast visual patches only the caster identity");
        Check.Equal(monsterId, ReadUInt32(visual, 16), "cast visual preserves selected monster target");
        Check.Equal(10u, ReadUInt32(visual, 20), "cast visual advances captured cast state");

        var impact = PacketBuilder.SkillCastImpact(clientCast, remoteCasterId);
        Check.Equal(24, impact.Length, "skill impact length");
        Check.Equal((ushort)10046, ReadUInt16(impact, 2), "skill impact opcode");
        Check.Equal(remoteCasterId, ReadUInt32(impact, 4), "skill impact attacker");
        Check.Equal(monsterId, ReadUInt32(impact, 8), "skill impact target");
        Check.Equal(0u, ReadUInt32(impact, 12), "skill impact supports skill ID zero");
        Check.Equal(44.75f, ReadSingle(impact, 16), "skill impact target X");
        Check.Equal(166.25f, ReadSingle(impact, 20), "skill impact target Z");

        var damage = PacketBuilder.SkillDamage(
            remoteCasterId,
            monsterId,
            resultFlags: 1,
            damage: 865,
            skillId: 0,
            targetX: 44.75f,
            targetZ: 166.25f);
        Check.Equal(32, damage.Length, "skill damage length");
        Check.Equal((ushort)10045, ReadUInt16(damage, 2), "skill damage opcode");
        Check.Equal(remoteCasterId, ReadUInt32(damage, 4), "skill damage attacker");
        Check.Equal(monsterId, ReadUInt32(damage, 8), "skill damage target");
        Check.Equal(1u, ReadUInt32(damage, 12), "skill damage normal-hit result");
        Check.Equal(865u, ReadUInt32(damage, 16), "skill damage reports the uncapped resolved amount");
        Check.Equal(0u, ReadUInt32(damage, 20), "skill damage skill ID zero");
        Check.Equal(44.75f, ReadSingle(damage, 24), "skill damage target X");
        Check.Equal(166.25f, ReadSingle(damage, 28), "skill damage target Z");

        var areaCast = clientCast.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(areaCast.AsSpan(8, 4), 334);
        BinaryPrimitives.WriteUInt32LittleEndian(areaCast.AsSpan(16, 4), localObjectId);
        var areaVisual = PacketBuilder.SelfTargetSkillCastVisual(areaCast, remoteCasterId);
        Check.Equal(remoteCasterId, ReadUInt32(areaVisual, 4), "area cast visual patches caster identity");
        Check.Equal(remoteCasterId, ReadUInt32(areaVisual, 16), "area cast visual patches self-target identity");

        var emptyCluster = PacketBuilder.SkillClusterDamage(
            localObjectId,
            334,
            Array.Empty<SkillClusterDamageEntry>());
        Check.Equal(17, emptyCluster.Length, "empty area damage packet length");
        Check.Equal((ushort)10047, ReadUInt16(emptyCluster, 2), "area damage opcode");
        Check.Equal(localObjectId, ReadUInt32(emptyCluster, 4), "area damage caster");
        Check.Equal(0u, ReadUInt32(emptyCluster, 8), "empty area damage count");
        Check.Equal(334u, ReadUInt32(emptyCluster, 12), "area damage skill");
        Check.Equal((byte)0, emptyCluster[16], "area damage aggregate status flag");

        var cluster = PacketBuilder.SkillClusterDamage(
            remoteCasterId,
            334,
            [
                new SkillClusterDamageEntry(monsterId, 2055),
                new SkillClusterDamageEntry(monsterId + 1, 1200)
            ]);
        Check.Equal(41, cluster.Length, "two-target area damage packet length");
        Check.Equal(2u, ReadUInt32(cluster, 8), "area damage hit count");
        Check.Equal(monsterId, ReadUInt32(cluster, 17), "first area damage target");
        Check.Equal((byte)1, cluster[21], "first area damage hit result");
        Check.Equal((byte)0, cluster[22], "first area damage affects HP");
        Check.Equal((byte)0, cluster[23], "first area damage alignment byte one");
        Check.Equal((byte)0, cluster[24], "first area damage alignment byte two");
        Check.Equal(2055u, ReadUInt32(cluster, 25), "first area damage amount");
        Check.Equal(monsterId + 1, ReadUInt32(cluster, 29), "second area damage target");
        Check.Equal(1200u, ReadUInt32(cluster, 37), "second area damage amount");

        var capturedMeteorBlastCluster = Convert.FromHexString(
            "1D003F276B020000010000004D01000000A42800000100000001000000");
        var reproducedMeteorBlastCluster = PacketBuilder.SkillClusterDamage(
            0x26B,
            333,
            [new SkillClusterDamageEntry(0x28A4, 1)]);
        Check.True(
            reproducedMeteorBlastCluster.SequenceEqual(capturedMeteorBlastCluster),
            "Meteor Blast area damage matches the original capture byte-for-byte");

        var mana = PacketBuilder.PlayerManaUpdate(remoteCasterId, 165);
        Check.Equal(12, mana.Length, "mana update length");
        Check.Equal((ushort)10135, ReadUInt16(mana, 2), "mana update opcode");
        Check.Equal(remoteCasterId, ReadUInt32(mana, 4), "mana update caster");
        Check.Equal(165u, ReadUInt32(mana, 8), "mana update absolute current MP");
        return Task.CompletedTask;
    }

    private static Task CheckAttackPacketLayoutsAsync()
    {
        var clientAttack = Convert.FromHexString(
            "20002A279F0400009AC83043000000007B4731401D270000AED27D007F007F00");
        Check.True(BasicAttackRequest.TryParse(clientAttack, out var parsed), "captured basic attack parses");
        Check.Equal(0x49Fu, parsed.AttackerObjectId, "basic attack attacker");
        Check.Equal(ReadSingle(clientAttack, 8), parsed.AttackerX, "basic attack X");
        Check.Equal(ReadSingle(clientAttack, 12), parsed.AttackerY, "basic attack Y");
        Check.Equal(ReadSingle(clientAttack, 16), parsed.AttackerZ, "basic attack Z");
        Check.Equal(10013u, parsed.TargetObjectId, "basic attack target");

        var freeRevive = Convert.FromHexString("0C0023274814000002000000");
        Check.True(ReviveRequest.TryParse(freeRevive, out var revive), "original free-revive request parses");
        Check.Equal(0x1448u, revive.PlayerObjectId, "revive request player object");
        Check.Equal(2, revive.ReviveType, "revive request free-revival type");

        var capturedPlayerDamage = Convert.FromHexString(
            "1E002A279F0400000000000000000000000000001D270000370000000301");
        var playerDamage = PacketBuilder.PhysicalDamage(
            0x49F,
            0f,
            0f,
            0f,
            10013,
            55,
            result: 3);
        Check.True(playerDamage.SequenceEqual(capturedPlayerDamage), "player normal damage matches capture byte-for-byte");

        var capturedMonsterImpact = Convert.FromHexString(
            "18003E271D2700009F040000D007000078BD3043873C2C40");
        var monsterImpact = PacketBuilder.SkillCastImpact(
            10013,
            0x49F,
            2000,
            ReadSingle(capturedMonsterImpact, 16),
            ReadSingle(capturedMonsterImpact, 20));
        Check.True(monsterImpact.SequenceEqual(capturedMonsterImpact), "monster attack impact matches capture byte-for-byte");

        var capturedMonsterDamage = Convert.FromHexString(
            "1E002A271D270000F227324300000000A5064F409F040000180000000001");
        var monsterDamage = PacketBuilder.PhysicalDamage(
            10013,
            ReadSingle(capturedMonsterDamage, 8),
            ReadSingle(capturedMonsterDamage, 12),
            ReadSingle(capturedMonsterDamage, 16),
            0x49F,
            24,
            result: 0);
        Check.True(monsterDamage.SequenceEqual(capturedMonsterDamage), "monster physical damage matches capture byte-for-byte");

        var capturedDeath = Convert.FromHexString(
            "1C0022274F0200000000164300000000000012C30000000001000000");
        var death = PacketBuilder.PlayerDeath(
            0x24F,
            ReadSingle(capturedDeath, 8),
            ReadSingle(capturedDeath, 12),
            ReadSingle(capturedDeath, 16),
            0);
        Check.True(death.SequenceEqual(capturedDeath), "player death matches capture byte-for-byte");

        var firstExperience = PacketBuilder.ExperienceGain(80, 80);
        Check.True(
            firstExperience.SequenceEqual(Convert.FromHexString("0D002F27500000005000000000")),
            "first-kill EXP notice matches capture byte-for-byte");
        var laterExperience = PacketBuilder.ExperienceGain(80, 160);
        Check.Equal(80, ReadInt32(laterExperience, 4), "EXP notice displays gained delta at +4");
        Check.Equal(160, ReadInt32(laterExperience, 8), "EXP notice carries resulting total at +8");
        Check.True(
            PacketBuilder.TalentExperienceGain(2).SequenceEqual(
                Convert.FromHexString("0C0045280400000002000000")),
            "Talent EXP notice matches capture byte-for-byte");
        Check.True(
            PacketBuilder.PlayerLevelUp(
                0x466,
                2,
                252,
                0,
                1351,
                1331,
                386,
                380).SequenceEqual(
                Convert.FromHexString(
                    "24002E276604000002000000FC000000000000004705000033050000820100007C010000")),
            "fighter level-up notice matches capture byte-for-byte");
        Check.True(
            PacketBuilder.MonsterDeathReward(10013, 0x49F, 80, 2, 0).SequenceEqual(
                Convert.FromHexString(
                    "74002B271D2700009F040000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000000000000000000000000005000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000000000000001D27000000000000")),
            "monster-death progression refresh matches capture byte-for-byte");

        var physical = CreateCharacter();
        physical.Profession = 0;
        physical.CalculatedStats = new CharacterStats { PhysicalAttack = 55, MagicAttack = 99 };
        Check.Equal(55u, MonsterCombatResolver.CalculatePlayerBasicAttack(physical), "physical class basic damage");
        physical.Profession = 3;
        Check.Equal(99u, MonsterCombatResolver.CalculatePlayerBasicAttack(physical), "caster class basic damage");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(0, 0, 2.49f, 0),
            "normal attack accepts a target inside 2.5 units");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(0, 0, 2.5f, 0),
            "normal attack accepts the exact 2.5-unit collision boundary");
        Check.True(
            MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                153.39f,
                142.62f,
                153.2126f,
                142.6414f,
                out var resolvedAttackX,
                out var resolvedAttackZ),
            "normal attack accepts the captured final auto-approach position");
        Check.Equal(153.2126f, resolvedAttackX, "normal attack uses reported auto-approach X");
        Check.Equal(142.6414f, resolvedAttackZ, "normal attack uses reported auto-approach Z");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(
                resolvedAttackX,
                resolvedAttackZ,
                150.8749f,
                142.9226f),
            "warrior auto-approach position reaches the live snake target");
        Check.True(
            !MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                153.39f,
                142.62f,
                149f,
                142.62f,
                out _,
                out _),
            "normal attack rejects an implausible reported-position correction");

        var undefended = CreateCharacter();
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 0 };
        Check.Equal(24u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "tier-one monster attack");
        Check.Equal(27u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(2, undefended), "tier-two monster attack");
        Check.Equal(31u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(3, undefended), "tier-three monster attack");
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 22 };
        Check.Equal(2u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "physical defense reduces monster damage");
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 999 };
        Check.Equal(1u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "monster damage floors at one");
        return Task.CompletedTask;
    }

    private static Task CheckServerTimePacketAsync()
    {
        var capturedAt = DateTimeOffset.FromUnixTimeSeconds(1_778_666_596);
        var packet = PacketBuilder.ServerTime(capturedAt);
        Check.True(
            packet.SequenceEqual(Convert.FromHexString("0E004728808FFFFF644C046A0000")),
            "server-time response matches the working capture byte-for-byte");
        Check.Equal(14, packet.Length, "server-time response uses captured 14-byte shape");
        Check.Equal(-28_800, ReadInt32(packet, 4), "server-time response uses original fixed UTC-8 offset");
        Check.Equal(1_778_666_596u, ReadUInt32(packet, 8), "server-time response carries current Unix seconds");
        return Task.CompletedTask;
    }

    private static Task CheckZodiacProtocolAsync()
    {
        var capturedRequest = Convert.FromHexString(
            "180039280000000000000100000000000000000000000000");
        Check.True(
            ZodiacSyncRequest.TryParse(capturedRequest, out var request),
            "captured Zodiac request parses");
        Check.Equal(0u, request.PlayerId, "captured Zodiac request player placeholder");
        Check.Equal((ushort)0, request.Module, "captured Zodiac request module");
        Check.Equal((ushort)1, request.Sid, "captured Zodiac request full-sync SID");
        Check.Equal(0, request.Value1, "captured Zodiac request v1");
        Check.Equal(0, request.Value2, "captured Zodiac request v2");
        Check.Equal(0, request.Value3, "captured Zodiac request v3");
        Check.True(request.IsFullSync, "module zero SID one is the supported full sync");

        var unsupportedRequest = capturedRequest.ToArray();
        unsupportedRequest[10] = 2;
        Check.True(
            ZodiacSyncRequest.TryParse(unsupportedRequest, out var unsupported) &&
            !unsupported.IsFullSync,
            "other Zodiac SIDs parse but are not treated as a full sync");
        Check.True(
            !ZodiacSyncRequest.TryParse(capturedRequest.AsSpan(0, 23), out _),
            "truncated Zodiac request is rejected");

        var now = new DateTimeOffset(2026, 5, 13, 11, 33, 10, TimeSpan.Zero);
        var character = new GameCharacter
        {
            Id = 620,
            ZodiacType = 1,
            ZodiacLuckyStatus = 1,
            ZodiacLuckyExpiresAt = now.AddHours(1),
            ZodiacLevel = 9,
            ZodiacEnergy = 71_419,
            ZodiacAccumulatedExperienceX100 = 132_734,
            ZodiacAccumulatedTalentExperienceX100 = 728
        };
        var packet = PacketBuilder.ZodiacFullSync(character, now);
        Check.Equal(328, packet.Length, "Zodiac full sync uses the captured packet length");
        Check.True(
            packet.AsSpan(0, 24).SequenceEqual(Convert.FromHexString(
                "4801392848140000000001007E060200D802000001000000")),
            "Zodiac header uses the local-player object ID and captured v3 marker");
        Check.Equal(1, ReadInt32(packet, 24), "Zodiac type state");
        Check.Equal(1, ReadInt32(packet, 28), "active lucky-day state");
        Check.Equal(9, ReadInt32(packet, 32), "Zodiac level byte and zero padding");
        Check.Equal(71_419, ReadInt32(packet, 36), "Zodiac energy state");
        Check.Equal(0, ReadInt32(packet, 48), "safe default stone level");
        Check.Equal(0, ReadInt32(packet, 56), "safe default secondary stone attribute");
        Check.Equal(132_734f, ReadSingle(packet, 64), "accumulated combat EXP float mirror");
        Check.Equal(728f, ReadSingle(packet, 68), "accumulated talent EXP float mirror");

        foreach (var stoneOffset in new[] { 92, 108, 124 })
        {
            Check.Equal(-1, ReadInt32(packet, stoneOffset), "empty Zodiac stone uses ID -1");
        }

        for (var gridIndex = 0; gridIndex < 12; gridIndex++)
        {
            var gridOffset = 136 + (gridIndex * 16);
            Check.Equal(
                ((gridIndex / 4) + 1) << 8,
                ReadInt32(packet, gridOffset),
                $"Zodiac grid {gridIndex} keeps its captured row marker at level zero");
            Check.Equal(
                -1,
                ReadInt32(packet, gridOffset + 4),
                $"Zodiac grid {gridIndex} has no selected skill");
        }

        character.ZodiacLuckyExpiresAt = now.AddSeconds(-1);
        var expiredPacket = PacketBuilder.ZodiacFullSync(character, now);
        Check.Equal(0, ReadInt32(expiredPacket, 28), "expired lucky-day state is not advertised");

        Check.Equal(1_000, ZodiacEnergyCatalog.GetStorageLimit(1), "Zodiac level-one storage ceiling");
        Check.Equal(100_000, ZodiacEnergyCatalog.GetStorageLimit(9), "Zodiac level-nine storage ceiling");
        Check.Equal(1_090_000, ZodiacEnergyCatalog.GetStorageLimit(30), "Zodiac level-thirty storage ceiling");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ZodiacEnergyCatalog.GetStorageLimit(0),
            "Zodiac storage lookup rejects level zero");
        character.ZodiacEnergy = 100_001;
        var cappedPacket = PacketBuilder.ZodiacFullSync(character, now);
        Check.Equal(100_000, ReadInt32(cappedPacket, 36), "Zodiac full sync enforces the client storage ceiling");

        var energyPacket = PacketBuilder.ZodiacEnergyIncrease(
            currentEnergy: 71_420,
            gainedEnergyX100: 100);
        Check.True(
            energyPacket.SequenceEqual(Convert.FromHexString(
                "180039284814000000000500FC1601006400000000000000")),
            "Zodiac SID5 uses authoritative total energy and hundredths gain fields");

        var accumulationPacket = PacketBuilder.ZodiacAccumulationGain(
            new GameCharacter { Id = 1183 },
            experience: 8,
            talentExperience: 2);
        Check.True(
            accumulationPacket.SequenceEqual(Convert.FromHexString(
                "180039284814000000000700080000000200000000000000")),
            "Zodiac SID7 accumulation gain matches the capture with the local object ID");

        return Task.CompletedTask;
    }

    private static Task CheckZodiacOnlineEnergyPolicyAsync()
    {
        var policy = new ZodiacEnergyOptions().Snapshot();
        Check.Equal(300, policy.TickSeconds, "Zodiac accrual uses five-minute ticks");
        Check.Equal(10_800, policy.BoostedDailySeconds, "first three online hours use boosted policy");
        Check.Equal(2_000, policy.BoostedEnergyPerTickX100, "emulator boosted rate is explicit x100 policy");
        Check.Equal(1_000, policy.NormalEnergyPerTickX100, "emulator normal rate is explicit x100 policy");
        Check.Equal(-480, policy.ServerUtcOffsetMinutes, "Zodiac day follows original fixed UTC-8 clock");

        var start = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var character = new GameCharacter { ZodiacLevel = 1 };
        var incomplete = ZodiacEnergyAccrual.Apply(
            character,
            start,
            start.AddSeconds(299),
            policy);
        Check.Equal(0, incomplete.GainedEnergyX100, "an incomplete five-minute interval grants nothing");
        Check.Equal(0, character.ZodiacEnergy, "incomplete interval leaves energy unchanged");

        var firstTick = ZodiacEnergyAccrual.Apply(
            character,
            start.AddSeconds(299),
            start.AddMinutes(5),
            policy);
        Check.Equal(2_000, firstTick.GainedEnergyX100, "first completed tick grants boosted emulator rate");
        Check.Equal(20, character.ZodiacEnergy, "first tick updates authoritative integer energy");

        var staleFlush = ZodiacEnergyAccrual.Apply(
            character,
            start,
            start.AddMinutes(4),
            policy);
        Check.Equal(0, staleFlush.GainedEnergyX100, "out-of-order session flush cannot duplicate energy");
        Check.Equal(
            start.AddMinutes(5),
            staleFlush.LastOnlineAt,
            "out-of-order session flush cannot move the durable watermark backwards");
        Check.Equal(
            TimeSpan.FromMinutes(5).Ticks,
            staleFlush.OnlineDurationTicksToday,
            "out-of-order session flush preserves completed online duration");

        var restOfBoostedWindow = ZodiacEnergyAccrual.Apply(
            character,
            start.AddMinutes(5),
            start.AddHours(3),
            policy);
        Check.Equal(70_000, restOfBoostedWindow.GainedEnergyX100, "remaining first-three-hour ticks stay boosted");
        Check.Equal(720, character.ZodiacEnergy, "three boosted hours total thirty-six ticks");

        var firstNormalTick = ZodiacEnergyAccrual.Apply(
            character,
            start.AddHours(3),
            start.AddHours(3).AddMinutes(5),
            policy);
        Check.Equal(1_000, firstNormalTick.GainedEnergyX100, "tick after three online hours uses normal rate");
        Check.Equal(730, character.ZodiacEnergy, "normal tick adds ten emulator energy");

        var capped = new GameCharacter
        {
            ZodiacLevel = 1,
            ZodiacEnergy = 999,
            ZodiacEnergyRemainderX100 = 50
        };
        var cappedResult = ZodiacEnergyAccrual.Apply(
            capped,
            start,
            start.AddMinutes(5),
            policy);
        Check.Equal(50, cappedResult.GainedEnergyX100, "cap reports only the actually applied fractional gain");
        Check.Equal(1_000, capped.ZodiacEnergy, "client MaxPower ceiling caps accrued energy");
        Check.Equal(0, capped.ZodiacEnergyRemainderX100, "cap clears impossible fractional overflow");

        var utcEightMidnight = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        Check.Equal(
            new DateOnly(2026, 7, 19),
            ZodiacEnergyAccrual.GetServerDay(utcEightMidnight.AddTicks(-1), policy.ServerUtcOffset),
            "instant before UTC-8 midnight remains on prior Zodiac day");
        Check.Equal(
            new DateOnly(2026, 7, 20),
            ZodiacEnergyAccrual.GetServerDay(utcEightMidnight, policy.ServerUtcOffset),
            "UTC-8 midnight rotates the Zodiac day");

        var compensation = new GameCharacter
        {
            ZodiacLevel = 1,
            ZodiacOnlineDay = new DateOnly(2026, 7, 19),
            ZodiacOnlineDurationTicksToday = TimeSpan.FromMinutes(59).Ticks
        };
        var compensationResult = ZodiacEnergyAccrual.Apply(
            compensation,
            utcEightMidnight,
            utcEightMidnight.AddSeconds(1),
            policy);
        Check.True(compensationResult.CompensationApplied, "prior day below one hour triggers compensation");
        Check.Equal(24_000, compensationResult.GainedEnergyX100, "compensation is one boosted online hour");
        Check.Equal(240, compensation.ZodiacEnergy, "compensation updates stored energy");
        Check.Equal(
            new DateOnly(2026, 7, 20),
            compensation.ZodiacLastCompensationDay!.Value,
            "compensation day marker prevents duplicate awards");
        var noDuplicate = ZodiacEnergyAccrual.Apply(
            compensation,
            utcEightMidnight.AddSeconds(1),
            utcEightMidnight.AddSeconds(2),
            policy);
        Check.Equal(0, noDuplicate.GainedEnergyX100, "same-day follow-up does not duplicate compensation");

        var absent = new GameCharacter
        {
            ZodiacLevel = 1,
            ZodiacOnlineDay = new DateOnly(2026, 7, 17),
            ZodiacOnlineDurationTicksToday = TimeSpan.FromHours(2).Ticks
        };
        var absentResult = ZodiacEnergyAccrual.Apply(
            absent,
            utcEightMidnight,
            utcEightMidnight.AddSeconds(1),
            policy);
        Check.True(absentResult.CompensationApplied, "absence longer than one day triggers compensation");

        return Task.CompletedTask;
    }

    private static async Task CheckJsonZodiacPersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-zodiac-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var luckyExpiry = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var onlineStart = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero);
        var energyPolicy = new ZodiacEnergyOptions().Snapshot();

        try
        {
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync("zodiac-check", "");
                var created = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "ZodiacHero",
                        Faith = 3,
                        ZodiacType = 11,
                        ZodiacLuckyStatus = 1,
                        ZodiacLuckyExpiresAt = luckyExpiry,
                        ZodiacLevel = 7,
                        ZodiacEnergy = 54_321,
                        ZodiacAccumulatedExperienceX100 = 12_345,
                        ZodiacAccumulatedTalentExperienceX100 = 6_789
                    });
                var accumulation = await store.AddZodiacAccumulationAsync(
                    account.Id,
                    created.Id,
                    experienceGainX100: 800,
                    talentExperienceGainX100: 200)
                    ?? throw new InvalidOperationException("Zodiac accumulation was not persisted");
                Check.Equal(13_145, accumulation.CurrentExperienceX100, "Zodiac EXP mutation result");
                Check.Equal(6_989, accumulation.CurrentTalentExperienceX100, "Zodiac talent mutation result");
                var partialOnline = await store.ApplyZodiacOnlineTimeAsync(
                    account.Id,
                    created.Id,
                    onlineStart,
                    onlineStart.AddSeconds(299),
                    energyPolicy)
                    ?? throw new InvalidOperationException("Zodiac online interval was not persisted");
                Check.Equal(0, partialOnline.GainedEnergyX100, "disconnect before five minutes grants no energy");
            }

            await using var reloadedStore = new JsonGameStore(dataPath);
            var accountReloaded = await reloadedStore.LoginOrCreateAccountAsync("zodiac-check", "");
            var character = await reloadedStore.GetFirstCharacterAsync(accountReloaded.Id)
                ?? throw new InvalidOperationException("Zodiac character was not reloaded");
            Check.Equal((byte)3, character.Faith, "Faith remains independent from Zodiac type");
            Check.Equal((byte)11, character.ZodiacType, "Zodiac type persists");
            Check.Equal(1, character.ZodiacLuckyStatus, "Zodiac lucky status persists");
            Check.Equal(luckyExpiry, character.ZodiacLuckyExpiresAt!.Value, "Zodiac lucky expiry persists");
            Check.Equal((byte)7, character.ZodiacLevel, "Zodiac level persists");
            Check.Equal(54_321, character.ZodiacEnergy, "Zodiac energy persists");
            Check.Equal(
                TimeSpan.FromSeconds(299).Ticks,
                character.ZodiacOnlineDurationTicksToday,
                "partial online interval persists across disconnect");
            Check.Equal(13_145, character.ZodiacAccumulatedExperienceX100, "Zodiac combat EXP persists");
            Check.Equal(6_989, character.ZodiacAccumulatedTalentExperienceX100, "Zodiac talent EXP persists");

            var resumedTick = await reloadedStore.ApplyZodiacOnlineTimeAsync(
                accountReloaded.Id,
                character.Id,
                onlineStart.AddSeconds(299),
                onlineStart.AddMinutes(5),
                energyPolicy)
                ?? throw new InvalidOperationException("Resumed Zodiac interval was not persisted");
            Check.Equal(2_000, resumedTick.GainedEnergyX100, "reconnect resumes the persisted five-minute remainder");
            var afterResumedTick = await reloadedStore.GetFirstCharacterAsync(accountReloaded.Id)
                ?? throw new InvalidOperationException("Resumed Zodiac character was not reloaded");
            Check.Equal(54_341, afterResumedTick.ZodiacEnergy, "completed resumed tick persists its energy");
            Check.Equal(
                TimeSpan.FromMinutes(5).Ticks,
                afterResumedTick.ZodiacOnlineDurationTicksToday,
                "daily online accounting includes both sides of reconnect");

            var creationPayload = new byte[71];
            creationPayload[35] = 11;
            creationPayload[70] = 3;
            Check.Equal(
                (byte)11,
                GameClientHandler.ReadZodiacTypeFromCreationPayload(creationPayload),
                "creation payload byte 35 is the Zodiac selection");
            creationPayload[35] = 12;
            Check.Equal(
                (byte)0,
                GameClientHandler.ReadZodiacTypeFromCreationPayload(creationPayload),
                "invalid creation Zodiac safely falls back to Aries");
            Check.Equal(
                (byte)0,
                GameClientHandler.ReadZodiacTypeFromCreationPayload(ReadOnlySpan<byte>.Empty),
                "short creation payload safely falls back to Aries");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckPlayerRecoveryProtocolAsync()
    {
        Check.Equal(TimeSpan.FromSeconds(6), GameSessionRegistry.PlayerRecoveryInterval, "modern recovery cadence");
        Check.Equal(63, PlayerRecoveryCatalog.GetBaseHp(1, 0), "level-one warrior base HP recovery");
        Check.Equal(38, PlayerRecoveryCatalog.GetBaseMp(1, 0), "level-one warrior base MP recovery");
        Check.Equal(496, PlayerRecoveryCatalog.GetBaseHp(200, 0), "level-cap warrior base HP recovery");
        Check.Equal(496, PlayerRecoveryCatalog.GetBaseMp(200, 3), "level-cap mage base MP recovery");

        var character = new GameCharacter
        {
            Level = 4,
            Profession = 0,
            CurrentHp = 1_000,
            MaxHp = 1_500,
            CurrentMp = 9,
            MaxMp = 177,
            CalculatedStats = new CharacterStats
            {
                HpRecovery = 10,
                MpRecovery = 5
            }
        };
        Check.True(PlayerRecoveryCatalog.TryApply(character), "living damaged character recovers");
        Check.Equal(1L, character.VitalsRevision, "recovery advances the vitals revision");
        Check.Equal(1_076, character.CurrentHp, "base and bonus HP recovery are added");
        Check.Equal(53, character.CurrentMp, "base and bonus MP recovery are added");
        Check.True(
            PacketBuilder.PlayerVitalsUpdate(0x00001448, character.CurrentHp, character.CurrentMp)
                .SequenceEqual(Convert.FromHexString("10007127481400003404000035000000")),
            "modern absolute HP/MP recovery packet");

        character.CurrentHp = 1_499;
        character.CurrentMp = 176;
        Check.True(PlayerRecoveryCatalog.TryApply(character), "near-full character recovers");
        Check.Equal(2L, character.VitalsRevision, "each changed recovery advances the vitals revision");
        Check.Equal(1_500, character.CurrentHp, "HP recovery clamps to max");
        Check.Equal(177, character.CurrentMp, "MP recovery clamps to max");
        Check.True(!PlayerRecoveryCatalog.TryApply(character), "full character does not produce an update");
        Check.Equal(2L, character.VitalsRevision, "unchanged recovery does not advance the vitals revision");

        character.CurrentHp = 0;
        character.CurrentMp = 1;
        Check.True(!PlayerRecoveryCatalog.TryApply(character), "dead character cannot passively recover");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldSpawnAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x6A17C04D;
        var packet = PacketBuilder.PlayerWorldSpawn(character, objectId);

        Check.Equal(300, packet.Length, "PlayerWorldSpawn packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerWorldSpawn declared length");
        Check.Equal((ushort)0x2725, ReadUInt16(packet, 2), "PlayerWorldSpawn opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerWorldSpawn object id");
        Check.Equal(character.PositionX, ReadSingle(packet, 60), "PlayerWorldSpawn X at offset 60");
        Check.Equal(character.PositionZ, ReadSingle(packet, 64), "PlayerWorldSpawn Z at offset 64");
        Check.Equal(0f, ReadSingle(packet, 68), "PlayerWorldSpawn terrain-height float at offset 68");
        Check.Equal(1f, ReadSingle(packet, 72), "PlayerWorldSpawn facing at offset 72");
        Check.Equal(character.Face, packet[56], "PlayerWorldSpawn face");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x613u);

        ReadOnlySpan<byte> expectedVisuals = [0xCA, 0xCA, 0xCA, 0x87, 0x11];
        Check.True(
            packet.AsSpan(81, expectedVisuals.Length).SequenceEqual(expectedVisuals),
            "world visual bytes preserve compact item order and grade/quality nibbles");
        Check.True(
            packet.AsSpan(81 + expectedVisuals.Length, 18 - expectedVisuals.Length).IndexOfAnyExcept((byte)0) < 0,
            "unused world visual bytes are zero");

        ReadOnlySpan<byte> expectedAttributeCounts = [4, 5, 5, 2, 0];
        Check.True(
            packet.AsSpan(102, expectedAttributeCounts.Length).SequenceEqual(expectedAttributeCounts),
            "world item attribute counts preserve compact item order");
        Check.True(
            packet.AsSpan(102 + expectedAttributeCounts.Length, 17 - expectedAttributeCounts.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused world item attribute counts are zero");

        ushort[] expectedIds = [2443, 2261, 1834, 14504, 16184];
        for (var index = 0; index < expectedIds.Length; index++)
        {
            Check.Equal(
                expectedIds[index],
                ReadUInt16(packet, 124 + (index * sizeof(ushort))),
                $"world compact equipment id {index}");
        }

        Check.Equal(0x00108409u, ReadUInt32(packet, 168), "world source-slot equipment mask");

        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "world full-visual extension marker");
        ReadOnlySpan<byte> expectedFullQualities = [10, 10, 10, 7, 1];
        ReadOnlySpan<byte> expectedFullGrades = [12, 12, 12, 8, 1];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "world extension preserves full quality values");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "world extension preserves full grade values");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldExtendedAppearanceAsync()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2344,4,80,40,60,240,20,25,1,1,0]";
        slots[8] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[9] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[10] = "[1435,4,80,90,60,230,20,25,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';

        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x814u);

        ReadOnlySpan<byte> expectedLegacyVisuals = [0xCD, 0xCD, 0xCD, 0xCD];
        Check.True(
            packet.AsSpan(81, expectedLegacyVisuals.Length).SequenceEqual(expectedLegacyVisuals),
            "legacy world decoder carries the supported Q13/G12 forge projection");
        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "extended world marker is GWX1");

        ReadOnlySpan<byte> expectedFullQualities = [20, 20, 20, 20];
        ReadOnlySpan<byte> expectedFullGrades = [25, 25, 25, 25];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "extended world qualities preserve Q20");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "extended world grades preserve G25");
        Check.True(
            packet.AsSpan(264 + expectedFullQualities.Length, 18 - expectedFullQualities.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world quality bytes are zero");
        Check.True(
            packet.AsSpan(282 + expectedFullGrades.Length, 18 - expectedFullGrades.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world grade bytes are zero");

        Check.Equal((ushort)3246, ReadUInt16(packet, 126), "first extended ring remains packed");
        Check.Equal((ushort)3246, ReadUInt16(packet, 128), "second extended ring remains packed");
        Check.Equal((byte)5, packet[102], "extended head keeps its real append-attribute count");
        Check.Equal((byte)5, packet[105], "extended weapon keeps its real append-attribute count");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerAuxiliaryAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        const uint objectId = 0x716u;

        var refresh = PacketBuilder.EquipmentVisualRefresh(character, objectId);
        Check.Equal(objectId, ReadUInt32(refresh, 4), "EquipmentVisualRefresh object id");
        Check.Equal((uint)character.Hair, ReadUInt32(refresh, 8), "EquipmentVisualRefresh hair/model");
        Check.Equal((uint)character.Gender + 1u, ReadUInt32(refresh, 12), "EquipmentVisualRefresh one-based gender");
        Check.Equal(2443u, ReadUInt32(refresh, 16), "EquipmentVisualRefresh source slot 0");
        Check.Equal(2261u, ReadUInt32(refresh, 28), "EquipmentVisualRefresh source slot 3");
        Check.Equal(1834u, ReadUInt32(refresh, 56), "EquipmentVisualRefresh source slot 10");

        var extras = PacketBuilder.PlayerAppearanceExtras(character, objectId);
        Check.Equal(objectId, ReadUInt32(extras, 8), "PlayerAppearanceExtras object id");
        Check.Equal((byte)1, extras[64], "PlayerAppearanceExtras neutral presence marker");
        for (var offset = 4; offset < extras.Length; offset++)
        {
            if (offset is >= 8 and < 12 || offset == 64)
            {
                continue;
            }

            Check.Equal((byte)0, extras[offset], $"PlayerAppearanceExtras neutral byte {offset}");
        }

        var title = PacketBuilder.PlayerTitleInfo(character, objectId);
        Check.Equal(objectId, ReadUInt32(title, 4), "PlayerTitleInfo object id");
        Check.True(
            title.AsSpan(8).IndexOfAnyExcept((byte)0) < 0,
            "PlayerTitleInfo untitled body is zero");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerInspectExtendedSlotsAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        const int headerLength = 8;
        const int recordLength = 72;
        const int maskOffset = 1520;

        Check.Equal(1524, packet.Length, "inspect packet includes trailing slot mask");
        Check.Equal(2443u, ReadUInt32(packet, headerLength), "inspect packed record 0 is source slot 0");
        Check.Equal(
            2261u,
            ReadUInt32(packet, headerLength + recordLength),
            "inspect packed record 1 skips empty source slots");
        Check.Equal(
            14504u,
            ReadUInt32(packet, headerLength + (3 * recordLength)),
            "inspect packed cosmetic source slot 15 item");
        Check.Equal(
            16184u,
            ReadUInt32(packet, headerLength + (4 * recordLength)),
            "inspect packed title/cosmetic source slot 20 item");
        Check.Equal(
            uint.MaxValue,
            ReadUInt32(packet, headerLength + (5 * recordLength)),
            "first unused inspect record uses empty sentinel");
        Check.Equal(0x00108409u, ReadUInt32(packet, maskOffset), "inspect source-slot mask");

        var detailedSlots = Enumerable.Repeat("[]", 21).ToArray();
        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[8] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[9] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';

        var detailedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        Check.Equal(2344u, ReadUInt32(detailedPacket, headerLength), "inspect detailed head item");
        Check.Equal((byte)20, detailedPacket[headerLength + 24], "inspect preserves Q20");
        Check.Equal((byte)25, detailedPacket[headerLength + 25], "inspect preserves G25");
        var expectedAttributes = new[] { 4, 80, 40, 60, 240 };
        for (var attribute = 0; attribute < expectedAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedAttributes[attribute],
                ReadInt32(detailedPacket, headerLength + 4 + (attribute * 4)),
                $"inspect preserves append attribute {attribute + 1}");
        }

        Check.Equal((ushort)710, ReadUInt16(detailedPacket, headerLength + 32), "inspect preserves holy suit code");
        Check.Equal((ushort)4, ReadUInt16(detailedPacket, headerLength + 34), "inspect preserves socket count");
        var expectedStoneCodes = new ushort[] { 109, 509, 709, 309 };
        var expectedStoneValues = new ushort[] { 1400, 2200, 1200, 1400 };
        for (var stone = 0; stone < expectedStoneCodes.Length; stone++)
        {
            Check.Equal(
                expectedStoneCodes[stone],
                ReadUInt16(detailedPacket, headerLength + 36 + (stone * 2)),
                $"inspect preserves holy-stone code {stone + 1}");
            Check.Equal(
                expectedStoneValues[stone],
                ReadUInt16(detailedPacket, headerLength + 44 + (stone * 2)),
                $"inspect preserves holy-stone value {stone + 1}");
        }

        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + recordLength), "inspect first ring record");
        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + (2 * recordLength)), "inspect second ring record");
        var expectedRingAttributes = new[] { 4, 80, 240, 60, 134 };
        for (var attribute = 0; attribute < expectedRingAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedRingAttributes[attribute],
                ReadInt32(
                    detailedPacket,
                    headerLength + (2 * recordLength) + 4 + (attribute * 4)),
                $"inspect second ring preserves append attribute {attribute + 1}");
        }

        Check.Equal(
            expectedStoneCodes[3],
            ReadUInt16(detailedPacket, headerLength + (2 * recordLength) + 42),
            "inspect second ring preserves fourth holy stone");
        Check.Equal(0x00000301u, ReadUInt32(detailedPacket, maskOffset), "inspect distinguishes both ring slots");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 64)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 64),
            "identical ring types have distinct item state identities");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 68)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 68),
            "identical ring types have distinct item slot identities");

        var repeatedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x818u);
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 64),
            ReadUInt32(repeatedPacket, headerLength + 64),
            "inspect item state identity is stable");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(repeatedPacket, headerLength + 68),
            "inspect item slot identity is stable");

        var otherCharacter = CreateAppearanceCharacter();
        otherCharacter.Id = character.Id + 1;
        otherCharacter.Equipment = character.Equipment;
        var otherPacket = PacketBuilder.PlayerInspectEquipment(otherCharacter, 0x819u);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(otherPacket, headerLength + 64),
            "inspect item state identity is character-specific");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 68) != ReadUInt32(otherPacket, headerLength + 68),
            "inspect item slot identity is character-specific");

        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,711,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';
        var upgradedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x81Au);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(upgradedPacket, headerLength + 64),
            "inspect item state identity changes with item metadata");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(upgradedPacket, headerLength + 68),
            "inspect item slot identity survives item metadata changes");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerDetailAsync()
    {
        var character = CreateCharacter();
        character.Silver = 38_832;
        character.Gold = 6;
        var packet = PacketBuilder.PlayerDetail(character);

        Check.Equal(136, packet.Length, "PlayerDetail packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerDetail declared length");
        Check.Equal((ushort)0x273B, ReadUInt16(packet, 2), "PlayerDetail opcode");
        Check.Equal(character.Name, ReadFixedAscii(packet, 4, 32), "PlayerDetail character name");
        Check.Equal(character.Level, ReadInt32(packet, 96), "PlayerDetail level");
        Check.Equal(character.MaxHp, ReadInt32(packet, 100), "PlayerDetail max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 104), "PlayerDetail max MP");
        Check.Equal(character.CurrentHp, ReadInt32(packet, 108), "PlayerDetail current HP");
        Check.Equal(character.CurrentMp, ReadInt32(packet, 112), "PlayerDetail current MP");
        Check.Equal(character.Silver, ReadInt32(packet, 116), "PlayerDetail captured silver field");
        Check.Equal(character.Gold, ReadInt32(packet, 120), "PlayerDetail captured gold field");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusUpdateAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x7135B24E;
        var packet = PacketBuilder.PlayerStatusUpdate(character, objectId);

        Check.Equal(236, packet.Length, "PlayerStatusUpdate packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerStatusUpdate declared length");
        Check.Equal((ushort)0x27B6, ReadUInt16(packet, 2), "PlayerStatusUpdate opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerStatusUpdate object id");
        Check.Equal(character.Name, ReadFixedAscii(packet, 8, 32), "PlayerStatusUpdate character name");
        Check.Equal(character.Gender, packet[40], "PlayerStatusUpdate gender");
        Check.Equal(character.PositionX, ReadSingle(packet, 44), "PlayerStatusUpdate X at offset 44");
        Check.Equal(0f, ReadSingle(packet, 48), "PlayerStatusUpdate terrain-height float at offset 48");
        Check.Equal(character.PositionZ, ReadSingle(packet, 52), "PlayerStatusUpdate Z at offset 52");
        Check.Equal(1f, ReadSingle(packet, 56), "PlayerStatusUpdate facing at offset 56");
        Check.Equal((int)character.Profession, ReadInt32(packet, 92), "PlayerStatusUpdate profession");
        Check.Equal(character.Experience, ReadInt32(packet, 96), "PlayerStatusUpdate fighter EXP");
        Check.Equal(character.Level, ReadInt32(packet, 100), "PlayerStatusUpdate level");
        Check.Equal(character.CurrentHp, ReadInt32(packet, 104), "PlayerStatusUpdate current HP");
        Check.Equal(character.CurrentMp, ReadInt32(packet, 108), "PlayerStatusUpdate current MP");
        Check.Equal(0, ReadInt32(packet, 120), "remote PlayerStatusUpdate does not disclose silver");
        Check.Equal(0, ReadInt32(packet, 124), "remote PlayerStatusUpdate does not disclose gold");
        Check.Equal(character.MaxHp, ReadInt32(packet, 144), "PlayerStatusUpdate max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 148), "PlayerStatusUpdate max MP");
        Check.Equal(PlayerRecoveryCatalog.GetTotalHp(character), ReadInt32(packet, 152), "PlayerStatusUpdate HP recovery");
        Check.Equal(PlayerRecoveryCatalog.GetTotalMp(character), ReadInt32(packet, 156), "PlayerStatusUpdate MP recovery");
        Check.Equal(character.CalculatedStats!.PhysicalAttack, ReadInt32(packet, 160), "PlayerStatusUpdate physical attack");
        Check.Equal(character.CalculatedStats.PhysicalDefense, ReadInt32(packet, 164), "PlayerStatusUpdate physical defense");
        Check.Equal(character.CalculatedStats.MagicAttack, ReadInt32(packet, 168), "PlayerStatusUpdate magic attack");
        Check.Equal(character.CalculatedStats.MagicDefense, ReadInt32(packet, 172), "PlayerStatusUpdate magic defense");
        Check.Equal(character.CalculatedStats.Hit, ReadInt32(packet, 176), "PlayerStatusUpdate hit");
        Check.Equal(character.CalculatedStats.Dodge, ReadInt32(packet, 180), "PlayerStatusUpdate dodge");
        Check.Equal(character.CalculatedStats.Critical, ReadInt32(packet, 184), "PlayerStatusUpdate critical");
        Check.Equal(character.CalculatedStats.CriticalResistance, ReadInt32(packet, 188), "PlayerStatusUpdate critical resistance");
        Check.Equal(character.TalentPoints, ReadInt32(packet, 228), "PlayerStatusUpdate talent points");

        character.Silver = 10_010_000;
        character.Gold = 73;
        var localPacket = PacketBuilder.PlayerStatusUpdate(character);
        Check.Equal(character.Silver, ReadInt32(localPacket, 120), "local PlayerStatusUpdate silver");
        Check.Equal(character.Gold, ReadInt32(localPacket, 124), "local PlayerStatusUpdate gold");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusEffectsAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x7135B24E;
        var effects = new ClientStatusEffect[]
        {
            new(1504, 43_200),
            new(511, 28_800),
            new(1503, uint.MaxValue),
            new(586, 28_800)
        };
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            objectId,
            effects,
            new ClientStatusAggregate(0, 0, 6.2f));

        Check.Equal(340, packet.Length, "status-effect packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "status-effect declared length");
        Check.Equal((ushort)10167, ReadUInt16(packet, 2), "status-effect opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "status-effect object id");
        Check.Equal(4u, ReadUInt32(packet, 8), "status-effect count");

        // Preserved MSG_STATUS writes std::map entries in ascending status-ID order.
        Check.Equal(511u, ReadUInt32(packet, 12), "first sorted status ID");
        Check.Equal(586u, ReadUInt32(packet, 16), "second sorted status ID");
        Check.Equal(1503u, ReadUInt32(packet, 20), "third sorted status ID");
        Check.Equal(1504u, ReadUInt32(packet, 24), "fourth sorted status ID");
        Check.Equal(28_800u, ReadUInt32(packet, 92), "first status remaining time");
        Check.Equal(28_800u, ReadUInt32(packet, 96), "second status remaining time");
        Check.Equal(uint.MaxValue, ReadUInt32(packet, 100), "permanent status remaining-time sentinel");
        Check.Equal(43_200u, ReadUInt32(packet, 104), "area status remaining time");
        Check.Equal(0u, ReadUInt32(packet, 28), "unused status ID slot remains zero");
        Check.Equal(0u, ReadUInt32(packet, 108), "unused status time slot remains zero");
        Check.Equal(character.MaxHp, ReadInt32(packet, 172), "full StatusData max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 176), "full StatusData max MP");
        Check.Equal(PlayerRecoveryCatalog.GetTotalHp(character), ReadInt32(packet, 180), "full StatusData HP recovery");
        Check.Equal(PlayerRecoveryCatalog.GetTotalMp(character), ReadInt32(packet, 184), "full StatusData MP recovery");
        Check.Equal(character.CalculatedStats!.PhysicalAttack, ReadInt32(packet, 188), "full StatusData physical attack");
        Check.Equal(character.CalculatedStats.PhysicalDefense, ReadInt32(packet, 192), "full StatusData physical defense");
        Check.Equal(character.CalculatedStats.MagicAttack, ReadInt32(packet, 196), "full StatusData magic attack");
        Check.Equal(character.CalculatedStats.MagicDefense, ReadInt32(packet, 200), "full StatusData magic defense");
        Check.Equal(character.CalculatedStats.Hit, ReadInt32(packet, 204), "full StatusData hit");
        Check.Equal(character.CalculatedStats.Dodge, ReadInt32(packet, 208), "full StatusData dodge");
        Check.Equal(character.CalculatedStats.Critical, ReadInt32(packet, 212), "full StatusData critical");
        Check.Equal(character.CalculatedStats.CriticalResistance, ReadInt32(packet, 216), "full StatusData critical resistance");
        Check.Equal(0.1234f, ReadSingle(packet, 220), "full StatusData physical damage bonus");
        Check.Equal(0.2345f, ReadSingle(packet, 224), "full StatusData magic damage bonus");
        Check.Equal(character.CalculatedStats.DamageAbsorb, ReadInt32(packet, 228), "full StatusData damage absorb");
        Check.Equal(0.3456f, ReadSingle(packet, 232), "full StatusData received-cure bonus");
        Check.Equal(0.4567f, ReadSingle(packet, 236), "full StatusData cure bonus");
        Check.Equal(0u, ReadUInt32(packet, 240), "unimplemented status-hit field remains zero");
        Check.Equal(6.2f, ReadSingle(packet, 300), "status aggregate fighter-EXP bonus");
        Check.Equal(1f, ReadSingle(packet, 324), "status movement-speed baseline");
        Check.Equal(0u, ReadUInt32(packet, 336), "unused final StatusData field remains zero");

        var localPacket = PacketBuilder.PlayerStatusEffects(
            character,
            [],
            ClientStatusAggregate.Empty);
        Check.Equal(0x1448u, ReadUInt32(localPacket, 4), "status-effect local player object ID");
        Check.Equal(0u, ReadUInt32(localPacket, 8), "empty status-effect count");

        var bootstrapPacket = PacketBuilder.PlayerExtendedStatus(character);
        Check.Equal(340, bootstrapPacket.Length, "legacy extended-status entry point uses canonical length");
        Check.Equal((ushort)10167, ReadUInt16(bootstrapPacket, 2), "legacy extended-status entry point uses canonical opcode");
        Check.Equal(character.MaxHp, ReadInt32(bootstrapPacket, 172), "legacy extended-status entry point includes full data");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects(
                character,
                Enumerable.Range(1, 21)
                    .Select(static id => new ClientStatusEffect((uint)id, 1))
                    .ToArray(),
                ClientStatusAggregate.Empty),
            "status-effect packet rejects more than twenty entries");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects(
                character,
                [],
                new ClientStatusAggregate(0, 0, float.NaN)),
            "status-effect packet rejects non-finite aggregate EXP");

        return Task.CompletedTask;
    }

    private static Task CheckPostEnterBootstrapGateAsync()
    {
        Check.Equal((ushort)10357, Opcodes.EnterUiReady, "final enter/UI-ready opcode");
        Check.Equal(nameof(Opcodes.EnterUiReady), Opcodes.Name(Opcodes.EnterUiReady), "final enter/UI-ready opcode name");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: false,
                playerDetailSent: true,
                enterUiReadyReceived: true),
            "bootstrap waits for ClientReady");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: false,
                enterUiReadyReceived: true),
            "bootstrap waits for PlayerDetail");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: true,
                enterUiReadyReceived: false),
            "bootstrap waits for the final UI-ready signal");
        Check.True(
            GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: true,
                enterUiReadyReceived: true),
            "bootstrap starts after every enter signal");

        return Task.CompletedTask;
    }

    private static Task CheckCapturedAcceptedQuestReplayExclusionAsync()
    {
        const int acceptedQuestRecordLength = 0x2A8;
        const int acceptedQuestCount = 3;
        var acceptedQuestSnapshot = new byte[8 + acceptedQuestCount * acceptedQuestRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            acceptedQuestSnapshot,
            checked((ushort)acceptedQuestSnapshot.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            acceptedQuestSnapshot.AsSpan(2),
            Opcodes.PlayerAcceptedQuests);
        BinaryPrimitives.WriteInt32LittleEndian(
            acceptedQuestSnapshot.AsSpan(4),
            acceptedQuestCount);

        Check.Equal(2048, acceptedQuestSnapshot.Length, "three-record accepted-quest snapshot length");
        Check.Equal((ushort)10090, Opcodes.PlayerAcceptedQuests, "native MSG_PLAYER_ACCEPTQUESTS opcode");
        Check.Equal(
            nameof(Opcodes.PlayerAcceptedQuests),
            Opcodes.Name(Opcodes.PlayerAcceptedQuests),
            "accepted-quest opcode name");
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(acceptedQuestSnapshot),
            "captured accepted-quest snapshots are never replayed during post-enter bootstrap");

        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(ReadOnlySpan<byte>.Empty),
            "empty captured packet is rejected");
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(new byte[3]),
            "captured packet shorter than its frame header is rejected");
        var malformedFrame = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(malformedFrame, 11);
        BinaryPrimitives.WriteUInt16LittleEndian(malformedFrame.AsSpan(2), Opcodes.UiHeartbeat);
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(malformedFrame),
            "captured packet with a mismatched declared length is rejected");

        var benignFrame = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(benignFrame, checked((ushort)benignFrame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(benignFrame.AsSpan(2), Opcodes.UiHeartbeat);
        Check.True(
            GameClientHandler.CanReplayCapturedPostEnterPacket(benignFrame),
            "valid framed non-quest packet remains eligible for replay");

        return Task.CompletedTask;
    }


    private static Task CheckOccupiedGhostSlotBagMoveParsingAsync()
    {
        // Live account-13 request from 2026-07-21 00:30:35 UTC. The client
        // still believed bag slot 18 contained an equipped weapon, so its
        // full StorageItem request carried an opaque pointer at bytes 12..15
        // instead of the FFFF/FFFF markers used for an ordinary empty slot.
        var occupiedGhostMove = Convert.FromHexString(
            "F0DB7658000001000000120074AC3E67" +
            "4000000038000000282F9A2200000000" +
            "65AE3E670400000001000000E4F71A00" +
            "01000000000000000828291400000100" +
            "34F41A004000000040000000");
        Check.Equal(76, occupiedGhostMove.Length, "captured occupied-slot request payload length");
        Check.True(
            GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove,
                out var capturedSource,
                out var capturedDestination),
            "captured occupied ghost-slot move parses");
        Check.Equal(1, capturedSource, "captured occupied ghost-slot source");
        Check.Equal(18, capturedDestination, "captured occupied ghost-slot destination");

        var ordinaryShortMove = Convert.FromHexString(
            "000000000000010000001200FFFFFFFF");
        Check.True(
            GameClientHandler.TryReadStorageItemKitBagMove(
                ordinaryShortMove,
                out var ordinarySource,
                out var ordinaryDestination),
            "short ordinary move retains strict marker parsing");
        Check.Equal(1, ordinarySource, "short ordinary move source");
        Check.Equal(18, ordinaryDestination, "short ordinary move destination");

        var opaqueShortMove = occupiedGhostMove.AsSpan(0, 16).ToArray();
        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(opaqueShortMove, out _, out _),
            "short move rejects opaque occupied-slot markers");
        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove.AsSpan(0, occupiedGhostMove.Length - 1),
                out _,
                out _),
            "truncated full request rejects opaque occupied-slot markers");

        foreach (var (offset, invalidValue, label) in new (int Offset, ushort InvalidValue, string Label)[]
                 {
                     (4, 4, "source page"),
                     (6, 24, "source index"),
                     (8, 4, "destination page"),
                     (10, 24, "destination index")
                 })
        {
            var malformed = occupiedGhostMove.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(offset, 2), invalidValue);
            Check.True(
                !GameClientHandler.TryReadStorageItemKitBagMove(malformed, out _, out _),
                $"full occupied-slot move rejects out-of-bounds {label}");
        }

        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove.AsSpan(0, 15),
                out _,
                out _),
            "malformed undersized move is rejected");

        return Task.CompletedTask;
    }

    private static async Task CheckBagItemDeletionAsync()
    {
        // Live client request after dragging bag slots 0 and 1 onto the ground
        // and accepting both confirmation dialogs. Destination page/index -1/-1
        // is the delete sentinel; trailing request bytes are unrelated stack data.
        var slotZeroPayload = Convert.FromHexString(
            "48F91A0000000000FFFFFFFF070000000800000009000000");
        Check.True(
            GameClientHandler.TryReadStorageItemDelete(slotZeroPayload, out var slotZero),
            "captured ground-drop request parses");
        Check.Equal(0, slotZero, "captured ground-drop source slot zero");

        var slotOnePayload = Convert.FromHexString(
            "48F91A0000000100FFFFFFFF070000000800000009000000");
        Check.True(
            GameClientHandler.TryReadStorageItemDelete(slotOnePayload, out var slotOne),
            "second captured ground-drop request parses");
        Check.Equal(1, slotOne, "captured ground-drop source slot one");

        var ordinaryMovePayload = Convert.FromHexString(
            "48F91A000000010000000200FFFFFFFF");
        Check.True(
            !GameClientHandler.TryReadStorageItemDelete(ordinaryMovePayload, out _),
            "ordinary bag move is not parsed as deletion");

        var acknowledgement = PacketBuilder.StorageItemKitBagDelete(sourceSlot: 25);
        Check.Equal(16, acknowledgement.Length, "bag delete acknowledgement length");
        Check.Equal((ushort)10052, ReadUInt16(acknowledgement, 2), "bag delete acknowledgement opcode");
        Check.Equal(0x1448u, ReadUInt32(acknowledgement, 4), "bag delete local player object ID");
        Check.Equal((ushort)1, ReadUInt16(acknowledgement, 8), "bag delete source page");
        Check.Equal((ushort)1, ReadUInt16(acknowledgement, 10), "bag delete source index");
        Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 12), "bag delete destination page sentinel");
        Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 14), "bag delete destination index sentinel");

        Check.Equal(0u, KitBagSlots.GetItemId(GameDefaults.EmptyKitBag, 0), "empty bag has no slot-zero potion");
        Check.Equal(0u, KitBagSlots.GetItemId(GameDefaults.EmptyKitBag, 1), "empty bag has no slot-one potion");
        Check.Equal(4000u, KitBagSlots.GetItemId(GameDefaults.StarterKitBag, 0), "starter bag has its HP potion");
        Check.Equal(4030u, KitBagSlots.GetItemId(GameDefaults.StarterKitBag, 1), "starter bag has its MP potion");

        var blankBagMutation = KitBagSlots.SetSlot(
            string.Empty,
            2,
            "[4230,,,,,,1,1,1,1,0]");
        Check.Equal(0u, KitBagSlots.GetItemId(blankBagMutation, 0), "blank mutation fallback does not grant HP potion");
        Check.Equal(0u, KitBagSlots.GetItemId(blankBagMutation, 1), "blank mutation fallback does not grant MP potion");
        Check.Equal(4230u, KitBagSlots.GetItemId(blankBagMutation, 2), "blank mutation writes only the requested slot");

        var blankCharacter = new GameCharacter();
        var blankDetails = PacketBuilder.KitBagDetailPages(blankCharacter);
        var blankIndexes = PacketBuilder.KitBagSlotIndexes(blankCharacter);
        Check.Equal(uint.MaxValue, ReadUInt32(blankDetails[0], 24), "blank hydration serializes an empty first detail slot");
        Check.Equal(-1, ReadInt32(blankIndexes[0], 20), "blank hydration serializes an empty first slot index");

        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-bag-delete-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            var ownerId = 0;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var owner = await store.LoginOrCreateAccountAsync("bag-delete-owner", "");
                var other = await store.LoginOrCreateAccountAsync("bag-delete-other", "");
                var character = await store.CreateCharacterAsync(
                    owner.Id,
                    new GameCharacter { Name = "BagDeleteHero" });
                ownerId = owner.Id;

                Check.Equal(4000u, KitBagSlots.GetItemId(character.KitBag, 0), "new character receives starter HP potion once");
                Check.Equal(4030u, KitBagSlots.GetItemId(character.KitBag, 1), "new character receives starter MP potion once");

                var unauthorized = await store.DeleteKitBagItemAsync(other.Id, character.Id, 1);
                Check.True(unauthorized is null, "different account cannot delete bag item");

                var firstDelete = await store.DeleteKitBagItemAsync(owner.Id, character.Id, 0)
                    ?? throw new InvalidOperationException("owner HP potion deletion returned no character");
                Check.Equal(0u, KitBagSlots.GetItemId(firstDelete.KitBag, 0), "deleted HP potion slot is empty");
                Check.Equal(4030u, KitBagSlots.GetItemId(firstDelete.KitBag, 1), "neighboring MP potion is unchanged");

                var secondDelete = await store.DeleteKitBagItemAsync(owner.Id, character.Id, 1)
                    ?? throw new InvalidOperationException("owner MP potion deletion returned no character");
                Check.Equal(0u, KitBagSlots.GetItemId(secondDelete.KitBag, 0), "HP potion remains deleted");
                Check.Equal(0u, KitBagSlots.GetItemId(secondDelete.KitBag, 1), "MP potion is deleted");

                await store.EnsureSeedDataAsync();
                var reseeded = await store.GetFirstCharacterAsync(owner.Id)
                    ?? throw new InvalidOperationException("bag deletion fixture was not reloaded after seed check");
                Check.Equal(0u, KitBagSlots.GetItemId(reseeded.KitBag, 0), "seed check does not restore HP potion");
                Check.Equal(0u, KitBagSlots.GetItemId(reseeded.KitBag, 1), "seed check does not restore MP potion");
            }

            await using var restartedStore = new JsonGameStore(dataPath);
            await restartedStore.EnsureSeedDataAsync();
            var restarted = await restartedStore.GetFirstCharacterAsync(ownerId)
                ?? throw new InvalidOperationException("bag deletion fixture was not reloaded after store restart");
            Check.Equal(0u, KitBagSlots.GetItemId(restarted.KitBag, 0), "restart does not restore HP potion");
            Check.Equal(0u, KitBagSlots.GetItemId(restarted.KitBag, 1), "restart does not restore MP potion");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckDeveloperForgingMaterialCommandAsync()
    {
        var expectedCatalog = new (uint Id, string Name, string Type, short StackCap)[]
        {
            (4200, "Level 1 Ruby", "consume item", 99),
            (4201, "Level 2 Ruby", "consume item", 99),
            (4202, "Level 3 Ruby", "consume item", 99),
            (4210, "Level 1 Sapphire", "consume item", 99),
            (4211, "Level 2 Sapphire", "consume item", 99),
            (4212, "Level 3 Sapphire", "consume item", 99),
            (4213, "Level 4 Sapphire", "consume item", 99),
            (4214, "Level 4 Sapphire Pieces", "consume item", 99),
            (4215, "Level 5 Sapphire", "consume item", 99),
            (4216, "Level 5 Sapphire Pieces", "consume item", 99),
            (4220, "Level 1 Emerald", "consume item", 99),
            (4221, "Level 2 Emerald", "consume item", 99),
            (4222, "Level 3 Emerald", "consume item", 99),
            (4223, "Level 4 Emerald", "consume item", 99),
            (4224, "Level 4 Emerald Pieces", "consume item", 99),
            (4225, "Level 5 Emerald", "consume item", 99),
            (4226, "Level 5 Emerald Pieces", "consume item", 99),
            (4230, "Level 1 Crystal", "consume item", 99),
            (4231, "Level 2 Crystal", "consume item", 99),
            (4232, "Level 3 Crystal", "consume item", 99),
            (4233, "Level 4 Crystal", "consume item", 99),
            (4234, "Level 5 Crystal", "consume item", 99),
            (4235, "Level 5 Crystal Pieces", "consume item", 99)
        };
        Check.Equal(expectedCatalog.Length, ForgingMaterialCatalog.All.Count, "forging-material catalog count");
        foreach (var expected in expectedCatalog)
        {
            Check.True(
                ForgingMaterialCatalog.TryResolve(expected.Id, out var material),
                $"catalogued material {expected.Id} resolves");
            Check.Equal(expected.Name, material.DisplayName, $"catalogued material {expected.Id} display name");
            Check.Equal(expected.Type, material.ItemType, $"catalogued material {expected.Id} item type");
            Check.Equal(expected.StackCap, material.StackCap, $"catalogued material {expected.Id} stack cap");

            var itemTemplate = material.ToItemTemplateSeed();
            Check.Equal(checked((int)expected.Id), itemTemplate.Id, $"native material {expected.Id} template ID");
            Check.Equal(expected.Type, itemTemplate.Kind, $"native material {expected.Id} template kind");
            Check.Equal((short)0, itemTemplate.EquipmentSlot, $"native material {expected.Id} is not equipable");
        }

        var levelTwoCrystalTemplate = ForgingMaterialCatalog.All
            .Single(material => material.ItemId == 4231)
            .ToItemTemplateSeed();
        var levelTwoCrystalStats = JsonNode.Parse(levelTwoCrystalTemplate.StatsJson)
            ?? throw new InvalidOperationException("Level 2 Crystal template stats did not parse.");
        Check.Equal("2", levelTwoCrystalStats["Random"]?.GetValue<string>() ?? string.Empty, "Level 2 Crystal native random table");
        Check.Equal("201,201", levelTwoCrystalStats["Distribution"]?.GetValue<string>() ?? string.Empty, "Level 2 Crystal native distribution");
        Check.Equal("99", levelTwoCrystalStats["Overlap"]?.GetValue<string>() ?? string.Empty, "forging material native stack cap metadata");
        Check.Equal(
            (short)0,
            ForgingMaterialCatalog.All.Single(material => material.ItemId == 4230).GrantedBound,
            "Level 1 Crystal grant preserves its native unbound state");
        Check.Equal(
            (short)1,
            ForgingMaterialCatalog.All.Single(material => material.ItemId == 4231).GrantedBound,
            "Level 2 Crystal grant preserves its native bound state");

        Check.True(
            !ForgingMaterialCatalog.TryResolve("ruby4", out _),
            "nonexistent Ruby level 4 is not synthesized");
        Check.True(
            ForgingMaterialCatalog.TryResolve("crystal5", out var crystalFive) &&
            crystalFive.ItemId == 4234 && !crystalFive.IsPiece,
            "locally authored Crystal level 5 resolves independently");
        Check.True(
            ForgingMaterialCatalog.TryResolve("sapphire5", out var sapphireFive) &&
            sapphireFive.ItemId == 4215 && !sapphireFive.IsPiece,
            "locally authored Sapphire level 5 does not alias level-4 pieces");
        Check.True(
            ForgingMaterialCatalog.TryResolve("emerald5", out var emeraldFive) &&
            emeraldFive.ItemId == 4225 && !emeraldFive.IsPiece,
            "locally authored Emerald level 5 does not alias level-4 pieces");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            crystalFive.Texture,
            "Level 5 Crystal uses the dedicated icon atlas");
        Check.Equal("0,0", crystalFive.Icon, "Level 5 Crystal icon cell");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            sapphireFive.Texture,
            "Level 5 Sapphire uses the dedicated icon atlas");
        Check.Equal("36,0", sapphireFive.Icon, "Level 5 Sapphire icon cell");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            emeraldFive.Texture,
            "Level 5 Emerald uses the dedicated icon atlas");
        Check.Equal("72,0", emeraldFive.Icon, "Level 5 Emerald icon cell");
        Check.True(
            ForgingMaterialCatalog.TryResolve("sapphire4pieces", out var sapphirePieces) &&
            sapphirePieces.ItemId == 4214 && sapphirePieces.IsPiece,
            "native Sapphire pieces have a distinct alias");
        Check.Equal(
            ForgingMaterialCatalog.All.Count +
                GearEnhancementMaterialCatalog.All.Count +
                GearMentorMaterialCatalog.AttributeDusts.Count,
            DeveloperGrantMaterialCatalog.All.Count,
            "developer grant catalog combines forging, enhancement, and Gear Mentor materials");
        Check.True(
            DeveloperGrantMaterialCatalog.TryResolve(9930, out var strengthStoneDefinition) &&
            strengthStoneDefinition.DisplayName == "Strength Stone" &&
            strengthStoneDefinition.StackCap == 99 &&
            strengthStoneDefinition.GrantedBound == 0,
            "gear-enhancement material grant policy is resolved by the unified server catalog");

        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add 4233 17",
                out var numericRequest,
                out _) &&
            numericRequest is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4233,
                Quantity: 17
            },
            "developer item command retains the legacy alias for direct protocol clients");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag confirm",
                out var clearBagRequest,
                out _) &&
            clearBagRequest is
            {
                Operation: DeveloperItemOperation.ClearBag,
                Material: null,
                Quantity: 0
            },
            "developer item command requires and accepts the explicit clear-bag confirmation");
        Check.True(
            DeveloperItemCommand.TryParse(
                "test2:/****** clearbag confirm",
                out var maskedClearBagRequest,
                out _) &&
            maskedClearBagRequest is { Operation: DeveloperItemOperation.ClearBag },
            "stock-client masking and sender prefixes preserve the guarded clear-bag command");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag",
                out var unconfirmedClearBagRequest,
                out var unconfirmedClearBagError) &&
            unconfirmedClearBagRequest is null &&
            unconfirmedClearBagError.Contains("clearbag confirm", StringComparison.Ordinal),
            "clear-bag command without confirmation is consumed but rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag yes",
                out var wronglyConfirmedClearBagRequest,
                out _) &&
            wronglyConfirmedClearBagRequest is null,
            "clear-bag command rejects the wrong confirmation token");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag confirm now",
                out var overlongClearBagRequest,
                out _) &&
            overlongClearBagRequest is null,
            "clear-bag command rejects trailing arguments after confirmation");
        Check.True(
            DeveloperItemCommand.TryParse(
                "ProtocolHero:/item add crystal1 99",
                out var clientSafeRequest,
                out _) &&
            clientSafeRequest is { Material.ItemId: 4230, Quantity: 99 },
            "developer item command accepts the stock-client-safe alias after a sender prefix");
        Check.True(
            DeveloperItemCommand.TryParse(
                "test2:/****** add crystal1 99",
                out var maskedLegacyRequest,
                out _) &&
            maskedLegacyRequest is { Material.ItemId: 4230, Quantity: 99 },
            "developer item command recognizes the stock client's masked legacy prefix");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add crystal 2 99",
                out var splitAliasRequest,
                out _) &&
            splitAliasRequest is { Material.ItemId: 4231, Quantity: 99 },
            "developer item command accepts an unambiguous material and level alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "ProtocolHero:/gmitem add emerald-l4",
                out var prefixedRequest,
                out _) &&
            prefixedRequest is { Material.ItemId: 4223, Quantity: 1 },
            "developer item command tolerates a captured sender prefix and defaults quantity to one");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add sapphire4pieces 5",
                out var pieceRequest,
                out _) &&
            pieceRequest is { Material.ItemId: 4214, Quantity: 5 },
            "developer item command accepts the distinct native pieces alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add emerald5 12",
                out var levelFiveRequest,
                out _) &&
            levelFiveRequest is { Material.ItemId: 4225, Quantity: 12 },
            "developer item command accepts locally authored level-5 material aliases");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add 9930 7",
                out var numericEnhancementRequest,
                out _) &&
            numericEnhancementRequest is { Material.ItemId: 9930, Quantity: 7 },
            "developer item command accepts an allowlisted gear-enhancement numeric ID");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add strengthstone 8",
                out var strengthStoneRequest,
                out _) &&
            strengthStoneRequest is { Material.ItemId: 9930, Quantity: 8 },
            "developer item command resolves the Strength Stone alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add quartzplate1 9",
                out var quartzPlateRequest,
                out _) &&
            quartzPlateRequest is { Material.ItemId: 9960, Quantity: 9 },
            "developer item command resolves the Quartz Plate 1 alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add flamespark 10",
                out var flameSparkRequest,
                out _) &&
            flameSparkRequest is { Material.ItemId: 9990, Quantity: 10 },
            "developer item command resolves the Flame Spark alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add watergrain 11",
                out var waterGrainRequest,
                out _) &&
            waterGrainRequest is { Material.ItemId: 9991, Quantity: 11 },
            "developer item command resolves the Water Grain alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add 999999 1",
                out var arbitraryRequest,
                out var arbitraryError) &&
            arbitraryRequest is null && arbitraryError.Contains("not an allowlisted"),
            "arbitrary numeric item IDs are consumed but rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add 9939 1",
                out var catalogGapRequest,
                out _) && catalogGapRequest is null,
            "the deliberately absent gear-enhancement material ID remains rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/gmitem add crystal1 {DeveloperItemCommand.MaximumQuantity + 1}",
                out var oversizedRequest,
                out _) && oversizedRequest is null,
            "developer item command enforces the strict quantity maximum");
        Check.True(
            !DeveloperItemCommand.TryParse("ordinary map chat", out _, out _),
            "ordinary chat is not consumed as a developer command");

        var talkText = "/item add ruby1 3";
        var talkTextBytes = Encoding.Unicode.GetBytes(talkText);
        var talkPayload = new byte[12 + talkTextBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            talkPayload.AsSpan(4, 4),
            checked((uint)talkTextBytes.Length + sizeof(ushort)));
        talkTextBytes.CopyTo(talkPayload.AsSpan(12));
        Check.True(
            GameClientHandler.TryReadTalkText(talkPayload, out var parsedTalkText) &&
            parsedTalkText == talkText,
            "captured Talk payload shape yields the developer command text");

        var capturedMaskedTalkPayload = Convert.FromHexString(
            "481400003C000000100500D5" +
            "740065007300740032003A002F002A002A002A002A002A002A002000" +
            "61006400640020006300720079007300740061006C0031002000390039000000");
        Check.True(
            GameClientHandler.TryReadTalkText(capturedMaskedTalkPayload, out var maskedTalkText) &&
            maskedTalkText == "test2:/****** add crystal1 99" &&
            DeveloperItemCommand.TryParse(maskedTalkText, out var capturedMaskedRequest, out _) &&
            capturedMaskedRequest is { Material.ItemId: 4230, Quantity: 99 },
            "live masked /gmitem Talk payload still reaches the guarded grant command");

        BinaryPrimitives.WriteUInt32LittleEndian(talkPayload.AsSpan(4, 4), uint.MaxValue);
        Check.True(
            !GameClientHandler.TryReadTalkText(talkPayload, out _),
            "malformed Talk text length is rejected");

        var disabledAccess = new DeveloperCommandOptions
        {
            Enabled = false,
            AllowedAccountIds = [3, 7, 13, 347]
        };
        Check.True(!disabledAccess.Allows(3), "developer command defaults can fail closed");
        var allowlistedAccess = new DeveloperCommandOptions
        {
            Enabled = true,
            AllowedAccountIds = [3, 7, 13, 347]
        };
        Check.True(allowlistedAccess.Allows(13), "exact configured account is authorized");
        Check.True(!allowlistedAccess.Allows(14), "unlisted neighboring account is denied");

        var partialStackBag = KitBagSlots.SetSlot(
            GameDefaults.StarterKitBag,
            2,
            "[4230,,,,,,1,1,1,98,0]");
        Check.True(
            KitBagItemGrantPlanner.TryAdd(
                partialStackBag,
                4230,
                quantity: 101,
                stackCap: 99,
                bound: 1,
                out var plannedBag),
            "bag planner can fill one partial stack and allocate additional stacks");
        Check.Equal((short)99, KitBagSlots.GetItem(plannedBag, 2).Stack, "partial native stack is filled first");
        Check.Equal((short)99, KitBagSlots.GetItem(plannedBag, 3).Stack, "new native stack respects cap");
        Check.Equal((short)1, KitBagSlots.GetItem(plannedBag, 4).Stack, "remaining quantity uses the next empty slot");

        var nearlyFullBag = GameDefaults.StarterKitBag;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            nearlyFullBag = KitBagSlots.SetSlot(
                nearlyFullBag,
                slot,
                slot == 0
                    ? "[4230,,,,,,1,1,1,98,0]"
                    : "[4000,,,,,,1,1,1,99,0]");
        }

        Check.True(
            !KitBagItemGrantPlanner.TryAdd(
                nearlyFullBag,
                4230,
                quantity: 2,
                stackCap: 99,
                bound: 1,
                out var rejectedBag),
            "bag planner rejects a quantity that cannot fully fit");
        Check.Equal(nearlyFullBag, rejectedBag, "failed capacity plan is atomic and leaves the bag byte-for-byte unchanged");

        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-developer-item-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("developer-item-owner", "");
            var other = await store.LoginOrCreateAccountAsync("developer-item-other", "");
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "DeveloperItemHero" });

            var unauthorized = await store.AddForgingMaterialAsync(
                other.Id,
                character.Id,
                4230,
                1);
            Check.True(
                unauthorized.Status == KitBagItemGrantStatus.CharacterNotFound,
                "different account cannot grant into another character bag");
            var unauthorizedEnhancement = await store.AddForgingMaterialAsync(
                other.Id,
                character.Id,
                9990,
                1);
            Check.True(
                unauthorizedEnhancement.Status == KitBagItemGrantStatus.CharacterNotFound,
                "different account cannot grant a gear-enhancement material into another character bag");

            var granted = await store.AddForgingMaterialAsync(
                owner.Id,
                character.Id,
                4230,
                150);
            Check.True(granted.Added && granted.Character is not null, "owner material grant succeeds atomically");
            Check.Equal(4230u, KitBagSlots.GetItemId(granted.Character!.KitBag, 2), "grant uses first empty slot");
            Check.Equal((short)99, KitBagSlots.GetItem(granted.Character.KitBag, 2).Stack, "persisted first stack uses native cap");
            Check.Equal((short)0, KitBagSlots.GetItem(granted.Character.KitBag, 2).Bound, "native unbound material remains unbound");
            Check.Equal((short)51, KitBagSlots.GetItem(granted.Character.KitBag, 3).Stack, "persisted second stack has remainder");

            var detailPages = PacketBuilder.KitBagDetailPages(granted.Character);
            Check.Equal(8, detailPages.Length, "bag refresh contains all detail half-pages");
            var slotTwoRecordOffset = 24 + (2 * 72);
            Check.Equal(4230u, ReadUInt32(detailPages[0], slotTwoRecordOffset), "bag refresh details include granted item");
            Check.Equal((byte)99, detailPages[0][slotTwoRecordOffset + 27], "bag refresh details include granted stack");
            var slotIndexes = PacketBuilder.KitBagSlotIndexes(granted.Character);
            Check.Equal(96, slotIndexes.Length, "bag refresh contains every slot index");
            Check.Equal(4230u, ReadUInt32(slotIndexes[2], 20), "bag refresh slot index includes granted item");

            var reloaded = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer item fixture was not reloaded");
            Check.Equal((short)99, KitBagSlots.GetItem(reloaded.KitBag, 2).Stack, "first material stack persists after reload");
            Check.Equal((short)51, KitBagSlots.GetItem(reloaded.KitBag, 3).Stack, "second material stack persists after reload");

            var enhancementGranted = await store.AddForgingMaterialAsync(
                owner.Id,
                character.Id,
                9930,
                100);
            Check.True(
                enhancementGranted.Added && enhancementGranted.Character is not null,
                "owner gear-enhancement material grant succeeds through the same authoritative store path");
            Check.Equal(
                9930u,
                KitBagSlots.GetItemId(enhancementGranted.Character!.KitBag, 4),
                "gear-enhancement grant uses the next authoritative empty slot");
            Check.Equal(
                (short)99,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 4).Stack,
                "gear-enhancement grant obeys its server-owned stack cap");
            Check.Equal(
                (short)0,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 4).Bound,
                "gear-enhancement grant obeys its server-owned native binding state");
            Check.Equal(
                (short)1,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 5).Stack,
                "gear-enhancement grant allocates its remainder atomically");

            var rejectedArbitraryId = false;
            try
            {
                await store.AddForgingMaterialAsync(owner.Id, character.Id, 999999, 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedArbitraryId = true;
            }

            Check.True(rejectedArbitraryId, "store rejects IDs outside the unified developer material allowlist");

            var beforeClear = enhancementGranted.Character!;
            var bagBeforeClear = beforeClear.KitBag;
            var equipmentBeforeClear = beforeClear.Equipment;
            var occupiedSlotsBeforeClear = Enumerable
                .Range(0, KitBagItemGrantPlanner.SlotCount)
                .Where(slot => !KitBagSlots.GetItem(beforeClear.KitBag, slot).IsEmpty)
                .ToArray();
            var deletionAcknowledgements =
                PacketBuilder.KitBagDeletionAcknowledgements(beforeClear);
            Check.Equal(
                occupiedSlotsBeforeClear.Length,
                deletionAcknowledgements.Length,
                "bulk clear emits one native deletion acknowledgement per occupied client slot");
            for (var index = 0; index < deletionAcknowledgements.Length; index++)
            {
                var expectedSlot = occupiedSlotsBeforeClear[index];
                var expectedPage = Math.DivRem(expectedSlot, 24, out var expectedPageIndex);
                var acknowledgement = deletionAcknowledgements[index];
                Check.Equal((ushort)10052, ReadUInt16(acknowledgement, 2), "bulk clear uses native deletion opcode");
                Check.Equal((ushort)expectedPage, ReadUInt16(acknowledgement, 8), "bulk clear deletion source page");
                Check.Equal((ushort)expectedPageIndex, ReadUInt16(acknowledgement, 10), "bulk clear deletion source index");
                Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 12), "bulk clear deletion destination page sentinel");
                Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 14), "bulk clear deletion destination index sentinel");
            }

            var skillsBeforeClear = string.Join(
                ',',
                (await store.GetSkillStatesAsync(owner.Id, character.Id))
                    .OrderBy(skill => skill.SkillId)
                    .Select(skill => $"{skill.SkillId}:{skill.Level}"));

            var unauthorizedClear = await store.ClearKitBagAsync(other.Id, character.Id);
            Check.True(
                unauthorizedClear is null,
                "different account cannot clear another character's bag");
            var afterUnauthorizedClear = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer clear-bag fixture was not reloaded after denied clear");
            Check.Equal(
                bagBeforeClear,
                afterUnauthorizedClear.KitBag,
                "denied clear leaves the authoritative bag byte-for-byte unchanged");

            var cleared = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("owner clear-bag operation unexpectedly failed");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                cleared.KitBag,
                "owner clear replaces only the kit bag with its canonical empty representation");
            Check.Equal(
                0,
                PacketBuilder.KitBagDeletionAcknowledgements(cleared).Length,
                "an already-empty bag produces no redundant client deletion acknowledgements");
            for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
            {
                Check.True(
                    KitBagSlots.GetItem(cleared.KitBag, slot).IsEmpty,
                    $"clear-bag operation empties authoritative slot {slot}");
            }

            Check.Equal(
                equipmentBeforeClear,
                cleared.Equipment,
                "clear-bag operation preserves equipped gear byte-for-byte");
            Check.Equal(beforeClear.Silver, cleared.Silver, "clear-bag operation preserves silver");
            Check.Equal(beforeClear.Gold, cleared.Gold, "clear-bag operation preserves gold");
            Check.Equal(beforeClear.Level, cleared.Level, "clear-bag operation preserves level");
            Check.Equal(beforeClear.Experience, cleared.Experience, "clear-bag operation preserves experience");
            Check.Equal(beforeClear.TalentPoints, cleared.TalentPoints, "clear-bag operation preserves talent points");
            Check.Equal(beforeClear.CurrentMap, cleared.CurrentMap, "clear-bag operation preserves current map");
            Check.Equal(beforeClear.PositionX, cleared.PositionX, "clear-bag operation preserves X position");
            Check.Equal(beforeClear.PositionZ, cleared.PositionZ, "clear-bag operation preserves Z position");
            Check.Equal(beforeClear.CurrentHp, cleared.CurrentHp, "clear-bag operation preserves current HP");
            Check.Equal(beforeClear.CurrentMp, cleared.CurrentMp, "clear-bag operation preserves current MP");

            var skillsAfterClear = string.Join(
                ',',
                (await store.GetSkillStatesAsync(owner.Id, character.Id))
                    .OrderBy(skill => skill.SkillId)
                    .Select(skill => $"{skill.SkillId}:{skill.Level}"));
            Check.Equal(
                skillsBeforeClear,
                skillsAfterClear,
                "clear-bag operation preserves character skills");

            var clearedDetailPages = PacketBuilder.KitBagDetailPages(cleared);
            Check.Equal(8, clearedDetailPages.Length, "empty-bag refresh still contains all detail half-pages");
            foreach (var detailPage in clearedDetailPages)
            {
                for (var record = 0; record < 12; record++)
                {
                    Check.Equal(
                        uint.MaxValue,
                        ReadUInt32(detailPage, 24 + (record * 72)),
                        "empty-bag detail refresh reports the client's empty-item sentinel");
                }
            }

            var clearedSlotIndexes = PacketBuilder.KitBagSlotIndexes(cleared);
            Check.Equal(96, clearedSlotIndexes.Length, "empty-bag refresh still contains all slot indexes");
            foreach (var slotIndex in clearedSlotIndexes)
            {
                Check.Equal(
                    uint.MaxValue,
                    ReadUInt32(slotIndex, 20),
                    "empty-bag slot-index refresh reports the client's empty-item sentinel");
            }

            var clearedAgain = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("idempotent clear-bag operation unexpectedly failed");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                clearedAgain.KitBag,
                "clearing an already-empty bag is idempotent");

            await using var restartedStore = new JsonGameStore(dataPath);
            await restartedStore.EnsureSeedDataAsync();
            var restarted = await restartedStore.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer clear-bag fixture was not reloaded after restart");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                restarted.KitBag,
                "clear-bag state persists across a JSON-store restart without starter-item restoration");
            Check.Equal(
                equipmentBeforeClear,
                restarted.Equipment,
                "equipped gear remains unchanged after clear-bag restart persistence");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckGuardedEquipmentMoveAsync()
    {
        Check.True(
            EquipmentSlots.TryGetAuthoritativeSlot(1000, out var weaponSlot),
            "starter sword is present in the authoritative equipment catalog");
        Check.Equal(EquipmentSlots.Weapon, weaponSlot, "starter sword resolves to the weapon slot");
        Check.True(
            !EquipmentSlots.TryGetAuthoritativeSlot(4030, out _),
            "MP potion is absent from the authoritative equipment catalog");
        Check.Equal(-1, EquipmentSlots.ResolveSlotForItem(4030, -1), "unknown item has no weapon fallback");
        Check.Equal(
            EquipmentSlots.Weapon,
            EquipmentSlots.ResolveSlotForItem(1000, requestedSlot: -1),
            "right-click equip infers the authoritative weapon slot");
        Check.Equal(
            EquipmentSlots.Weapon,
            EquipmentSlots.ResolveSlotForItem(1000, EquipmentSlots.Weapon),
            "explicit drag accepts the authoritative weapon slot");
        Check.Equal(
            -1,
            EquipmentSlots.ResolveSlotForItem(1000, EquipmentSlots.Armor),
            "explicit drag rejects an incompatible equipment slot");
        Check.Equal(
            EquipmentSlots.Ring2,
            EquipmentSlots.ResolveSlotForItem(3200, EquipmentSlots.Ring2),
            "explicit ring drag accepts either ring slot");
        Check.Equal(
            -1,
            EquipmentSlots.ResolveSlotForItem(3200, EquipmentSlots.Weapon),
            "explicit ring drag rejects a non-ring slot");

        var rightClickEquipPayload = Enumerable.Repeat((byte)0xFF, 88).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(rightClickEquipPayload.AsSpan(0, 4), 7u);
        BinaryPrimitives.WriteUInt32LittleEndian(rightClickEquipPayload.AsSpan(4, 4), 5067u);
        BinaryPrimitives.WriteUInt16LittleEndian(rightClickEquipPayload.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(rightClickEquipPayload.AsSpan(10, 2), 0);
        Check.True(
            GameClientHandler.TryReadBreakItemEquip(rightClickEquipPayload, out var rightClickSourceSlot),
            "right-click 10051 request parses its bag source while an NPC is selected");
        Check.Equal(0, rightClickSourceSlot, "live sword request resolves authoritative bag slot zero");

        var dragEquipPayload = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(dragEquipPayload.AsSpan(4, 4), 5140u);
        BinaryPrimitives.WriteUInt16LittleEndian(dragEquipPayload.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dragEquipPayload.AsSpan(10, 2), 7);
        dragEquipPayload.AsSpan(12).Fill(0xA5);
        Check.True(
            GameClientHandler.TryReadBreakItemEquip(dragEquipPayload, out var dragSourceSlot),
            "drag/drop 10051 request ignores the selected NPC and uses the stable bag source");
        Check.Equal(55, dragSourceSlot, "drag/drop 10051 source uses packed page/index coordinates");

        var unequipPayload = new byte[76];
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(4, 2), EquipmentSlots.Shield);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(6, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(10, 2), 7);
        Check.True(
            GameClientHandler.TryReadStorageItemEquipmentBagTransfer(
                unequipPayload,
                out var parsedUnequipSlot,
                out var parsedUnequipDestination),
            "unequip parses an exact valid equipment-to-bag destination");
        Check.Equal(EquipmentSlots.Shield, parsedUnequipSlot, "unequip equipment source slot");
        Check.Equal(55, parsedUnequipDestination, "unequip exact bag destination");
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 4);
        Check.True(
            !GameClientHandler.TryReadStorageItemEquipmentBagTransfer(unequipPayload, out _, out _),
            "unequip rejects a destination outside the four bag pages");

        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 2);
        var transferAck = PacketBuilder.StorageItemEquipmentBagTransfer(
            EquipmentSlots.Shield,
            bagSlot: 55);
        Check.Equal(42, transferAck.Length, "equipment/bag transfer acknowledgement length");
        Check.Equal((ushort)10052, ReadUInt16(transferAck, 2), "equipment/bag transfer acknowledgement opcode");
        Check.Equal(0x1448u, ReadUInt32(transferAck, 4), "equipment/bag transfer local player object ID");
        Check.Equal((ushort)EquipmentSlots.Shield, ReadUInt16(transferAck, 8), "equipment descriptor is first");
        Check.Equal(ushort.MaxValue, ReadUInt16(transferAck, 10), "equipment descriptor sentinel");
        Check.Equal((ushort)2, ReadUInt16(transferAck, 12), "bag descriptor page is second");
        Check.Equal((ushort)7, ReadUInt16(transferAck, 14), "bag descriptor index is second");
        Check.Equal(-1, ReadInt32(transferAck, 16), "equipment/bag transfer move sentinel");

        var shieldEntry = EquipmentSlots.GetEntry(
            GameDefaults.DefaultEquipment(profession: 0),
            profession: 0,
            EquipmentSlots.Shield);
        var emptyTargetCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = GameDefaults.DefaultEquipment(profession: 0),
            KitBag = GameDefaults.EmptyKitBag
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(
                emptyTargetCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Unequip,
            "occupied equipment to an empty requested bag slot resolves as unequip");

        var occupiedTargetCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = GameDefaults.DefaultEquipment(profession: 0),
            KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 55, shieldEntry)
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(
                occupiedTargetCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Reject,
            "two occupied locations reject instead of swapping during unequip");

        var emptyEquipmentCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.ClearSlot(
                GameDefaults.DefaultEquipment(profession: 0),
                profession: 0,
                EquipmentSlots.Shield),
            KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 55, shieldEntry)
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(
                emptyEquipmentCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Equip,
            "compatible bag gear to an empty equipment slot resolves as explicit drag equip");
        var incompatibleEmptyEquipmentCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.ClearSlot(
                emptyEquipmentCharacter.Equipment,
                profession: 0,
                EquipmentSlots.Weapon),
            KitBag = emptyEquipmentCharacter.KitBag
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(
                incompatibleEmptyEquipmentCharacter,
                EquipmentSlots.Weapon,
                bagSlot: 55) == EquipmentBagTransferAction.Reject,
            "explicit drag rejects a bag item aimed at an incompatible empty equipment slot");

        var matchingCharacter = new GameCharacter { KitBag = GameDefaults.StarterKitBag };
        Check.True(
            GameClientHandler.MatchesCurrentKitBagItem(matchingCharacter, 0, 4000),
            "current bag item matches its authoritative slot");
        Check.True(
            !GameClientHandler.MatchesCurrentKitBagItem(matchingCharacter, 0, 0xFFFFFFFC),
            "client sentinel item ID cannot be cached as an equip source");

        var priorRingEquipment = GameDefaults.DefaultEquipment(0);
        priorRingEquipment = EquipmentSlots.SetSlot(
            priorRingEquipment,
            0,
            EquipmentSlots.Ring1,
            "[3200,,,,,,1,1,1,1,0]");
        var duplicateRingCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.SetSlot(
                priorRingEquipment,
                0,
                EquipmentSlots.Ring2,
                "[3200,,,,,,10,12,1,1,0]")
        };
        Check.Equal(
            EquipmentSlots.Ring2,
            GameClientHandler.ResolveEquippedSlotForAck(
                duplicateRingCharacter,
                priorRingEquipment,
                requestedEquipmentSlot: -1,
                itemIdHint: 3200),
            "inferred duplicate ring resolves to the slot changed by the equip");

        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-equipment-guard-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("equipment-guard-owner", "");
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "EquipmentGuardHero", Profession = 0 });

            var occupiedExplicitOwner = await store.LoginOrCreateAccountAsync(
                "equipment-occupied-explicit-owner",
                "");
            const string replacementShieldEntry = "[2000,,,,,,10,12,1,1,0]";
            var occupiedExplicitCharacter = await store.CreateCharacterAsync(
                occupiedExplicitOwner.Id,
                new GameCharacter
                {
                    Name = "OccupiedExplicitGuardHero",
                    Profession = 0,
                    Equipment = GameDefaults.DefaultEquipment(profession: 0),
                    KitBag = KitBagSlots.SetSlot(
                        GameDefaults.EmptyKitBag,
                        55,
                        replacementShieldEntry)
                });
            var rejectedOccupiedExplicit = await store.MoveKitBagToEquipmentAsync(
                occupiedExplicitOwner.Id,
                occupiedExplicitCharacter.Id,
                kitBagSlot: 55,
                requestedEquipmentSlot: EquipmentSlots.Shield,
                requireEmptyEquipmentSlot: true)
                ?? throw new InvalidOperationException("occupied explicit-equipment guard did not return the character");
            Check.Equal(
                shieldEntry,
                EquipmentSlots.GetEntry(
                    rejectedOccupiedExplicit.Equipment,
                    rejectedOccupiedExplicit.Profession,
                    EquipmentSlots.Shield),
                "explicit drag does not replace an occupied equipment slot");
            Check.Equal(
                replacementShieldEntry,
                KitBagSlots.GetEntry(rejectedOccupiedExplicit.KitBag, 55),
                "rejected explicit drag preserves its exact bag source");

            var rightClickReplacement = await store.MoveKitBagToEquipmentAsync(
                occupiedExplicitOwner.Id,
                occupiedExplicitCharacter.Id,
                kitBagSlot: 55,
                requestedEquipmentSlot: -1)
                ?? throw new InvalidOperationException("right-click replacement could not equip the shield");
            Check.Equal(
                replacementShieldEntry,
                EquipmentSlots.GetEntry(
                    rightClickReplacement.Equipment,
                    rightClickReplacement.Profession,
                    EquipmentSlots.Shield),
                "right-click equip can replace compatible occupied equipment");
            Check.Equal(
                shieldEntry,
                KitBagSlots.GetEntry(rightClickReplacement.KitBag, 55),
                "right-click replacement returns the previous gear to the source bag slot");

            var rejectedPotion = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 1,
                requestedEquipmentSlot: -1);
            Check.True(rejectedPotion is null, "consumable cannot be moved into equipment");

            var afterRejectedPotion = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("equipment guard fixture was not reloaded");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(afterRejectedPotion.Equipment, afterRejectedPotion.Profession, EquipmentSlots.Weapon),
                "rejected consumable does not displace the equipped weapon");
            Check.Equal(4030u, KitBagSlots.GetItemId(afterRejectedPotion.KitBag, 1), "rejected consumable remains in bag");

            var rejectedEmptySlot = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 23,
                requestedEquipmentSlot: -1);
            Check.True(rejectedEmptySlot is null, "empty bag slot is not reported as a successful equip");

            var occupiedDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 1)
                ?? throw new InvalidOperationException("occupied-destination unequip guard did not return the character");
            Check.Equal(4030u, KitBagSlots.GetItemId(occupiedDestination.KitBag, 1), "occupied destination item is preserved");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    occupiedDestination.Equipment,
                    occupiedDestination.Profession,
                    EquipmentSlots.Weapon),
                "occupied destination leaves the weapon equipped");

            var invalidDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 96)
                ?? throw new InvalidOperationException("invalid-destination unequip guard did not return the character");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    invalidDestination.Equipment,
                    invalidDestination.Profession,
                    EquipmentSlots.Weapon),
                "invalid destination leaves the weapon equipped");

            var unequipped = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 95)
                ?? throw new InvalidOperationException("starter sword could not be moved into the bag");
            Check.Equal(1000u, KitBagSlots.GetItemId(unequipped.KitBag, 95), "starter sword uses the exact empty destination requested by the client");
            Check.Equal(0u, KitBagSlots.GetItemId(unequipped.KitBag, 2), "an earlier empty slot is not substituted for the requested destination");

            var equipped = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 95,
                requestedEquipmentSlot: -1)
                ?? throw new InvalidOperationException("starter sword could not be equipped again");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(equipped.Equipment, equipped.Profession, EquipmentSlots.Weapon),
                "right-click starter sword is equipped in its inferred authoritative slot");
            Check.Equal(0u, KitBagSlots.GetItemId(equipped.KitBag, 95), "right-click source bag slot is cleared after equip");

            var snapshot = PacketBuilder.EquipmentItemEquipSnapshot(
                equipped,
                sourceSlot: 95,
                EquipmentSlots.Weapon);
            Check.Equal(92, snapshot.Length, "equip snapshot length");
            Check.Equal((ushort)10051, ReadUInt16(snapshot, 2), "equip snapshot opcode");
            Check.Equal(0x1448u, ReadUInt32(snapshot, 4), "equip snapshot local player object ID");
            Check.Equal(0u, ReadUInt32(snapshot, 8), "equip snapshot bag operation marker");
            Check.Equal((ushort)3, ReadUInt16(snapshot, 12), "equip snapshot source page");
            Check.Equal((ushort)23, ReadUInt16(snapshot, 14), "equip snapshot source index");
            Check.Equal(1000u, ReadUInt32(snapshot, 20), "equip snapshot describes the equipped sword");
            Check.Equal((byte)0, snapshot[46], "equip move snapshot uses captured zero bound flag");
            Check.Equal((byte)0, snapshot[47], "equip move snapshot uses captured zero stack flag");

            var sourceSlotRefresh = PacketBuilder.KitBagSlotIndex(equipped, 95);
            Check.Equal(40, sourceSlotRefresh.Length, "equipped source-slot refresh length");
            Check.Equal((ushort)10056, ReadUInt16(sourceSlotRefresh, 2), "equipped source-slot refresh opcode");
            Check.Equal(3u, ReadUInt32(sourceSlotRefresh, 12), "equipped source-slot refresh page");
            Check.Equal(23u, ReadUInt32(sourceSlotRefresh, 16), "equipped source-slot refresh index");
            Check.Equal(-1, ReadInt32(sourceSlotRefresh, 20), "equipped source slot is explicitly cleared");

            var afterDeletingFirstPotion = await store.DeleteKitBagItemAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0)
                ?? throw new InvalidOperationException("first starter potion could not be deleted");
            Check.Equal(0u, KitBagSlots.GetItemId(afterDeletingFirstPotion.KitBag, 0), "slot zero is open before shield unequip");

            var rejectedMissingDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Shield,
                kitBagSlot: -1)
                ?? throw new InvalidOperationException("missing-destination unequip guard did not return the character");
            Check.Equal(
                2000u,
                EquipmentSlots.GetItemId(
                    rejectedMissingDestination.Equipment,
                    rejectedMissingDestination.Profession,
                    EquipmentSlots.Shield),
                "unequip without a valid drop destination leaves the shield equipped");

            var shieldUnequipped = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Shield,
                kitBagSlot: 0)
                ?? throw new InvalidOperationException("starter shield could not be moved into the bag");
            Check.Equal(2000u, KitBagSlots.GetItemId(shieldUnequipped.KitBag, 0), "non-weapon gear uses its exact requested empty bag slot");
            Check.Equal(
                0u,
                EquipmentSlots.GetItemId(shieldUnequipped.Equipment, shieldUnequipped.Profession, EquipmentSlots.Shield),
                "shield equipment slot is cleared after exact-slot unequip");

            var rejectedShieldTarget = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0,
                requestedEquipmentSlot: EquipmentSlots.Armor);
            Check.True(rejectedShieldTarget is null, "explicit drag rejects shield-to-armor placement");
            var afterRejectedShieldTarget = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("explicit shield-target guard fixture was not reloaded");
            Check.Equal(2000u, KitBagSlots.GetItemId(afterRejectedShieldTarget.KitBag, 0), "rejected explicit target leaves shield in its bag slot");
            Check.Equal(
                2100u,
                EquipmentSlots.GetItemId(
                    afterRejectedShieldTarget.Equipment,
                    afterRejectedShieldTarget.Profession,
                    EquipmentSlots.Armor),
                "rejected explicit target does not displace armor");

            var shieldEquipped = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0,
                requestedEquipmentSlot: EquipmentSlots.Shield)
                ?? throw new InvalidOperationException("starter shield could not be equipped again");
            Check.Equal(
                2000u,
                EquipmentSlots.GetItemId(shieldEquipped.Equipment, shieldEquipped.Profession, EquipmentSlots.Shield),
                "explicit drag equips non-weapon gear in its compatible slot");
            Check.Equal(0u, KitBagSlots.GetItemId(shieldEquipped.KitBag, 0), "shield source slot clears after re-equip");

            var duplicateBefore = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                EquipmentSlots.GetEntry(
                    GameDefaults.DefaultEquipment(profession: 0),
                    profession: 0,
                    EquipmentSlots.Shield));
            var duplicateAfter = KitBagSlots.SetSlot(
                duplicateBefore,
                1,
                EquipmentSlots.GetEntry(
                    GameDefaults.DefaultEquipment(profession: 0),
                    profession: 0,
                    EquipmentSlots.Shield));
            Check.Equal(
                1,
                GameClientHandler.ResolveMovedKitBagDestination(
                    duplicateBefore,
                    duplicateAfter,
                    EquipmentSlots.GetEntry(
                        GameDefaults.DefaultEquipment(profession: 0),
                        profession: 0,
                        EquipmentSlots.Shield)),
                "unequip acknowledgement resolves the newly changed slot when an identical item already exists earlier in the bag");

            var fullBagEntry = CompactItemEntry.Parse(
                KitBagSlots.GetEntry(GameDefaults.StarterKitBag, 0)).ToCompactString();
            var fullBag = string.Join('#', Enumerable.Repeat(fullBagEntry, 96)) + '#';
            var fullBagOwner = await store.LoginOrCreateAccountAsync("equipment-full-bag-owner", "");
            var fullBagCharacter = await store.CreateCharacterAsync(
                fullBagOwner.Id,
                new GameCharacter
                {
                    Name = "EquipmentFullBagHero",
                    Profession = 0,
                    KitBag = fullBag
                });
            var afterFullBagUnequip = await store.MoveEquipmentToKitBagAsync(
                fullBagOwner.Id,
                fullBagCharacter.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 12)
                ?? throw new InvalidOperationException("full-bag unequip guard did not return the character");
            Check.Equal(fullBag, afterFullBagUnequip.KitBag, "full bag is unchanged when no unequip destination exists");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    afterFullBagUnequip.Equipment,
                    afterFullBagUnequip.Profession,
                    EquipmentSlots.Weapon),
                "full bag leaves the weapon equipped instead of losing it");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static Task CheckHolyStoneAuthoritativePersistencePlanAsync()
    {
        const byte profession = 0;
        var equipment = string.Join('#', Enumerable.Repeat("[]", 24)) + '#';
        var kitBag = string.Join('#', Enumerable.Repeat("[]", 96)) + '#';

        var target = CompactItemEntry.Empty with
        {
            Id = 1000,
            Attribute1 = 11,
            Attribute2 = 12,
            Attribute3 = 13,
            Attribute4 = 14,
            Attribute5 = 15,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            Exp = 87_654,
            HolySuitCode = 610,
            AttributeLevel1 = 21,
            AttributeLevel2 = 22,
            AttributeLevel3 = 23,
            AttributeLevel4 = 24,
            AttributeLevel5 = 25,
            SocketCount = 2,
            Socket1EffectId = 2,
            Socket1Level = 9
        };
        var stone = CompactItemEntry.Empty with
        {
            Id = 9060,
            Quality = 9,
            Grade = 7,
            Bound = 1,
            Stack = 1
        };
        var unrelatedBagItem = CompactItemEntry.Empty with
        {
            Id = 2200,
            Attribute1 = 31,
            Attribute2 = 32,
            Quality = 22,
            Grade = 24,
            Bound = 1,
            Stack = 1,
            Exp = 44_444,
            HolySuitCode = 509,
            AttributeLevel1 = 24,
            AttributeLevel2 = 23,
            SocketCount = 4,
            Socket1EffectId = 3,
            Socket1Level = 10,
            Socket4EffectId = 8,
            Socket4Level = 6
        };
        var unrelatedEquipmentItem = CompactItemEntry.Empty with
        {
            Id = 2100,
            Attribute1 = 41,
            Attribute5 = 45,
            Quality = 21,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            Exp = 55_555,
            HolySuitCode = 407,
            AttributeLevel1 = 25,
            AttributeLevel5 = 21,
            SocketCount = 2,
            Socket1EffectId = 5,
            Socket1Level = 8,
            Socket2EffectId = 7,
            Socket2Level = 5
        };

        kitBag = KitBagSlots.SetSlot(kitBag, 0, target.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, stone.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 2, unrelatedBagItem.ToCompactString());
        equipment = EquipmentSlots.SetSlot(
            equipment,
            profession,
            EquipmentSlots.Armor,
            unrelatedEquipmentItem.ToCompactString());

        Check.True(
            HolyStonePersistencePlanner.TryCreate(
                equipment,
                kitBag,
                profession,
                HolyStoneOperation.MountStone,
                targetKitBagSlot: 0,
                socketIndex: 1,
                stoneKitBagSlot: 1,
                destinationKitBagSlot: -1,
                out var plan,
                out var summary),
            $"valid authoritative holy-stone plan is created: {summary}");

        Check.Equal(2, plan!.Mutations.Count, "only target and consumed-stone slots are scheduled for persistence");
        Check.True(
            plan.Mutations.Any(static mutation => mutation.IsKitBag && mutation.Slot == 0),
            "target weapon slot is scheduled");
        Check.True(
            plan.Mutations.Any(static mutation => mutation.IsKitBag && mutation.Slot == 1),
            "consumed stone slot is scheduled");
        Check.True(
            plan.Mutations.All(static mutation => mutation.IsKitBag && mutation.Slot is 0 or 1),
            "no unrelated equipment or bag slot is scheduled");

        var updatedTarget = KitBagSlots.GetItem(plan.UpdatedKitBag, 0);
        Check.Equal(target.Quality, updatedTarget.Quality, "extended target quality remains authoritative");
        Check.Equal(target.Grade, updatedTarget.Grade, "extended target grade remains authoritative");
        Check.True(target.Attribute1 == updatedTarget.Attribute1, "target attributes remain unchanged");
        Check.True(target.Attribute5 == updatedTarget.Attribute5, "all target attribute positions remain unchanged");
        Check.True(target.AttributeLevel1 == updatedTarget.AttributeLevel1, "target attribute levels remain unchanged");
        Check.True(target.AttributeLevel5 == updatedTarget.AttributeLevel5, "all target attribute levels remain unchanged");
        Check.Equal(target.HolySuitCode, updatedTarget.HolySuitCode, "target holy-suit state remains unchanged");
        Check.True(target.Socket1EffectId == updatedTarget.Socket1EffectId, "existing target stone remains unchanged");
        Check.True(updatedTarget.Socket2EffectId == 1, "new stone is mounted in the requested socket");
        Check.True(updatedTarget.Socket2Level == 7, "new stone grade determines its level");

        Check.Equal(
            unrelatedBagItem,
            KitBagSlots.GetItem(plan.UpdatedKitBag, 2),
            "unrelated high-ceiling bag quality, grade, attributes, holy suit, and stones remain byte-equivalent");
        Check.Equal(
            unrelatedEquipmentItem,
            EquipmentSlots.GetItem(plan.UpdatedEquipment, profession, EquipmentSlots.Armor),
            "unrelated high-ceiling equipment quality, grade, attributes, holy suit, and stones remain byte-equivalent");

        return Task.CompletedTask;
    }

    private static Task CheckGearEnhancerInitialProtocolAsync()
    {
        const uint capturedOriginEnhancerId = 5140;
        const int originEnhancerDialogIndex = 118;

        Check.True(
            GearEnhancerProtocol.IsEnhancerNpcKey("Sparta_070"),
            "Sparta enhancer script key is routed");
        Check.True(
            GearEnhancerProtocol.IsEnhancerNpcKey("Athens_070"),
            "Athens enhancer script key is routed");
        Check.True(
            !GearEnhancerProtocol.IsEnhancerNpcKey("Sparta_143"),
            "Origin Enhancer is not repurposed as the Gear Mentor");
        Check.True(
            !GearEnhancerProtocol.IsEnhancerNpcKey("sparta_070"),
            "enhancer script-key matching remains exact");
        Check.True(
            GearEnhancerProtocol.IsOriginEnhancerNpcKey("Sparta_143") &&
            GearEnhancerProtocol.IsOriginEnhancerNpcKey("Athens_143"),
            "Origin Enhancer keys have their own exact route");
        Check.True(
            GearEnhancerProtocol.IsOriginEnhancerEndpoint(
                "Sparta_143",
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId) &&
            GearEnhancerProtocol.IsOriginEnhancerEndpoint(
                "Athens_143",
                GearEnhancerProtocol.AthensOriginEnhancerNpcId),
            "only the two physical NPC-143 endpoints enter the Origin Enhancer route");
        Check.True(
            !GearEnhancerProtocol.IsOriginEnhancerEndpoint("Sparta_143", 0),
            "virtual NPC zero cannot enter the physical Origin Enhancer route");
        Check.True(
            !GearEnhancerProtocol.IsOriginEnhancerNpcKey("Sparta_070"),
            "Gear Mentor cannot enter the Origin Enhancer route");
        Check.Equal(originEnhancerDialogIndex, GearEnhancerProtocol.OriginDialogIndex, "physical Origin Enhancer owns dialog 118");

        var spartaEndpoint = GearEnhancerProtocol.ResolveEndpoint(GameDefaults.SpartaCamp);
        Check.Equal(GearEnhancerProtocol.SpartaEnhancerNpcId, spartaEndpoint.NpcId, "physical Sparta Gear Mentor id");
        Check.Equal("Sparta_070", spartaEndpoint.NpcKey, "physical Sparta Gear Mentor script");
        var athensEndpoint = GearEnhancerProtocol.ResolveEndpoint(GameDefaults.AthensCamp);
        Check.Equal(GearEnhancerProtocol.AthensEnhancerNpcId, athensEndpoint.NpcId, "physical Athens Gear Mentor id");
        Check.Equal("Athens_070", athensEndpoint.NpcKey, "physical Athens Gear Mentor script");
        Check.Equal(
            athensEndpoint,
            GearEnhancerProtocol.ResolveEndpoint(byte.MaxValue),
            "invalid faction uses the same safe Athens fallback as character creation");

        var spartaDefinition = NpcSpawnDefinitionFactory.Create(
                0,
                [],
                [],
                [])
            .Single(definition => definition.NpcKey == spartaEndpoint.NpcKey);
        Check.Equal(spartaEndpoint.NpcId, spartaDefinition.InteractionId, "Sparta protocol id matches physical factory id");
        Check.Equal("Sparta_070_Male22", spartaDefinition.TemplateKey, "Sparta Gear Mentor uses the stock client appearance");
        Check.Equal(142f, spartaDefinition.X, "Sparta Gear Mentor captured x coordinate");
        Check.Equal(-165.9f, spartaDefinition.Z, "Sparta Gear Mentor captured z coordinate");
        var athensDefinition = NpcSpawnDefinitionFactory.Create(
                1,
                [],
                [],
                [])
            .Single(definition => definition.NpcKey == athensEndpoint.NpcKey);
        Check.Equal(athensEndpoint.NpcId, athensDefinition.InteractionId, "Athens protocol id matches physical factory id");
        Check.Equal("Athens_070_Male22", athensDefinition.TemplateKey, "Athens Gear Mentor uses the stock client appearance");
        Check.Equal(142f, athensDefinition.X, "Athens Gear Mentor mirrored x coordinate");
        Check.Equal(-165.9f, athensDefinition.Z, "Athens Gear Mentor mirrored z coordinate");

        var dialogOpen = PacketBuilder.NpcDialogOpenAck(
            spartaEndpoint.NpcId,
            GearEnhancerProtocol.DialogIndex,
            spartaEndpoint.NpcKey);
        Check.Equal((ushort)48, ReadUInt16(dialogOpen, 0), "Sparta enhancer dialog-open packet length");
        Check.Equal((ushort)Opcodes.NpcDialogOpen, ReadUInt16(dialogOpen, 2), "Sparta enhancer dialog-open opcode");
        Check.Equal(spartaEndpoint.NpcId, ReadUInt32(dialogOpen, 4), "Sparta enhancer dialog-open NPC id");
        Check.Equal(4, GearEnhancerProtocol.DialogIndex, "Gear Mentor uses NPC_FLAG_SYS_BREAK dialog 4");
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(dialogOpen, 12), "Sparta enhancer dialog-open index");
        Check.Equal(spartaEndpoint.NpcKey, ReadFixedAscii(dialogOpen, 16, 32), "Sparta enhancer dialog-open script key");

        Check.True(
            GearEnhancerProtocol.TryBuildInitialMenuResponse(
                spartaEndpoint.NpcKey,
                spartaEndpoint.NpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var spartaMenu),
            "physical Sparta Gear Mentor initial-menu request is accepted");
        Check.Equal((ushort)48, ReadUInt16(spartaMenu, 0), "Sparta original Gear Mentor menu packet length");
        Check.Equal(spartaEndpoint.NpcId, ReadUInt32(spartaMenu, 4), "Sparta enhancer menu NPC id");
        for (var menuId = GearEnhancerProtocol.FirstGearMentorMenuSubId;
             menuId <= GearEnhancerProtocol.LastGearMentorMenuSubId;
             menuId++)
        {
            Check.Equal(
                menuId,
                ReadInt32(spartaMenu, 12 + ((menuId - 1) * sizeof(int))),
                $"Sparta original Gear Mentor menu includes position {menuId}");
        }

        var athensEnhancerId = athensEndpoint.NpcId;
        Check.True(
            GearEnhancerProtocol.TryBuildInitialMenuResponse(
                athensEndpoint.NpcKey,
                athensEnhancerId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var athensMenu),
            "Athens uses the same capture-backed enhancer menu");
        Check.Equal((ushort)48, ReadUInt16(athensMenu, 0), "Athens original Gear Mentor menu packet length");
        Check.Equal((ushort)Opcodes.NpcFunctionActionResponse, ReadUInt16(athensMenu, 2), "Athens enhancer menu opcode");
        Check.Equal(athensEnhancerId, ReadUInt32(athensMenu, 4), "Athens enhancer NPC id");
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(athensMenu, 8), "Athens enhancer dialog index");
        for (var menuId = GearEnhancerProtocol.FirstGearMentorMenuSubId;
             menuId <= GearEnhancerProtocol.LastGearMentorMenuSubId;
             menuId++)
        {
            Check.Equal(
                menuId,
                ReadInt32(athensMenu, 12 + ((menuId - 1) * sizeof(int))),
                $"Athens original Gear Mentor menu includes position {menuId}");
        }
        Check.True(
            GearEnhancerProtocol.IsGearMentorMenuSubId(1) &&
            GearEnhancerProtocol.IsGearMentorMenuSubId(9) &&
            !GearEnhancerProtocol.IsGearMentorMenuSubId(0) &&
            !GearEnhancerProtocol.IsGearMentorMenuSubId(10),
            "only original Gear Mentor menu IDs 1 through 9 are recognized");
        Check.True(
            new[] { 5, 7 }.All(GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId) &&
            new[] { 1, 2, 3, 4, 6, 8, 9 }.All(static subId => !GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId)),
            "only Instructions and Wash Dust remain temporarily disabled");
        Check.Equal(999, GearEnhancerProtocol.TemporarilyDisabledResultSubId, "unimplemented original operations use native Temporarily Disabled result");

        var originDialogOpen = PacketBuilder.NpcDialogOpenAck(
            capturedOriginEnhancerId,
            GearEnhancerProtocol.OriginDialogIndex,
            "Sparta_143");
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originDialogOpen, 4), "physical Origin Enhancer dialog-open NPC id");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originDialogOpen, 12), "physical Origin Enhancer opens dialog 118");
        Check.Equal("Sparta_143", ReadFixedAscii(originDialogOpen, 16, 32), "physical Origin Enhancer keeps its own script key");
        Check.True(
            GearEnhancerProtocol.TryBuildOriginInitialMenuResponse(
                "Sparta_143",
                capturedOriginEnhancerId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var originMenu),
            "physical Origin Enhancer accepts its captured dialog-118 initial request");
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originMenu, 4), "physical Origin menu keeps object 5140");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originMenu, 8), "physical Origin menu keeps dialog 118");
        Check.Equal(GearEnhancerProtocol.EnhanceAttributeSubId, ReadInt32(originMenu, 12), "physical Origin captured first menu id");
        Check.Equal(GearEnhancerProtocol.AddAttributeSubId, ReadInt32(originMenu, 16), "physical Origin captured second menu id");
        Check.Equal(GearEnhancerProtocol.DeleteAttributesSubId, ReadInt32(originMenu, 20), "physical Origin captured third menu id");

        var physicalOperationPage = GearEnhancerProtocol.BuildOperationPageResponse(
            spartaEndpoint.NpcId,
            GearEnhancerProtocol.DialogIndex,
            GearEnhancerProtocol.EnhanceAttributeSubId);
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(physicalOperationPage, 8), "physical operation page remains dialog 4");
        var originOperationPage = GearEnhancerProtocol.BuildOperationPageResponse(
            capturedOriginEnhancerId,
            GearEnhancerProtocol.OriginDialogIndex,
            GearEnhancerProtocol.EnhanceAttributeSubId);
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originOperationPage, 4), "physical Origin operation page keeps object 5140");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originOperationPage, 8), "physical Origin operation page remains dialog 118");

        var selectionArgs = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        Check.True(
            GearEnhancerProtocol.ReadSelection(selectionArgs, out _, out _, out _) ==
                GearEnhancerSelectionShape.MenuSelection,
            "empty fixed slots open the selected operation page");
        selectionArgs[GearEnhancerProtocol.GearArgumentIndex] = 100;
        selectionArgs[GearEnhancerProtocol.CatalystArgumentIndex] = 195;
        selectionArgs[GearEnhancerProtocol.AttributeStoneArgumentIndex] = 142;
        Check.True(
            GearEnhancerProtocol.ReadSelection(
                selectionArgs,
                out var gearSlot,
                out var catalystSlot,
                out var stoneSlot) == GearEnhancerSelectionShape.Commit,
            "fixed native enhancer records are accepted as a commit");
        Check.Equal(0, gearSlot, "native gear bag reference decodes exactly");
        Check.Equal(95, catalystSlot, "native catalyst bag reference decodes exactly");
        Check.Equal(42, stoneSlot, "native stone bag reference decodes exactly");
        selectionArgs[0] = 100;
        Check.True(
            GearEnhancerProtocol.ReadSelection(selectionArgs, out _, out _, out _) ==
                GearEnhancerSelectionShape.MenuSelection,
            "a scratch-tail lookalike cannot mutate inventory");

        var emptyItem = CompactItemEntry.Empty;
        var missingCatalystRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Add,
            new GearEnhancementSlotSelection(0, emptyItem with { Id = 1000, Stack = 1 }),
            new GearEnhancementSlotSelection(1, emptyItem with { Id = 9930, Stack = 1 }),
            new GearEnhancementSlotSelection(2, emptyItem));
        var missingCatalystResult = new GearEnhancementResult(
            GearEnhancementStatus.SelectionMissing,
            GearEnhancementOperation.Add,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            missingCatalystRequest.Gear.ExpectedItem,
            missingCatalystRequest.Gear.ExpectedItem,
            []);
        Check.Equal(
            GearEnhancerProtocol.MissingFlameSparkResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Add,
                missingCatalystResult,
                missingCatalystRequest),
            "missing Add catalyst maps to the native Flame Spark message");
        Check.Equal(
            GearEnhancerProtocol.QuartzLevelMismatchResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                missingCatalystResult with
                {
                    Status = GearEnhancementStatus.QuartzLevelMismatch,
                    Operation = GearEnhancementOperation.Enhance
                }),
            "wrong Quartz level maps to the native mismatch message");
        Check.Equal(
            GearEnhancerProtocol.DeleteSucceededResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Delete,
                missingCatalystResult with
                {
                    Status = GearEnhancementStatus.Succeeded,
                    Operation = GearEnhancementOperation.Delete
                }),
            "Delete success maps to the native result page");

        Check.True(
            !GearEnhancerProtocol.TryBuildInitialMenuResponse(
                spartaEndpoint.NpcKey,
                spartaEndpoint.NpcId,
                GearEnhancerProtocol.DialogIndex + 1,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var wrongDialogResponse) &&
            wrongDialogResponse.Length == 0,
            "wrong enhancer dialog is rejected without a response");
        Check.True(
            !GearEnhancerProtocol.TryBuildInitialMenuResponse(
                spartaEndpoint.NpcKey,
                spartaEndpoint.NpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancerProtocol.EnhanceAttributeSubId,
                out var unsupportedOperationResponse) &&
            unsupportedOperationResponse.Length == 0,
            "the initial-menu helper does not mistake an operation action for a new menu request");

        Check.True(
            !GearEnhancerProtocol.TryBuildInitialMenuResponse(
                "Sparta_143",
                capturedOriginEnhancerId,
                originEnhancerDialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var originEnhancerResponse) &&
            originEnhancerResponse.Length == 0,
            "captured Origin Enhancer 5140/dialog 118 cannot enter the Gear Mentor dialog-4 protocol");
        Check.True(
            !GearEnhancerProtocol.TryBuildInitialMenuResponse(
                spartaEndpoint.NpcKey,
                spartaEndpoint.NpcId,
                originEnhancerDialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var gearMentorAsOriginResponse) &&
            gearMentorAsOriginResponse.Length == 0,
            "Gear Mentor 070 cannot enter the Origin Enhancer dialog-118 path");
        Check.True(
            !GearEnhancerProtocol.TryBuildOriginInitialMenuResponse(
                spartaEndpoint.NpcKey,
                spartaEndpoint.NpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var originHelperGearMentorResponse) &&
            originHelperGearMentorResponse.Length == 0,
            "Origin initial-menu helper rejects Gear Mentor 070 even on dialog 118");
        Check.True(
            !GearEnhancerProtocol.TryBuildOriginInitialMenuResponse(
                "Sparta_143",
                0,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancerProtocol.InitialMenuRequestSubId,
                out var virtualOriginResponse) &&
            virtualOriginResponse.Length == 0,
            "Origin initial-menu helper rejects removed virtual NPC zero");

        return Task.CompletedTask;
    }

    private static Task CheckHolySuitDesignProtocolAsync()
    {
        Check.True(
            HolySuitDesignProtocol.IsNpcKey("Sparta_085") &&
            HolySuitDesignProtocol.IsNpcKey("Athens_085"),
            "paired Master Vestment Forgers own the Holy Suit Design protocol");
        Check.True(
            !HolySuitDesignProtocol.IsNpcKey("Sparta_070") &&
            !HolySuitDesignProtocol.IsNpcKey("Sparta_044") &&
            !HolySuitDesignProtocol.IsNpcKey("Sparta_122"),
            "Gear Mentor, Class Shifter, and Ingredients Vendor cannot enter Holy Suit Design");
        Check.Equal(29, HolySuitDesignProtocol.DialogIndex, "Master Vestment Forger uses captured dialog 29");

        // Deliberately use a coordinate which would sort before the captured
        // value. This proves the authoritative actor correction wins by source
        // priority, not by the old incidental coordinate ordering.
        var staleIngredientReference = new NpcSpawnReferenceDefinition(
            0,
            "Sparta",
            "Sparta_122",
            "Sparta_122_FemVillager3",
            -500f,
            -500f);
        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(
            0,
            [],
            [],
            [staleIngredientReference]);
        var spartaClassShifter = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_044");
        Check.Equal(5041u, spartaClassShifter.InteractionId, "Sparta Class Shifter object 5041");
        Check.Equal("Sparta_044_Male34", spartaClassShifter.TemplateKey, "Sparta Class Shifter original appearance");
        Check.Equal(141f, spartaClassShifter.X, "Sparta Class Shifter captured x coordinate");
        Check.Equal(-174f, spartaClassShifter.Z, "Sparta Class Shifter captured z coordinate");
        var spartaForger = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_085");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, spartaForger.InteractionId, "Sparta forger object 5082");
        Check.Equal("Sparta_085_Male34", spartaForger.TemplateKey, "Sparta forger original appearance");
        Check.Equal(126f, spartaForger.X, "Sparta forger captured x coordinate");
        Check.Equal(-161.1f, spartaForger.Z, "Sparta forger captured z coordinate");
        var spartaIngredients = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_122");
        Check.Equal(5119u, spartaIngredients.InteractionId, "Sparta Ingredients Vendor object 5119");
        Check.Equal(97f, spartaIngredients.X, "Ingredients Vendor captured x overrides stale quest reference");
        Check.Equal(-174f, spartaIngredients.Z, "Ingredients Vendor captured z overrides stale quest reference");

        var athensDefinitions = NpcSpawnDefinitionFactory.Create(1, [], [], []);
        var athensClassShifter = athensDefinitions.Single(definition => definition.NpcKey == "Athens_044");
        Check.Equal(5183u, athensClassShifter.InteractionId, "Athens paired Class Shifter object 5183");
        Check.Equal("Athens_044_Male34", athensClassShifter.TemplateKey, "Athens paired Class Shifter appearance");
        Check.Equal(141f, athensClassShifter.X, "Athens paired Class Shifter x coordinate");
        Check.Equal(-174f, athensClassShifter.Z, "Athens paired Class Shifter z coordinate");
        var athensForger = athensDefinitions.Single(definition => definition.NpcKey == "Athens_085");
        Check.Equal(HolySuitDesignProtocol.AthensNpcId, athensForger.InteractionId, "Athens paired forger object 5224");
        Check.Equal("Athens_085_Male34", athensForger.TemplateKey, "Athens paired forger appearance");
        Check.Equal(126f, athensForger.X, "Athens paired forger x coordinate");
        Check.Equal(-161.1f, athensForger.Z, "Athens paired forger z coordinate");
        var athensIngredients = athensDefinitions.Single(definition => definition.NpcKey == "Athens_122");
        Check.Equal(5261u, athensIngredients.InteractionId, "Athens paired Ingredients Vendor object 5261");
        Check.Equal(97f, athensIngredients.X, "Athens Ingredients Vendor paired x coordinate");
        Check.Equal(-174f, athensIngredients.Z, "Athens Ingredients Vendor paired z coordinate");

        var dialogOpen = PacketBuilder.NpcDialogOpenAck(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.DialogIndex,
            "Sparta_085");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, ReadUInt32(dialogOpen, 4), "Holy Suit open keeps object 5082");
        Check.Equal(HolySuitDesignProtocol.DialogIndex, ReadInt32(dialogOpen, 12), "Holy Suit open keeps dialog 29");
        Check.Equal("Sparta_085", ReadFixedAscii(dialogOpen, 16, 32), "Holy Suit open keeps NPC085 key");

        Check.True(
            HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                "Sparta_085",
                HolySuitDesignProtocol.SpartaNpcId,
                HolySuitDesignProtocol.DialogIndex,
                HolySuitDesignProtocol.InitialMenuRequestSubId,
                out var menu),
            "captured Master Vestment Forger initial request is accepted");
        Check.Equal((ushort)28, ReadUInt16(menu, 0), "Holy Suit original menu packet length");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, ReadUInt32(menu, 4), "Holy Suit menu NPC id");
        Check.Equal(HolySuitDesignProtocol.DialogIndex, ReadInt32(menu, 8), "Holy Suit menu dialog index");
        Check.Equal(HolySuitDesignProtocol.StoreExperienceSubId, ReadInt32(menu, 12), "Holy Suit first captured menu id");
        Check.Equal(HolySuitDesignProtocol.TransferExperienceSubId, ReadInt32(menu, 16), "Holy Suit second captured menu id");
        Check.Equal(HolySuitDesignProtocol.ConsumeEquipmentSubId, ReadInt32(menu, 20), "Holy Suit third captured menu id");
        Check.Equal(HolySuitDesignProtocol.TransformExperienceSubId, ReadInt32(menu, 24), "Holy Suit fourth captured menu id");
        Check.True(
            !HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                "Sparta_085",
                HolySuitDesignProtocol.SpartaNpcId,
                37,
                HolySuitDesignProtocol.InitialMenuRequestSubId,
                out var classSuitResponse) &&
            classSuitResponse.Length == 0,
            "dialog 37 Class Suit cannot replace captured dialog 29 Holy Suit Design");

        return Task.CompletedTask;
    }

    private static Task CheckNpcDefinitionsAndSpawnLayoutAsync()
    {
        var capturedPacket = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(0, 2), 108);
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(4, 4), 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(8, 4), 5083);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(28, 4), 126f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(36, 4), -169.9f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(40, 4), 4.7f);
        Encoding.ASCII.GetBytes("Sparta_086_Male35").CopyTo(capturedPacket, 44);

        var detail10077 = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(0, 2), (ushort)detail10077.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(2, 2), 10077);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10077.AsSpan(4, 4), 5083);
        var detail10080 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(0, 2), (ushort)detail10080.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(2, 2), 10080);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10080.AsSpan(4, 4), 5083);

        var capturedSpartaArtisan = new CapturedNpcSpawn(
            0,
            "Sparta",
            "Sparta_086",
            "Sparta_086_Male35",
            5083,
            126f,
            -169.9f,
            capturedPacket,
            detail10077,
            detail10080);

        var originPacket = capturedPacket.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(originPacket.AsSpan(8, 4), 5140);
        BinaryPrimitives.WriteSingleLittleEndian(originPacket.AsSpan(28, 4), 126f);
        BinaryPrimitives.WriteSingleLittleEndian(originPacket.AsSpan(36, 4), -165.9f);
        originPacket.AsSpan(44, 64).Clear();
        Encoding.ASCII.GetBytes("Sparta_143_Hallo").CopyTo(originPacket, 44);
        var capturedOriginEnhancer = new CapturedNpcSpawn(
            0,
            "Sparta",
            "Sparta_143",
            "Sparta_143_Hallo",
            5140,
            126f,
            -165.9f,
            originPacket,
            [],
            []);
        var athensDefinitions = NpcSpawnDefinitionFactory.Create(1, [], [capturedSpartaArtisan], []);
        var athensArtisan = athensDefinitions.Single(definition => definition.NpcKey == "Athens_086");
        Check.Equal(5225u, athensArtisan.ObjectId, "Athens artisan object id");
        Check.Equal(5225u, athensArtisan.InteractionId, "Athens artisan interaction id");
        Check.Equal(126f, athensArtisan.X, "Athens artisan paired X");
        Check.Equal(-169.9f, athensArtisan.Z, "Athens artisan paired Z");
        Check.Equal(4.7f, athensArtisan.Facing, "Athens artisan paired facing");
        Check.Equal("Athens_086_Male35", athensArtisan.TemplateKey, "Athens artisan paired template");
        Check.Equal(0, athensArtisan.Detail10077.Length, "Athens fallback does not inherit Sparta detail 10077");
        Check.Equal(0, athensArtisan.Detail10080.Length, "Athens fallback does not inherit Sparta detail 10080");

        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(
            0,
            [capturedSpartaArtisan, capturedOriginEnhancer],
            [],
            []);
        var spartaArtisan = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_086");
        Check.Equal(5083u, spartaArtisan.ObjectId, "Sparta artisan object id");
        Check.True(spartaArtisan.Detail10077.SequenceEqual(detail10077), "Sparta detail 10077 is preserved");
        Check.True(spartaArtisan.Detail10080.SequenceEqual(detail10080), "Sparta detail 10080 is preserved");
        var spartaGearMentor = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_070");
        var spartaOriginEnhancer = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_143");
        Check.Equal(5067u, spartaGearMentor.ObjectId, "Gear Mentor has its own physical object id");
        Check.Equal(142f, spartaGearMentor.X, "Gear Mentor keeps its captured x coordinate");
        Check.Equal(-165.9f, spartaGearMentor.Z, "Gear Mentor keeps its captured z coordinate");
        Check.Equal(5140u, spartaOriginEnhancer.ObjectId, "Origin Enhancer keeps captured object id 5140");
        Check.Equal(126f, spartaOriginEnhancer.X, "Origin Enhancer keeps its captured x coordinate");
        Check.Equal(-165.9f, spartaOriginEnhancer.Z, "Origin Enhancer keeps its captured z coordinate");

        var stream = PacketBuilder.NpcSpawns([spartaArtisan, athensArtisan]);
        var athensOffset = 108 + detail10077.Length + detail10080.Length;
        Check.Equal(athensOffset + 108, stream.Length, "authoritative NPC frames include captured details");
        CheckNpcSpawnFrame(stream, 0, spartaArtisan);
        Check.True(
            stream.AsSpan(108, detail10077.Length).SequenceEqual(detail10077),
            "detail 10077 follows captured NPC appearance");
        Check.True(
            stream.AsSpan(108 + detail10077.Length, detail10080.Length).SequenceEqual(detail10080),
            "detail 10080 follows captured NPC appearance");
        CheckNpcSpawnFrame(stream, athensOffset, athensArtisan);
        return Task.CompletedTask;
    }

    private static Task CheckNpcMovementCellVisibilityAsync()
    {
        var nearEast = CreateNpcDefinition(6001, 124.5f, -149f);
        var nextSouthRow = CreateNpcDefinition(6002, 64f, -165f);
        var oldNorthRow = CreateNpcDefinition(6003, 85f, -116f);
        var farAway = CreateNpcDefinition(6004, 10f, 10f);
        var tracker = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            [nearEast, nextSouthRow, oldNorthRow, farAway],
            npc => npc.ObjectId,
            npc => npc.X,
            npc => npc.Z,
            "NPC");

        Check.True(
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(-0.1f, -32f, out var negativeCell),
            "negative coordinates produce a valid NPC cell");
        Check.Equal(new WorldGridCell(-1, -1), negativeCell, "NPC cells use floor for negatives");
        Check.True(
            !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(float.NaN, 0f, out _),
            "non-finite positions are rejected");
        Check.True(
            !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(float.MaxValue, 0f, out _),
            "finite positions outside the grid range are rejected");

        Check.True(
            tracker.TryCalculate(85f, -119f, out var initial),
            "initial captured position resolves a visibility cell");
        Check.Equal(new WorldGridCell(2, -4), initial.PlayerCell, "initial captured player cell");
        Check.True(
            initial.Entering.Select(npc => npc.ObjectId).SequenceEqual([6001u, 6003u]),
            "initial 3x3 cell window contains only nearby NPCs");
        Check.Equal(0, initial.Leaving.Count, "initial visibility removes nothing");
        Check.True(!tracker.IsVisible(6001), "visibility is not committed before packets are sent");
        tracker.Commit(initial);
        Check.True(tracker.IsVisible(6001) && tracker.IsVisible(6003), "initial NPCs commit as visible");
        Check.True(!tracker.IsVisible(6002) && !tracker.IsVisible(6004), "outside NPCs remain hidden");

        Check.True(
            tracker.TryCalculate(92f, -127.9f, out var sameCell),
            "movement inside one cell is accepted");
        Check.Equal(0, sameCell.Entering.Count, "same-cell movement spawns nothing");
        Check.Equal(0, sameCell.Leaving.Count, "same-cell movement removes nothing");

        Check.True(
            tracker.TryCalculate(92f, -129f, out var southCrossing),
            "south cell crossing is accepted");
        Check.Equal(new WorldGridCell(2, -5), southCrossing.PlayerCell, "south crossing player cell");
        Check.True(
            southCrossing.Entering.Select(npc => npc.ObjectId).SequenceEqual([6002u]),
            "new southern NPC row enters after crossing z=-128");
        Check.Equal(0, southCrossing.Leaving.Count, "first south crossing keeps overlapping rows");
        Check.True(!tracker.IsVisible(6002), "new row waits for successful spawn send");
        tracker.Commit(southCrossing);

        Check.True(
            tracker.TryCalculate(92f, -161f, out var secondSouthCrossing),
            "second south cell crossing is accepted");
        Check.True(
            secondSouthCrossing.Leaving.SequenceEqual([6003u]),
            "old northern NPC row leaves after crossing z=-160");
        Check.Equal(0, secondSouthCrossing.Entering.Count, "second crossing has no synthetic entries");
        Check.True(tracker.IsVisible(6003), "old row waits for successful remove send");

        var removePacket = PacketBuilder.RemoveWorldObjects(secondSouthCrossing.Leaving.ToArray());
        Check.Equal((ushort)12, ReadUInt16(removePacket, 0), "single NPC remove packet length");
        Check.Equal((ushort)10024, ReadUInt16(removePacket, 2), "NPC remove opcode");
        Check.Equal(1u, ReadUInt32(removePacket, 4), "NPC remove count");
        Check.Equal(6003u, ReadUInt32(removePacket, 8), "NPC remove uses object ID");
        tracker.Commit(secondSouthCrossing);
        Check.True(!tracker.IsVisible(6003), "old row commits as hidden");

        return Task.CompletedTask;
    }

    private static Task CheckMonsterMovementCellVisibilityAsync()
    {
        // These positions and player transitions come from the working-server
        // monster capture and exercise both axes of the 32-unit sector grid.
        var eastMonster = CreateCapturedMonster(10004, 210.353653f, -17.122650f, "A_normal_stub_001");
        var westMonster = CreateCapturedMonster(10038, 143.051132f, -6.025902f, "A_normal_stub_001");
        var farWestMonster = CreateCapturedMonster(10042, 119.999641f, 13.100252f, "A_normal_stub_001");
        var northMonster = CreateCapturedMonster(10079, 141.978607f, 40.799419f, "A_normal_stub_003");
        var tracker = new WorldSectorVisibilityTracker<CapturedMonsterSpawn>(
            [westMonster, eastMonster, farWestMonster, northMonster],
            monster => monster.ObjectId,
            monster => monster.AppearanceX,
            monster => monster.AppearanceZ,
            "monster");

        Check.True(
            WorldObjectIds.IsReservedForPlayer(0x1448) &&
            WorldObjectIds.IsReservedForPlayer(0x6000) &&
            WorldObjectIds.IsReservedForPlayer(0x7FFF) &&
            !WorldObjectIds.IsReservedForPlayer(westMonster.ObjectId),
            "NPC and monster IDs cannot overlap the local or remote player namespace");

        westMonster.Validate(0);
        var roundedMetadata = westMonster with
        {
            X = westMonster.X + 0.00004f,
            Z = westMonster.Z - 0.00004f
        };
        roundedMetadata.Validate(0);
        Check.Equal(westMonster.X, roundedMetadata.AppearanceX, "packet X remains authoritative after metadata rounding");
        Check.Equal(westMonster.Z, roundedMetadata.AppearanceZ, "packet Z remains authoritative after metadata rounding");
        CreateCapturedMonster(10100, 1f, 1f, "field_monster", 0x00000112).Validate(0);
        CreateCapturedMonster(10101, 1f, 1f, "newbie_monster", 0x00040212).Validate(0);
        CreateCapturedMonster(10102, 1f, 1f, "elite_monster", 0x00040012).Validate(0);

        var capturedTierFourPacket = Convert.FromHexString(
            "6C00242712020000752700000400000000000000320100003201000017ED144300000000E0D55F42B70B05C0415F6E6F726D616C5F737475625F3030330000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        var capturedTierFour = new CapturedMonsterSpawn(
            0,
            "Sparta",
            "A_normal_stub_003",
            "captured tier-four monster",
            ReadUInt32(capturedTierFourPacket, 8),
            ReadSingle(capturedTierFourPacket, 28),
            ReadSingle(capturedTierFourPacket, 36),
            capturedTierFourPacket);
        capturedTierFour.Validate(0);
        Check.Equal(4u, ReadUInt32(capturedTierFourPacket, 12), "captured monster tier fixture");
        Check.Equal(306u, ReadUInt32(capturedTierFourPacket, 20), "captured monster HP metadata fixture");
        Check.True(
            PacketBuilder.CapturedMonsterSpawns([capturedTierFour]).SequenceEqual(capturedTierFourPacket),
            "captured tier-four appearance is replayed byte-for-byte");

        Check.True(
            tracker.TryCalculate(160.627f, -64.357f, out var initial),
            "initial monster position resolves a visibility cell");
        Check.Equal(new WorldGridCell(5, -3), initial.PlayerCell, "initial captured monster player cell");
        Check.Equal(0, initial.Entering.Count, "initial captured sector contains none of the fixture monsters");
        Check.Equal(0, initial.Leaving.Count, "initial monster visibility removes nothing");
        tracker.Commit(initial);

        Check.True(
            tracker.TryCalculate(160.9f, -64.1f, out var sameCell),
            "same-cell monster movement is accepted");
        Check.Equal(0, sameCell.Entering.Count, "same-cell movement spawns no monsters");
        Check.Equal(0, sameCell.Leaving.Count, "same-cell movement removes no monsters");

        Check.True(
            tracker.TryCalculate(160.627f, -63.638f, out var firstNorthCrossing),
            "first captured north crossing updates monster visibility");
        Check.True(
            firstNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10004u, 10038u]),
            "captured first north crossing enters the observed monster row");
        Check.True(!tracker.IsVisible(10004), "monster visibility waits for a successful spawn send");

        var firstVisibleStream = PacketBuilder.CapturedMonsterSpawns(firstNorthCrossing.Entering);
        Check.Equal(eastMonster.Packet.Length + westMonster.Packet.Length, firstVisibleStream.Length, "nearby monster stream length");
        Check.Equal(10004u, ReadUInt32(firstVisibleStream, 8), "first nearby monster object ID");
        Check.Equal(10038u, ReadUInt32(firstVisibleStream, eastMonster.Packet.Length + 8), "second nearby monster object ID");
        tracker.Commit(firstNorthCrossing);

        Check.True(
            tracker.TryCalculate(159.841f, -50.757f, out var westCrossing),
            "captured west crossing updates monster visibility");
        Check.True(
            westCrossing.Leaving.SequenceEqual([10004u]),
            "captured west crossing removes the old eastern monster");
        var removePacket = PacketBuilder.RemoveWorldObjects(westCrossing.Leaving.ToArray());
        Check.Equal((ushort)10024, ReadUInt16(removePacket, 2), "monster remove opcode");
        Check.Equal(10004u, ReadUInt32(removePacket, 8), "monster remove uses captured object ID");
        tracker.Commit(westCrossing);

        Check.True(
            tracker.TryCalculate(157.447f, -31.132f, out var secondNorthCrossing),
            "second captured north crossing updates monster visibility");
        Check.True(
            secondNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10042u]),
            "second captured north crossing enters the far-west monster");
        tracker.Commit(secondNorthCrossing);

        Check.True(
            tracker.TryCalculate(160.338f, -17.239f, out var eastCrossing),
            "captured east crossing updates monster visibility");
        Check.True(
            eastCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10004u]) &&
            eastCrossing.Leaving.SequenceEqual([10042u]),
            "one captured crossing can remove the old column and enter the new column");
        tracker.Commit(eastCrossing);

        Check.True(
            tracker.TryCalculate(175.733f, 0.970f, out var thirdNorthCrossing),
            "third captured north crossing updates monster visibility");
        Check.True(
            thirdNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10079u]),
            "third captured north crossing enters the northern monster");
        tracker.Commit(thirdNorthCrossing);

        Check.True(
            tracker.TryCalculate(187.140f, -0.560f, out var finalSouthCrossing),
            "captured south crossing updates monster visibility");
        Check.True(
            finalSouthCrossing.Leaving.SequenceEqual([10079u]),
            "captured south crossing removes the northern monster");

        var mismatchedPacket = westMonster.Packet.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(mismatchedPacket.AsSpan(8, 4), westMonster.ObjectId + 1);
        var mismatchedMonster = westMonster with { Packet = mismatchedPacket };
        Check.Throws<InvalidDataException>(
            () => mismatchedMonster.Validate(0),
            "captured monster metadata mismatch is rejected");
        Check.Throws<InvalidDataException>(
            () => (westMonster with { X = westMonster.X + 0.01f }).Validate(0),
            "captured monster coordinate drift outside importer tolerance is rejected");

        return Task.CompletedTask;
    }

    private static Task CheckMonsterMovementPacketLayoutsAsync()
    {
        var capturedStart = Convert.FromHexString(
            "28002027112700000000000001000000E5FA3043000000000E3D89C1CD05A63E0000000047E14ABE");
        var generatedStart = PacketBuilder.MonsterMovementStart(
            ReadUInt32(capturedStart, 4),
            ReadSingle(capturedStart, 16),
            ReadSingle(capturedStart, 20),
            ReadSingle(capturedStart, 24),
            ReadSingle(capturedStart, 28),
            ReadSingle(capturedStart, 32),
            ReadSingle(capturedStart, 36));
        Check.True(
            generatedStart.SequenceEqual(capturedStart),
            "opcode-10016 movement start matches the working-server fixture byte-for-byte");
        Check.Equal((ushort)40, ReadUInt16(generatedStart, 0), "monster movement-start length");
        Check.Equal((ushort)10016, ReadUInt16(generatedStart, 2), "monster movement-start opcode");
        Check.Equal(10001u, ReadUInt32(generatedStart, 4), "monster movement-start object ID");
        Check.Equal(0u, ReadUInt32(generatedStart, 8), "monster movement-start reserved field");
        Check.Equal(1u, ReadUInt32(generatedStart, 12), "monster idle-roaming movement mode");

        var capturedEnd = Convert.FromHexString(
            "220021271127000000000000060000002A0B32430000000063D398C1107C2F400000");
        var generatedEnd = PacketBuilder.MonsterMovementEnd(
            ReadUInt32(capturedEnd, 4),
            ReadUInt32(capturedEnd, 12),
            ReadSingle(capturedEnd, 16),
            ReadSingle(capturedEnd, 20),
            ReadSingle(capturedEnd, 24),
            ReadSingle(capturedEnd, 28));
        Check.True(
            generatedEnd.SequenceEqual(capturedEnd),
            "opcode-10017 movement end matches the working-server fixture byte-for-byte");
        Check.Equal((ushort)34, ReadUInt16(generatedEnd, 0), "monster movement-end length");
        Check.Equal((ushort)10017, ReadUInt16(generatedEnd, 2), "monster movement-end opcode");
        Check.Equal(6u, ReadUInt32(generatedEnd, 12), "monster movement-end tick count");
        Check.Equal((ushort)0, ReadUInt16(generatedEnd, 32), "monster movement-end trailing field");

        var capturedLifecycleMarker = Convert.FromHexString("0800272734270000");
        Check.True(
            PacketBuilder.MonsterLifecycleMarker(10036).SequenceEqual(capturedLifecycleMarker),
            "opcode-10023 corpse/respawn marker matches the working-server fixture byte-for-byte");
        return Task.CompletedTask;
    }

    private static Task CheckPersistedWorldBossRespawnAsync()
    {
        var initializedAt = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var respawnAt = initializedAt.AddHours(6);
        var definition = CreateCapturedMonster(
            12003,
            25f,
            -30f,
            "A_boss_boar_001",
            mapId: 3,
            sceneKey: "Parnitha_1");
        var persisted = new WorldBossRespawnState(3, definition.TemplateKey, respawnAt);
        var runtime = new MonsterMapRuntime(
            3,
            [definition],
            initializedAt,
            activeWorldBossRespawn: persisted);

        var suppressed = runtime.Snapshot().Single();
        Check.True(!suppressed.IsAlive, "persisted world boss remains dead after server restart");
        Check.True(!suppressed.IsSpawned, "persisted world boss remains hidden before its next cycle");
        Check.True(suppressed.RespawnAt == respawnAt, "persisted world-boss respawn timestamp is restored");

        var beforeRespawn = runtime.Advance(respawnAt.AddTicks(-1));
        Check.Equal(0, beforeRespawn.Updates.Count, "world boss does not respawn before persisted expiry");
        var atRespawn = runtime.Advance(respawnAt);
        Check.Equal(1, atRespawn.Updates.Count, "world boss respawns exactly at persisted expiry");
        Check.True(
            atRespawn.Updates[0].Kind == MonsterRuntimeUpdateKind.Respawned,
            "persisted lifecycle emits respawn event");
        Check.True(atRespawn.Updates[0].Monster.IsAlive, "respawned world boss is alive");
        return Task.CompletedTask;
    }

    private static Task CheckMonsterRuntimeAppearancePatchAsync()
    {
        var monster = CreateCapturedMonster(
            10038,
            143.051132f,
            -6.025902f,
            "A_normal_stub_001");
        monster.Packet[16] = 0xA5;
        monster.Packet[17] = 0x5A;
        monster.Packet[107] = 0xC3;
        BinaryPrimitives.WriteSingleLittleEndian(monster.Packet.AsSpan(32, 4), 7.25f);
        var original = monster.Packet.ToArray();
        var state = new CapturedMonsterAppearanceState(
            monster,
            150.25f,
            -12.5f,
            -2.25f,
            123,
            456);

        var patched = PacketBuilder.CapturedMonsterAppearance(state);
        Check.Equal(123u, ReadUInt32(patched, 20), "runtime appearance current HP");
        Check.Equal(456u, ReadUInt32(patched, 24), "runtime appearance maximum HP");
        Check.Equal(state.X, ReadSingle(patched, 28), "runtime appearance X");
        Check.Equal(7.25f, ReadSingle(patched, 32), "runtime appearance preserves captured Y");
        Check.Equal(state.Z, ReadSingle(patched, 36), "runtime appearance Z");
        Check.Equal(state.Facing, ReadSingle(patched, 40), "runtime appearance facing");
        Check.True(monster.Packet.SequenceEqual(original), "runtime appearance does not mutate the capture template");

        for (var offset = 0; offset < original.Length; offset++)
        {
            var patchedField = offset is >= 20 and < 32 or >= 36 and < 44;
            if (!patchedField)
            {
                Check.Equal(original[offset], patched[offset], $"runtime appearance preserves byte {offset}");
            }
        }

        var stream = PacketBuilder.CapturedMonsterSpawns([state, state]);
        Check.Equal(patched.Length * 2, stream.Length, "runtime appearance stream length");
        Check.True(
            stream.AsSpan(0, patched.Length).SequenceEqual(patched) &&
            stream.AsSpan(patched.Length, patched.Length).SequenceEqual(patched),
            "runtime appearance stream contains patched packets in order");
        return Task.CompletedTask;
    }

    private static Task CheckSharedBoundedMonsterRuntimeAsync()
    {
        var initializedAt = new DateTimeOffset(2026, 5, 12, 17, 56, 0, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10001,
            176.979568f,
            -17.154812f,
            "A_normal_stub_001");
        var runtimeA = new MonsterMapRuntime(0, [definition], initializedAt);
        var runtimeB = new MonsterMapRuntime(0, [definition], initializedAt);
        var now = initializedAt;
        var starts = 0;
        var arrivals = 0;

        for (var tickIndex = 0; tickIndex < 8_000; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            var tickA = runtimeA.Advance(now);
            var tickB = runtimeB.Advance(now);
            Check.Equal(tickA.PositionsChanged, tickB.PositionsChanged, "deterministic runtime movement flag");
            Check.Equal(tickA.Updates.Count, tickB.Updates.Count, "deterministic runtime update count");

            for (var updateIndex = 0; updateIndex < tickA.Updates.Count; updateIndex++)
            {
                var updateA = tickA.Updates[updateIndex];
                var updateB = tickB.Updates[updateIndex];
                Check.True(updateA.Kind == updateB.Kind, "deterministic runtime update kind");
                Check.Equal(updateA.Monster.X, updateB.Monster.X, "deterministic runtime update X");
                Check.Equal(updateA.Monster.Z, updateB.Monster.Z, "deterministic runtime update Z");

                if (updateA.Kind == MonsterRuntimeUpdateKind.Started)
                {
                    starts++;
                    var speed = MathF.Sqrt(
                        (updateA.Monster.VelocityX * updateA.Monster.VelocityX) +
                        (updateA.Monster.VelocityZ * updateA.Monster.VelocityZ));
                    Check.True(
                        MathF.Abs(speed - MonsterMapRuntime.MovementStep) < 0.00001f,
                        "roaming step magnitude is the captured 0.38 units");
                    Check.True(
                        updateA.Monster.MovementTicks is >= MonsterMapRuntime.MinimumMovementTicks and
                            <= MonsterMapRuntime.MaximumMovementTicks,
                        "roaming leg uses one to twenty-one captured ticks");
                }
                else if (updateA.Kind == MonsterRuntimeUpdateKind.Arrived)
                {
                    arrivals++;
                    var idleSeconds = (updateA.Monster.NextMovementAt - now).TotalSeconds;
                    Check.True(
                        idleSeconds >= 15 && idleSeconds <= 20.001,
                        "arrival schedules the next deterministic roam within 15-20 seconds");
                    var expectedFacing = MathF.Atan2(
                        updateA.Monster.VelocityX,
                        updateA.Monster.VelocityZ);
                    Check.True(
                        MathF.Abs(expectedFacing - updateA.Monster.Facing) < 0.00001f,
                        "arrival facing is atan2(dx,dz)");
                }
            }

            var snapshotA = runtimeA.Snapshot().Single();
            var snapshotB = runtimeB.Snapshot().Single();
            Check.Equal(snapshotA.X, snapshotB.X, "deterministic current X");
            Check.Equal(snapshotA.Z, snapshotB.Z, "deterministic current Z");
            var homeDistance = Math.Sqrt(
                Math.Pow(snapshotA.X - snapshotA.HomeX, 2) +
                Math.Pow(snapshotA.Z - snapshotA.HomeZ, 2));
            Check.True(
                homeDistance <= MonsterMapRuntime.MaximumRoamRadius + 0.0001,
                "every interpolated roaming position remains within eight units of home");
        }

        Check.True(starts >= 20 && arrivals >= 20, "bounded simulation exercises repeated roaming legs");
        Check.True(
            runtimeA.TryGetSnapshot(definition.ObjectId, out var current) && current.IsAlive && current.IsSpawned,
            "runtime exposes the current live monster snapshot");
        Check.True(
            !runtimeA.TryGetSnapshot(99999, out _),
            "runtime rejects an unknown monster snapshot");

        var map = new MapInstance(0);
        var sharedRuntime = map.InitializeMonsters([definition], initializedAt);
        var ignoredDefinition = CreateCapturedMonster(
            10002,
            definition.X + 10,
            definition.Z + 10,
            "A_normal_stub_003");
        var sameRuntime = map.InitializeMonsters([ignoredDefinition], initializedAt + TimeSpan.FromMinutes(1));
        Check.True(ReferenceEquals(sharedRuntime, sameRuntime), "map monster runtime initializes exactly once");
        Check.True(
            map.TryGetMonsterSnapshot(definition.ObjectId, out _) &&
            !map.TryGetMonsterSnapshot(ignoredDefinition.ObjectId, out _),
            "all viewers share the first authoritative map monster set");

        var lifecycle = new MonsterMapRuntime(0, [definition], initializedAt);
        var lethalAt = initializedAt + TimeSpan.FromSeconds(21);
        var movementStart = lifecycle.Advance(lethalAt);
        Check.True(
            movementStart.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Started),
            "lifecycle fixture kills a monster during an active roaming leg");
        Check.True(
            lifecycle.TryApplyDamage(
                definition.ObjectId,
                1_000,
                lethalAt,
                out var damageResult),
            "atomic damage resolves a known monster");
        Check.Equal(237u, damageResult.BeforeHealth, "lethal damage before HP");
        Check.Equal(0u, damageResult.AfterHealth, "lethal damage after HP");
        var lethalPacket = PacketBuilder.SkillDamage(1, definition.ObjectId, 0, 1_000, 0, definition.X, definition.Z);
        Check.Equal(1_000u, ReadUInt32(lethalPacket, 16), "lethal packet keeps raw damage above remaining HP");
        Check.True(
            damageResult.Killed &&
            !damageResult.Monster.IsAlive &&
            damageResult.Monster.IsSpawned &&
            !damageResult.Monster.IsMoving,
            "death atomically stops roaming but retains the corpse");

        var deathTick = lifecycle.Advance(lethalAt);
        Check.True(
            deathTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Died]),
            "death emits exactly one immediate state event");
        lifecycle.Advance(lethalAt + TimeSpan.FromMilliseconds(4_999));
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var corpse) && corpse.IsSpawned && !corpse.IsAlive,
            "corpse remains spawned until five seconds");
        var despawnTick = lifecycle.Advance(lethalAt + TimeSpan.FromSeconds(5));
        Check.True(
            despawnTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Despawned]),
            "corpse emits a despawn event at five seconds");
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var despawned) && !despawned.IsSpawned,
            "despawned corpse leaves monster visibility");
        lifecycle.Advance(lethalAt + TimeSpan.FromMilliseconds(9_999));
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var waiting) && !waiting.IsSpawned,
            "monster remains absent before the ten-second respawn");
        var respawnTick = lifecycle.Advance(lethalAt + TimeSpan.FromSeconds(10));
        Check.True(
            respawnTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Respawned]),
            "monster emits a respawn event at ten seconds");
        var respawned = respawnTick.Updates.Single().Monster;
        Check.True(respawned.IsAlive && respawned.IsSpawned, "respawn restores live spawned state");
        Check.Equal(respawned.MaximumHealth, respawned.CurrentHealth, "respawn restores full HP");
        Check.Equal(respawned.HomeX, respawned.X, "respawn returns to home X");
        Check.Equal(respawned.HomeZ, respawned.Z, "respawn returns to home Z");
        return Task.CompletedTask;
    }

    private static Task CheckMonsterRetaliationRuntimeAsync()
    {
        var start = new DateTimeOffset(2026, 5, 12, 17, 59, 50, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10013,
            100f,
            50f,
            "A_normal_stub_001",
            tier: 1,
            maximumHealth: 237);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 8.68f,
            Z: definition.Z,
            IsAlive: true);

        var passive = new MonsterMapRuntime(0, [definition], start);
        var passiveTick = passive.Advance(start + MonsterMapRuntime.TickInterval, [target]);
        Check.True(
            passiveTick.Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "nearby players do not proximity-aggro passive monsters");

        var runtime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var hit) &&
            !hit.Killed,
            "a nonlethal hit attaches retaliation aggro");

        var chaseStart = runtime.Advance(start, [target]);
        var initialMovement = chaseStart.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Started);
        Check.Equal(0u, initialMovement.MovementMode, "initial combat chase uses movement mode zero");

        var now = start;
        var movementSteps = 0;
        MonsterRuntimeTick arrivalTick = new(false, []);
        while (movementSteps < 30)
        {
            now += MonsterMapRuntime.TickInterval;
            var tick = runtime.Advance(now, [target]);
            movementSteps++;
            if (tick.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Arrived))
            {
                arrivalTick = tick;
                break;
            }

            var continuation = tick.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Started);
            Check.Equal(1u, continuation.MovementMode, "combat chase continuation uses movement mode one");
            if (movementSteps == 5)
            {
                Check.True(
                    runtime.TryApplyDamage(
                        definition.ObjectId,
                        damage: 1,
                        attackerCharacterId: target.CharacterId,
                        now,
                        out var repeatedChaseHit) &&
                    !repeatedChaseHit.Killed,
                    "a repeated hit from the aggro target preserves an active chase");
            }
        }

        Check.Equal(15, movementSteps, "8.68-unit chase reaches three-unit attack range in fifteen steps");
        var arrival = arrivalTick.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Arrived);
        Check.Equal(1u, arrival.MovementEndField ?? 0, "combat movement end carries field one");
        var distance = Math.Sqrt(
            Math.Pow(arrival.Monster.X - target.X, 2) +
            Math.Pow(arrival.Monster.Z - target.Z, 2));
        Check.True(distance <= MonsterMapRuntime.CombatRange + 0.0001, "combat chase stops within three units");

        now += MonsterMapRuntime.TickInterval;
        var firstAttack = runtime.Advance(now, [target]);
        var attack = firstAttack.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Attacked);
        Check.Equal(target.CharacterId, attack.TargetCharacterId ?? 0, "monster attacks the character who hit it");
        Check.Equal(target.X, attack.TargetX, "monster attack captures target X");
        Check.Equal(target.Z, attack.TargetZ, "monster attack captures target Z");

        for (var cooldownTick = 1; cooldownTick < MonsterMapRuntime.AttackCooldownTicks; cooldownTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            if (cooldownTick == 5)
            {
                Check.True(
                    runtime.TryApplyDamage(
                        definition.ObjectId,
                        damage: 1,
                        attackerCharacterId: target.CharacterId,
                        now,
                        out var repeatedAttackHit) &&
                    !repeatedAttackHit.Killed,
                    "a repeated hit from the aggro target preserves the attack cooldown");
            }

            Check.True(
                runtime.Advance(now, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
                $"monster does not attack early at cooldown tick {cooldownTick}");
        }

        now += MonsterMapRuntime.TickInterval;
        Check.True(
            runtime.Advance(now, [target]).Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked),
            "monster repeats its attack exactly twenty-one ticks later");

        runtime.ClearAggroForCharacter(target.CharacterId, now);
        now += MonsterMapRuntime.AttackCooldown;
        Check.True(
            runtime.Advance(now, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "clearing a disconnected/dead target stops retaliation");

        var lethal = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            lethal.TryApplyDamage(
                definition.ObjectId,
                damage: 1_000,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var lethalHit) && lethalHit.Killed,
            "lethal player damage resolves without retaliation");
        Check.True(
            lethal.Advance(start, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "dead monsters never attack their killer");
        return Task.CompletedTask;
    }

    private static Task CheckMonsterLeashReturnAsync()
    {
        var start = new DateTimeOffset(2026, 5, 12, 18, 0, 0, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10014,
            100f,
            50f,
            "A_normal_stub_001",
            tier: 1,
            maximumHealth: 237);
        var target = new MonsterCombatTarget(
            CharacterId: 732,
            X: definition.X + 20f,
            Z: definition.Z,
            IsAlive: true);
        var runtime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 37,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var hit) &&
            hit.AfterHealth == 200,
            "leash fixture begins with a damaged aggroed monster");

        Check.True(
            MonsterMapRuntime.CombatLeashRadius >=
            MonsterMapRuntime.MaximumRoamRadius * 4,
            "combat chase boundary is substantially larger than idle roaming");

        runtime.Advance(start, [target]);
        var now = start;
        for (var chaseTick = 0; chaseTick < 24; chaseTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            runtime.Advance(now, [target]);
        }

        var chased = runtime.Snapshot().Single();
        var chasedHomeDistance = Math.Sqrt(
            Math.Pow(chased.X - chased.HomeX, 2) +
            Math.Pow(chased.Z - chased.HomeZ, 2));
        Check.True(
            chased.X > chased.HomeX &&
            chasedHomeDistance > MonsterMapRuntime.MaximumRoamRadius &&
            chased.CurrentHealth == 200 &&
            chased.IsAlive &&
            chased.IsSpawned,
            "monster chases well beyond the former eight-unit leash without resetting");

        var escapedTarget = target with
        {
            X = definition.X + MonsterMapRuntime.CombatLeashRadius +
                MonsterMapRuntime.CombatRange + 1f
        };
        now += MonsterMapRuntime.TickInterval;
        var returnStartTick = runtime.Advance(now, [escapedTarget]);
        var returnStart = returnStartTick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Started);
        Check.Equal(0u, returnStart.MovementMode, "leash return starts a new inward movement leg");
        Check.True(
            returnStart.Monster.IsAlive &&
            returnStart.Monster.IsSpawned &&
            returnStart.Monster.IsMoving &&
            returnStart.Monster.CombatPhase == MonsterCombatPhase.Returning &&
            returnStart.Monster.VelocityX < 0 &&
            returnStart.Monster.CurrentHealth == 200 &&
            returnStart.Monster.SpawnGeneration == 1,
            "crossing the leash keeps the damaged monster visible while it turns home");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 100,
                attackerCharacterId: target.CharacterId,
                now,
                out var returnHit) &&
            returnHit.BeforeHealth == 200 &&
            returnHit.AfterHealth == 200 &&
            returnHit.Monster.CombatPhase == MonsterCombatPhase.Returning,
            "the visible returning monster is invulnerable");
        Check.True(
            runtime.TryApplyStun(
                definition.ObjectId,
                target.CharacterId,
                TimeSpan.FromSeconds(1),
                now,
                out var returnStun) &&
            !returnStun.Applied &&
            returnStun.Monster.CombatPhase == MonsterCombatPhase.Returning,
            "the visible returning monster rejects control effects");

        var previousHomeDistance = Math.Abs(returnStart.Monster.X - returnStart.Monster.HomeX);
        MonsterRuntimeUpdate? returned = null;
        for (var returnTick = 0; returnTick < 64 && returned is null; returnTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            var tick = runtime.Advance(now, [escapedTarget]);
            returned = tick.Updates.SingleOrDefault(update =>
                update.Kind == MonsterRuntimeUpdateKind.Returned);
            if (returned is null)
            {
                Check.True(
                    tick.Updates.All(update =>
                        update.Kind is not MonsterRuntimeUpdateKind.Attacked and
                            not MonsterRuntimeUpdateKind.Despawned),
                    "return movement neither attacks nor retires before reaching home");
                var snapshot = runtime.Snapshot().Single();
                var homeDistance = Math.Abs(snapshot.X - snapshot.HomeX);
                Check.True(
                    homeDistance <= previousHomeDistance + 0.0001,
                    "every return step moves monotonically toward home");
                previousHomeDistance = homeDistance;
                Check.True(
                    snapshot.IsAlive &&
                    snapshot.IsSpawned &&
                    snapshot.CurrentHealth == 200 &&
                    snapshot.CombatPhase == MonsterCombatPhase.Returning,
                    "the damaged old generation remains visible throughout its return");
            }
            else
            {
                Check.True(
                    tick.Updates.Select(update => update.Kind).SequenceEqual(
                        [MonsterRuntimeUpdateKind.Returned, MonsterRuntimeUpdateKind.Despawned]),
                    "exact-home arrival orders movement-end before retiring the old generation");
            }
        }

        Check.True(returned is not null, "leashed monster reaches its exact home");
        Check.Equal(1u, returned!.MovementEndField ?? 0, "home arrival emits movement completion");
        Check.True(
            returned.Monster.X == returned.Monster.HomeX &&
            returned.Monster.Z == returned.Monster.HomeZ &&
            returned.Monster.IsAlive &&
            returned.Monster.IsSpawned &&
            !returned.Monster.IsMoving &&
            returned.Monster.CurrentHealth == 200 &&
            returned.Monster.CombatPhase == MonsterCombatPhase.AwaitingRetirement &&
            returned.Monster.SpawnGeneration == 1,
            "home arrival preserves the damaged old generation until movement-end is published");
        var retired = runtime.Snapshot().Single();
        Check.True(
            !retired.IsAlive &&
            !retired.IsSpawned &&
            retired.CurrentHealth == 200 &&
            retired.SpawnGeneration == 1,
            "the old generation retires only after its exact-home snapshot is captured");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 100,
                attackerCharacterId: target.CharacterId,
                now,
                out var retiredHit) &&
            retiredHit.BeforeHealth == retiredHit.AfterHealth,
            "the retired generation cannot receive damage before replacement");

        now += MonsterMapRuntime.TickInterval;
        var respawnTick = runtime.Advance(now, [escapedTarget]);
        var respawned = respawnTick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Respawned).Monster;
        Check.True(
            respawned.IsAlive &&
            respawned.IsSpawned &&
            !respawned.IsMoving &&
            respawned.CombatPhase == MonsterCombatPhase.None &&
            respawned.CurrentHealth == respawned.MaximumHealth &&
            respawned.X == respawned.HomeX &&
            respawned.Z == respawned.HomeZ &&
            respawned.SpawnGeneration == 2,
            "the following world tick creates a fresh full-health runtime generation at home");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now,
                out var replacementHit) &&
            replacementHit.AfterHealth + 1 == replacementHit.BeforeHealth,
            "the replacement is attackable immediately after its respawn tick");

        var queuedAtHomeRuntime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            queuedAtHomeRuntime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                start,
                out _),
            "at-home return fixture acquires aggro");
        queuedAtHomeRuntime.ClearAggroForCharacter(target.CharacterId, start);
        var queuedAtHomeTick = queuedAtHomeRuntime.Advance(
            start + TimeSpan.FromSeconds(2),
            []);
        Check.True(
            queuedAtHomeTick.Updates.Select(update => update.Kind)
                .SequenceEqual(
                    [MonsterRuntimeUpdateKind.Returned, MonsterRuntimeUpdateKind.Despawned]) &&
            queuedAtHomeRuntime.Snapshot().Single() is
            {
                IsAlive: false,
                IsSpawned: false,
                CombatPhase: MonsterCombatPhase.AwaitingRetirement
            },
            "a queued at-home return orders movement-end before retirement without respawning");
        var queuedRespawnTick = queuedAtHomeRuntime.Advance(
            start + TimeSpan.FromSeconds(2) + MonsterMapRuntime.TickInterval,
            []);
        Check.True(
            queuedRespawnTick.Updates.Single().Kind == MonsterRuntimeUpdateKind.Respawned &&
            queuedAtHomeRuntime.Snapshot().Single() is
            {
                IsAlive: true,
                IsSpawned: true,
                SpawnGeneration: 2
            },
            "queued at-home replacement spawns as a fresh generation one tick later");

        var lostTargetRuntime = new MonsterMapRuntime(0, [definition], start);
        lostTargetRuntime.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: target.CharacterId,
            now: start,
            out _);
        lostTargetRuntime.Advance(start, [target]);
        lostTargetRuntime.Advance(start + MonsterMapRuntime.TickInterval, [target]);
        var lostTargetTick = lostTargetRuntime.Advance(
            start + (MonsterMapRuntime.TickInterval * 2),
            []);
        Check.True(
            lostTargetTick.Updates.Any(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.IsSpawned &&
                update.Monster.CombatPhase == MonsterCombatPhase.Returning),
            "a missing combat target also starts a visible smooth return");

        var boundaryRuntime = new MonsterMapRuntime(0, [definition], start);
        var radialTarget = new MonsterCombatTarget(
            CharacterId: target.CharacterId,
            X: definition.X + MonsterMapRuntime.CombatLeashRadius +
                MonsterMapRuntime.CombatRange - 0.1f,
            Z: definition.Z,
            IsAlive: true);
        boundaryRuntime.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: radialTarget.CharacterId,
            now: start,
            out _);
        var boundaryNow = start;
        boundaryRuntime.Advance(boundaryNow, [radialTarget]);
        MonsterRuntimeSnapshot? radialArrival = null;
        for (var chaseTick = 0; chaseTick < 128 && radialArrival is null; chaseTick++)
        {
            boundaryNow += MonsterMapRuntime.TickInterval;
            boundaryRuntime.Advance(boundaryNow, [radialTarget]);
            var snapshot = boundaryRuntime.Snapshot().Single();
            if (snapshot.CombatPhase == MonsterCombatPhase.Attacking)
            {
                radialArrival = snapshot;
            }
        }

        Check.True(
            radialArrival is not null &&
            radialArrival.X - radialArrival.HomeX >
            MonsterMapRuntime.CombatLeashRadius - MonsterMapRuntime.CombatRange - 0.2f,
            "boundary fixture first reaches the outer attack ring without leashing");
        var tangentialTarget = new MonsterCombatTarget(
            CharacterId: target.CharacterId,
            X: radialArrival!.X,
            Z: radialArrival.HomeZ + 14f,
            IsAlive: true);
        MonsterRuntimeUpdate? predictedBoundaryReturn = null;
        for (var boundaryTick = 0; boundaryTick < 32 && predictedBoundaryReturn is null; boundaryTick++)
        {
            boundaryNow += MonsterMapRuntime.TickInterval;
            var tick = boundaryRuntime.Advance(boundaryNow, [tangentialTarget]);
            predictedBoundaryReturn = tick.Updates.SingleOrDefault(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.CombatPhase == MonsterCombatPhase.Returning);
        }

        var boundaryTargetHomeDistance = Math.Sqrt(
            Math.Pow(tangentialTarget.X - definition.X, 2) +
            Math.Pow(tangentialTarget.Z - definition.Z, 2));
        Check.True(
            predictedBoundaryReturn is not null &&
            boundaryTargetHomeDistance <=
            MonsterMapRuntime.CombatLeashRadius + MonsterMapRuntime.CombatRange &&
            predictedBoundaryReturn.Monster.IsAlive &&
            predictedBoundaryReturn.Monster.IsSpawned,
            "predicted next chase step crossing the home boundary starts a visible return");
        return Task.CompletedTask;
    }

    private static async Task CheckMonsterReturnViewerPacketOrderAsync()
    {
        const int movementStartLength = 40;
        const int movementEndLength = 34;
        const int lifecycleMarkerLength = 8;
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            using var targetOutbound = new TcpClient();
            var targetAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await targetOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var targetInbound = await targetAcceptTask;
            await using var targetSession = new ClientSession(targetOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var targetCharacter = CreateCharacter();
            targetCharacter.Id += 1;
            targetCharacter.AccountId += 1;
            targetCharacter.Name = "ReturnTarget";
            targetCharacter.CurrentMap = 0;

            var monster = CreateCapturedMonster(
                10038,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            targetCharacter.PositionX = monster.X + 20f;
            targetCharacter.PositionZ = monster.Z;
            var initializedAt = DateTimeOffset.UtcNow;
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(viewerCharacter.CurrentMap, [monster], initializedAt);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id));
            registry.JoinMap(
                targetSession,
                targetCharacter.AccountId,
                targetCharacter,
                WorldObjectIds.ForPlayer(targetCharacter.Id));

            await using (var transition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("replacement viewer transition was unavailable"))
            {
                Check.True(
                    transition.Delta.Entering.Select(entry => entry.ObjectId).SequenceEqual([monster.ObjectId]),
                    "replacement viewer initially enters the monster AOI");
                transition.Commit();
            }

            Check.True(
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "replacement viewer is committed visible before the leash tick");

            var receiveCipher = new PacketCipher();
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    attackerCharacterId: targetCharacter.Id,
                    out _),
                "replacement setup damages and aggros the monster");

            var chaseAt = DateTimeOffset.UtcNow;
            var chaseStartRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementStartLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(chaseAt, timeout.Token);
            var chaseStartFrame = await chaseStartRead;
            receiveCipher.Transform(chaseStartFrame);
            AssertMonsterMovementFrame(
                chaseStartFrame,
                movementStartLength,
                expectedOpcode: 10016,
                monster.ObjectId,
                "initial chase start");

            for (var chaseStep = 1; chaseStep <= 6; chaseStep++)
            {
                var continuationRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    movementStartLength,
                    timeout.Token);
                await registry.AdvanceMonsterWorldOnceAsync(
                    chaseAt + TimeSpan.FromTicks(
                        MonsterMapRuntime.TickInterval.Ticks * chaseStep),
                    timeout.Token);
                var continuationFrame = await continuationRead;
                receiveCipher.Transform(continuationFrame);
                AssertMonsterMovementFrame(
                    continuationFrame,
                    movementStartLength,
                    expectedOpcode: 10016,
                    monster.ObjectId,
                    $"chase continuation {chaseStep}");
            }

            Check.Equal(
                true,
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var chasedMonster) &&
                chasedMonster.X > chasedMonster.HomeX &&
                chasedMonster.IsSpawned,
                "socket fixture first moves the damaged monster away from home");

            targetCharacter.PositionX = 500;
            targetCharacter.PositionZ = 500;
            var returnAt = chaseAt + TimeSpan.FromTicks(
                MonsterMapRuntime.TickInterval.Ticks * 7);
            var returnStartRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementStartLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(returnAt, timeout.Token);
            var returnStartFrame = await returnStartRead;
            receiveCipher.Transform(returnStartFrame);
            AssertMonsterMovementFrame(
                returnStartFrame,
                movementStartLength,
                expectedOpcode: 10016,
                monster.ObjectId,
                "leash return start");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var returningMonster) &&
                returningMonster.IsAlive &&
                returningMonster.IsSpawned &&
                returningMonster.IsMoving &&
                returningMonster.VelocityX < 0 &&
                returningMonster.CombatPhase == MonsterCombatPhase.Returning &&
                returningMonster.CurrentHealth == monsterMaximumHealth - 37,
                "return start keeps the damaged old generation visible and moving inward");
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 19,
                    attackerCharacterId: targetCharacter.Id,
                    out var blockedReturnHit) &&
                blockedReturnHit.BeforeHealth == blockedReturnHit.AfterHealth,
                "returning socket fixture is authoritatively invulnerable");

            var arrivalAndRetireRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementEndLength + lifecycleMarkerLength,
                timeout.Token);
            var arrivalAt = returnAt + TimeSpan.FromTicks(
                MonsterMapRuntime.TickInterval.Ticks *
                checked((long)returningMonster.RemainingMovementTicks));
            await registry.AdvanceMonsterWorldOnceAsync(arrivalAt, timeout.Token);
            var arrivalAndRetireFrames = await arrivalAndRetireRead;
            receiveCipher.Transform(arrivalAndRetireFrames);
            Check.Equal(
                (ushort)movementEndLength,
                ReadUInt16(arrivalAndRetireFrames, 0),
                "home-arrival movement-end length");
            Check.Equal(
                (ushort)10017,
                ReadUInt16(arrivalAndRetireFrames, 2),
                "home-arrival movement-end precedes retirement");
            Check.Equal(
                monster.ObjectId,
                ReadUInt32(arrivalAndRetireFrames, 4),
                "home-arrival movement-end object id");
            Check.Equal(
                (ushort)lifecycleMarkerLength,
                ReadUInt16(arrivalAndRetireFrames, movementEndLength),
                "retirement marker length");
            Check.Equal(
                (ushort)10023,
                ReadUInt16(arrivalAndRetireFrames, movementEndLength + 2),
                "retirement marker follows movement-end");
            Check.Equal(
                monster.ObjectId,
                ReadUInt32(arrivalAndRetireFrames, movementEndLength + 4),
                "retirement marker object id");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var retiredMonster) &&
                !retiredMonster.IsAlive &&
                !retiredMonster.IsSpawned &&
                retiredMonster.X == retiredMonster.HomeX &&
                retiredMonster.Z == retiredMonster.HomeZ &&
                retiredMonster.CurrentHealth == monsterMaximumHealth - 37 &&
                retiredMonster.SpawnGeneration == 1 &&
                !registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "retirement commits the damaged exact-home generation as absent");

            var respawnRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                lifecycleMarkerLength + appearanceLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                arrivalAt + MonsterMapRuntime.TickInterval,
                timeout.Token);
            var respawnFrames = await respawnRead;
            receiveCipher.Transform(respawnFrames);
            AssertMonsterReplacementFrames(
                respawnFrames,
                monster.ObjectId,
                monsterMaximumHealth,
                "leash replacement respawn");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacementMonster) &&
                replacementMonster.IsAlive &&
                replacementMonster.IsSpawned &&
                replacementMonster.CurrentHealth == monsterMaximumHealth &&
                replacementMonster.SpawnGeneration == 2 &&
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "respawn publishes a new full-health viewer-visible runtime generation");
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 19,
                    attackerCharacterId: targetCharacter.Id,
                    out var replacementHit) &&
                replacementHit.BeforeHealth == monsterMaximumHealth &&
                replacementHit.AfterHealth == monsterMaximumHealth - 19,
                "freshly published replacement is immediately attackable");

            registry.Remove(targetSession);
            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AssertMonsterMovementFrame(
        byte[] frame,
        int expectedLength,
        ushort expectedOpcode,
        uint monsterObjectId,
        string label)
    {
        Check.Equal(expectedLength, frame.Length, $"{label} frame length");
        Check.Equal((ushort)expectedLength, ReadUInt16(frame, 0), $"{label} declared length");
        Check.Equal(expectedOpcode, ReadUInt16(frame, 2), $"{label} opcode");
        Check.Equal(monsterObjectId, ReadUInt32(frame, 4), $"{label} object id");
    }

    private static void AssertMonsterReplacementFrames(
        byte[] stream,
        uint monsterObjectId,
        uint maximumHealth,
        string label)
    {
        const int lifecycleMarkerLength = 8;
        const int appearanceLength = 108;
        Check.Equal(
            lifecycleMarkerLength + appearanceLength,
            stream.Length,
            $"{label} combined frame length");
        Check.Equal((ushort)lifecycleMarkerLength, ReadUInt16(stream, 0), $"{label} marker length");
        Check.Equal((ushort)10023, ReadUInt16(stream, 2), $"{label} marker comes first");
        Check.Equal(monsterObjectId, ReadUInt32(stream, 4), $"{label} marker object id");

        Check.Equal(
            (ushort)appearanceLength,
            ReadUInt16(stream, lifecycleMarkerLength),
            $"{label} appearance length");
        Check.Equal(
            (ushort)10020,
            ReadUInt16(stream, lifecycleMarkerLength + 2),
            $"{label} fresh appearance follows marker");
        Check.Equal(
            monsterObjectId,
            ReadUInt32(stream, lifecycleMarkerLength + 8),
            $"{label} appearance object id");
        Check.Equal(
            maximumHealth,
            ReadUInt32(stream, lifecycleMarkerLength + 20),
            $"{label} current health is full");
        Check.Equal(
            maximumHealth,
            ReadUInt32(stream, lifecycleMarkerLength + 24),
            $"{label} maximum health");
    }

    private static async Task CheckMonsterGenerationReconciliationAsync()
    {
        const int removeLength = 12;
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10039,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var initializedAt = DateTimeOffset.UtcNow;
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                initializedAt);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                worldReady: false);

            await using (var initialTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("initial generation transition was unavailable"))
            {
                Check.True(
                    initialTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1
                    } && objectId == monster.ObjectId,
                    "non-ready viewer commits the first monster generation during bootstrap");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    attackerCharacterId: viewerCharacter.Id,
                    out _),
                "bootstrap-gap fixture damages generation one");
            var retirementAt = initializedAt + MonsterMapRuntime.TickInterval;
            await registry.AdvanceMonsterWorldOnceAsync(retirementAt, timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                retirementAt + MonsterMapRuntime.TickInterval,
                timeout.Token);
            Check.Equal(
                0,
                viewerInbound.Available,
                "non-ready viewer receives neither retirement nor respawn ticks");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacement) &&
                replacement.SpawnGeneration == 2 &&
                replacement.CurrentHealth == monsterMaximumHealth &&
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "viewer still holds generation one while runtime has reused the object ID for generation two");
            Check.True(
                registry.IsMonsterVisibleTo(
                    viewerSession,
                    monster.ObjectId,
                    spawnGeneration: 1) &&
                !registry.IsMonsterVisibleTo(
                    viewerSession,
                    monster.ObjectId,
                    replacement.SpawnGeneration),
                "viewer visibility remains tied to the stale generation until reconciliation");
            Check.True(
                !registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 10,
                    attackerCharacterId: viewerCharacter.Id,
                    expectedSpawnGeneration: 1,
                    out _),
                "stale generation-one attack cannot damage the unseen replacement");
            Check.True(
                !registry.TryApplyMonsterStun(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    viewerCharacter.Id,
                    TimeSpan.FromSeconds(1),
                    expectedSpawnGeneration: 1,
                    now: retirementAt + (MonsterMapRuntime.TickInterval * 2),
                    out _),
                "stale generation-one control cannot stun the unseen replacement");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var untouchedReplacement) &&
                untouchedReplacement.SpawnGeneration == 2 &&
                untouchedReplacement.CurrentHealth == monsterMaximumHealth &&
                !untouchedReplacement.IsStunned,
                "generation guard preserves replacement health and control state");

            Check.True(
                registry.TryMarkWorldReady(
                    viewerSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers) &&
                unseenPlayers.Count == 0,
                "bootstrap-gap viewer resumes world delivery");

            await using (var reconcileTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("generation reconciliation was unavailable"))
            {
                Check.True(
                    reconcileTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    reconcileTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 2
                    } && objectId == monster.ObjectId,
                    "generation mismatch is both a removal and a fresh entry despite stable object ID");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(
                        reconcileTransition.Delta.Leaving.ToArray()),
                    timeout.Token,
                    "MonsterGenerationReconcileRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        reconcileTransition.Delta.Entering
                            .Select(entry => entry.Appearance)
                            .ToArray()),
                    timeout.Token,
                    "MonsterGenerationReconcileSpawn",
                    framed: false);
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal((ushort)10024, ReadUInt16(stream, 2), "generation reconcile removes stale entity first");
                Check.Equal(monster.ObjectId, ReadUInt32(stream, 8), "generation reconcile removal object id");
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, removeLength + 2),
                    "generation reconcile publishes fresh appearance second");
                Check.Equal(
                    monster.ObjectId,
                    ReadUInt32(stream, removeLength + 8),
                    "generation reconcile appearance object id");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, removeLength + 20),
                    "generation reconcile appearance has full current health");
                reconcileTransition.Commit();
            }

            await using (var stableTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("stable generation transition was unavailable"))
            {
                Check.True(
                    stableTransition.Delta.Entering.Count == 0 &&
                    stableTransition.Delta.Leaving.Count == 0,
                    "committing generation two prevents duplicate reconciliation");
                stableTransition.Commit();
            }

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterOldGenerationEventSuppressionAsync()
    {
        const uint maximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var selfOutbound = new TcpClient();
            var selfAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await selfOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var selfInbound = await selfAcceptTask;
            await using var selfSession = new ClientSession(selfOutbound);

            using var worldOutbound = new TcpClient();
            var worldAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await worldOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var worldInbound = await worldAcceptTask;
            await using var worldSession = new ClientSession(worldOutbound);

            var selfCharacter = CreateCharacter();
            selfCharacter.CurrentMap = 0;
            selfCharacter.PositionX = 100;
            selfCharacter.PositionZ = 100;
            var worldCharacter = CreateCharacter();
            worldCharacter.Id += 1;
            worldCharacter.AccountId += 1;
            worldCharacter.Name = "GenerationWorldViewer";
            worldCharacter.CurrentMap = 0;
            worldCharacter.PositionX = 102;
            worldCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10046,
                101,
                101,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
            var start = new DateTimeOffset(2026, 5, 12, 19, 0, 0, TimeSpan.FromHours(12));
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(selfCharacter.CurrentMap, [monster], start);
            registry.JoinMap(
                selfSession,
                selfCharacter.AccountId,
                selfCharacter,
                WorldObjectIds.ForPlayer(selfCharacter.Id),
                worldReady: false);
            registry.JoinMap(
                worldSession,
                worldCharacter.AccountId,
                worldCharacter,
                WorldObjectIds.ForPlayer(worldCharacter.Id),
                worldReady: false);

            foreach (var (session, character) in new[]
                     {
                         (selfSession, selfCharacter),
                         (worldSession, worldCharacter)
                     })
            {
                await using var initialTransition =
                    await registry.BeginMonsterVisibilityTransitionAsync(
                        session,
                        character.CurrentMap,
                        character.PositionX,
                        character.PositionZ,
                        timeout.Token)
                    ?? throw new InvalidOperationException("old-generation initial transition was unavailable");
                Check.True(
                    initialTransition.Delta.Entering.Single().SpawnGeneration == 1,
                    "old-generation viewer initially commits generation one");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    maximumHealth,
                    attackerCharacterId: null,
                    expectedSpawnGeneration: 1,
                    now: start,
                    out var lethalDamage) &&
                lethalDamage.Killed,
                "old-generation event fixture kills generation one");
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11),
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11) + MonsterMapRuntime.TickInterval,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11) + (MonsterMapRuntime.TickInterval * 2),
                timeout.Token);
            Check.True(
                registry.TryGetMonsterSnapshot(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacement) &&
                replacement.SpawnGeneration == 2 &&
                replacement.CurrentHealth == maximumHealth,
                "old-generation packet fixture has a full-health replacement");

            Check.True(
                registry.TryMarkWorldReady(
                    selfSession,
                    new Dictionary<uint, long>(),
                    out var selfUnseen) &&
                selfUnseen.Count == 0,
                "self viewer activates before old-generation packet checks");
            var worldKnownRevisions = new Dictionary<uint, long>();
            while (!registry.TryMarkWorldReady(
                       worldSession,
                       worldKnownRevisions,
                       out var worldUnseen))
            {
                Check.True(worldUnseen.Count > 0, "world viewer activation has a resolvable player delta");
                foreach (var unseen in worldUnseen)
                {
                    worldKnownRevisions[unseen.ObjectId] = unseen.WorldRevision;
                }
            }

            foreach (var (session, character) in new[]
                     {
                         (selfSession, selfCharacter),
                         (worldSession, worldCharacter)
                     })
            {
                await using var replacementTransition =
                    await registry.BeginMonsterVisibilityTransitionAsync(
                        session,
                        character.CurrentMap,
                        character.PositionX,
                        character.PositionZ,
                        timeout.Token)
                    ?? throw new InvalidOperationException("replacement transition was unavailable");
                Check.True(
                    replacementTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    replacementTransition.Delta.Entering.Single().SpawnGeneration == 2,
                    "viewer replaces generation one before delayed packets arrive");
                replacementTransition.Commit();
            }

            var oldGenerationPackets = new (string Label, byte[] Packet)[]
            {
                (
                    "SkillImpact",
                    PacketBuilder.SkillCastImpact(
                        0x1448u,
                        monster.ObjectId,
                        2000,
                        monster.X,
                        monster.Z)),
                (
                    "StunStatus",
                    PacketBuilder.WorldObjectStatusEffects(
                        monster.ObjectId,
                        [new ClientStatusEffect(4001, 1)])),
                (
                    "DeathProgression",
                    PacketBuilder.MonsterDeathReward(
                        monster.ObjectId,
                        0x1448u,
                        currentExperience: 80,
                        currentTalentExperience: 2,
                        currentTalentPoints: 0))
            };
            foreach (var (label, eventPacket) in oldGenerationPackets)
            {
                Check.Equal(
                    false,
                    await registry.DeliverMonsterPacketToViewerAsync(
                        selfSession,
                        selfCharacter.CurrentMap,
                        monster.ObjectId,
                        eventPacket,
                        expectedSpawnGeneration: 1,
                        timeout.Token,
                        $"DelayedOldGeneration{label}Self"),
                    $"delayed old-generation {label} is suppressed for self");
                Check.Equal(
                    0,
                    await registry.BroadcastToMonsterViewersAsync(
                        selfCharacter.CurrentMap,
                        monster.ObjectId,
                        eventPacket,
                        timeout.Token,
                        excludeSession: selfSession,
                        label: $"DelayedOldGeneration{label}World",
                        expectedSpawnGeneration: 1),
                    $"delayed old-generation {label} is suppressed for world viewers");
            }

            Check.Equal(0, selfInbound.Available, "self receives no generation-one event bytes on replacement");
            Check.Equal(0, worldInbound.Available, "world receives no generation-one event bytes on replacement");
            var replacementMarker = PacketBuilder.MonsterLifecycleMarker(monster.ObjectId);
            var selfRead = ReadExactlyAsync(
                selfInbound.GetStream(),
                replacementMarker.Length,
                timeout.Token);
            var worldRead = ReadExactlyAsync(
                worldInbound.GetStream(),
                replacementMarker.Length,
                timeout.Token);
            Check.Equal(
                true,
                await registry.DeliverMonsterPacketToViewerAsync(
                    selfSession,
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    replacementMarker,
                    expectedSpawnGeneration: 2,
                    timeout.Token,
                    "CurrentGenerationMarkerSelf"),
                "current-generation ordinary self packet still delivers");
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    replacementMarker,
                    timeout.Token,
                    excludeSession: selfSession,
                    label: "CurrentGenerationMarkerWorld",
                    expectedSpawnGeneration: 2),
                "current-generation ordinary world packet still delivers");
            var selfFrame = await selfRead;
            var selfCipher = new PacketCipher();
            selfCipher.Transform(selfFrame);
            var worldFrame = await worldRead;
            var worldCipher = new PacketCipher();
            worldCipher.Transform(worldFrame);
            Check.Equal((ushort)10023, ReadUInt16(selfFrame, 2), "current-generation self marker opcode");
            Check.Equal((ushort)10023, ReadUInt16(worldFrame, 2), "current-generation world marker opcode");

            registry.Remove(selfSession);
            registry.Remove(worldSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterSameGenerationActivationRefreshAsync()
    {
        const int removeLength = 12;
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        const uint damage = 37;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10040,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                worldReady: false);

            await using (var initialTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("initial health-gap transition was unavailable"))
            {
                Check.True(
                    initialTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1,
                        CurrentHealth: monsterMaximumHealth
                    } && objectId == monster.ObjectId,
                    "non-ready viewer captures generation one at full health");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage,
                    out var damageResult) &&
                damageResult.AfterHealth == monsterMaximumHealth - damage,
                "monster health changes while the bootstrap viewer is hidden");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var damagedMonster) &&
                damagedMonster.SpawnGeneration == 1 &&
                damagedMonster.CurrentHealth == monsterMaximumHealth - damage,
                "bootstrap health drift remains within the already-committed generation");
            Check.Equal(
                0,
                viewerInbound.Available,
                "non-ready viewer receives no health-change broadcast");
            Check.True(
                registry.TryMarkWorldReady(
                    viewerSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers) &&
                unseenPlayers.Count == 0,
                "same-generation fixture reaches the activation handoff");

            await using (var activationTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token,
                             forceRefreshVisible: true)
                         ?? throw new InvalidOperationException("activation health refresh was unavailable"))
            {
                Check.True(
                    activationTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    activationTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1,
                        CurrentHealth: var currentHealth
                    } &&
                    objectId == monster.ObjectId &&
                    currentHealth == monsterMaximumHealth - damage,
                    "activation forcibly replaces a stale same-generation appearance");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(
                        activationTransition.Delta.Leaving.ToArray()),
                    timeout.Token,
                    "MonsterActivationHealthRefreshRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        activationTransition.Delta.Entering
                            .Select(entry => entry.Appearance)
                            .ToArray()),
                    timeout.Token,
                    "MonsterActivationHealthRefreshSpawn",
                    framed: false);
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal(
                    (ushort)10024,
                    ReadUInt16(stream, 2),
                    "activation health refresh removes stale appearance first");
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, removeLength + 2),
                    "activation health refresh sends a fresh appearance second");
                Check.Equal(
                    monsterMaximumHealth - damage,
                    ReadUInt32(stream, removeLength + 20),
                    "fresh activation appearance carries authoritative current health");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, removeLength + 24),
                    "fresh activation appearance preserves maximum health");
                activationTransition.Commit();
            }

            await using (var stableTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("stable health transition was unavailable"))
            {
                Check.True(
                    stableTransition.Delta.Entering.Count == 0 &&
                    stableTransition.Delta.Leaving.Count == 0,
                    "forced activation refresh commits back to stable normal AOI tracking");
                stableTransition.Commit();
            }

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterEnteringViewerDamageLeaseAsync()
    {
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        const uint damage = 37;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10041,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id));

            var enteringTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    viewerCharacter.CurrentMap,
                    viewerCharacter.PositionX,
                    viewerCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("entering-viewer transition was unavailable");
            try
            {
                var enteringMonster = enteringTransition.Delta.Entering.Single();
                Check.True(
                    enteringMonster.ObjectId == monster.ObjectId &&
                    enteringMonster.CurrentHealth == monsterMaximumHealth &&
                    !registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                    "entering snapshot is full health and remains uncommitted during its send lease");
                Check.True(
                    registry.TryApplyMonsterDamage(
                        viewerCharacter.CurrentMap,
                        monster.ObjectId,
                        damage,
                        out var damageResult) &&
                    damageResult.AfterHealth == monsterMaximumHealth - damage,
                    "another actor can damage the monster while the appearance send is in flight");

                var damagePacket = PacketBuilder.PhysicalDamage(
                    WorldObjectIds.ForPlayer(viewerCharacter.Id),
                    monster.X,
                    0f,
                    monster.Z,
                    monster.ObjectId,
                    damage,
                    result: 1);
                var broadcastTask = registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damagePacket,
                    timeout.Token,
                    label: "MonsterEnteringViewerDamageRace",
                    healthMutation: damageResult.HealthMutation);
                Check.True(
                    !broadcastTask.IsCompleted,
                    "damage broadcast waits behind the entering viewer transition lease");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength + damagePacket.Length,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        [enteringMonster.Appearance]),
                    timeout.Token,
                    "MonsterEnteringViewerAppearance",
                    framed: false);
                enteringTransition.Commit();
                await enteringTransition.DisposeAsync();

                Check.Equal(
                    1,
                    await broadcastTask,
                    "damage broadcast re-checks committed visibility and reaches the entering viewer");
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, 2),
                    "stale full-health appearance is delivered before its queued damage");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, 20),
                    "race fixture appearance captured the pre-damage health");
                Check.Equal(
                    (ushort)10026,
                    ReadUInt16(stream, appearanceLength + 2),
                    "queued damage follows appearance commit");
                Check.Equal(
                    monster.ObjectId,
                    ReadUInt32(stream, appearanceLength + 20),
                    "queued damage targets the entering monster");
                Check.Equal(
                    damage,
                    ReadUInt32(stream, appearanceLength + 24),
                    "queued damage preserves its authoritative amount");
            }
            finally
            {
                await enteringTransition.DisposeAsync();
            }

            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var damagedMonster) &&
                damagedMonster.CurrentHealth == monsterMaximumHealth - damage,
                "runtime health matches the appearance-then-damage delivery sequence");
            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterHealthRevisionOrderingAsync()
    {
        const int appearanceLength = 108;
        const int removeLength = 12;
        const uint maximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10042,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id));

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    out var firstDamage) &&
                firstDamage.HealthMutation is
                {
                    BeforeHealthRevision: 0,
                    AfterHealthRevision: 1
                },
                "inverse race mutates health before the entering snapshot");
            var enteringTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    viewerCharacter.CurrentMap,
                    viewerCharacter.PositionX,
                    viewerCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("inverse-race transition was unavailable");
            var receiveCipher = new PacketCipher();
            try
            {
                var enteringMonster = enteringTransition.Delta.Entering.Single();
                Check.True(
                    enteringMonster.CurrentHealth == maximumHealth - 37 &&
                    enteringMonster.HealthRevision == 1,
                    "entering appearance captures the already-applied health revision");
                var firstPacket = PacketBuilder.PhysicalDamage(
                    WorldObjectIds.ForPlayer(viewerCharacter.Id),
                    monster.X,
                    0f,
                    monster.Z,
                    monster.ObjectId,
                    37,
                    result: 1);
                var firstBroadcast = registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    firstPacket,
                    timeout.Token,
                    label: "MonsterInverseDamageRace",
                    healthMutation: firstDamage.HealthMutation);
                Check.True(
                    !firstBroadcast.IsCompleted,
                    "inverse-race delta waits for the entering transition decision");

                var appearanceRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([enteringMonster.Appearance]),
                    timeout.Token,
                    "MonsterInverseDamageAppearance",
                    framed: false);
                enteringTransition.Commit();
                await enteringTransition.DisposeAsync();
                Check.Equal(
                    0,
                    await firstBroadcast,
                    "delta already represented by the committed appearance is suppressed");
                var appearance = await appearanceRead;
                receiveCipher.Transform(appearance);
                Check.Equal(
                    maximumHealth - 37,
                    ReadUInt32(appearance, 20),
                    "inverse-race viewer receives the reduced authoritative health once");
                Check.Equal(0, viewerInbound.Available, "suppressed inverse delta emits no trailing bytes");
            }
            finally
            {
                await enteringTransition.DisposeAsync();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 10,
                    out var secondDamage) &&
                secondDamage.HealthMutation is { BeforeHealthRevision: 1, AfterHealthRevision: 2 },
                "next damage advances exactly one health revision");
            var secondPacket = PacketBuilder.PhysicalDamage(
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                monster.X,
                0f,
                monster.Z,
                monster.ObjectId,
                10,
                result: 1);
            var secondRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                secondPacket.Length,
                timeout.Token);
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    timeout.Token,
                    label: "MonsterExactNextDamage",
                    healthMutation: secondDamage.HealthMutation),
                "exact-next health delta is delivered");
            var secondFrame = await secondRead;
            receiveCipher.Transform(secondFrame);
            Check.Equal((ushort)10026, ReadUInt16(secondFrame, 2), "exact-next damage opcode");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    timeout.Token,
                    label: "MonsterDuplicateDamage",
                    healthMutation: secondDamage.HealthMutation),
                "replayed damage revision is suppressed after successful delivery");

            var appliedSkippedDamage = registry.TryApplyMonsterDamage(
                viewerCharacter.CurrentMap,
                monster.ObjectId,
                damage: 5,
                out var skippedDamage);
            var appliedGapDamage = registry.TryApplyMonsterDamage(
                viewerCharacter.CurrentMap,
                monster.ObjectId,
                damage: 7,
                out var gapDamage);
            Check.True(
                appliedSkippedDamage &&
                appliedGapDamage &&
                skippedDamage.HealthMutation is { BeforeHealthRevision: 2, AfterHealthRevision: 3 } &&
                gapDamage.HealthMutation is { BeforeHealthRevision: 3, AfterHealthRevision: 4 },
                "gap fixture creates two ordered runtime revisions before delivery");
            var gapPacket = PacketBuilder.PhysicalDamage(
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                monster.X,
                0f,
                monster.Z,
                monster.ObjectId,
                7,
                result: 1);
            var reconciliationRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                removeLength + appearanceLength,
                timeout.Token);
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    gapPacket,
                    timeout.Token,
                    label: "MonsterHealthRevisionGap",
                    healthMutation: gapDamage.HealthMutation),
                "revision gap triggers authoritative viewer reconciliation");
            var reconciliation = await reconciliationRead;
            receiveCipher.Transform(reconciliation);
            Check.Equal((ushort)10024, ReadUInt16(reconciliation, 2), "gap reconciliation removes first");
            Check.Equal(
                (ushort)10020,
                ReadUInt16(reconciliation, removeLength + 2),
                "gap reconciliation respawns current appearance second");
            Check.Equal(
                maximumHealth - 37 - 10 - 5 - 7,
                ReadUInt32(reconciliation, removeLength + 20),
                "gap reconciliation carries current health through the latest revision");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    PacketBuilder.PhysicalDamage(
                        WorldObjectIds.ForPlayer(viewerCharacter.Id),
                        monster.X,
                        0f,
                        monster.Z,
                        monster.ObjectId,
                        5,
                        result: 1),
                    timeout.Token,
                    label: "MonsterDelayedGapDamage",
                    healthMutation: skippedDamage.HealthMutation),
                "older delta is suppressed after authoritative gap reconciliation");

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterSelfViewerDamageOrderingAsync()
    {
        const int appearanceLength = 108;
        const int removeLength = 12;
        const uint maximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var character = CreateCharacter();
            character.CurrentMap = 0;
            character.PositionX = 100;
            character.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10045,
                character.PositionX + 1,
                character.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(character.CurrentMap, [monster], DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id));
            var receiveCipher = new PacketCipher();

            await using (var initialTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             character.CurrentMap,
                             character.PositionX,
                             character.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("self-viewer initial transition was unavailable"))
            {
                var initialMonster = initialTransition.Delta.Entering.Single();
                var initialRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([initialMonster.Appearance]),
                    timeout.Token,
                    "SelfViewerInitialAppearance",
                    framed: false);
                initialTransition.Commit();
                var initialFrame = await initialRead;
                receiveCipher.Transform(initialFrame);
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    monster.ObjectId,
                    damage: 17,
                    attackerCharacterId: character.Id,
                    expectedSpawnGeneration: 1,
                    out var firstDamage),
                "self-viewer inverse fixture applies its first authoritative hit");
            var refreshTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    character.CurrentMap,
                    character.PositionX,
                    character.PositionZ,
                    timeout.Token,
                    forceRefreshVisible: true)
                ?? throw new InvalidOperationException("self-viewer refresh transition was unavailable");
            try
            {
                var refreshedMonster = refreshTransition.Delta.Entering.Single();
                Check.True(
                    refreshedMonster.HealthRevision == 1 &&
                    refreshedMonster.CurrentHealth == maximumHealth - 17,
                    "self-viewer forced appearance already includes its first hit");
                var selfPacket = PacketBuilder.PhysicalDamage(
                    0x1448u,
                    0f,
                    0f,
                    0f,
                    monster.ObjectId,
                    17,
                    result: 3);
                var selfDelivery = registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    selfPacket,
                    firstDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerInverseDamage");
                Check.True(
                    !selfDelivery.IsCompleted,
                    "self damage waits behind its own forced appearance transition");

                var refreshRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(monster.ObjectId),
                    timeout.Token,
                    "SelfViewerRefreshRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([refreshedMonster.Appearance]),
                    timeout.Token,
                    "SelfViewerRefreshAppearance",
                    framed: false);
                refreshTransition.Commit();
                await refreshTransition.DisposeAsync();
                Check.Equal(
                    false,
                    await selfDelivery,
                    "self delta already included by its appearance is suppressed");
                var refreshFrames = await refreshRead;
                receiveCipher.Transform(refreshFrames);
                Check.Equal((ushort)10024, ReadUInt16(refreshFrames, 2), "self refresh removes first");
                Check.Equal(
                    maximumHealth - 17,
                    ReadUInt32(refreshFrames, removeLength + 20),
                    "self refresh publishes reduced health exactly once");
                Check.Equal(0, viewerInbound.Available, "suppressed self delta emits no trailing bytes");
            }
            finally
            {
                await refreshTransition.DisposeAsync();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    monster.ObjectId,
                    damage: 9,
                    attackerCharacterId: character.Id,
                    expectedSpawnGeneration: 1,
                    out var secondDamage),
                "self-viewer exact-next fixture applies a second hit");
            var secondPacket = PacketBuilder.PhysicalDamage(
                0x1448u,
                0f,
                0f,
                0f,
                monster.ObjectId,
                9,
                result: 3);
            var secondRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                secondPacket.Length,
                timeout.Token);
            Check.Equal(
                true,
                await registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    secondDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerExactNextDamage"),
                "exact-next self damage is sent and advances its viewer stamp");
            var secondFrame = await secondRead;
            receiveCipher.Transform(secondFrame);
            Check.Equal((ushort)10026, ReadUInt16(secondFrame, 2), "self exact-next damage opcode");
            Check.Equal(
                false,
                await registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    secondDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerDuplicateDamage"),
                "duplicate self damage revision is suppressed");

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterAreaDamageDeliveryAsync()
    {
        const int appearanceLength = 108;
        const int markerLength = 8;
        const int oneHitClusterLength = 29;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var partialOutbound = new TcpClient();
            var partialAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await partialOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var partialInbound = await partialAcceptTask;
            await using var partialSession = new ClientSession(partialOutbound);

            using var farOutbound = new TcpClient();
            var farAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await farOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var farInbound = await farAcceptTask;
            await using var farSession = new ClientSession(farOutbound);

            var partialCharacter = CreateCharacter();
            partialCharacter.CurrentMap = 0;
            partialCharacter.PositionX = 70;
            partialCharacter.PositionZ = 100;
            var farCharacter = CreateCharacter();
            farCharacter.Id += 1;
            farCharacter.AccountId += 1;
            farCharacter.Name = "AreaFarViewer";
            farCharacter.CurrentMap = 0;
            farCharacter.PositionX = 500;
            farCharacter.PositionZ = 500;
            var firstMonster = CreateCapturedMonster(
                10043,
                100,
                100,
                "A_normal_stub_001");
            var secondMonster = CreateCapturedMonster(
                10044,
                164,
                100,
                "A_normal_stub_002");
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                partialCharacter.CurrentMap,
                [firstMonster, secondMonster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                partialSession,
                partialCharacter.AccountId,
                partialCharacter,
                WorldObjectIds.ForPlayer(partialCharacter.Id));
            registry.JoinMap(
                farSession,
                farCharacter.AccountId,
                farCharacter,
                WorldObjectIds.ForPlayer(farCharacter.Id));

            var partialTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    partialSession,
                    partialCharacter.CurrentMap,
                    partialCharacter.PositionX,
                    partialCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("partial AoE transition was unavailable");
            try
            {
                Check.True(
                    partialTransition.Delta.Entering.Select(monster => monster.ObjectId)
                        .SequenceEqual([firstMonster.ObjectId]),
                    "partial AoE viewer is entering only one of two hit monsters");
                await using (var farTransition =
                             await registry.BeginMonsterVisibilityTransitionAsync(
                                 farSession,
                                 farCharacter.CurrentMap,
                                 farCharacter.PositionX,
                                 farCharacter.PositionZ,
                                 timeout.Token)
                             ?? throw new InvalidOperationException("far AoE transition was unavailable"))
                {
                    Check.Equal(0, farTransition.Delta.Entering.Count, "far AoE viewer sees no hit monsters");
                    farTransition.Commit();
                }

                var appliedFirstDamage = registry.TryApplyMonsterDamage(
                    partialCharacter.CurrentMap,
                    firstMonster.ObjectId,
                    damage: 11,
                    out var firstDamage);
                var appliedSecondDamage = registry.TryApplyMonsterDamage(
                    partialCharacter.CurrentMap,
                    secondMonster.ObjectId,
                    damage: 13,
                    out var secondDamage);
                Check.True(
                    appliedFirstDamage && appliedSecondDamage,
                    "AoE fixture applies both authoritative monster hits");
                var visual = PacketBuilder.MonsterLifecycleMarker(0xABC001);
                var impact = PacketBuilder.MonsterLifecycleMarker(0xABC002);
                var areaBroadcast = registry.BroadcastMonsterAreaDamageToViewersAsync(
                    partialCharacter.CurrentMap,
                    visual,
                    impact,
                    WorldObjectIds.ForPlayer(partialCharacter.Id),
                    skillId: 2000,
                    [
                        new MonsterAreaDamageBroadcastHit(
                            firstDamage.HealthMutation!.Value,
                            11),
                        new MonsterAreaDamageBroadcastHit(
                            secondDamage.HealthMutation!.Value,
                            13)
                    ],
                    timeout.Token,
                    labelPrefix: "AreaDamageLeaseCheck");
                Check.True(
                    !areaBroadcast.IsCompleted,
                    "AoE delivery waits behind the partial viewer's entering appearance");

                var streamRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    appearanceLength + markerLength + markerLength + oneHitClusterLength,
                    timeout.Token);
                var enteringMonster = partialTransition.Delta.Entering.Single();
                await partialSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([enteringMonster.Appearance]),
                    timeout.Token,
                    "AreaDamageEnteringAppearance",
                    framed: false);
                partialTransition.Commit();
                await partialTransition.DisposeAsync();
                Check.Equal(1, await areaBroadcast, "AoE reaches only the viewer of an eligible hit");

                var stream = await streamRead;
                var partialCipher = new PacketCipher();
                partialCipher.Transform(stream);
                var visualOffset = appearanceLength;
                var impactOffset = visualOffset + markerLength;
                var clusterOffset = impactOffset + markerLength;
                Check.Equal((ushort)10020, ReadUInt16(stream, 2), "AoE appearance precedes event packets");
                Check.Equal((ushort)10023, ReadUInt16(stream, visualOffset + 2), "AoE visual follows appearance");
                Check.Equal((ushort)10023, ReadUInt16(stream, impactOffset + 2), "AoE impact follows visual");
                Check.Equal((ushort)10047, ReadUInt16(stream, clusterOffset + 2), "filtered AoE cluster opcode");
                Check.Equal(1, ReadInt32(stream, clusterOffset + 8), "AoE cluster contains one visible hit");
                Check.Equal(
                    firstMonster.ObjectId,
                    ReadUInt32(stream, clusterOffset + 17),
                    "AoE cluster includes the visible monster");
                Check.Equal(
                    11u,
                    ReadUInt32(stream, clusterOffset + 25),
                    "AoE cluster preserves visible hit damage");
                Check.Equal(0, farInbound.Available, "far viewer receives no monster-linked AoE bytes");
                Check.Equal(
                    0,
                    await registry.BroadcastMonsterAreaDamageToViewersAsync(
                        partialCharacter.CurrentMap,
                        visual,
                        impact,
                        WorldObjectIds.ForPlayer(partialCharacter.Id),
                        skillId: 2000,
                        [
                            new MonsterAreaDamageBroadcastHit(
                                firstDamage.HealthMutation!.Value,
                                11),
                            new MonsterAreaDamageBroadcastHit(
                                secondDamage.HealthMutation!.Value,
                                13)
                        ],
                        timeout.Token,
                        labelPrefix: "AreaDamageReplayCheck"),
                    "AoE replay suppresses already-applied and invisible hits");

                Check.True(
                    registry.TryApplyMonsterDamage(
                        partialCharacter.CurrentMap,
                        firstMonster.ObjectId,
                        damage: 7,
                        attackerCharacterId: partialCharacter.Id,
                        expectedSpawnGeneration: 1,
                        out var selfAreaDamage),
                    "self AoE fixture applies an exact-next visible hit");
                var selfAreaRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    oneHitClusterLength,
                    timeout.Token);
                Check.Equal(
                    true,
                    await registry.DeliverMonsterAreaDamageToViewerAsync(
                        partialSession,
                        partialCharacter.CurrentMap,
                        0x1448u,
                        skillId: 2000,
                        [new MonsterAreaDamageBroadcastHit(
                            selfAreaDamage.HealthMutation!.Value,
                            7)],
                        timeout.Token,
                        "AreaDamageSelfCheck"),
                    "self AoE cluster uses the same revision-aware viewer lease");
                var selfAreaFrame = await selfAreaRead;
                partialCipher.Transform(selfAreaFrame);
                Check.Equal((ushort)10047, ReadUInt16(selfAreaFrame, 2), "self AoE cluster opcode");
                Check.Equal(1, ReadInt32(selfAreaFrame, 8), "self AoE cluster hit count");
                Check.Equal(
                    false,
                    await registry.DeliverMonsterAreaDamageToViewerAsync(
                        partialSession,
                        partialCharacter.CurrentMap,
                        0x1448u,
                        skillId: 2000,
                        [new MonsterAreaDamageBroadcastHit(
                            selfAreaDamage.HealthMutation!.Value,
                            7)],
                        timeout.Token,
                        "AreaDamageSelfReplay"),
                    "self AoE replay is suppressed after stamp advancement");

                var partialZeroRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    markerLength * 2,
                    timeout.Token);
                var farZeroRead = ReadExactlyAsync(
                    farInbound.GetStream(),
                    markerLength * 2,
                    timeout.Token);
                Check.Equal(
                    2,
                    await registry.BroadcastMonsterAreaDamageToViewersAsync(
                        partialCharacter.CurrentMap,
                        visual,
                        impact,
                        WorldObjectIds.ForPlayer(partialCharacter.Id),
                        skillId: 2000,
                        [],
                        timeout.Token,
                        labelPrefix: "AreaDamageZeroHitCheck"),
                    "zero-hit AoE preserves map-wide cast visibility");
                var partialZero = await partialZeroRead;
                partialCipher.Transform(partialZero);
                var farZero = await farZeroRead;
                var farCipher = new PacketCipher();
                farCipher.Transform(farZero);
                Check.Equal((ushort)10023, ReadUInt16(partialZero, 2), "partial viewer zero-hit visual");
                Check.Equal((ushort)10023, ReadUInt16(partialZero, markerLength + 2), "partial viewer zero-hit impact");
                Check.Equal((ushort)10023, ReadUInt16(farZero, 2), "far viewer zero-hit visual");
                Check.Equal((ushort)10023, ReadUInt16(farZero, markerLength + 2), "far viewer zero-hit impact");
            }
            finally
            {
                await partialTransition.DisposeAsync();
            }

            registry.Remove(partialSession);
            registry.Remove(farSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterViewerRegistryAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var nearOutbound = new TcpClient();
            var nearAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await nearOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var nearInbound = await nearAcceptTask;
            await using var nearSession = new ClientSession(nearOutbound);

            using var farOutbound = new TcpClient();
            var farAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await farOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var farInbound = await farAcceptTask;
            await using var farSession = new ClientSession(farOutbound);

            var nearCharacter = CreateCharacter();
            nearCharacter.CurrentMap = 0;
            nearCharacter.PositionX = 100;
            nearCharacter.PositionZ = 100;
            var farCharacter = CreateCharacter();
            farCharacter.Id += 1;
            farCharacter.AccountId += 1;
            farCharacter.Name = "FarViewer";
            farCharacter.CurrentMap = 0;
            farCharacter.PositionX = 500;
            farCharacter.PositionZ = 500;

            var monster = CreateCapturedMonster(
                10038,
                nearCharacter.PositionX + 1,
                nearCharacter.PositionZ + 1,
                "A_normal_stub_001");
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                nearCharacter.CurrentMap,
                [monster],
                new DateTimeOffset(2026, 5, 12, 17, 56, 0, TimeSpan.FromHours(12)));
            registry.JoinMap(
                nearSession,
                nearCharacter.AccountId,
                nearCharacter,
                WorldObjectIds.ForPlayer(nearCharacter.Id));
            registry.JoinMap(
                farSession,
                farCharacter.AccountId,
                farCharacter,
                WorldObjectIds.ForPlayer(farCharacter.Id));

            await using (var nearTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             nearSession,
                             nearCharacter.CurrentMap,
                             nearCharacter.PositionX,
                             nearCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("near monster transition was unavailable"))
            {
                Check.True(
                    nearTransition.Delta.Entering.Select(entry => entry.ObjectId).SequenceEqual([monster.ObjectId]),
                    "near viewer receives the monster AOI entry");
                Check.True(
                    !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "monster AOI is uncommitted before its appearance send");
                nearTransition.Commit();
            }

            await using (var farTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             farSession,
                             farCharacter.CurrentMap,
                             farCharacter.PositionX,
                             farCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("far monster transition was unavailable"))
            {
                Check.Equal(0, farTransition.Delta.Entering.Count, "far viewer receives no monster AOI entry");
                farTransition.Commit();
            }

            Check.True(
                registry.IsMonsterVisibleTo(nearSession, monster.ObjectId) &&
                !registry.IsMonsterVisibleTo(farSession, monster.ObjectId),
                "committed monster visibility differs per viewer");
            var marker = PacketBuilder.MonsterLifecycleMarker(monster.ObjectId);
            var recipients = await registry.BroadcastToMonsterViewersAsync(
                nearCharacter.CurrentMap,
                monster.ObjectId,
                marker,
                timeout.Token,
                label: "MonsterViewerScopeCheck");
            Check.Equal(1, recipients, "monster broadcast reaches only committed AOI viewers");
            var received = new byte[marker.Length];
            await nearInbound.GetStream().ReadExactlyAsync(received, timeout.Token);
            Check.Equal(0, farInbound.Available, "far viewer receives no monster broadcast bytes");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    nearCharacter.CurrentMap,
                    monster.ObjectId,
                    marker,
                    timeout.Token,
                    excludeSession: nearSession),
                "monster broadcast exclusion can omit the only visible viewer");

            await using (var leavingTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             nearSession,
                             nearCharacter.CurrentMap,
                             nearCharacter.PositionX + 200,
                             nearCharacter.PositionZ + 200,
                             timeout.Token)
                         ?? throw new InvalidOperationException("leaving monster transition was unavailable"))
            {
                Check.True(
                    leavingTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]),
                    "viewer movement produces the monster AOI leave");
                Check.True(
                    registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "monster remains visible until its removal send commits");
                leavingTransition.Commit();
            }

            Check.True(
                !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                "monster removal commit updates combat AOI scope");

            var removalTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    nearSession,
                    nearCharacter.CurrentMap,
                    nearCharacter.PositionX + 200,
                    nearCharacter.PositionZ + 200,
                    timeout.Token)
                ?? throw new InvalidOperationException("map-removal transition was unavailable");
            try
            {
                var removalStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var removalTask = Task.Run(() =>
                {
                    removalStarted.SetResult();
                    registry.Remove(nearSession);
                });
                await removalStarted.Task.WaitAsync(timeout.Token);
                await Task.Delay(50, timeout.Token);
                Check.True(
                    !removalTask.IsCompleted,
                    "map removal waits for the active viewer transition lease");
                await removalTransition.DisposeAsync();
                await removalTask.WaitAsync(timeout.Token);
                Check.True(
                    !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "map removal clears viewer membership after the lease releases");
            }
            finally
            {
                await removalTransition.DisposeAsync();
            }

            registry.Remove(farSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static NpcSpawnDefinition CreateNpcDefinition(uint objectId, float x, float z)
    {
        return new NpcSpawnDefinition(
            0,
            "Sparta",
            $"Sparta_Test_{objectId}",
            $"Sparta_Test_{objectId}_Male1",
            objectId,
            x,
            z,
            objectId,
            NpcSpawnDefinitionFactory.DefaultAppearanceType,
            NpcSpawnDefinitionFactory.DefaultFacing,
            [],
            []);
    }

    private static CapturedMonsterSpawn CreateCapturedMonster(
        uint objectId,
        float x,
        float z,
        string templateKey,
        uint objectType = 0x00000212,
        uint tier = 1,
        uint maximumHealth = 237,
        short mapId = 0,
        string sceneKey = "Sparta")
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), tier);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), maximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), 1f);
        Encoding.ASCII.GetBytes(templateKey).CopyTo(packet.AsSpan(44));

        return new CapturedMonsterSpawn(
            mapId,
            sceneKey,
            templateKey,
            templateKey,
            objectId,
            x,
            z,
            packet);
    }

    private static async Task CheckMapRegistryWorldReadinessAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var inboundClient = await acceptTask;
            await using var session = new ClientSession(outboundClient);

            using var existingOutboundClient = new TcpClient();
            var existingAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await existingOutboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var existingInboundClient = await existingAcceptTask;
            await using var existingSession = new ClientSession(existingOutboundClient);

            var character = CreateCharacter();
            var existingCharacter = CreateCharacter();
            existingCharacter.Id += 1;
            existingCharacter.AccountId += 1;
            existingCharacter.Name = "ExistingHero";
            var registry = new GameSessionRegistry();
            registry.JoinMap(
                existingSession,
                existingCharacter.AccountId,
                existingCharacter,
                0x6402);
            Check.Throws<InvalidOperationException>(
                () => registry.JoinMap(session, character.AccountId, character, 0x6402, worldReady: false),
                "map registry rejects duplicate player world object IDs");
            registry.JoinMap(session, character.AccountId, character, 0x6401, worldReady: false);
            Check.Equal(1, registry.GetMapSessions(character.CurrentMap).Count, "not-ready session is hidden from map snapshots");
            Check.True(
                !registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out _),
                "not-ready session is hidden from object lookup");

            Check.True(
                !registry.TryMarkWorldReady(session, new Dictionary<uint, long>(), out var unseenPlayers),
                "activation waits for unseen ready players");
            Check.Equal(1, unseenPlayers.Count, "activation returns the unseen ready player");
            Check.Equal(0x6402u, unseenPlayers[0].ObjectId, "activation returns the correct unseen object");

            var knownWorldRevisions = unseenPlayers.ToDictionary(
                player => player.ObjectId,
                player => player.WorldRevision);
            existingCharacter.Equipment = "[2443,24,90,60,250,,10,12,1,1,0]#";
            registry.UpdateCharacter(existingSession, existingCharacter);
            Check.True(
                !registry.TryMarkWorldReady(session, knownWorldRevisions, out var changedPlayers),
                "activation waits for a player changed during bootstrap");
            Check.Equal(1, changedPlayers.Count, "activation returns the changed ready player");
            Check.True(
                changedPlayers[0].WorldRevision > unseenPlayers[0].WorldRevision,
                "changed player has a newer world revision");

            knownWorldRevisions[changedPlayers[0].ObjectId] = changedPlayers[0].WorldRevision;
            for (var movementIndex = 0; movementIndex < 512; movementIndex++)
            {
                existingCharacter.PositionX += 1f;
                registry.UpdateCharacter(
                    existingSession,
                    existingCharacter,
                    advanceWorldRevision: false);
            }

            Check.True(
                registry.TryMarkWorldReady(
                    session,
                    knownWorldRevisions,
                    out var remainingPlayers),
                "activation succeeds after existing players are known");
            Check.Equal(0, remainingPlayers.Count, "successful activation has no unseen players");
            Check.Equal(2, registry.GetMapSessions(character.CurrentMap).Count, "ready session enters map snapshots");
            Check.True(
                registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out var context) &&
                context.WorldReady,
                "ready session enters object lookup");
            registry.Remove(session);
            registry.Remove(existingSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void CheckNpcSpawnFrame(byte[] stream, int offset, NpcSpawnDefinition definition)
    {
        Check.Equal((ushort)108, ReadUInt16(stream, offset), $"NPC {definition.NpcKey} declared length");
        Check.Equal((ushort)0x2724, ReadUInt16(stream, offset + 2), $"NPC {definition.NpcKey} opcode");
        Check.Equal(definition.AppearanceType, ReadUInt32(stream, offset + 4), $"NPC {definition.NpcKey} appearance type");
        Check.Equal(definition.ObjectId, ReadUInt32(stream, offset + 8), $"NPC {definition.NpcKey} object id");
        Check.Equal(1u, ReadUInt32(stream, offset + 12), $"NPC {definition.NpcKey} active marker");
        Check.Equal(0u, ReadUInt32(stream, offset + 20), $"NPC {definition.NpcKey} neutral field");
        Check.Equal(1521u, ReadUInt32(stream, offset + 24), $"NPC {definition.NpcKey} appearance metadata");
        Check.Equal(definition.X, ReadSingle(stream, offset + 28), $"NPC {definition.NpcKey} X");
        Check.Equal(0f, ReadSingle(stream, offset + 32), $"NPC {definition.NpcKey} Y");
        Check.Equal(definition.Z, ReadSingle(stream, offset + 36), $"NPC {definition.NpcKey} Z");
        Check.Equal(definition.Facing, ReadSingle(stream, offset + 40), $"NPC {definition.NpcKey} facing");
        Check.Equal(
            definition.TemplateKey,
            ReadFixedAscii(stream, offset + 44, 64),
            $"NPC {definition.NpcKey} template");
    }

    private static async Task CheckConcurrentSendOrderingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellationToken = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            using var inboundClient = await acceptTask;
            await using var session = new ClientSession(outboundClient);

            var clearPackets = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(CreateConcurrentPacket)
                .ToArray();
            var receiveTask = ReadExactlyAsync(
                inboundClient.GetStream(),
                ConcurrentPacketCount * ConcurrentPacketLength,
                cancellationToken);

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendTasks = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(packetId => Task.Run(async () =>
                {
                    await startGate.Task;
                    await session.SendAsync(clearPackets[packetId], cancellationToken);
                }, cancellationToken))
                .ToArray();

            startGate.SetResult(true);
            await Task.WhenAll(sendTasks).WaitAsync(cancellationToken);
            var encryptedStream = await receiveTask;

            var receiveCipher = new PacketCipher();
            receiveCipher.Transform(encryptedStream);
            AssertConcurrentFrames(encryptedStream);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GameCharacter CreateCharacter()
    {
        return new GameCharacter
        {
            Id = 731,
            AccountId = 17,
            Name = "ProtocolHero",
            Gender = 2,
            Camp = 1,
            Profession = 3,
            Hair = 7,
            CurrentMap = 2,
            Level = 177,
            Experience = 123_987,
            CurrentHp = 123_456,
            CurrentMp = 23_456,
            MaxHp = 234_567,
            MaxMp = 34_567,
            TalentPoints = 456_789,
            TalentExperience = 67,
            PositionX = 321.125f,
            PositionZ = -654.75f,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = 91_001,
                PhysicalDefense = 82_002,
                MagicAttack = 73_003,
                MagicDefense = 64_004,
                Hit = 55_005,
                Dodge = 46_006,
                Critical = 37_007,
                CriticalResistance = 28_008,
                PhysicalDamageBonus = 1_234,
                MagicDamageBonus = 2_345,
                DamageAbsorb = 19_009,
                BeCureBonus = 3_456,
                CureBonus = 4_567
            }
        };
    }

    private static GameCharacter CreateAppearanceCharacter()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2443,24,90,60,250,,10,12,1,1,0]";
        slots[3] = "[2261,13,103,133,33,40,10,12,1,1,0]";
        slots[10] = "[1834,24,90,250,60,230,10,12,1,1,0]";
        slots[15] = "[14504,374,414,,,,7,8,1,1,0]";
        slots[20] = "[16184,,,,,,1,1,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';
        character.Face = 4;
        return character;
    }

    private static byte[] CreateConcurrentPacket(int packetId)
    {
        var packet = new byte[ConcurrentPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), ConcurrentPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), ConcurrentPacketOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), packetId);

        for (var offset = 8; offset < packet.Length; offset++)
        {
            packet[offset] = ConcurrentPayloadByte(packetId, offset);
        }

        return packet;
    }

    private static void AssertConcurrentFrames(byte[] clearStream)
    {
        Check.Equal(
            ConcurrentPacketCount * ConcurrentPacketLength,
            clearStream.Length,
            "concurrent send stream length");

        var seenPacketIds = new HashSet<int>();
        for (var frameIndex = 0; frameIndex < ConcurrentPacketCount; frameIndex++)
        {
            var frame = clearStream.AsSpan(
                frameIndex * ConcurrentPacketLength,
                ConcurrentPacketLength);
            Check.Equal(
                (ushort)ConcurrentPacketLength,
                BinaryPrimitives.ReadUInt16LittleEndian(frame),
                $"frame {frameIndex} declared length");
            Check.Equal(
                ConcurrentPacketOpcode,
                BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]),
                $"frame {frameIndex} opcode");

            var packetId = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
            Check.True(
                packetId is >= 0 and < ConcurrentPacketCount,
                $"frame {frameIndex} packet id {packetId} is in range");
            Check.True(seenPacketIds.Add(packetId), $"packet id {packetId} is unique");

            for (var offset = 8; offset < frame.Length; offset++)
            {
                Check.Equal(
                    ConcurrentPayloadByte(packetId, offset),
                    frame[offset],
                    $"frame {frameIndex} packet {packetId} payload byte {offset}");
            }
        }

        Check.Equal(ConcurrentPacketCount, seenPacketIds.Count, "unique concurrent packet count");
    }

    private static byte ConcurrentPayloadByte(int packetId, int offset)
    {
        return (byte)((packetId * 31 + offset * 17 + 0x5A) & 0xFF);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Loopback stream closed after {offset} of {buffer.Length} bytes.");
            }

            offset += read;
        }

        return buffer;
    }

    private static ushort ReadUInt16(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static float ReadSingle(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, 4));
    }

    private static string ReadFixedAscii(byte[] packet, int offset, int length)
    {
        return Encoding.ASCII.GetString(packet, offset, length).TrimEnd('\0');
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string description)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }

    public static void True(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {description}.");
        }
    }

    public static void Throws<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }
}
