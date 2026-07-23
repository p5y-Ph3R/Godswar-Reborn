using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleNpcDialogOpenAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < 4)
        {
            Console.WriteLine("[npc] dialog open ignored: payload too short");
            return;
        }

        ClearGearEnhancerSelection();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload[..4]);

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine($"[npc] dialog open ignored: unknown npc={npcId} map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        if (GearEnhancerProtocol.IsEnhancerNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    GearEnhancerProtocol.DialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine($"[gear-enhancer] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
            return;
        }

        if (GearEnhancerProtocol.IsOriginEnhancerNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    GearEnhancerProtocol.OriginDialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine($"[origin-enhancer] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
            return;
        }

        if (HolySuitDesignProtocol.IsNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    HolySuitDesignProtocol.DialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine(
                $"[holy-suit-design] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
            return;
        }

        if (!IsHolyStoneArtisan(npc))
        {
            Console.WriteLine($"[npc] dialog open has no implemented script npc={npcId} key={npc.NpcKey}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(npc.InteractionId, HolyStoneDialogIndex, npc.NpcKey),
            cancellationToken,
            "NpcDialogOpenAck");
        Console.WriteLine($"[holy-stone] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
    }

    private async Task HandleNpcDialogPageRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < 4)
        {
            Console.WriteLine("[npc] page request ignored: payload too short");
            return;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload[..4]);

        Console.WriteLine(
            TryResolveMapNpc(npcId, out var npc)
                ? $"[npc] page request npc={npcId} key={npc.NpcKey}"
                : $"[npc] page request ignored: unknown npc={npcId}");
    }

    private async Task HandleNpcFunctionActionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            Console.WriteLine("[npc] function action ignored: no active character");
            return;
        }

        if (!TryReadNpcFunctionAction(packet.Payload, out var npcId, out var dialogIndex, out var subId, out var args))
        {
            Console.WriteLine("[npc] function action ignored: payload does not match captured NPC function shape");
            return;
        }

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        if (GearEnhancerProtocol.IsEnhancerNpcKey(npc.NpcKey))
        {
            if (GearEnhancerProtocol.TryBuildInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var gearMentorResponse))
            {
                ClearGearEnhancerSelection();
                // Stock NpcFunBreak changes from its menu to Enhance/Add/Delete
                // entirely client-side. Start an operation-unbound staging
                // context here so the following native 10193 selections are
                // retained until final 10069 identifies operation 2/3/6.
                _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    operation: null,
                    expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
                await _session.SendAsync(
                    gearMentorResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine($"[gear-mentor] original initial menu npc={npcId} items=1,2,3,4,5,6,7,8,9");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    cancellationToken);
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                subId == GearEnhancerProtocol.CombineGemPiecesMenuSubId)
            {
                // The stock client re-sends menu action 9 when the
                // server-backed combination page is confirmed. Normalize that
                // wire alias to operation 201 only while that page is active.
                if (IsCombineGemPiecesConfirmAlias(
                        subId,
                        _gearMentorOperationPageSubId))
                {
                    await HandleGearMentorTransactionAsync(
                        npcId,
                        GearEnhancerProtocol.CombineGemPiecesActionSubId,
                        args,
                        cancellationToken);
                    return;
                }

                ClearGearEnhancerSelection();
                _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    operation: null,
                    expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
                _gearMentorOperationPageSubId = GearEnhancerProtocol.CombineGemPiecesActionSubId;
                await _session.SendAsync(
                    GearEnhancerProtocol.BuildGemPieceCombinationPageResponse(npcId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] gem-piece combination page character={_character.Name} npc={npcId}");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsGearMentorTransactionSubId(subId))
            {
                await HandleGearMentorTransactionAsync(
                    npcId,
                    subId,
                    args,
                    cancellationToken);
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId))
            {
                ClearGearEnhancerSelection();
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        GearEnhancerProtocol.TemporarilyDisabledResultSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] unsupported original operation npc={npcId} subId={subId} response={GearEnhancerProtocol.TemporarilyDisabledResultSubId}");
            }
            return;
        }

        if (GearEnhancerProtocol.IsOriginEnhancerNpcKey(npc.NpcKey))
        {
            if (GearEnhancerProtocol.TryBuildOriginInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var originResponse))
            {
                ClearGearEnhancerSelection();
                await _session.SendAsync(
                    originResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine($"[origin-enhancer] initial menu npc={npcId} items=2,3,6");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.OriginDialogIndex &&
                GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    cancellationToken);
            }
            return;
        }

        if (HolySuitDesignProtocol.IsNpcKey(npc.NpcKey))
        {
            if (HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var holySuitResponse))
            {
                await _session.SendAsync(
                    holySuitResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[holy-suit-design] original initial menu npc={npcId} items=101,201,301,401");
                return;
            }

            if (dialogIndex == HolySuitDesignProtocol.DialogIndex &&
                HolySuitDesignProtocol.IsMenuSubId(subId))
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        HolySuitDesignProtocol.TemporarilyDisabledResultSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[holy-suit-design] unsupported original operation npc={npcId} subId={subId} response={HolySuitDesignProtocol.TemporarilyDisabledResultSubId}");
            }
            return;
        }

        if (!IsHolyStoneArtisan(npc))
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        Console.WriteLine(
            $"[holy-stone] action npc={npcId} dialog={dialogIndex} subId={subId} args={string.Join(',', args)}");

        if (subId == -1)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, 101, 201, 301, 401, 501, 601, 701),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        if (subId == HolyStoneMenuMount && !HasClientKitBagSlot(args))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, 106, 206, 306, 406),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var operation = subId switch
        {
            HolyStoneMenuMount or 106 or 206 or 306 or 406 => HolyStoneOperation.MountStone,
            HolyStoneMenuRemove => HolyStoneOperation.RemoveStone,
            HolyStoneMenuDrill => HolyStoneOperation.DrillSocket,
            _ => (HolyStoneOperation?)null
        };

        if (operation is null)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, HolyStoneInsufficientFunds),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var targetSlot = FirstClientKitBagSlot(args);
        var stoneSlot = NextClientKitBagSlot(args, targetSlot);
        var destinationSlot = stoneSlot >= 0 ? stoneSlot : -1;
        var socketIndex = SocketIndexFromSubId(subId);
        var updatedCharacter = await _store.ApplyWeaponHolyStoneAsync(
            _account.Id,
            _character.Id,
            operation.Value,
            targetSlot,
            socketIndex,
            stoneSlot,
            destinationSlot,
            cancellationToken);

        var responseSubId = updatedCharacter is null
            ? HolyStoneInsufficientFunds
            : operation.Value switch
            {
                HolyStoneOperation.MountStone => HolyStoneMountSuccess,
                HolyStoneOperation.RemoveStone => HolyStoneRemoveSuccess,
                HolyStoneOperation.DrillSocket => HolyStoneDrillSuccess,
                _ => HolyStoneInsufficientFunds
            };

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (updatedCharacter is null)
        {
            return;
        }

        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync($"holy-stone-{operation.Value}", cancellationToken);
        _registry.UpdateCharacter(_session, _character);

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.EquipmentItemSnapshot(_character, EquipmentSlots.Weapon),
            cancellationToken,
            "EquipmentItemSnapshot");
        foreach (var detailPage in PacketBuilder.KitBagDetailPages(_character))
        {
            await _session.SendAsync(detailPage, cancellationToken, "KitBagDetail");
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync($"holy-stone-{operation.Value}", cancellationToken);
    }

}
