using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
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
            _character = mountResult.Character;
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
            // Opcode 10033 detail pages and opcode 10056 slot-index packets do
            // not evict an icon already instantiated by this client. Capture
            // the occupied slots so the successful clear can use the native
            // source-to-FFFF deletion acknowledgement for every old icon.
            var deletionAcknowledgements =
                PacketBuilder.KitBagDeletionAcknowledgements(_character);

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
            _character = cleared;
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
        _character = result.Character;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[developer-item] granted account={_account.Id} character={_character.Name} item={material.ItemId} name=\"{material.DisplayName}\" quantity={request.Quantity}");
        return true;
    }

    private async Task HandleDurableDeveloperItemGrantAsync(
        GamePacket packet,
        uint itemId,
        string displayName,
        int quantity,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (_developerItemGrantCommands is null)
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Durable item grants are unavailable for this " +
                "storage provider.",
                cancellationToken);
            return;
        }

        if (!DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                itemId,
                quantity,
                clientOperationId,
                out var command))
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Invalid durable grant request.",
                cancellationToken);
            return;
        }

        var envelope = DeveloperItemGrantCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                _session.IsSecure
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);

        DeveloperItemGrantExecutionResult execution;
        try
        {
            execution = await _developerItemGrantCommands.ExecuteAsync(
                envelope,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.ProviderUnavailable);
            throw;
        }

        if (!execution.IsSuccess)
        {
            var outcome = execution.Disposition switch
            {
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict =>
                    CommandOutcome.RequestHashConflict,
                DeveloperItemGrantExecutionDisposition.InvalidIntent =>
                    CommandOutcome.InvalidIntent,
                _ => CommandOutcome.PreconditionFailed
            };
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                outcome);
            var feedback = execution.Disposition switch
            {
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict =>
                    "[item] That operation ID was already used for a " +
                    "different request.",
                DeveloperItemGrantExecutionDisposition.InvalidIntent =>
                    "[item] The requested item is not allowlisted.",
                _ =>
                    "[item] Not added: the character or kit-bag " +
                    "precondition failed."
            };
            await SendDeveloperItemFeedbackAsync(
                packet,
                feedback,
                cancellationToken);
            Console.WriteLine(
                "[developer-item] durable grant rejected " +
                $"account={_account.Id} character={_character.Name} " +
                $"item={itemId} quantity={quantity} " +
                $"outcome={outcome}");
            return;
        }

        var duplicate =
            execution.Disposition ==
            DeveloperItemGrantExecutionDisposition.Duplicate;
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            duplicate
                ? CommandOutcome.Duplicate
                : CommandOutcome.Accepted);

        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account.Id,
            cancellationToken);
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character.Id)
        {
            throw new InvalidDataException(
                "A committed inventory grant character could not be " +
                "reloaded.");
        }

        ApplyDeveloperItemGrantProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        await SendDeveloperItemFeedbackAsync(
            packet,
            duplicate
                ? $"[item] Operation already completed; bag refreshed."
                : $"[item] Added {quantity} {displayName}.",
            cancellationToken);
        Console.WriteLine(
            "[developer-item] durable grant completed " +
            $"account={_account.Id} character={_character.Name} " +
            $"item={itemId} quantity={quantity} " +
            $"outcome={(duplicate ? "duplicate" : "committed")}");
    }

    internal static void ApplyDeveloperItemGrantProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "An inventory projection cannot change character " +
                "identity.");
        }

        // A bag-only command must not replace the live mutable aggregate.
        // Position, vitals, and other runtime fields can be newer than their
        // asynchronously persisted snapshot.
        liveCharacter.KitBag = persistedCharacter.KitBag;
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
