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
    private void HandleGearEnhancerItemSelection(GamePacket packet)
    {
        if (_account is null ||
            _character is null ||
            !GearEnhancerItemSelectionPacket.TryParse(packet.Payload, out var selection))
        {
            Console.WriteLine(
                $"[gear-enhancer] ignored malformed/inactive item selection len={packet.Payload.Length}");
            return;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow))
        {
            ClearGearEnhancerSelection();
            Console.WriteLine(
                $"[gear-enhancer] ignored item selection without active operation character={_character.Name} bagSlot={selection.KitBagSlot} selected={selection.Selected}");
            return;
        }

        var staged = context.Apply(selection, _character.KitBag);
        Console.WriteLine(
            $"[gear-enhancer] item selection character={_character.Name} npc={context.NpcId} dialog={context.DialogIndex} operation={context.Operation?.ToString() ?? "pending-final-action"} selected={selection.Selected} bagSlot={staged.KitBagSlot} item={staged.Item.Id} stack={staged.Item.Stack} role={staged.Role?.ToString() ?? "none"} status={staged.Status}");
    }

    private async Task HandleGearEnhancerOperationAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> args,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var operation = (GearEnhancementOperation)subId;
        var now = DateTimeOffset.UtcNow;
        var stagedContext = _gearEnhancerSelectionContext;
        var contextIsActive = GearEnhancerCommitContextMatches(
            stagedContext,
            _gearMentorOperationPageSubId,
            _account.Id,
            _character.Id,
            npcId,
            dialogIndex,
            operation,
            now);
        GearEnhancerSelectionTriplet? nativeSelections = null;
        var selectionShape = GearEnhancerProtocol.ReadSelection(
            args,
            out var gearKitBagSlot,
            out var catalystKitBagSlot,
            out var attributeStoneKitBagSlot);
        if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
            selectionShape == GearEnhancerSelectionShape.Commit)
        {
            // Physical NpcFunBreak sends authoritative choices only through
            // opcode 10193. Its scratch tail can accidentally resemble the
            // Origin Enhancer's inline triplet and must never override the
            // staged role order that the secure UUID actually identifies.
            gearKitBagSlot = -1;
            catalystKitBagSlot = -1;
            attributeStoneKitBagSlot = -1;
            selectionShape = GearEnhancerSelectionShape.MalformedCommit;
        }

        if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
            selectionShape is GearEnhancerSelectionShape.MenuSelection or
                GearEnhancerSelectionShape.MalformedCommit)
        {
            if (contextIsActive &&
                stagedContext!.TryResolveNativeCommit(
                    selectionShape,
                    out var stagedSelections))
            {
                nativeSelections = stagedSelections;
                gearKitBagSlot = stagedSelections.GearKitBagSlot;
                catalystKitBagSlot = stagedSelections.CatalystKitBagSlot;
                attributeStoneKitBagSlot = stagedSelections.AttributeStoneKitBagSlot;
                selectionShape = GearEnhancerSelectionShape.Commit;
            }
        }

        if (selectionShape == GearEnhancerSelectionShape.MenuSelection)
        {
            ClearGearEnhancerSelection();
            _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                _account.Id,
                _character.Id,
                npcId,
                dialogIndex,
                operation,
                DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
            var workflow = dialogIndex == GearEnhancerProtocol.DialogIndex
                ? "gear-mentor"
                : "origin-enhancer";
            await _session.SendAsync(
                GearEnhancerProtocol.BuildOperationPageResponse(npcId, dialogIndex, subId),
                cancellationToken,
                "NpcFunctionActionResponse");
            Console.WriteLine(
                $"[{workflow}] operation page character={_character.Name} npc={npcId} dialog={dialogIndex} operation={operation}");
            return;
        }

        if (_session.IsSecure &&
            !clientOperationId.HasValue)
        {
            ClearGearEnhancerSelection();
            var family =
                ResolveSecureGearMentorCommandFamily(subId) ??
                throw new InvalidDataException(
                    $"Gear Enhancement operation {subId} has no command family.");
            CommandMetrics.RecordUnsupportedLegacyIdentity(family);
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.InvalidIntent);
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    GearEnhancerProtocol.InvalidSelectionResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
            Console.WriteLine(
                "[gear-enhancer] rejected secure commit without " +
                $"operation UUID family={family}");
            return;
        }

        // Consume the staged workflow before awaiting persistence. A replayed
        // confirmation cannot reuse the same three native selections.
        ClearGearEnhancerSelection();

        var responseSubId = GearEnhancerProtocol.InvalidSelectionResultSubId;
        GearEnhancementRequest? request = null;
        GearEnhancementTransactionResult? transaction = null;
        var selectionSummary =
            $"gear={DescribeGearEnhancerSelection(_character.KitBag, gearKitBagSlot)} " +
            $"catalyst={DescribeGearEnhancerSelection(_character.KitBag, catalystKitBagSlot)} " +
            $"stone={DescribeGearEnhancerSelection(_character.KitBag, attributeStoneKitBagSlot)}";

        if (clientOperationId.HasValue)
        {
            GearEnhancerSelectionTriplet? durableSelections = null;
            if (selectionShape == GearEnhancerSelectionShape.Commit &&
                contextIsActive &&
                gearKitBagSlot >= 0 &&
                catalystKitBagSlot >= 0 &&
                attributeStoneKitBagSlot >= 0)
            {
                durableSelections =
                    nativeSelections ?? new GearEnhancerSelectionTriplet(
                        CaptureGearEnhancerSelection(
                            _character.KitBag,
                            gearKitBagSlot),
                        CaptureGearEnhancerSelection(
                            _character.KitBag,
                            catalystKitBagSlot),
                        CaptureGearEnhancerSelection(
                            _character.KitBag,
                            attributeStoneKitBagSlot));
            }

            await HandleDurableGearEnhancementAsync(
                npcId,
                dialogIndex,
                operation,
                clientOperationId.Value,
                durableSelections,
                _character.KitBag,
                selectionSummary,
                cancellationToken);
            return;
        }

        if (selectionShape == GearEnhancerSelectionShape.Commit && contextIsActive)
        {
            if (gearKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingGearResultSubId;
            }
            else if (catalystKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingCatalystResultSubId(operation);
            }
            else if (attributeStoneKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingAttributeStoneResultSubId;
            }
            else
            {
                var selections = nativeSelections ?? new GearEnhancerSelectionTriplet(
                    CaptureGearEnhancerSelection(_character.KitBag, gearKitBagSlot),
                    CaptureGearEnhancerSelection(_character.KitBag, catalystKitBagSlot),
                    CaptureGearEnhancerSelection(_character.KitBag, attributeStoneKitBagSlot));
                request = new GearEnhancementRequest(
                    operation,
                    ToGearEnhancementSelection(selections.Gear),
                    ToGearEnhancementSelection(selections.AttributeStone),
                    ToGearEnhancementSelection(selections.Catalyst));

                try
                {
                    transaction = await _store.EnhanceGearAsync(
                        _account.Id,
                        _character.Id,
                        request,
                        cancellationToken);
                    responseSubId = GearEnhancerProtocol.ResolveResultSubId(
                        operation,
                        transaction.Enhancement,
                        request);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[gear-enhancer] persistence failure account={_account.Id} character={_character.Name} operation={operation}: {ex.Message}");
                }
            }
        }

        if (transaction?.Character is not null)
        {
            InstallUpdatedCharacter(transaction.Character);
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        }

        var authoritativeBagChanged = transaction?.Committed == true;
        var staleSelection = transaction?.Enhancement?.Status ==
            GearEnhancementStatus.StaleSelection;
        if (authoritativeBagChanged || staleSelection)
        {
            ClearForgeSelection();
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (authoritativeBagChanged || staleSelection)
        {
            // The native result must release the dialog's pending state before
            // authoritative inventory packets replace its staged client view.
            await SendKitBagRefreshAsync(cancellationToken);
        }

        var resultWorkflow = dialogIndex == GearEnhancerProtocol.DialogIndex
            ? "gear-mentor"
            : "origin-enhancer";
        Console.WriteLine(
            $"[{resultWorkflow}] result account={_account.Id} character={_character.Name} npc={npcId} dialog={dialogIndex} operation={operation} status={transaction?.Enhancement?.Status.ToString() ?? selectionShape.ToString()} response={responseSubId} committed={transaction?.Committed == true} selections=({selectionSummary}) reason=\"{transaction?.Enhancement?.RejectionReason ?? "none"}\"");
    }

}
