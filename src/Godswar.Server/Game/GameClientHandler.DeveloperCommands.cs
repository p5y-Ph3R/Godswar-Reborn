using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IDeveloperItemGrantCommandExecutor?
        _developerItemGrantCommands;

    private async Task<bool> HandleDeveloperItemCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text) ||
            !DeveloperItemCommand.TryParse(text, out var request, out var error))
        {
            return false;
        }

        // A recognized developer command is always consumed locally. It must
        // never be echoed into public map chat, including on denied attempts.
        if (_account is null || _character is null)
        {
            Console.WriteLine("[developer-item] denied before account/character enter");
            return true;
        }

        if (!_developerCommands.Allows(_account.Id))
        {
            Console.WriteLine(
                $"[developer-item] denied account={_account.Id} character={_character.Name}");
            return true;
        }

        if (request is null)
        {
            Console.WriteLine(
                $"[developer-item] invalid account={_account.Id} character={_character.Name}: {error}");
            await SendDeveloperItemFeedbackAsync(packet, $"[item] {error}", cancellationToken);
            return true;
        }

        if (request.Operation == DeveloperItemOperation.MountList && request.MountList is not null)
        {
            await SendDeveloperMountListAsync(packet, request.MountList, cancellationToken);
            return true;
        }

        if (request.Operation == DeveloperItemOperation.MountAdd && request.Mount is not null)
        {
            var mount = request.Mount;
            if (request.ClientOperationId.HasValue)
            {
                await HandleDurableDeveloperItemGrantAsync(
                    packet,
                    mount.ItemId,
                    mount.DisplayName,
                    quantity: 1,
                    clientOperationId:
                        request.ClientOperationId.Value,
                    cancellationToken: cancellationToken);
                return true;
            }

            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.DeveloperItemGrant);
            if (!AllowLegacyPlayerMutationFallback(
                    "developer_mount_grant"))
            {
                return true;
            }

            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.AddDeveloperMount);
            var mountResult = await _store.AddDeveloperMountAsync(
                _account.Id,
                _character.Id,
                mount.ItemId,
                cancellationToken);
            if (!mountResult.Added || mountResult.Character is null)
            {
                CommandMetrics.Record(
                    CommandFamily.DeveloperItemGrant,
                    CommandIdentityStrength.UnsupportedLegacyRetry,
                    CommandOutcome.PreconditionFailed);
                var reason = mountResult.Status == KitBagItemGrantStatus.InsufficientCapacity
                    ? "Your kit bag has no empty slot."
                    : "The character is no longer available.";
                await SendDeveloperItemFeedbackAsync(
                    packet,
                    $"[mount] Not added: {reason}",
                    cancellationToken);
                Console.WriteLine(
                    $"[developer-mount] grant failed account={_account.Id} character={_character.Name} item={mount.ItemId} status={mountResult.Status}");
                return true;
            }

            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.Accepted);
            InstallUpdatedCharacter(mountResult.Character);
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            await SendKitBagRefreshAsync(cancellationToken);
            await SendDeveloperItemFeedbackAsync(
                packet,
                $"[mount] Added {mount.DisplayName} ({mount.ItemId}) to your bag.",
                cancellationToken);
            Console.WriteLine(
                $"[developer-mount] granted account={_account.Id} character={_character.Name} item={mount.ItemId} name=\"{mount.DisplayName}\"");
            return true;
        }

        if (request.Operation == DeveloperItemOperation.ClearBag)
        {
            if (request.ClientOperationId.HasValue)
            {
                await HandleDurableDeveloperBagClearAsync(
                    packet,
                    request.ClientOperationId.Value,
                    cancellationToken);
                return true;
            }

            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.DeveloperBagClear);
            if (!AllowLegacyPlayerMutationFallback(
                    "developer_bag_clear"))
            {
                return true;
            }

            // Opcode 10033 detail pages and opcode 10056 slot-index packets do
            // not evict an icon already instantiated by this client. Capture
            // the occupied slots so the successful clear can use the native
            // source-to-FFFF deletion acknowledgement for every old icon.
            var deletionAcknowledgements =
                PacketBuilder.KitBagDeletionAcknowledgements(_character);

            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.ClearKitBag);
            var cleared = await _store.ClearKitBagAsync(
                _account.Id,
                _character.Id,
                cancellationToken);
            if (cleared is null)
            {
                CommandMetrics.Record(
                    CommandFamily.DeveloperBagClear,
                    CommandIdentityStrength.UnsupportedLegacyRetry,
                    CommandOutcome.PreconditionFailed);
                Console.WriteLine(
                    $"[developer-item] clear bag failed account={_account.Id} character={_character.Name}");
                return true;
            }

            CommandMetrics.Record(
                CommandFamily.DeveloperBagClear,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.Accepted);
            InstallUpdatedCharacter(cleared);
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            _pendingUnequipFollowup = null;
            ClearForgeSelection();
            ClearGearEnhancerSelection();
            foreach (var acknowledgement in deletionAcknowledgements)
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "DeveloperItemClearBagDeleteAck");
            }

            Console.WriteLine(
                $"[developer-item] cleared bag account={_account.Id} character={_character.Name} removed={deletionAcknowledgements.Length}");
            return true;
        }

        if (request.Operation != DeveloperItemOperation.Add || request.Material is null)
        {
            Console.WriteLine(
                $"[developer-item] invalid operation account={_account.Id} character={_character.Name}");
            return true;
        }

        var material = request.Material;

        if (request.ClientOperationId.HasValue)
        {
            await HandleDurableDeveloperItemGrantAsync(
                packet,
                material.ItemId,
                material.DisplayName,
                request.Quantity,
                request.ClientOperationId.Value,
                cancellationToken);
            return true;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.DeveloperItemGrant);
        if (!AllowLegacyPlayerMutationFallback(
                "developer_item_grant"))
        {
            return true;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.AddForgingMaterial);
        var result = await _store.AddForgingMaterialAsync(
            _account.Id,
            _character.Id,
            material.ItemId,
            request.Quantity,
            cancellationToken);
        if (!result.Added || result.Character is null)
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.PreconditionFailed);
            Console.WriteLine(
                $"[developer-item] grant failed account={_account.Id} character={_character.Name} item={material.ItemId} quantity={request.Quantity} status={result.Status}");
            return true;
        }

        CommandMetrics.Record(
            CommandFamily.DeveloperItemGrant,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.Accepted);
        InstallUpdatedCharacter(result.Character);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[developer-item] granted account={_account.Id} character={_character.Name} item={material.ItemId} name=\"{material.DisplayName}\" quantity={request.Quantity}");
        return true;
    }

    private async Task SendDeveloperMountListAsync(
        GamePacket commandPacket,
        DeveloperMountListRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Family is not null)
        {
            var family = request.Family;
            await SendDeveloperItemFeedbackAsync(
                commandPacket,
                $"[mount] {family.Alias}: {family.DisplayName}",
                cancellationToken);
            foreach (var group in family.Mounts.Chunk(5))
            {
                var variants = string.Join(
                    "  ",
                    group.Select(mount =>
                        $"{mount.Tier}={mount.ItemId}{(mount.CanGrant ? string.Empty : " (list only)")}"));
                await SendDeveloperItemFeedbackAsync(
                    commandPacket,
                    $"[mount] {variants}",
                    cancellationToken);
            }

            return;
        }

        var page = request.Page ?? 1;
        await SendDeveloperItemFeedbackAsync(
            commandPacket,
            $"[mount] Families {page}/{DeveloperMountCatalog.PageCount}. Use: /item mount list <page|family>",
            cancellationToken);
        foreach (var family in DeveloperMountCatalog.GetPage(page))
        {
            var firstId = family.Mounts.Min(static mount => mount.ItemId);
            var lastId = family.Mounts.Max(static mount => mount.ItemId);
            var idSummary = firstId == lastId ? $"{firstId}" : $"{firstId}-{lastId}";
            await SendDeveloperItemFeedbackAsync(
                commandPacket,
                $"[mount] {family.Alias}: {family.DisplayName} [{idSummary}]",
                cancellationToken);
        }
    }

    private Task SendDeveloperItemFeedbackAsync(
        GamePacket commandPacket,
        string message,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.DeveloperCommandTalkReply(commandPacket.Payload, message),
            cancellationToken,
            "DeveloperItemFeedback");

    private async Task SendKitBagRefreshAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        foreach (var packet in PacketBuilder.KitBagDetailPages(_character))
        {
            await _session.SendAsync(packet, cancellationToken, "DeveloperItemKitBagDetail");
        }

        foreach (var packet in PacketBuilder.KitBagSlotIndexes(_character))
        {
            await _session.SendAsync(packet, cancellationToken, "DeveloperItemKitBagSlotIndex");
        }
    }

    internal static bool TryReadTalkText(ReadOnlySpan<byte> payload, out string text)
    {
        const int textLengthOffset = 4;
        const int textOffset = 12;
        text = string.Empty;
        if (payload.Length < textOffset)
        {
            return false;
        }

        var lengthWithTerminator = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(textLengthOffset, sizeof(uint)));
        if (lengthWithTerminator < sizeof(ushort) ||
            (lengthWithTerminator & 1) != 0 ||
            lengthWithTerminator - sizeof(ushort) > payload.Length - textOffset)
        {
            return false;
        }

        var textLength = checked((int)lengthWithTerminator - sizeof(ushort));
        text = Encoding.Unicode.GetString(payload.Slice(textOffset, textLength)).TrimEnd('\0');
        return text.Length > 0;
    }

}
