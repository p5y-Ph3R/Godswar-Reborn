using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int PetPresenceRequestLength = 8;

    private async Task HandlePetPresenceRequestAsync(
        GamePacket packet,
        PetPresenceOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TryReadPetId(packet, out var petId))
        {
            Console.WriteLine(
                $"[pet] rejected malformed presence operation=" +
                $"{operation} length={packet.Length}");
            return;
        }

        if (packet.ClientOperationId is { } operationId &&
            petId != 0)
        {
            await HandleDurablePetPresenceAsync(
                operationId,
                petId,
                operation,
                cancellationToken);
            return;
        }
        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.PetPresenceTransition);
        Console.WriteLine(
            $"[pet] rejected presence operation without durable " +
            $"identity operation={operation} pet={petId}");
    }

    private async Task RestorePersistedPetPresenceAsync(
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        IReadOnlyList<PetBootstrapSnapshot> pets;
        try
        {
            pets = (await _ownedPetSnapshots.ReadOwnedPetsAsync(
                _account.Id,
                _character.Id,
                cancellationToken))
                .Select(CharacterLoadSnapshotHydrator.MapPet)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[pet] presence restore skipped character={_character.Name} error={ex.GetType().Name}");
            return;
        }

        await RestorePetPresenceAsync(pets, cancellationToken);
    }

    private async Task RestorePetPresenceAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var carried = pets.SingleOrDefault(static pet => pet.IsCarried);
        if (carried is null)
        {
            return;
        }

        var petId = checked((uint)carried.PetId);
        if (carried.IsSummoned)
        {
            await _session.SendAsync(
                PacketBuilder.PetWorldPresence(
                    petId,
                    LocalPlayerObjectId),
                cancellationToken,
                "PetWorldPresenceRestore");
        }
        else
        {
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    petId,
                    PetOperationResultCode.TakeSucceeded),
                cancellationToken,
                "PetTakeRestore");
        }

        Console.WriteLine(
            $"[pet] presence restored character={_character.Name} pet={petId} summoned={carried.IsSummoned}");
    }

    private static bool TryReadPetId(
        GamePacket packet,
        out uint petId)
    {
        petId = 0;
        if (packet.Length != PetPresenceRequestLength ||
            packet.Buffer.Length != PetPresenceRequestLength)
        {
            return false;
        }

        petId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
        return petId != 0;
    }

}
