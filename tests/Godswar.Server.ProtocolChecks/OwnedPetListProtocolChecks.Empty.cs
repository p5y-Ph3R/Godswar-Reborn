using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private static void CheckCanonicalEmptyPacket()
    {
        var packet = PacketBuilder.OwnedPetList(PetContentTestCatalog.Instance, []);

        Check.True(
            packet.SequenceEqual(
                Convert.FromHexString("0800FD2702000000")),
            "empty owned-pet list retains the captured canonical bytes");
    }

    private static void CheckCapacity(int count, byte expectedCapacity)
    {
        var packet = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            CreatePets(count));
        Check.Equal(
            8 + (count * PetRecordLength),
            packet.Length,
            $"owned-pet packet length for {count} pets");
        Check.Equal(
            expectedCapacity,
            packet[4],
            $"owned-pet capacity for {count} pets");
        Check.Equal(
            checked((byte)count),
            packet[5],
            $"owned-pet count for {count} pets");
    }
}
