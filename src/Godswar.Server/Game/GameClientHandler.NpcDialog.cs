using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.Domain.World.Content;

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

        var route = await ResolveNpcDialogueRouteAsync(
            npc,
            cancellationToken);
        if (route is null)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                route.DialogIndex,
                route.ClientScriptKey),
            cancellationToken,
            "NpcDialogOpenAck");
        Console.WriteLine(
            $"[npc] dialog open npc={npc.InteractionId} " +
            $"script={route.ClientScriptKey} " +
            $"behavior={route.Behavior} dialog={route.DialogIndex}");
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
            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId: null,
                "no_active_character",
                cancellationToken);
            Console.WriteLine("[npc] function action ignored: no active character");
            return;
        }

        if (!TryReadNpcFunctionAction(packet.Payload, out var npcId, out var dialogIndex, out var subId, out var args))
        {
            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId: null,
                "malformed_function_action",
                cancellationToken);
            Console.WriteLine("[npc] function action ignored: payload does not match captured NPC function shape");
            return;
        }

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                    packet,
                    npcId,
                    subId,
                    cancellationToken))
            {
                return;
            }

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "npc_not_authoritative_for_map",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId));
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        var route = await ResolveNpcDialogueRouteAsync(
            npc,
            cancellationToken);
        if (route is null || dialogIndex != route.DialogIndex)
        {
            if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                    packet,
                    npcId,
                    subId,
                    cancellationToken))
            {
                return;
            }

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "dialogue_route_mismatch",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId));
            Console.WriteLine(
                $"[npc] function action rejected npc={npcId} " +
                $"dialog={dialogIndex} subId={subId}");
            return;
        }

        if (subId == -1)
        {
            await SendNpcInitialMenuAsync(
                npc,
                route,
                cancellationToken);
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.GearMentor)
        {
            if (GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    cancellationToken);
                return;
            }

            if (subId == GearEnhancerProtocol.CombineGemPiecesMenuSubId)
            {
                // The stock client re-sends menu action 9 when the
                // server-backed combination page is confirmed. Normalize that
                // wire alias to operation 201 only while that page is active.
                if (IsCombineGemPiecesConfirmAlias(
                        subId,
                        _gearMentorOperationPageSubId,
                        packet.ClientOperationId.HasValue))
                {
                    await HandleGearMentorTransactionAsync(
                        npcId,
                        GearEnhancerProtocol.CombineGemPiecesActionSubId,
                        args,
                        packet.ClientOperationId,
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

            if (GearEnhancerProtocol.IsGearMentorTransactionSubId(subId))
            {
                await HandleGearMentorTransactionAsync(
                    npcId,
                    subId,
                    args,
                    packet.ClientOperationId,
                    cancellationToken);
                return;
            }

            if (GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId))
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

        if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                packet,
                npcId,
                subId,
                cancellationToken))
        {
            return;
        }

        if (await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "valuable_command_wrong_npc_behavior",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId)))
        {
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.OriginEnhancer)
        {
            if (GearEnhancerProtocol.IsOperationSubId(subId))
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

        if (route.Behavior == NpcDialogueBehavior.HolySuitDesign)
        {
            if (HolySuitDesignProtocol.IsMenuSubId(subId))
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

        if (route.Behavior != NpcDialogueBehavior.HolyStone)
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        Console.WriteLine(
            $"[holy-stone] action npc={npcId} dialog={dialogIndex} subId={subId} args={string.Join(',', args)}");

        if (subId == -1)
        {
            return;
        }

        if (subId == HolyStoneMenuMount && !HasClientKitBagSlot(args))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    route.DialogIndex,
                    106,
                    206,
                    306,
                    406),
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
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    route.DialogIndex,
                    HolyStoneInsufficientFunds),
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
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                route.DialogIndex,
                responseSubId),
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

    private static CommandFamily?
        ResolveSecureGearMentorCommandFamily(int wireSubId) =>
        wireSubId switch
        {
            GearEnhancerProtocol.DecomposeGearSubId =>
                CommandFamily.GearMentorDecomposeGear,
            GearEnhancerProtocol.MakeAttributeStoneSubId =>
                CommandFamily.GearMentorMakeAttributeStone,
            GearEnhancerProtocol.TransformCrystalSubId =>
                CommandFamily.GearMentorTransformCrystal,
            GearEnhancerProtocol.CombineGemPiecesMenuSubId =>
                CommandFamily.GearMentorCombineGemPieces,
            _ => null
        };

}
