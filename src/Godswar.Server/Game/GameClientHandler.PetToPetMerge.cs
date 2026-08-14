using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int PetToPetMergeRequestLength = 20;

    private async Task HandlePetToPetMergeRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadPetToPetMergeRequest(
                packet,
                out var primaryPetId,
                out var deputyPetId,
                out var materialItemId,
                out var materialQuantity))
        {
            Console.WriteLine(
                $"[pet] rejected malformed pet Merge request length={packet.Length}");
            return;
        }
        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[pet] rejected unauthenticated pet Merge request");
            return;
        }

        PetCommandOperationIdentity identity;
        if (packet.ClientOperationId is { } operationId &&
            operationId != Guid.Empty)
        {
            identity = PetCommandOperationIdentity.SecureClient(operationId);
        }
        else
        {
            if (_session.IsSecure)
            {
                Console.WriteLine(
                    "[pet] rejected tokenless secure pet Merge request");
                return;
            }
            if (!AllowLegacyPlayerMutationFallback("pet_to_pet_merge"))
            {
                return;
            }
            identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        }

        await HandleDurablePetToPetMergeAsync(
            identity,
            primaryPetId,
            deputyPetId,
            materialItemId,
            materialQuantity,
            cancellationToken);
    }

    private static bool TryReadPetToPetMergeRequest(
        GamePacket packet,
        out int primaryPetId,
        out int deputyPetId,
        out uint materialItemId,
        out byte materialQuantity)
    {
        primaryPetId = 0;
        deputyPetId = 0;
        materialItemId = 0;
        materialQuantity = 0;
        if (packet.Length != PetToPetMergeRequestLength ||
            packet.Buffer.Length != PetToPetMergeRequestLength ||
            packet.Payload.Length != PetToPetMergeRequestLength - 4)
        {
            return false;
        }

        var payload = packet.Payload;
        primaryPetId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        deputyPetId = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var material = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        materialQuantity = payload[12];
        if (primaryPetId <= 0 || deputyPetId <= 0 ||
            primaryPetId == deputyPetId || material < 0 ||
            payload[13] != 0 || payload[14] != 0 || payload[15] != 0)
        {
            return false;
        }

        materialItemId = checked((uint)material);
        return materialItemId == 0 && materialQuantity == 0 ||
            materialItemId is
                    PetToPetMergeCommandEnvelope.StandardMaterialItemId or
                    PetToPetMergeCommandEnvelope.RestrictedMaterialItemId &&
                materialQuantity is >= 1 and <=
                    PetToPetMergeCommandEnvelope.MaximumMaterialQuantity;
    }
}
