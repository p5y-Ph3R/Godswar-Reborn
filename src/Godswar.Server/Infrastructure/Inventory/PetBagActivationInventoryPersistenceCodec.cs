using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed record PetBagActivationInventoryReceipt(
    int CharacterId,
    long InventoryRevision,
    int LedgerEntryCount,
    Guid OutboxEventId);

/// <summary>
/// Projection contract for the inventory half of the atomic pet bag
/// activation transaction. The command receipt remains on the pet stream;
/// this second event keeps the shared inventory stream contiguous.
/// </summary>
internal static class PetBagActivationInventoryPersistenceCodec
{
    public const short ContractVersion = 1;
    public const string ConsumerKey = "inventory_projection_v1";
    public const string AggregateType = "character_inventory";
    public const string EventType = "inventory.pet_bag_item_activated";
    public const string OrderingPolicy = "strict";

    public static string AggregateKey(int characterId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"character:{characterId}:inventory");

    public static byte[] Encode(
        PetBagActivationInventoryReceipt receipt)
    {
        Validate(receipt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new PersistedReceipt(
                ContractVersion,
                receipt.CharacterId,
                receipt.InventoryRevision,
                receipt.LedgerEntryCount,
                receipt.OutboxEventId));
        return payload.Length <= OutboxEventMessage.MaximumPayloadBytes
            ? payload
            : throw new InvalidDataException(
                "The pet bag inventory event exceeds its payload bound.");
    }

    public static PetBagActivationInventoryReceipt Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or >
            OutboxEventMessage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The pet bag inventory event has an invalid size.");
        }

        var stored = JsonSerializer.Deserialize<PersistedReceipt>(
            payload) ?? throw new InvalidDataException(
                "The pet bag inventory event is malformed.");
        if (stored.ContractVersion != ContractVersion)
        {
            throw new InvalidDataException(
                "The pet bag inventory event version is unsupported.");
        }

        var receipt = new PetBagActivationInventoryReceipt(
            stored.CharacterId,
            stored.InventoryRevision,
            stored.LedgerEntryCount,
            stored.OutboxEventId);
        Validate(receipt);
        return receipt;
    }

    private static void Validate(
        PetBagActivationInventoryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.CharacterId <= 0 ||
            receipt.InventoryRevision <= 0 ||
            receipt.LedgerEntryCount is < 1 or > 2 ||
            receipt.OutboxEventId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The pet bag inventory event identity is invalid.");
        }
    }

    private sealed record PersistedReceipt(
        short ContractVersion,
        int CharacterId,
        long InventoryRevision,
        int LedgerEntryCount,
        Guid OutboxEventId);
}
