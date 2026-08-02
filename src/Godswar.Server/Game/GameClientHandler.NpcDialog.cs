using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
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

        var routes = await ResolveNpcDialogueRoutesAsync(
            npc,
            cancellationToken);
        if (routes.Count == 0)
        {
            return;
        }

        var routePackets = routes
            .Select(route => PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                route.DialogIndex,
                route.ClientScriptKey))
            .ToArray();
        var routeStream = new byte[
            routePackets.Sum(static routePacket => routePacket.Length)];
        var routeOffset = 0;
        foreach (var routePacket in routePackets)
        {
            routePacket.CopyTo(routeStream, routeOffset);
            routeOffset += routePacket.Length;
        }

        await _session.SendAsync(
            routeStream,
            cancellationToken,
            routes.Count == 1
                ? "NpcDialogOpenAck"
                : "NpcDialogOpenRouteStream",
            framed: routes.Count == 1);

        foreach (var route in routes)
        {
            Console.WriteLine(
                $"[npc] dialog open npc={npc.InteractionId} " +
                $"script={route.ClientScriptKey} " +
                $"behavior={route.Behavior} dialog={route.DialogIndex} " +
                $"order={route.RouteOrder}");
        }
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

        if (await TryHandleNonCanonicalSecureGearMentorPacketAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken))
        {
            return;
        }

        HolyStoneWireIntent? secureHolyStoneIntent = null;
        HolyStoneWireIntent? rawHolyStoneIntent = null;
        if (packet.ClientOperationId.HasValue &&
            _session.IsSecure &&
            HolyStoneProtocol.IsEndpoint(npcId, dialogIndex) &&
            HolyStoneProtocol.TryResolveBoundaryOperation(
                subId,
                out var secureHolyStoneOperation))
        {
            if (!HolyStoneProtocol.TryReadMutation(
                    packet,
                    out var exactNpcId,
                    out var exactDialogIndex,
                    out var exactIntent) ||
                exactNpcId != npcId ||
                exactDialogIndex != dialogIndex ||
                exactIntent.Operation != secureHolyStoneOperation)
            {
                await RejectMalformedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    secureHolyStoneOperation,
                    "packet_shape_mismatch",
                    cancellationToken);
                return;
            }

            secureHolyStoneIntent = exactIntent;
        }
        else if (!packet.ClientOperationId.HasValue &&
                 _session.IsSecure &&
                 HolyStoneProtocol.IsEndpoint(npcId, dialogIndex) &&
                 HolyStoneProtocol.IsMutationSubId(subId) &&
                 !HolyStoneProtocol.IsExactMountNavigation(packet))
        {
            await RejectUnidentifiedSecureHolyStoneAsync(
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return;
        }
        else if (!_session.IsSecure &&
                 HolyStoneProtocol.IsEndpoint(npcId, dialogIndex))
        {
            var exactNavigation =
                !packet.ClientOperationId.HasValue &&
                (HolyStoneProtocol.IsExactMountNavigation(packet) ||
                 HolyStoneProtocol.IsExactAdvancedDrillNavigation(
                     packet));
            var exactNpcId = 0u;
            var exactDialogIndex = 0;
            var exactIntent = default(HolyStoneWireIntent);
            var exactMutation =
                !packet.ClientOperationId.HasValue &&
                HolyStoneProtocol.TryReadMutation(
                    packet,
                    out exactNpcId,
                    out exactDialogIndex,
                    out exactIntent) &&
                exactNpcId == npcId &&
                exactDialogIndex == dialogIndex;
            if (!exactNavigation && !exactMutation)
            {
                if (HolyStoneProtocol.IsAdvancedDrillSubId(subId))
                {
                    await RejectUnsupportedAdvancedHolyStoneAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        cancellationToken);
                    return;
                }

                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        HolyStoneNativeResults.WrongSelectionSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                return;
            }

            if (exactMutation)
            {
                rawHolyStoneIntent = exactIntent;
            }
        }

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                if (await TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        cancellationToken))
                {
                    return;
                }

                await RejectUnroutedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    "npc_not_authoritative_for_map",
                    cancellationToken);
                return;
            }

            if (await TryResolveSecureHolySuitOutsideRouteAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    subId,
                    "npc_not_authoritative_for_map",
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                    packet,
                    subId,
                    cancellationToken))
            {
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

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "npc_not_authoritative_for_map",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId) ??
                    ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex);
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        var route = await ResolveNpcDialogueRouteAsync(
            npc,
            dialogIndex,
            cancellationToken);
        if (route is null)
        {
            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                if (await TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
                        packet,
                        npcId,
                        dialogIndex,
                        holyStoneIntent,
                        cancellationToken))
                {
                    return;
                }

                await RejectUnroutedSecureHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    "dialogue_route_mismatch",
                    cancellationToken);
                return;
            }

            if (await TryResolveSecureHolySuitOutsideRouteAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    subId,
                    "dialogue_route_mismatch",
                    cancellationToken))
            {
                return;
            }

            if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                    packet,
                    subId,
                    cancellationToken))
            {
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

            await TryRejectUnroutedSecureCommandAsync(
                packet,
                npcId,
                "dialogue_route_mismatch",
                cancellationToken,
                ResolveSecureGearMentorCommandFamily(subId) ??
                    ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex);
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
                    packet.ClientOperationId,
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

        if (route.Behavior == NpcDialogueBehavior.OriginEnhancer)
        {
            if (GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    packet.ClientOperationId,
                    cancellationToken);
            }
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.HolyStone)
        {
            if (HolyStoneProtocol.IsAdvancedDrillSubId(subId))
            {
                await RejectUnsupportedAdvancedHolyStoneAsync(
                    packet,
                    npcId,
                    dialogIndex,
                    cancellationToken);
                return;
            }

            if (secureHolyStoneIntent is { } holyStoneIntent)
            {
                await HandleDurableHolyStoneAsync(
                    npcId,
                    dialogIndex,
                    holyStoneIntent,
                    packet.ClientOperationId!.Value,
                    cancellationToken);
                return;
            }

            // A UUID-bearing command is an authenticated secure command
            // boundary. If its body did not pass the exact Holy Stone shape
            // above, it must never fall through to the non-idempotent legacy
            // store path.
            if (packet.ClientOperationId.HasValue &&
                _session.IsSecure)
            {
                Console.WriteLine(
                    "[holy-stone] preserved unsupported secure packet " +
                    $"npc={npcId} dialog={dialogIndex} subId={subId}");
                return;
            }
        }

        if (route.Behavior == NpcDialogueBehavior.HolySuitDesign)
        {
            await HandleHolySuitDesignAsync(
                packet,
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return;
        }

        if (route.Behavior == NpcDialogueBehavior.ClassSuit)
        {
            await HandleClassSuitAsync(
                packet,
                route,
                npcId,
                subId,
                args,
                cancellationToken);
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

        if (await TryReplayClassSuitBeforeRouteRejectionAsync(
                packet,
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
            ResolveSecureGearMentorCommandFamily(subId) ??
                ResolveSecureClassSuitCommandFamily(subId),
                responseDialogIndex: dialogIndex))
        {
            return;
        }

        if (route.Behavior != NpcDialogueBehavior.HolyStone)
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        await HandleLegacyHolyStoneAsync(
            npcId,
            route.DialogIndex,
            subId,
            args,
            rawHolyStoneIntent,
            cancellationToken);
    }

    private static CommandFamily?
        ResolveSecureGearMentorCommandFamily(int wireSubId) =>
        wireSubId switch
        {
            GearEnhancerProtocol.DecomposeGearSubId =>
                CommandFamily.GearMentorDecomposeGear,
            GearEnhancerProtocol.EnhanceAttributeSubId =>
                CommandFamily.GearMentorEnhanceAttribute,
            GearEnhancerProtocol.AddAttributeSubId =>
                CommandFamily.GearMentorAddAttribute,
            GearEnhancerProtocol.MakeAttributeStoneSubId =>
                CommandFamily.GearMentorMakeAttributeStone,
            GearEnhancerProtocol.DeleteAttributesSubId =>
                CommandFamily.GearMentorDeleteAttribute,
            GearEnhancerProtocol.TransformCrystalSubId =>
                CommandFamily.GearMentorTransformCrystal,
            GearEnhancerProtocol.CombineGemPiecesMenuSubId =>
                CommandFamily.GearMentorCombineGemPieces,
            _ => null
        };

}
