using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const ushort PetSoulContractRequestLength = 12;

    private async Task HandlePetSoulContractRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[pet] rejected unauthenticated Soul Contract request");
            return;
        }
        if (packet.Length != PetSoulContractRequestLength ||
            packet.Buffer.Length != PetSoulContractRequestLength ||
            packet.Payload.Length != 8)
        {
            Console.WriteLine(
                "[pet] rejected malformed Soul Contract request " +
                $"length={packet.Length} bytes={packet.Buffer.Length}");
            return;
        }

        var payload = packet.Payload;
        var materialTemplateId =
            BinaryPrimitives.ReadInt32LittleEndian(payload);
        var quantity = payload[4];
        if (materialTemplateId !=
                PetSoulContractPolicy.ContractSpiritItemId ||
            quantity > PetSoulContractPolicy.MaximumSpiritCount ||
            payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
        {
            Console.WriteLine(
                "[pet] rejected non-canonical Soul Contract request");
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
                    "[pet] rejected tokenless secure Soul Contract request");
                return;
            }
            if (!AllowLegacyPlayerMutationFallback("pet_soul_contract"))
            {
                return;
            }
            identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        }

        await HandleDurablePetSoulContractAsync(
            identity,
            materialTemplateId,
            quantity,
            cancellationToken);
    }

    private async Task<PetDurableReceipt?>
        HandleDurablePetSoulContractAsync(
            PetCommandOperationIdentity identity,
            int materialTemplateId,
            int quantity,
            CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetSoulContract,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetSoulContractCommand(
            identity,
            materialTemplateId,
            quantity);
        var unownedEnvelope = identity.IsSecureClient
            ? PetSoulContractCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetSoulContractCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return null;
        }

        return await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetSoulContract,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetSoulContractProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        string previousKitBag,
        CancellationToken cancellationToken)
    {
        if (!receipt.Succeeded)
        {
            return true;
        }
        if (_character is null ||
            receipt.SoulContract is not { IsValid: true } evidence)
        {
            return false;
        }

        if (disposition == PetDurableExecutionDisposition.Committed)
        {
            var pet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (pet is null || !pet.IsCarried || !pet.IsSummoned ||
                pet.ContributesToCharacter ||
                pet.ProjectedSoulContractStage != evidence.NewStage ||
                pet.Revision != receipt.PetRevision)
            {
                return false;
            }
            await _session.SendAsync(
                PacketBuilder.PetSoulContract(evidence.NewStage),
                cancellationToken,
                "DurablePetSoulContractResult");
        }

        foreach (var deletion in
                 PacketBuilder.KitBagMutationDeletionAcknowledgements(
                     previousKitBag,
                     _character.KitBag))
        {
            await _session.SendAsync(
                deletion,
                cancellationToken,
                "DurablePetSoulContractBagMutationClear");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        // 10271 changes only the active native pet's contract stage. A full
        // 10237 here would destructively clear and rebuild the client pet
        // collection. Delayed duplicates therefore never replay stale 10271
        // and settle even when that historical pet is absent or no longer
        // summoned; the current bag refresh is the safe reconciliation.
        return true;
    }
}
