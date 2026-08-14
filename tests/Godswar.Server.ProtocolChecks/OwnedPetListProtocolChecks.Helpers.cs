using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private static PetBootstrapSnapshot[] CreatePets(int count) =>
        Enumerable.Range(1, count)
            .Select(index => CreateGodlyKingLion() with
            {
                PetId = index,
                Name = $"Lion {index}"
            })
            .ToArray();

    private static PetBootstrapSnapshot CreateGodlyKingLion() =>
        new(
            PetId: 1,
            AccountId: AccountId,
            OwnerCharacterId: CharacterId,
            SpeciesId: 37,
            Name: "Godly King Lion",
            Sex: 1,
            Level: 80,
            Experience: 123_456,
            Aptitude: PetAptitude.Godly,
            Rank: 25.25m,
            CompletedRebirths: 3,
            RebirthsRemaining: 2,
            CompletedPetMerges: 7,
            HasSoulContract: true,
            HasOwnerMergeTalent: true,
            CurrentEnergy: 90,
            MaximumEnergy: 100,
            Amity: 77,
            Satiety: 88,
            RemainingLifetime: 1_100,
            AvailableStatPoints: 9,
            GrowthRevealed: true,
            IsBound: true,
            ActivityState: "owned",
            IsCarried: false,
            IsSummoned: false,
            ContributesToCharacter: false,
            Revision: 12,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            StatValues:
            [
                Stat(1, 1.25m, 7.5m),
                Stat(2, 2.5m, 8.25m),
                Stat(3, 3.75m, 9m),
                Stat(4, 4m, 10.75m),
                Stat(5, 5.5m, 11.5m),
                Stat(6, 6.25m, 12.25m)
            ],
            CharacterBonuses: [],
            Skills:
            [
                new PetSkillSnapshot(5_200, 0, 1, 0, true, 1),
                new PetSkillSnapshot(6_000, 7, 3, 99, false, 2),
                new PetSkillSnapshot(5_555, 1, 2, 88, true, 3)
            ],
            OpenedSkillSlots: 2,
            AvailableSkillSlots: 3,
            TalentMask: 31,
            SoulContractStage: 6);

    private static PetStatValueSnapshot Stat(
        short code,
        decimal initial,
        decimal added) =>
        new(code, initial, added, 0m, 0m, 1);

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            Camp = GameDefaults.SpartaCamp,
            Profession = 0,
            Level = 80,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            PositionX = GameDefaults.StartingPositionX,
            PositionZ = GameDefaults.StartingPositionZ,
            CurrentHp = 5_000,
            MaxHp = 5_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    private static GamePacket CreateOpcodePacket(ushort opcode)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), opcode);
        return new GamePacket(bytes);
    }

    private static List<byte[]> SplitPackets(byte[] clearBytes)
    {
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clearBytes.Length)
        {
            Check.True(
                clearBytes.Length - offset >= 4,
                "enter bootstrap has a complete packet header");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clearBytes.AsSpan(offset, 2));
            Check.True(
                length >= 4 && length <= clearBytes.Length - offset,
                "enter bootstrap packet has a bounded declared length");
            packets.Add(clearBytes.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return packets;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> source)
    {
        var terminator = source.IndexOf((byte)0);
        var length = terminator >= 0 ? terminator : source.Length;
        return Encoding.ASCII.GetString(source[..length]);
    }

    private static uint ToFixedPoint(decimal value) =>
        checked((uint)decimal.Round(
            value * 100m,
            0,
            MidpointRounding.AwayFromZero));

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(offset, sizeof(ushort)));

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
}
