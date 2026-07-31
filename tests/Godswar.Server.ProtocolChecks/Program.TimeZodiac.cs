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
            ZodiacAccumulatedTalentExperienceX100 = 728,
            ZodiacSkillGridLevels =
            [
                12, 0, 0, 0,
                12, 0, 0, 0,
                9, 0, 0, 0,
                10, 0, 0, 0
            ],
            ZodiacSkillGridSkillIds =
            [
                10_057, -1, -1, -1,
                -1, -1, -1, -1,
                -1, -1, -1, -1,
                -1, -1, -1, -1
            ]
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
        Check.Equal(0, ReadInt32(packet, 40), "first native stone record starts zero-filled");
        Check.Equal(0, ReadInt32(packet, 52), "second native stone record starts zero-filled");
        Check.Equal(132_734f, ReadSingle(packet, 64), "accumulated combat EXP float mirror");
        Check.Equal(728f, ReadSingle(packet, 68), "accumulated talent EXP float mirror");

        for (var gridIndex = 0;
             gridIndex < ZodiacSkillGridCatalog.GridCount;
             gridIndex++)
        {
            var gridOffset = 72 + (gridIndex * 16);
            Check.Equal(
                ((gridIndex / 4) << 8) |
                    character.ZodiacSkillGridLevels[gridIndex],
                ReadInt32(packet, gridOffset),
                $"Zodiac grid {gridIndex} uses native row/level packing");
            Check.Equal(
                character.ZodiacSkillGridSkillIds[gridIndex],
                ReadInt32(packet, gridOffset + 4),
                $"Zodiac grid {gridIndex} selected skill");
        }
        Check.Equal(0x0000_000C, ReadInt32(packet, 72), "captured grid 0 is state +48");
        Check.Equal(0x0000_010C, ReadInt32(packet, 136), "captured grid 4 is state +112");
        Check.Equal(0x0000_0209, ReadInt32(packet, 200), "captured grid 8 is state +176");
        Check.Equal(0x0000_030A, ReadInt32(packet, 264), "captured grid 12 is state +240");
        Check.Equal(10_057, ReadInt32(packet, 76), "captured grid 0 selected skill");

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
        var overCapPacket = PacketBuilder.ZodiacFullSync(character, now);
        Check.Equal(
            100_001,
            ReadInt32(overCapPacket, 36),
            "explicit administrative over-cap energy remains visible");

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

        var administrativelyOverCap = new GameCharacter
        {
            ZodiacLevel = 1,
            ZodiacEnergy = 10_000_000
        };
        var preservedOverCap = ZodiacEnergyAccrual.Apply(
            administrativelyOverCap,
            start,
            start.AddMinutes(5),
            policy);
        Check.Equal(
            0,
            preservedOverCap.GainedEnergyX100,
            "an administrative over-cap balance earns no automatic energy");
        Check.Equal(
            10_000_000,
            administrativelyOverCap.ZodiacEnergy,
            "ordinary accrual never destroys an administrative over-cap balance");

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
                0L,
                character.ZodiacOnlineDurationTicksToday,
                "JSON compatibility reload does not invent online duration");
            Check.Equal(12_345, character.ZodiacAccumulatedExperienceX100, "Zodiac combat EXP field persists");
            Check.Equal(6_789, character.ZodiacAccumulatedTalentExperienceX100, "Zodiac talent EXP field persists");

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
}
