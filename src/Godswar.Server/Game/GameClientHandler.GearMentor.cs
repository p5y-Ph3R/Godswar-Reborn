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
    private async Task HandleGearMentorTransactionAsync(
        uint npcId,
        int subId,
        IReadOnlyList<int> args,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var operation = (GearMentorOperation)subId;
        var maximumSelections = operation == GearMentorOperation.Decompose ? 3 : 1;
        var kitBagBeforeTransaction = _character.KitBag;
        var now = DateTimeOffset.UtcNow;
        var stagedContext = _gearEnhancerSelectionContext;
        var contextIsActive = GearMentorCommitContextMatches(
            stagedContext,
            _gearMentorOperationPageSubId,
            _account.Id,
            _character.Id,
            npcId,
            subId,
            now);
        var selectionShape = GearEnhancerProtocol.ReadSelection(
            args,
            out var firstSlot,
            out var secondSlot,
            out var thirdSlot);
        IReadOnlyList<GearEnhancerSelectionSnapshot> selectedSelections =
            selectionShape == GearEnhancerSelectionShape.Commit
            ? new[] { firstSlot, secondSlot, thirdSlot }
                .Where(static slot => slot >= 0)
                .Select(slot => CaptureGearEnhancerSelection(_character.KitBag, slot))
                .ToArray()
            : [];

        if (selectionShape is GearEnhancerSelectionShape.MenuSelection or
            GearEnhancerSelectionShape.MalformedCommit)
        {
            if (contextIsActive &&
                stagedContext!.TryResolveNativeSlots(
                    selectionShape,
                    minimumCount: 1,
                    maximumSelections,
                    out var stagedSelections))
            {
                selectedSelections = stagedSelections;
                selectionShape = GearEnhancerSelectionShape.Commit;
            }
        }

        // NpcFunBreak opens Decompose, Make Attribute Stones, and Transform
        // entirely client-side after the initial menu. Their first 10069 is
        // already the final action, just like Add/Enhance/Delete; waiting for
        // an intermediate page marker rejects the real select/clear/action
        // packet sequence as "chosen item doesn't exist". Combination is the
        // exception: menu 9 asks the server to open native action page 201, so
        // its matching page marker remains mandatory.
        var canCommit = contextIsActive;
        var request = new GearMentorRequest(
            operation,
            canCommit
                ? selectedSelections.Select(ToGearMentorSelection).ToArray()
                : []);
        var selectionSummary = selectedSelections.Count == 0
            ? "none"
            : string.Join(
                ',',
                selectedSelections.Select(selection =>
                    DescribeGearEnhancerSelection(_character.KitBag, selection.KitBagSlot)));

        if (operation == GearMentorOperation.MakeAttributeStone &&
            clientOperationId.HasValue)
        {
            await HandleDurableMakeAttributeStoneAsync(
                npcId,
                clientOperationId.Value,
                canCommit && selectedSelections.Count == 1
                    ? selectedSelections[0]
                    : null,
                kitBagBeforeTransaction,
                selectionSummary,
                cancellationToken);
            return;
        }

        if (operation == GearMentorOperation.Decompose &&
            clientOperationId.HasValue)
        {
            await HandleDurableGearMentorDecomposeAsync(
                npcId,
                clientOperationId.Value,
                canCommit &&
                    selectedSelections.Count is >= 1 and <= 3
                    ? selectedSelections.ToArray()
                    : null,
                kitBagBeforeTransaction,
                selectionSummary,
                cancellationToken);
            return;
        }

        if ((operation is GearMentorOperation.TransformCrystal or
                GearMentorOperation.CombineGemPieces) &&
            clientOperationId.HasValue)
        {
            await HandleDurableGearMentorMaterialConversionAsync(
                operation,
                npcId,
                clientOperationId.Value,
                canCommit && selectedSelections.Count == 1
                    ? selectedSelections[0]
                    : null,
                kitBagBeforeTransaction,
                selectionSummary,
                cancellationToken);
            return;
        }

        if (_session.IsSecure &&
            !clientOperationId.HasValue)
        {
            ClearGearEnhancerSelection();
            var family =
                ResolveSecureGearMentorCommandFamily(subId) ??
                throw new InvalidDataException(
                    $"Gear Mentor operation {subId} has no command family.");
            CommandMetrics.RecordUnsupportedLegacyIdentity(family);
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.InvalidIntent);
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    GearEnhancerProtocol.DialogIndex,
                    GearEnhancerProtocol.SelectedItemMissingResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
            Console.WriteLine(
                "[gear-mentor] rejected secure commit without " +
                $"operation UUID family={family}");
            return;
        }

        if (operation == GearMentorOperation.MakeAttributeStone)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.GearMentorMakeAttributeStone);
        }
        else if (operation == GearMentorOperation.Decompose)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.GearMentorDecomposeGear);
        }
        else if (operation == GearMentorOperation.TransformCrystal)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.GearMentorTransformCrystal);
        }
        else if (operation == GearMentorOperation.CombineGemPieces)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.GearMentorCombineGemPieces);
        }

        // A final action consumes the session-scoped selection before any
        // persistence await so it cannot be replayed.
        ClearGearEnhancerSelection();

        GearMentorTransactionResult? transaction = null;
        var responseSubId = GearEnhancerProtocol.SelectedItemMissingResultSubId;
        try
        {
            if (canCommit)
            {
                transaction = await _store.ProcessGearMentorAsync(
                    _account.Id,
                    _character.Id,
                    request,
                    cancellationToken);
                responseSubId = GearEnhancerProtocol.ResolveGearMentorResultSubId(transaction.Result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[gear-mentor] persistence failure account={_account.Id} character={_character.Name} operation={operation}: {ex.Message}");
        }

        if (transaction?.Character is not null)
        {
            InstallUpdatedCharacter(transaction.Character);
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        }

        var staleSelection = transaction?.Result?.Status == GearMentorStatus.StaleSelection;
        if (transaction?.Committed == true || staleSelection)
        {
            ClearForgeSelection();
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (transaction?.Committed == true || staleSelection)
        {
            if (transaction?.Committed == true)
            {
                foreach (var acknowledgement in
                    PacketBuilder.KitBagMutationDeletionAcknowledgements(
                        kitBagBeforeTransaction,
                        _character.KitBag))
                {
                    await _session.SendAsync(
                        acknowledgement,
                        cancellationToken,
                        "GearMentorKitBagDeleteAck");
                }
            }

            await SendKitBagRefreshAsync(cancellationToken);
        }

        var outputs = transaction?.Result?.Outputs.Count > 0
            ? string.Join(
                ',',
                transaction.Result.Outputs.Select(output =>
                    $"{output.ItemId}x{output.Quantity}/bound:{output.Bound}"))
            : "none";
        Console.WriteLine(
            $"[gear-mentor] result account={_account.Id} character={_character.Name} npc={npcId} operation={operation} status={transaction?.Result?.Status.ToString() ?? selectionShape.ToString()} response={responseSubId} committed={transaction?.Committed == true} selections=({selectionSummary}) outputs=({outputs}) reason=\"{transaction?.Result?.RejectionReason ?? "none"}\"");
    }

    internal static bool GearEnhancerCommitContextMatches(
        GearEnhancerSelectionContext? context,
        int? gearMentorOperationPageSubId,
        int accountId,
        int characterId,
        uint npcId,
        int dialogIndex,
        GearEnhancementOperation operation,
        DateTimeOffset now)
    {
        return (dialogIndex != GearEnhancerProtocol.DialogIndex ||
                !gearMentorOperationPageSubId.HasValue) &&
               context is not null &&
               context.IsActiveFor(
                   accountId,
                   characterId,
                   npcId,
                   dialogIndex,
                   operation,
                   now);
    }

    internal static bool GearMentorCommitContextMatches(
        GearEnhancerSelectionContext? context,
        int? operationPageSubId,
        int accountId,
        int characterId,
        uint npcId,
        int actionSubId,
        DateTimeOffset now)
    {
        var routeMatches = actionSubId switch
        {
            GearEnhancerProtocol.DecomposeGearSubId or
                GearEnhancerProtocol.MakeAttributeStoneSubId or
                GearEnhancerProtocol.TransformCrystalSubId => !operationPageSubId.HasValue,
            GearEnhancerProtocol.CombineGemPiecesActionSubId =>
                operationPageSubId == GearEnhancerProtocol.CombineGemPiecesActionSubId,
            _ => false
        };

        return routeMatches &&
               context is not null &&
               context.NpcId == npcId &&
               context.DialogIndex == GearEnhancerProtocol.DialogIndex &&
               context.IsActiveForSelection(accountId, characterId, now);
    }

    internal static bool IsCombineGemPiecesConfirmAlias(
        int incomingSubId,
        int? operationPageSubId,
        bool hasClientOperationId = false) =>
        incomingSubId == GearEnhancerProtocol.CombineGemPiecesMenuSubId &&
        (operationPageSubId ==
            GearEnhancerProtocol.CombineGemPiecesActionSubId ||
         hasClientOperationId);

    private static GearEnhancerSelectionSnapshot CaptureGearEnhancerSelection(
        string kitBag,
        int kitBagSlot)
    {
        return new GearEnhancerSelectionSnapshot(
            kitBagSlot,
            KitBagSlots.GetItem(kitBag, kitBagSlot));
    }

    private static GearEnhancementSlotSelection ToGearEnhancementSelection(
        GearEnhancerSelectionSnapshot selection)
    {
        return new GearEnhancementSlotSelection(
            selection.KitBagSlot,
            selection.ExpectedItem);
    }

    private static GearMentorSlotSelection ToGearMentorSelection(
        GearEnhancerSelectionSnapshot selection)
    {
        return new GearMentorSlotSelection(
            selection.KitBagSlot,
            selection.ExpectedItem);
    }

    private static string DescribeGearEnhancerSelection(string kitBag, int kitBagSlot)
    {
        if (kitBagSlot < 0)
        {
            return "missing";
        }

        var item = KitBagSlots.GetItem(kitBag, kitBagSlot);
        return $"slot:{kitBagSlot}/item:{item.Id}/stack:{item.Stack}";
    }

    private bool TryResolveMapNpc(uint interactionId, out NpcSpawnDefinition npc)
    {
        if (_character is not null &&
            _mapNpcsByInteractionId.TryGetValue(interactionId, out var candidate) &&
            candidate.MapId == _character.CurrentMap &&
            _npcVisibility is not null &&
            _npcVisibility.IsVisible(candidate.ObjectId) &&
            _registry.IsCanonicalMapNpc(
                _character.CurrentMap,
                _npcCatalogRevision,
                candidate))
        {
            npc = candidate;
            return true;
        }

        npc = default!;
        return false;
    }

    private void ClearGearEnhancerSelection()
    {
        _gearEnhancerSelectionContext = null;
        _gearMentorOperationPageSubId = null;
    }

    private async Task RefreshNearbyWorldObjectsAsync(
        string reason,
        CancellationToken cancellationToken,
        bool forceMonsterRefresh = false)
    {
        if (_character is null ||
            _npcVisibility is null ||
            !_npcVisibility.TryCalculate(
                _character.PositionX,
                _character.PositionZ,
                out var npcDelta))
        {
            return;
        }


        await using var monsterTransition = await _registry.BeginMonsterVisibilityTransitionAsync(
            _session,
            _character.CurrentMap,
            _character.PositionX,
            _character.PositionZ,
            cancellationToken,
            forceMonsterRefresh);
        if (monsterTransition is null)
        {
            return;
        }

        var monsterDelta = monsterTransition.Delta;

        var leavingObjectIds = npcDelta.Leaving
            .Concat(monsterDelta.Leaving)
            .Distinct()
            .OrderBy(objectId => objectId)
            .ToArray();
        if (leavingObjectIds.Length > 0)
        {
            await _session.SendAsync(
                PacketBuilder.RemoveWorldObjects(leavingObjectIds),
                cancellationToken,
                "NearbyWorldObjectRemovals");
        }

        if (npcDelta.Entering.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.NpcSpawns(npcDelta.Entering),
                cancellationToken,
                "NearbyNpcSpawns",
                framed: false);
        }

        if (monsterDelta.Entering.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.CapturedMonsterSpawns(
                    monsterDelta.Entering.Select(monster => monster.Appearance).ToArray()),
                cancellationToken,
                "NearbyMonsterSpawns",
                framed: false);

            foreach (var monster in monsterDelta.Entering.Where(monster => monster.IsMoving))
            {
                await _session.SendAsync(
                    PacketBuilder.MonsterMovementStart(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        monster.VelocityX,
                        monster.VelocityY,
                        monster.VelocityZ),
                    cancellationToken,
                    "NearbyMonsterMovementContinuation");
            }
        }

        // Only advance either tracker after the complete remove/spawn transition
        // has been sent, so a failed transition is never recorded as visible.
        _npcVisibility.Commit(npcDelta);
        monsterTransition.Commit();
        if (npcDelta.Entering.Count > 0 ||
            npcDelta.Leaving.Count > 0 ||
            monsterDelta.Entering.Count > 0 ||
            monsterDelta.Leaving.Count > 0 ||
            reason == "initial")
        {
            Console.WriteLine(
                $"[world] visibility reason={reason} character={_character.Name} map={_character.CurrentMap} cell={npcDelta.PlayerCell.X},{npcDelta.PlayerCell.Z} x={_character.PositionX:F2} z={_character.PositionZ:F2} npc-entered={npcDelta.Entering.Count} npc-left={npcDelta.Leaving.Count} mob-entered={monsterDelta.Entering.Count} mob-left={monsterDelta.Leaving.Count}");
        }
    }

}
