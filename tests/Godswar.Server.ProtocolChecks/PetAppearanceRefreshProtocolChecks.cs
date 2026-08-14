using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetAppearanceRefreshProtocolChecks
{
    public const string CheckName =
        "Narrow pet species and bound refresh protocol";

    public static Task RunAsync()
    {
        var pet = CreatePet(speciesId: 45, isBound: true);
        var packet = PacketBuilder.PetAppearanceRefresh(
            PetContentTestCatalog.Instance,
            pet);
        var expected = Convert.FromHexString(
            "48002E28040302016B000000040302013C714300" +
            "65000000CA0000002F01000094010000F90100005E020000" +
            "C3020000280300008D030000F203000057040000BC040000" +
            "2D010000");

        Check.True(
            packet.SequenceEqual(expected),
            "appearance refresh retains the 68-byte prefix and exact tail");
        Check.Equal(72, packet.Length, "appearance refresh frame length");
        Check.Equal(
            (ushort)72,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "appearance refresh declared length");
        Check.Equal(
            Opcodes.PetLevelUpgrade,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
            "appearance refresh reuses patched opcode 10286");
        Check.Equal((byte)45, packet[68], "appearance refresh species");
        Check.Equal((byte)1, packet[69], "appearance refresh bound flag");
        Check.True(
            packet.AsSpan(70, 2).IndexOfAnyExcept((byte)0) < 0,
            "appearance refresh reserved tail remains zero");

        var unbound = PacketBuilder.PetAppearanceRefresh(
            PetContentTestCatalog.Instance,
            pet with { IsBound = false });
        Check.Equal((byte)0, unbound[69], "unbound flag remains exact");

        var ordinary = PacketBuilder.PetLevelUpgrade(
            PetContentTestCatalog.Instance,
            checked((uint)pet.PetId),
            pet.Level,
            pet.Experience,
            new PetSavvy(1.01m, 2.02m, 3.03m, 4.04m, 5.05m, 6.06m),
            new PetSavvy(7.07m, 8.08m, 9.09m, 10.10m, 11.11m, 12.12m));
        Check.Equal(68, ordinary.Length, "ordinary progression stays 68 bytes");
        Check.True(
            ordinary.AsSpan(2).SequenceEqual(packet.AsSpan(2, 66)),
            "ordinary progression payload is unchanged");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                pet with { SpeciesId = 0 }),
            "species zero fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                pet with { SpeciesId = 46 }),
            "species above the stock catalog fails closed");

        var partialProvenance = pet with
        {
            StatValues = pet.StatValues
                .Select(static stat => stat with
                {
                    RarityAddedSavvy = 1m
                })
                .ToArray()
        };
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                partialProvenance),
            "partial Savvy provenance fails closed");

        Check.Throws<InvalidDataException>(
            () => PacketBuilder.PetAppearanceRefresh(
                PetContentTestCatalog.Instance,
                pet with
                {
                    StatValues = pet.StatValues.Take(5).ToArray()
                }),
            "incomplete Savvy projection fails closed");

        return Task.CompletedTask;
    }

    private static PetBootstrapSnapshot CreatePet(
        short speciesId,
        bool isBound)
    {
        decimal[] basic = [1.01m, 2.02m, 3.03m, 4.04m, 5.05m, 6.06m];
        decimal[] added = [7.07m, 8.08m, 9.09m, 10.10m, 11.11m, 12.12m];
        var stats = basic.Select((value, index) =>
            new PetStatValueSnapshot(
                checked((short)(index + 1)),
                value,
                AddedSavvy: added[index],
                BaseGrowthRate: 1m,
                GrowthAcceleration: 0m,
                Revision: 1)).ToArray();
        return new PetBootstrapSnapshot(
            PetId: 0x01020304,
            AccountId: 13,
            OwnerCharacterId: 2,
            speciesId,
            Name: "Refresh Fixture",
            Sex: 0,
            Level: 107,
            Experience: 0x01020304,
            PetAptitude.Godly,
            Rank: 0m,
            CompletedRebirths: 0,
            RebirthsRemaining: 0,
            CompletedPetMerges: 0,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            Satiety: 100,
            RemainingLifetime: 600,
            AvailableStatPoints: 0,
            GrowthRevealed: true,
            isBound,
            ActivityState: "summoned",
            IsCarried: true,
            IsSummoned: true,
            ContributesToCharacter: false,
            Revision: 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            stats,
            CharacterBonuses: [],
            Skills: []);
    }
}
