using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandlePackedPetDetailRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            _sealedPetSnapshots is null ||
            packet.Opcode != Opcodes.PackedPetDetailRequest ||
            packet.Length != 8 ||
            packet.Buffer.Length != 8)
        {
            Console.WriteLine(
                "[sealed-pet] rejected malformed or unavailable detail request");
            return;
        }

        var petId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Buffer.AsSpan(4, sizeof(uint)));
        if (petId == 0)
        {
            return;
        }
        var authorized = await _sealedPetSnapshots
            .ReadAuthorizedSealedPetAsync(
                _account.Id,
                _character.Id,
                petId,
                cancellationToken);
        if (authorized is null)
        {
            Console.WriteLine(
                $"[sealed-pet] unauthorized detail request pet={petId}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PackedPetDetail(
                RequirePetContent(),
                CharacterLoadSnapshotHydrator.MapPet(authorized)),
            cancellationToken,
            "PackedPetDetailResponse");
    }
}
