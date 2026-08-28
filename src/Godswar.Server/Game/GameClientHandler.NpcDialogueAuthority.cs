using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async ValueTask<IReadOnlyList<NpcDialogueRouteDefinition>>
        ResolveNpcDialogueRoutesAsync(
            NpcSpawnDefinition npc,
            CancellationToken cancellationToken)
    {
        if (CapitalNpcServiceProtocol.TryResolve(npc, out var service) &&
            service == CapitalNpcServiceKind.ExchangeMentor)
        {
            return [CapitalNpcServiceProtocol.ExchangeRoute(npc)];
        }

        NpcDialogueContent dialogue;
        try
        {
            dialogue = await _worldContent.ReadNpcDialogueAsync(
                npc.NpcKey,
                cancellationToken);
        }
        catch (WorldContentUnavailableException ex) when (
            ex.Family == "npc-dialogues" &&
            ex.Reason == WorldContentFailureReason.Missing)
        {
            Console.WriteLine(
                $"[npc] dialogue missing npc={npc.InteractionId} " +
                $"key={npc.NpcKey}");
            return [];
        }

        if (dialogue.Routes.Count == 0)
        {
            Console.WriteLine(
                $"[npc] dialogue has no implemented behavior " +
                $"npc={npc.InteractionId} key={npc.NpcKey}");
            return [];
        }

        if (dialogue.Routes.Any(route =>
                !NpcDialogueBehaviorRegistry.IsAllowed(npc, route)))
        {
            Console.Error.WriteLine(
                "[npc] rejected dialogue capability mismatch " +
                $"npc={npc.InteractionId} key={npc.NpcKey}");
            return [];
        }

        return dialogue.Routes;
    }

    private async ValueTask<NpcDialogueRouteDefinition?>
        ResolveNpcDialogueRouteAsync(
            NpcSpawnDefinition npc,
            int dialogIndex,
            CancellationToken cancellationToken)
    {
        var routes = await ResolveNpcDialogueRoutesAsync(
            npc,
            cancellationToken);
        return routes.FirstOrDefault(route =>
            route.DialogIndex == dialogIndex);
    }

    private async Task SendNpcInitialMenuAsync(
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        CancellationToken cancellationToken)
    {
        ClearGearEnhancerSelection();
        ClearInstanceCallerPageContext();
        if (route.Behavior == NpcDialogueBehavior.WarehouseManager)
        {
            await SendWarehouseManagerMenuAsync(
                npc,
                route,
                cancellationToken);
            return;
        }
        if (route.Behavior == NpcDialogueBehavior.HolySuitDesign &&
            _character is { Level: < 70 })
        {
            await _session.SendAsync(
                HolySuitDesignProtocol.BuildResultResponse(
                    npc.InteractionId,
                    HolySuitDesignProtocol.StoreLevelTooLowResultSubId),
                cancellationToken,
                "HolySuitLevelRequirement");
            return;
        }

        if (route.Behavior is
            NpcDialogueBehavior.GearMentor or
            NpcDialogueBehavior.ClassSuit)
        {
            if (_account is null || _character is null)
            {
                return;
            }

            _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                _account.Id,
                _character.Id,
                npc.InteractionId,
                route.DialogIndex,
                operation: null,
                expiresAt:
                    DateTimeOffset.UtcNow +
                    GearEnhancerProtocol.SelectionContextLifetime);
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                route.DialogIndex,
                route.InitialMenuSubIds.ToArray()),
            cancellationToken,
            "NpcFunctionActionResponse");
        Console.WriteLine(
            $"[npc] initial menu npc={npc.InteractionId} " +
            $"behavior={route.Behavior} dialog={route.DialogIndex} " +
            $"items={string.Join(',', route.InitialMenuSubIds)}");
    }
}
