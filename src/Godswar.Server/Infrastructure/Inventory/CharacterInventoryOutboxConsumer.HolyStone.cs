using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class CharacterInventoryOutboxConsumer
{
    private static void ValidateHolyStone(
        OutboxEventMessage message)
    {
        var receipt = HolyStonePersistenceCodec.Decode(
            message.Payload.Span);
        var walletEvidenceValid =
            receipt.Status == HolyStoneCommandResultStatus.Drilled &&
            receipt.Operation is (
                HolyStoneCommandOperation.Drill or
                HolyStoneCommandOperation.MountGearDrill)
                ? receipt.GoldSpent > 0 &&
                  receipt.GoldAfter ==
                      receipt.GoldBefore - receipt.GoldSpent &&
                  receipt.WalletRevision > 0
                : receipt.GoldSpent == 0 &&
                  receipt.GoldAfter == receipt.GoldBefore &&
                  (receipt.Status != HolyStoneCommandResultStatus.Drilled ||
                   receipt.Operation ==
                       HolyStoneCommandOperation.AdvancedDrill);
        if (!HolyStoneNativeResults.IsSuccess(receipt.Status) ||
            !walletEvidenceValid ||
            receipt.OutboxEventId != message.EventId ||
            receipt.InventoryRevision != message.AggregateRevision ||
            !string.Equals(
                HolyStonePersistenceCodec.AggregateKey(
                    receipt.CharacterId),
                message.AggregateKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Holy Stone outbox identity is inconsistent.");
        }
    }
}
