using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void HandleForgeSelection(GamePacket packet)
    {
        if (_account is null ||
            _character is null ||
            !ForgeItemSelectionPacket.TryParse(packet.Payload, out var selection) ||
            selection.RequestMode != ForgeItemSelectionPacket.OrdinaryForgeMode)
        {
            Console.WriteLine(
                $"[forge] ignored malformed/unsupported selection character={_character?.Name ?? "<none>"} len={packet.Length}");
            return;
        }

        if (selection.IsOddsMaterialIncrement)
        {
            HandleForgeOddsIncrement(selection);
            return;
        }

        var item = KitBagSlots.GetItem(_character.KitBag, selection.KitBagSlot);
        if (!selection.Matches(item))
        {
            // Some client builds emit a second descriptor containing stale
            // scratch-buffer data. Never let that overwrite a valid staged
            // selection; only current authoritative bag contents are accepted.
            Console.WriteLine(
                $"[forge] ignored stale selection character={_character.Name} slot={selection.KitBagSlot} item={selection.ItemId}");
            return;
        }

        if (ForgingMaterialRuleCatalog.TryGet(item.Id, out var materialRule))
        {
            if (materialRule.MaterialType is >= 1 and <= 3)
            {
                if (selection.DestinationSlot != ForgeItemSelectionPacket.PrimaryMaterialDestinationSlot)
                {
                    Console.WriteLine(
                        $"[forge] ignored primary in invalid destination character={_character.Name} destination={selection.DestinationSlot}");
                    return;
                }

                EnsureForgeSelectionBatch();
                _forgePrimaryMaterial = new ForgeSlotSelection(
                    selection.KitBagSlot,
                    item,
                    1);
            }
            else if (materialRule.MaterialType == 4)
            {
                if (selection.DestinationSlot != ForgeItemSelectionPacket.OddsMaterialDestinationSlot)
                {
                    Console.WriteLine(
                        $"[forge] ignored crystal in invalid destination character={_character.Name} destination={selection.DestinationSlot}");
                    return;
                }

                EnsureForgeSelectionBatch();
                // Destination 5 is the trustworthy item descriptor paired
                // with action 88. It validates the source but does not add a
                // crystal by itself (important when the client is at cap 25).
                _forgeOddsMaterials.ValidateDescriptor(selection.KitBagSlot, item);
            }
            else
            {
                Console.WriteLine(
                    $"[forge] ignored non-ordinary material character={_character.Name} item={item.Id} type={materialRule.MaterialType}");
                return;
            }
        }
        else
        {
            if (selection.DestinationSlot != ForgeItemSelectionPacket.EquipmentDestinationSlot ||
                !EquipmentForgeCatalog.TryGet(item.Id, out _))
            {
                Console.WriteLine(
                    $"[forge] ignored non-forgeable equipment character={_character.Name} item={item.Id} destination={selection.DestinationSlot}");
                return;
            }

            EnsureForgeSelectionBatch();
            _forgeEquipment = new ForgeSlotSelection(selection.KitBagSlot, item, 1);
        }

        Console.WriteLine(
            $"[forge] staged character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} destination={selection.DestinationSlot} reserved={GetForgeReservedQuantity(selection.DestinationSlot)}");
    }

    private void HandleForgeOddsIncrement(ForgeItemSelectionPacket selection)
    {
        if (_character is null)
        {
            return;
        }

        // Action 88 intentionally has no valid item descriptor. Resolve its
        // trustworthy bag coordinates against the current server inventory.
        var item = KitBagSlots.GetItem(_character.KitBag, selection.KitBagSlot);
        if (item.IsEmpty ||
            !ForgingMaterialRuleCatalog.TryGet(item.Id, out var materialRule) ||
            materialRule.MaterialType != 4)
        {
            Console.WriteLine(
                $"[forge] ignored crystal increment character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id}");
            return;
        }

        EnsureForgeSelectionBatch();
        if (!_forgeOddsMaterials.TryIncrement(selection.KitBagSlot, item))
        {
            Console.WriteLine(
                $"[forge] ignored excess/stale crystal character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} reserved={_forgeOddsMaterials.TotalQuantity} stack={item.Stack}");
            return;
        }

        Console.WriteLine(
            $"[forge] staged character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} destination={selection.DestinationSlot} reserved={_forgeOddsMaterials.TotalQuantity}");
    }

    private async Task HandleForgeStartAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            !IsOrdinaryForgeStart(packet))
        {
            ClearForgeSelection();
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                $"[forge] rejected incomplete/expired start character={_character?.Name ?? "<none>"} len={packet.Length}");
            return;
        }

        if (_session.IsSecure &&
            !packet.ClientOperationId.HasValue)
        {
            ClearForgeSelection();
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.EquipmentForge);
            CommandMetrics.Record(
                CommandFamily.EquipmentForge,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.InvalidIntent);
            await _session.SendAsync(
                PacketBuilder.ForgeResult(
                    success: false,
                    resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                "[forge] rejected secure start without operation UUID");
            return;
        }

        if (_session.IsSecure)
        {
            await HandleDurableForgeStartAsync(
                packet.ClientOperationId!.Value,
                cancellationToken);
            return;
        }

        if (!TryCaptureForgeRequest(out var request))
        {
            ClearForgeSelection();
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                $"[forge] rejected incomplete/expired start character={_character.Name} len={packet.Length}");
            return;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.EquipmentForge);
        // Clear before awaiting persistence so a duplicated Start packet can
        // never consume the same reservation twice.
        ClearForgeSelection();

        ForgeTransactionResult result;
        try
        {
            result = await _store.ForgeEquipmentAsync(
                _account.Id,
                _character.Id,
                request!,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[forge] persistence failure account={_account.Id} character={_character.Name}: {ex.Message}");
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            return;
        }

        if (!result.Committed || result.Character is null)
        {
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                $"[forge] rejected account={_account.Id} character={_character.Name} status={result.Status} reason={result.RejectionReason}");
            return;
        }

        _character = result.Character;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        // The result must arrive before authoritative bag/status refreshes:
        // the legacy client releases its forge wait flag and applies its local
        // animation/result transition while processing this packet.
        await _session.SendAsync(
            PacketBuilder.ForgeResult(result.Succeeded, resultKind: 1),
            cancellationToken,
            "ForgeResult");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "ForgePlayerStatus");
        await SendKitBagRefreshAsync(cancellationToken);

        Console.WriteLine(
            $"[forge] committed account={_account.Id} character={_character.Name} status={result.Status} operation={result.MaterialType} chance={result.Probability} silver={result.SilverSpent} equipment={result.EquipmentBefore.Id}->{result.EquipmentAfter.Id} quality={result.EquipmentBefore.Quality}->{result.EquipmentAfter.Quality} grade={result.EquipmentBefore.Grade}->{result.EquipmentAfter.Grade}");
    }

    private static bool IsOrdinaryForgeStart(GamePacket packet)
    {
        const int expectedPayloadLength = 36;
        const int modeOffset = 4;
        return packet.Payload.Length == expectedPayloadLength &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.Payload.Slice(modeOffset, sizeof(uint))) ==
            ForgeItemSelectionPacket.OrdinaryForgeMode;
    }

    private bool TryCaptureForgeRequest(
        out ForgeTransactionRequest? request)
    {
        request = null;
        if (!HasCurrentForgeIdentity() ||
            _forgeEquipment is null ||
            _forgePrimaryMaterial is null ||
            (_forgeOddsMaterials.TotalQuantity > 0 &&
                !_forgeOddsMaterials.IsFullyLinked) ||
            IsForgeSelectionExpired())
        {
            return false;
        }

        var oddsMaterials = _forgeOddsMaterials.CaptureSelections();
        request = new ForgeTransactionRequest(
            _forgeEquipment,
            _forgePrimaryMaterial,
            oddsMaterials.FirstOrDefault(),
            oddsMaterials.Skip(1).ToArray());
        return true;
    }

    private void ClearForgeSelection()
    {
        _forgeEquipment = null;
        _forgePrimaryMaterial = null;
        _forgeOddsMaterials.Clear();
        _forgeAccountId = null;
        _forgeCharacterId = null;
        _forgeSelectionStartedTimestamp = 0;
    }

    private void EnsureForgeSelectionBatch()
    {
        if (HasCurrentForgeIdentity() && !IsForgeSelectionExpired())
        {
            return;
        }

        ClearForgeSelection();
        _forgeAccountId = _account!.Id;
        _forgeCharacterId = _character!.Id;
        _forgeSelectionStartedTimestamp = Stopwatch.GetTimestamp();
    }

    private int GetForgeReservedQuantity(int destinationSlot)
    {
        return destinationSlot switch
        {
            ForgeItemSelectionPacket.EquipmentDestinationSlot => _forgeEquipment?.Quantity ?? 0,
            ForgeItemSelectionPacket.PrimaryMaterialDestinationSlot => _forgePrimaryMaterial?.Quantity ?? 0,
            ForgeItemSelectionPacket.OddsMaterialDestinationSlot or
                ForgeItemSelectionPacket.OddsMaterialIncrementAction => _forgeOddsMaterials.TotalQuantity,
            _ => 0
        };
    }

    private bool HasCurrentForgeIdentity()
    {
        return ForgeSelectionMatchesIdentity(
            _forgeAccountId,
            _forgeCharacterId,
            _account,
            _character);
    }

    internal static bool ForgeSelectionMatchesIdentity(
        int? stagedAccountId,
        int? stagedCharacterId,
        GameAccount? account,
        GameCharacter? character)
    {
        return account is not null &&
               character is not null &&
               character.AccountId == account.Id &&
               stagedAccountId == account.Id &&
               stagedCharacterId == character.Id;
    }

    private bool IsForgeSelectionExpired()
    {
        return _forgeAccountId.HasValue &&
               Stopwatch.GetElapsedTime(_forgeSelectionStartedTimestamp) > ForgeSelectionTtl;
    }

}
