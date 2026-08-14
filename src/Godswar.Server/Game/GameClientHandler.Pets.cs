using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
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
                PetCommandOperationIdentity.SecureClient(operationId),
                petId,
                operation,
                cancellationToken);
            return;
        }

        if (petId == 0 ||
            !AllowLegacyPlayerMutationFallback("pet_presence"))
        {
            return;
        }

        await HandleDurablePetPresenceAsync(
            PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId),
            petId,
            operation,
            cancellationToken);
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

        await RestorePetPresenceAsync(
            pets,
            summonCarriedPet: false,
            cancellationToken);
    }

    private async Task RestorePetPresenceAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        bool summonCarriedPet,
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
        if (carried.ContributesToCharacter)
        {
            // A merged pet remains logically carried and summoned in durable
            // state, but the native unite presentation owns its model. Never
            // replay Call Out or the companion will appear beside its owner.
            await PublishPetOwnerMergeStartedAsync(
                carried,
                cancellationToken);
            return;
        }

        var callOutResultAlreadySent = false;
        if (!carried.IsSummoned && summonCarriedPet)
        {
            // Login policy keeps the selected companion visible. Persist the
            // transition through the ordinary authoritative command path so
            // the state, command receipt, and pet-operation audit all agree.
            // Reuse one operation identity for this session's login retry.
            var identity =
                PetCommandOperationIdentity.ServerSessionLifecycle(
                _loginPetCallOutOperationId ??= Guid.NewGuid(),
                _commandConnectionId);
            var receipt = await HandleDurablePetPresenceAsync(
                identity,
                carried.PetId,
                PetPresenceOperation.CallOut,
                cancellationToken);
            var refreshed = _characterLoadSnapshot?.Pets.SingleOrDefault(
                candidate => candidate.PetId == carried.PetId);
            if (receipt is not
                    {
                        Succeeded: true,
                        IsCarried: true,
                        IsSummoned: true
                    } ||
                refreshed is not
                    {
                        IsCarried: true,
                        IsSummoned: true
                    })
            {
                throw new InvalidDataException(
                    "The carried pet could not be called out during login.");
            }

            carried = refreshed;
            callOutResultAlreadySent = true;
        }
        if (carried.IsSummoned)
        {
            // The owned-pet bootstrap restores the durable selection, but the
            // stock client does not recreate the companion model from 10248
            // alone after a fresh login. Replay the same successful Call Out
            // presentation result used by a live summon first, then bind the
            // selected pet to its local world owner. Neither packet mutates
            // authoritative state.
            if (!callOutResultAlreadySent)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        petId,
                        PetOperationResultCode.CallOutSucceeded),
                    cancellationToken,
                    "PetCallOutRestore");
            }
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
