using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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
            ("EnterMain character identity and saved location", CheckEnterMainCharacterIdentityAsync),
            ("Warrior talent ID-zero upgrade protocol", CheckWarriorTalentIdZeroUpgradeAsync),
            ("JSON warrior talent persistence", CheckJsonWarriorTalentPersistenceAsync),
            ("Warrior starter skill packets", CheckWarriorStarterSkillPacketsAsync),
            ("JSON provider starter skill", CheckJsonProviderStarterSkillAsync),
            ("Skill combat catalog", CheckSkillCombatCatalogAsync),
            ("Skill cast target and impact layout", CheckSkillCastTargetAndImpactAsync),
            ("Basic and monster attack packet layouts", CheckAttackPacketLayoutsAsync),
            ("Player passive recovery protocol", CheckPlayerRecoveryProtocolAsync),
            ("PlayerWorldSpawn layout", CheckPlayerWorldSpawnAsync),
            ("PlayerWorldSpawn captured appearance", CheckPlayerWorldAppearanceAsync),
            ("PlayerWorldSpawn full quality/grade extension", CheckPlayerWorldExtendedAppearanceAsync),
            ("Player auxiliary appearance packets", CheckPlayerAuxiliaryAppearanceAsync),
            ("PlayerInspectEquipment packed slots and details", CheckPlayerInspectExtendedSlotsAsync),
            ("PlayerStatusUpdate layout", CheckPlayerStatusUpdateAsync),
            ("Native status-effect sync layout", CheckPlayerStatusEffectsAsync),
            ("Post-enter UI-ready bootstrap gate", CheckPostEnterBootstrapGateAsync),
            ("Guarded bag-to-equipment persistence and snapshot", CheckGuardedEquipmentMoveAsync),
            ("Confirmed bag-item deletion protocol and persistence", CheckBagItemDeletionAsync),
            ("NPC definitions and spawn layout", CheckNpcDefinitionsAndSpawnLayoutAsync),
            ("NPC movement-cell visibility", CheckNpcMovementCellVisibilityAsync),
            ("Monster movement-cell visibility and spawn layout", CheckMonsterMovementCellVisibilityAsync),
            ("World boss outdoor-area catalog", WorldBossCatalogChecks.RunAsync),
            ("Persisted world-boss respawn across restart", CheckPersistedWorldBossRespawnAsync),
            ("Monster movement and lifecycle packet layouts", CheckMonsterMovementPacketLayoutsAsync),
            ("Monster runtime appearance patch", CheckMonsterRuntimeAppearancePatchAsync),
            ("Shared bounded monster runtime and lifecycle", CheckSharedBoundedMonsterRuntimeAsync),
            ("Passive monster retaliation state machine", CheckMonsterRetaliationRuntimeAsync),
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
            new(ExperienceStatusIds.VipPlatinum, ExperienceBoostKinds.Vip, 2_000, 4, null, "vip:platinum"),
            new(ExperienceStatusIds.FactionAreaExperience, ExperienceBoostKinds.FactionArea, 2_500, 1, expiresAt, "world-boss")
        ]);

        Check.Equal(65_500, state.TotalBonusBasisPoints, "all six fighter EXP families add their bonus rates");
        Check.Equal(604, state.ApplyTo(80), "base 80 EXP receives the additive 7.55x total multiplier");
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
            !MonsterCombatResolver.IsWithinBasicAttackRange(0, 0, 2.5f, 0),
            "normal attack rejects the strict 2.5-unit boundary");

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

        ReadOnlySpan<byte> expectedLegacyVisuals = [0xCA, 0xCA, 0xCA, 0xCA];
        Check.True(
            packet.AsSpan(81, expectedLegacyVisuals.Length).SequenceEqual(expectedLegacyVisuals),
            "legacy world decoder retains the captured Q10/G12 projection");
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

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusEffectsAsync()
    {
        const uint objectId = 0x7135B24E;
        var effects = new ClientStatusEffect[]
        {
            new(1504, 43_200),
            new(511, 28_800),
            new(1503, uint.MaxValue),
            new(586, 28_800)
        };
        var packet = PacketBuilder.PlayerStatusEffects(objectId, effects, 6.2f);

        Check.Equal(340, packet.Length, "status-effect packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "status-effect declared length");
        Check.Equal((ushort)10120, ReadUInt16(packet, 2), "status-effect opcode");
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
        Check.Equal(0u, ReadUInt32(packet, 172), "unused first StatusData field remains zero");
        Check.Equal(6.2f, ReadSingle(packet, 300), "status aggregate fighter-EXP bonus");
        Check.Equal(0u, ReadUInt32(packet, 336), "unused final StatusData field remains zero");

        var localPacket = PacketBuilder.PlayerStatusEffects([], 0f);
        Check.Equal(0x1448u, ReadUInt32(localPacket, 4), "status-effect local player object ID");
        Check.Equal(0u, ReadUInt32(localPacket, 8), "empty status-effect count");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects(
                Enumerable.Range(1, 21)
                    .Select(static id => new ClientStatusEffect((uint)id, 1))
                    .ToArray(),
                0f),
            "status-effect packet rejects more than twenty entries");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects([], float.NaN),
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

        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-bag-delete-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("bag-delete-owner", "");
            var other = await store.LoginOrCreateAccountAsync("bag-delete-other", "");
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "BagDeleteHero" });

            Check.Equal(4000u, KitBagSlots.GetItemId(character.KitBag, 0), "delete fixture source item");
            Check.Equal(4030u, KitBagSlots.GetItemId(character.KitBag, 1), "delete fixture neighboring item");

            var unauthorized = await store.DeleteKitBagItemAsync(other.Id, character.Id, 1);
            Check.True(unauthorized is null, "different account cannot delete bag item");

            var deleted = await store.DeleteKitBagItemAsync(owner.Id, character.Id, 0)
                ?? throw new InvalidOperationException("owner bag deletion returned no character");
            Check.Equal(0u, KitBagSlots.GetItemId(deleted.KitBag, 0), "deleted slot is empty");
            Check.Equal(4030u, KitBagSlots.GetItemId(deleted.KitBag, 1), "neighboring slot is unchanged");

            var reloaded = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("bag deletion fixture was not reloaded");
            Check.Equal(0u, KitBagSlots.GetItemId(reloaded.KitBag, 0), "bag deletion persists after reload");
            Check.Equal(4030u, KitBagSlots.GetItemId(reloaded.KitBag, 1), "unauthorized deletion did not remove neighbor");
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

        var matchingCharacter = new GameCharacter { KitBag = GameDefaults.DefaultKitBag };
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

            var unequipped = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 2)
                ?? throw new InvalidOperationException("starter sword could not be moved into the bag");
            Check.Equal(1000u, KitBagSlots.GetItemId(unequipped.KitBag, 2), "starter sword moved into bag");

            var equipped = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 2,
                requestedEquipmentSlot: -1)
                ?? throw new InvalidOperationException("starter sword could not be equipped again");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(equipped.Equipment, equipped.Profession, EquipmentSlots.Weapon),
                "starter sword is equipped authoritatively");
            Check.Equal(0u, KitBagSlots.GetItemId(equipped.KitBag, 2), "source bag slot is cleared after equip");

            var snapshot = PacketBuilder.EquipmentItemEquipSnapshot(
                equipped,
                sourceSlot: 2,
                EquipmentSlots.Weapon);
            Check.Equal(92, snapshot.Length, "equip snapshot length");
            Check.Equal((ushort)10051, ReadUInt16(snapshot, 2), "equip snapshot opcode");
            Check.Equal(0x1448u, ReadUInt32(snapshot, 4), "equip snapshot local player object ID");
            Check.Equal(0u, ReadUInt32(snapshot, 8), "equip snapshot bag operation marker");
            Check.Equal((ushort)0, ReadUInt16(snapshot, 12), "equip snapshot source page");
            Check.Equal((ushort)2, ReadUInt16(snapshot, 14), "equip snapshot source index");
            Check.Equal(1000u, ReadUInt32(snapshot, 20), "equip snapshot describes the equipped sword");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
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

        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(0, [capturedSpartaArtisan], [], []);
        var spartaArtisan = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_086");
        Check.Equal(5083u, spartaArtisan.ObjectId, "Sparta artisan object id");
        Check.True(spartaArtisan.Detail10077.SequenceEqual(detail10077), "Sparta detail 10077 is preserved");
        Check.True(spartaArtisan.Detail10080.SequenceEqual(detail10080), "Sparta detail 10080 is preserved");

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
            registry.Remove(nearSession);
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
                CriticalResistance = 28_008
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
