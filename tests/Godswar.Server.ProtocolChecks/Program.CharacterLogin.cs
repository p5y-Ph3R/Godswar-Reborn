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

internal static partial class Program
{
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
}
