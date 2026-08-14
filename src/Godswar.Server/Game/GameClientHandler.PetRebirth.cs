using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const ushort PetRebirthRequestLength = 12;

    private async Task HandlePetRebirthRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            Console.WriteLine("[pet] rejected unauthenticated rebirth request");
            return;
        }
        if (packet.Length != PetRebirthRequestLength ||
            packet.Buffer.Length != PetRebirthRequestLength ||
            packet.Payload.Length != 8)
        {
            Console.WriteLine(
                "[pet] rejected malformed rebirth request " +
                $"length={packet.Length} bytes={packet.Buffer.Length}");
            return;
        }

        var payload = packet.Payload;
        var materialTemplateId =
            BinaryPrimitives.ReadInt32LittleEndian(payload);
        var quantity = payload[4];
        if (!PetRebirthSpiritPolicy.IsCanonicalMaterialSelection(
                materialTemplateId,
                quantity) ||
            payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
        {
            Console.WriteLine(
                "[pet] rejected non-canonical rebirth request");
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
                    "[pet] rejected tokenless secure rebirth request");
                return;
            }
            if (!AllowLegacyPlayerMutationFallback("pet_rebirth"))
            {
                return;
            }
            identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        }

        await HandleDurablePetRebirthAsync(
            identity,
            materialTemplateId,
            quantity,
            cancellationToken);
    }

    private async Task<PetDurableReceipt?> HandleDurablePetRebirthAsync(
        PetCommandOperationIdentity identity,
        int materialTemplateId,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetRebirth,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetRebirthCommand(
            identity,
            materialTemplateId,
            quantity);
        var unownedEnvelope = identity.IsSecureClient
            ? PetRebirthCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetRebirthCommandEnvelope.CreateRawLocal(
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
            CommandFamily.PetRebirth,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<bool> SendPetRebirthProjectionAsync(
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
        if (_character is null)
        {
            return false;
        }

        if (disposition == PetDurableExecutionDisposition.Committed)
        {
            var pet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (pet is null || !pet.IsCarried || !pet.IsSummoned ||
                pet.ContributesToCharacter ||
                pet.Level != 1 ||
                pet.Experience != receipt.PetExperience ||
                pet.Revision != receipt.PetRevision ||
                receipt.RebirthGrowth is not
                    { IsValid: true } growth)
            {
                return false;
            }
            await _session.SendAsync(
                PacketBuilder.PetRebirth(
                    growth,
                    RequirePetContent()
                        .RequiredExperienceForNextLevel(pet.Level)),
                cancellationToken,
                "DurablePetRebirthResult");
            await _session.SendAsync(
                PacketBuilder.PetAppearanceRefresh(
                    RequirePetContent(),
                    pet),
                cancellationToken,
                "DurablePetRebirthProgressionRefresh");
        }
        // 10273 increments the native completed-rebirth counter and is not
        // idempotent. A duplicate skips it and receives only narrow current
        // reconciliation. It must not require the historical receipt pet to
        // remain active: the player may have switched pets or entered owner
        // Merge before a delayed secure retry arrives.

        foreach (var deletion in
                 PacketBuilder.KitBagMutationDeletionAcknowledgements(
                     previousKitBag,
                     _character.KitBag))
        {
            await _session.SendAsync(
                deletion,
                cancellationToken,
                "DurablePetRebirthBagMutationClear");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        if (disposition == PetDurableExecutionDisposition.Duplicate)
        {
            // A delayed retry may follow a pet switch, recall, Merge, or
            // deletion. Never replay additive 10273 or destructive 10237.
            // If the historical pet still exists, refresh its current
            // authoritative bean by ID without touching collection/presence.
            var current = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (current is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.PetAppearanceRefresh(
                        RequirePetContent(),
                        current),
                    cancellationToken,
                    "DurablePetRebirthReplayProgressionRefresh");
            }
        }
        return true;
    }
}
