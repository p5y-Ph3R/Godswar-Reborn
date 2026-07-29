using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async ValueTask<NpcDialogueRouteDefinition?>
        ResolveNpcDialogueRouteAsync(
            NpcSpawnDefinition npc,
            CancellationToken cancellationToken)
    {
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
            return null;
        }

        if (dialogue.Route is null)
        {
            Console.WriteLine(
                $"[npc] dialogue has no implemented behavior " +
                $"npc={npc.InteractionId} key={npc.NpcKey}");
            return null;
        }

        if (!NpcDialogueBehaviorRegistry.IsAllowed(npc, dialogue.Route))
        {
            Console.Error.WriteLine(
                "[npc] rejected dialogue capability mismatch " +
                $"npc={npc.InteractionId} key={npc.NpcKey} " +
                $"behavior={dialogue.Route.Behavior}");
            return null;
        }

        return dialogue.Route;
    }

    private async Task SendNpcInitialMenuAsync(
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        CancellationToken cancellationToken)
    {
        ClearGearEnhancerSelection();
        if (route.Behavior == NpcDialogueBehavior.GearMentor)
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
