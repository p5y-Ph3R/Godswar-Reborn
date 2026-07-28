using System.Buffers.Binary;
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
        var petId = TryReadPetId(packet, out var parsedPetId)
            ? parsedPetId
            : 0u;
        var resultCode = FailureCode(operation);
        PetPresenceTransitionResult? transition = null;

        if (petId != 0 && _account is not null && _character is not null)
        {
            try
            {
                transition = await _store.TransitionPetPresenceAsync(
                    _account.Id,
                    _character.Id,
                    petId,
                    operation,
                    cancellationToken);
                if (transition.Succeeded)
                {
                    resultCode = SuccessCode(operation);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[pet] operation failed operation={operation} pet={petId} character={_character.Name} error={ex.GetType().Name}");
            }
        }

        await _session.SendAsync(
            PacketBuilder.PetOperationResult(petId, resultCode),
            cancellationToken,
            "PetOperationResult");
        Console.WriteLine(
            $"[pet] operation={operation} pet={petId} character={_character?.Name ?? "<none>"} result={resultCode} store={transition?.Status.ToString() ?? "RejectedRequest"}");
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
            pets = await _store.GetOwnedPetsAsync(
                _account.Id,
                _character.Id,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[pet] presence restore skipped character={_character.Name} error={ex.GetType().Name}");
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

    private static PetOperationResultCode SuccessCode(
        PetPresenceOperation operation) =>
        operation switch
        {
            PetPresenceOperation.Take =>
                PetOperationResultCode.TakeSucceeded,
            PetPresenceOperation.CallOut =>
                PetOperationResultCode.CallOutSucceeded,
            PetPresenceOperation.Recall =>
                PetOperationResultCode.RecallSucceeded,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown pet operation.")
        };

    private static PetOperationResultCode FailureCode(
        PetPresenceOperation operation) =>
        operation switch
        {
            PetPresenceOperation.Take =>
                PetOperationResultCode.TakeFailed,
            PetPresenceOperation.CallOut =>
                PetOperationResultCode.CallOutFailed,
            PetPresenceOperation.Recall =>
                PetOperationResultCode.RecallFailed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown pet operation.")
        };
}
