using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IMedusaDailyEntryClaimStore? _medusaDailyEntries;
    private InstanceCallerPageContext? _instanceCallerPageContext;

    private async Task HandleInstanceCallerAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            ClearInstanceCallerPageContext();
            return;
        }

        if (InstanceCallerProtocol.TryGetMedusaPage(
                dialogIndex,
                subId,
                arguments,
                out var pageSubIds))
        {
            if (!IsCanonicalInstanceCallerAction(
                    packet,
                    route,
                    npcId,
                    dialogIndex))
            {
                Console.Error.WriteLine(
                    "[instance-caller] rejected non-canonical Medusa page " +
                    $"request npc={npcId}");
                return;
            }

            if (!_registry.TryGetSessionWorldInstanceId(
                    _session,
                    out var sourceWorldInstanceId))
            {
                ClearInstanceCallerPageContext();
                return;
            }

            _instanceCallerPageContext = new InstanceCallerPageContext(
                _account.Id,
                _character.Id,
                route.NpcKey,
                npcId,
                dialogIndex,
                sourceWorldInstanceId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow +
                    InstanceCallerProtocol.PageContextLifetime);
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "InstanceCallerMedusaPage");
            return;
        }

        if (!InstanceCallerProtocol.TryResolveDifficulty(
                dialogIndex,
                subId,
                arguments,
                out var difficulty))
        {
            return;
        }

        if (!IsCanonicalInstanceCallerAction(
                packet,
                route,
                npcId,
                dialogIndex))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected non-canonical Medusa " +
                $"difficulty request npc={npcId}");
            return;
        }

        var context = _instanceCallerPageContext;
        ClearInstanceCallerPageContext();
        if (!IsCurrentInstanceCallerPageContext(
                context,
                route,
                npcId,
                dialogIndex))
        {
            Console.Error.WriteLine(
                "[instance-caller] rejected Medusa difficulty without " +
                $"current page context npc={npcId} difficulty={difficulty}");
            return;
        }

        if (!_registry.CanInitiateInstance(_session))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.QueueUnavailableResultSubId),
                cancellationToken,
                "InstanceCallerPartyLeaderRequired");
            Console.WriteLine(
                "[instance-caller] rejected non-leader instance " +
                $"request character={_character.Name}");
            return;
        }

        var partyStatus = _registry.TryCaptureMedusaParty(
            _session,
            out var party);
        if (partyStatus != MedusaPartyEntryStatus.Ready)
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                partyStatus,
                cancellationToken);
            return;
        }

        var encounterDifficulty = difficulty switch
        {
            InstanceCallerDifficulty.Advanced =>
                MedusaEncounterDifficulty.Enhanced,
            InstanceCallerDifficulty.Normal =>
                MedusaEncounterDifficulty.Normal,
            InstanceCallerDifficulty.Mythic =>
                MedusaEncounterDifficulty.Mythic,
            _ => throw new InvalidOperationException(
                $"Unsupported Instance Caller difficulty {difficulty}.")
        };
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                encounterDifficulty,
                out var encounter) ||
            !encounter.ContentMapId.TryGetLegacyValue(out var targetMapId) ||
            !MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry",
                out _))
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                MedusaPartyEntryStatus.RuntimeUnavailable,
                cancellationToken);
            return;
        }

        var entryStatus = await BeginMedusaLeaderEntryAsync(
            party,
            encounterDifficulty,
            targetMapId,
            cancellationToken);
        if (entryStatus != MedusaPartyEntryStatus.Ready)
        {
            await SendInstanceCallerFailureAsync(
                npcId,
                dialogIndex,
                entryStatus,
                cancellationToken);
        }
    }

    private async Task SendInstanceCallerFailureAsync(
        uint npcId,
        int dialogIndex,
        MedusaPartyEntryStatus status,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                InstanceCallerProtocol.QueueUnavailableResultSubId),
            cancellationToken,
            "InstanceCallerAdmissionRejected");
        Console.WriteLine(
            "[instance-caller] Medusa admission rejected " +
            $"character={_character?.Name ?? "<none>"} status={status}");
    }

    private bool IsCurrentInstanceCallerPageContext(
        InstanceCallerPageContext? context,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        context is not null &&
        _account is not null &&
        _character is not null &&
        context.ExpiresAt > DateTimeOffset.UtcNow &&
        context.AccountId == _account.Id &&
        context.CharacterId == _character.Id &&
        _character.AccountId == _account.Id &&
        context.NpcInteractionId == npcId &&
        context.DialogIndex == dialogIndex &&
        string.Equals(
            context.NpcKey,
            route.NpcKey,
            StringComparison.Ordinal) &&
        _registry.TryGetSessionWorldInstanceId(
            _session,
            out var currentWorldInstanceId) &&
        currentWorldInstanceId == context.SourceWorldInstanceId;

    private static bool IsCanonicalInstanceCallerAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == InstanceCallerProtocol.ActionPacketBytes &&
        packet.Buffer.Length == InstanceCallerProtocol.ActionPacketBytes &&
        route.Behavior == NpcDialogueBehavior.InstanceCaller &&
        route.DialogIndex == InstanceCallerProtocol.DialogIndex &&
        InstanceCallerProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == InstanceCallerProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;

    private void ClearInstanceCallerPageContext() =>
        _instanceCallerPageContext = null;
}
