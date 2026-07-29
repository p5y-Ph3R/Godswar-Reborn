using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetLevelUpgradeProtocolChecks
{
    private const int AccountId = 13;
    private const int CharacterId = 2;
    private const uint PetId = 1;

    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly int[] ExpectedAdvancementCosts =
    [
        1_500,
        4_500,
        7_500,
        10_500,
        13_500,
        16_500,
        19_500,
        27_540,
        37_305,
        49_500,
        78_450,
        133_725,
        187_650,
        240_300,
        291_600,
        341_775,
        390_675,
        438_375,
        485_025,
        530_550,
        575_025,
        618_450,
        660_900,
        702_450,
        743_025,
        782_775,
        821_625,
        859_725,
        897_075,
        933_675,
        969_525,
        1_004_775,
        1_039_425,
        1_073_475,
        1_106_925,
        1_139_925,
        1_172_400,
        1_204_425,
        1_236_075,
        1_267_425,
        1_298_325,
        1_329_000,
        1_359_375,
        1_389_600,
        1_419_525,
        1_449_375,
        1_479_075,
        1_508_775,
        1_538_325,
        1_567_950,
        1_597_575,
        1_627_200,
        1_657_050,
        1_686_975,
        1_717_050,
        1_747_350,
        1_777_875,
        1_808_775,
        1_839_900,
        1_871_400,
        1_903_350,
        1_935_675,
        1_968_450,
        2_001_750,
        2_035_575,
        2_070_000,
        2_105_025,
        2_140_650,
        2_177_025,
        2_214_075,
        2_251_875,
        2_290_500,
        2_329_875,
        2_370_150,
        2_411_400,
        2_453_475,
        2_496_600,
        2_540_700,
        2_585_775,
        2_632_050,
        2_679_375,
        2_727_825,
        2_777_550,
        2_828_400,
        2_880_525,
        2_934_000,
        2_988_825,
        3_044_925,
        3_102_525,
        3_161_475,
        3_222_000,
        3_283_950,
        3_347_475,
        3_412_575,
        3_479_325,
        3_547_725,
        3_617_850,
        3_689_625,
        3_763_275,
        3_838_650,
        3_915_900,
        3_994_950,
        4_076_025,
        4_158_975,
        4_243_875,
        4_330_875,
        4_419_900,
        4_511_025,
        4_604_250,
        4_699_650,
        4_797_225,
        4_897_125,
        4_999_200,
        5_103_600,
        5_210_400,
        5_319_525,
        5_431_125,
        5_545_125,
        5_661_675
    ];

    public static async Task RunAsync()
    {
        CheckOpcodeCatalog();
        CheckExperienceCurve();
        CheckNativeUpgradeFrame();
        await CheckSuccessfulUpgradeAsync();
        await CheckRejectedUpgradeAsync();
        await CheckInvalidRequestsAsync();
    }

    private static void CheckOpcodeCatalog()
    {
        Check.Equal(
            (ushort)10_285,
            Opcodes.PetLevelUpgradeRequest,
            "pet level-up request opcode");
        Check.Equal(
            (ushort)10_286,
            Opcodes.PetLevelUpgrade,
            "pet level-up response opcode");
        Check.Equal(
            nameof(Opcodes.PetLevelUpgradeRequest),
            Opcodes.Name(Opcodes.PetLevelUpgradeRequest),
            "pet level-up request has a diagnostic name");
        Check.Equal(
            nameof(Opcodes.PetLevelUpgrade),
            Opcodes.Name(Opcodes.PetLevelUpgrade),
            "pet level-up response has a diagnostic name");
    }

    private static void CheckExperienceCurve()
    {
        Check.Equal(
            120,
            PetExperienceCatalog.MaximumLevel,
            "pet maximum level");
        Check.Equal(
            119,
            ExpectedAdvancementCosts.Length,
            "pet advancement count");

        long cumulative = 0;
        for (var currentLevel = 1;
             currentLevel < PetExperienceCatalog.MaximumLevel;
             currentLevel++)
        {
            var expected = ExpectedAdvancementCosts[currentLevel - 1];
            var actual =
                PetExperienceCatalog.RequiredForNextLevel(currentLevel);
            Check.Equal(
                expected,
                actual,
                $"pet level {currentLevel} advancement cost");
            cumulative = checked(cumulative + (long)actual);
        }

        Check.Equal(
            252_947_820L,
            cumulative,
            "pet level 1-to-120 cumulative experience");
        Check.Equal(
            cumulative,
            PetExperienceCatalog.TotalExperienceToMaximumLevel,
            "pet experience catalog published total");
        Check.Equal(
            47_052_180L,
            300_000_000L - cumulative,
            "300-million test grant leaves deterministic level-120 overflow");
        Check.Equal(
            0,
            PetExperienceCatalog.RequiredForNextLevel(
                PetExperienceCatalog.MaximumLevel),
            "maximum-level pet has no next-level cost");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetExperienceCatalog.RequiredForNextLevel(0),
            "level zero is outside the pet curve");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetExperienceCatalog.RequiredForNextLevel(121),
            "level above maximum is outside the pet curve");
    }

    private static void CheckNativeUpgradeFrame()
    {
        const uint petId = 0x01020304;
        const int level = 107;
        const long currentExperience = 0x01020304;
        var basicSavvy = new PetSavvy(
            Agility: 1.01m,
            Strength: 2.02m,
            Accuracy: 3.03m,
            Technique: 4.04m,
            Wisdom: 5.05m,
            Luck: 6.06m);

        var packet = PacketBuilder.PetLevelUpgrade(
            petId,
            level,
            currentExperience,
            basicSavvy);
        var expected = Convert.FromHexString(
            "2C002E28040302016B000000040302013C714300" +
            "65000000CA0000002F01000094010000F90100005E020000");

        Check.True(
            packet.SequenceEqual(expected),
            "pet level-up response retains native little-endian bytes");
        Check.Equal(44, packet.Length, "pet level-up response length");
        Check.Equal(
            (ushort)44,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "pet level-up declared length");
        Check.Equal(
            Opcodes.PetLevelUpgrade,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "pet level-up response opcode");
        Check.Equal(
            petId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)),
            "pet level-up response pet ID");
        Check.Equal(
            checked((byte)level),
            packet[8],
            "pet level-up response level");
        Check.True(
            packet.AsSpan(9, 3).IndexOfAnyExcept((byte)0) < 0,
            "pet level-up response reserved bytes remain zero");
        Check.Equal(
            checked((uint)currentExperience),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)),
            "pet level-up response remaining experience");
        Check.Equal(
            checked((uint)PetExperienceCatalog.RequiredForNextLevel(level)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)),
            "pet level-up response next-level requirement");
        var expectedSavvy = new uint[] { 101, 202, 303, 404, 505, 606 };
        for (var index = 0; index < expectedSavvy.Length; index++)
        {
            Check.Equal(
                expectedSavvy[index],
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(20 + (index * sizeof(uint)))),
                $"pet level-up basic-savvy value {index + 1}");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetLevelUpgrade(
                petId: 0,
                level: 1,
                currentExperience: 0,
                basicSavvy),
            "zero pet ID cannot produce a native upgrade response");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetLevelUpgrade(
                petId,
                level: 121,
                currentExperience: 0,
                basicSavvy),
            "out-of-range pet level cannot be serialized");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetLevelUpgrade(
                petId,
                level: 1,
                currentExperience: -1,
                basicSavvy),
            "negative pet experience cannot be serialized");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetLevelUpgrade(
                petId,
                level: 1,
                currentExperience: 0,
                basicSavvy with { Strength = -0.01m }),
            "negative pet basic savvy cannot be serialized");
    }

    private static async Task CheckSuccessfulUpgradeAsync()
    {
        var result = new PetLevelUpgradeResult(
            PetLevelUpgradeStatus.Succeeded,
            PetId,
            PreviousLevel: 1,
            Level: 2,
            PreviousExperience: 300_000_000,
            Experience: 299_998_500,
            ExperienceSpent: 1_500,
            Revision: 15,
            BasicSavvy: new PetSavvy(
                2m,
                18m,
                6m,
                8m,
                10m,
                12m));
        var store = new PetLevelStore(result);

        var response = await InvokeAsync(
            store,
            CreateUpgradePacket(PetId));

        Check.True(
            response.SequenceEqual(
                PacketBuilder.PetLevelUpgrade(
                    PetId,
                    level: 2,
                    currentExperience: 299_998_500,
                    result.BasicSavvy)),
            "successful level-up emits the native authoritative refresh");
        Check.Equal(1, store.CallCount, "pet level-up persists once");
        Check.Equal(
            AccountId,
            store.AccountId,
            "pet level-up uses the authenticated account");
        Check.Equal(
            CharacterId,
            store.CharacterId,
            "pet level-up uses the active character");
        Check.Equal(
            (long)PetId,
            store.PetId,
            "pet level-up uses the requested pet ID");
    }

    private static async Task CheckInvalidRequestsAsync()
    {
        var result = PetLevelUpgradeResult.Rejected(
            PetLevelUpgradeStatus.PetNotFound,
            PetId);
        var store = new PetLevelStore(result);
        var malformed = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(malformed, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(
            malformed.AsSpan(2),
            Opcodes.PetLevelUpgradeRequest);

        var malformedResponse = await InvokeAsync(
            store,
            new GamePacket(malformed));
        var zeroIdResponse = await InvokeAsync(
            store,
            CreateUpgradePacket(petId: 0));

        Check.Equal(
            0,
            malformedResponse.Length,
            "malformed pet level-up emits no invented native frame");
        Check.Equal(
            0,
            zeroIdResponse.Length,
            "zero-ID pet level-up emits no invented native frame");
        Check.Equal(
            0,
            store.CallCount,
            "invalid pet level-up cannot reach persistence");
    }

    private static async Task CheckRejectedUpgradeAsync()
    {
        var store = new PetLevelStore(
            PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.InsufficientExperience,
                PetId,
                level: 1,
                experience: 1_499,
                revision: 14));

        var response = await InvokeAsync(
            store,
            CreateUpgradePacket(PetId));

        Check.Equal(
            0,
            response.Length,
            "rejected upgrade emits no success-only native frame");
        Check.Equal(
            1,
            store.CallCount,
            "well-formed rejected upgrade reaches persistence once");
    }

    private static async Task<byte[]> InvokeAsync(
        PetLevelStore store,
        GamePacket packet)
    {
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                monsterRuntimeMode: MonsterRuntimeMode.Ecs,
                playerRuntimeMode: PlayerRuntimeMode.Ecs),
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = AccountId,
                Username = "test2"
            });
        SetField(
            handler,
            "_character",
            new GameCharacter
            {
                Id = CharacterId,
                AccountId = AccountId,
                Name = "test2"
            });

        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        return clearBytes;
    }

    private static GamePacket CreateUpgradePacket(uint petId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetLevelUpgradeRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), petId);
        return new GamePacket(packet);
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private sealed class PetLevelStore : GameStoreTestStub
    {
        private readonly PetLevelUpgradeResult _result;

        public PetLevelStore(PetLevelUpgradeResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public int AccountId { get; private set; }

        public int CharacterId { get; private set; }

        public long PetId { get; private set; }

        public override Task<PetLevelUpgradeResult> UpgradePetLevelAsync(
            int accountId,
            int characterId,
            long petId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            AccountId = accountId;
            CharacterId = characterId;
            PetId = petId;
            return Task.FromResult(_result);
        }
    }
}
