using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterInventoryOutboxConsumerChecks
{
    private const int CharacterId = 73;

    public static async Task RunAsync()
    {
        var consumer = new CharacterInventoryOutboxConsumer();
        Check.True(
            consumer.ConsumerKey ==
                DeveloperItemGrantPersistenceCodec.ConsumerKey &&
            consumer.ConsumerKey ==
                DeveloperBagClearPersistenceCodec.ConsumerKey &&
            consumer.ConsumerKey ==
                MakeAttributeStonePersistenceCodec.ConsumerKey,
            "all character-inventory event types share one checkpoint");
        Check.True(
            consumer.ConsumerKey ==
                GearMentorMaterialConversionPersistenceCodec.ConsumerKey,
            "material conversions share the inventory checkpoint");
        Check.True(
            consumer.OrderingPolicy ==
                OutboxOrderingPolicy.StrictSequence,
            "character-inventory projection uses strict ordering");
        Check.True(
            DeveloperItemGrantPersistenceCodec.AggregateType ==
                DeveloperBagClearPersistenceCodec.AggregateType &&
            DeveloperItemGrantPersistenceCodec.AggregateType ==
                MakeAttributeStonePersistenceCodec.AggregateType &&
            DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId) ==
                DeveloperBagClearPersistenceCodec.AggregateKey(CharacterId) &&
            DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId) ==
                MakeAttributeStonePersistenceCodec.AggregateKey(CharacterId),
            "all character-inventory event types share one aggregate stream");
        Check.True(
            DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId) ==
                GearMentorMaterialConversionPersistenceCodec.AggregateKey(
                    CharacterId),
            "material conversions share the inventory aggregate stream");

        var messages = CreateCompatibleSequence();
        var currentRevision = 0L;
        foreach (var message in messages)
        {
            Check.True(
                OutboxOrderingRules.Decide(
                    consumer.OrderingPolicy,
                    currentRevision,
                    message) == OutboxOrderingDecision.Deliver,
                $"inventory revision {message.AggregateRevision} is contiguous");
            await consumer.ConsumeAsync(message);
            currentRevision = message.AggregateRevision;
        }

        Check.True(
            OutboxOrderingRules.Decide(
                consumer.OrderingPolicy,
                currentRevision,
                messages[1]) == OutboxOrderingDecision.Stale,
            "a repeated generic grant is stale after bag clear");
        Check.True(
            OutboxOrderingRules.Decide(
                consumer.OrderingPolicy,
                currentRevision,
                CreateGrantMessage(
                    revision: currentRevision + 2,
                    DeveloperItemGrantPersistenceCodec.EventType)) ==
                OutboxOrderingDecision.Gap,
            "a missing mixed inventory revision remains a strict gap");

        await CheckIdentityRejectionAsync(consumer, messages[2]);
        await CheckStoneIdentityRejectionAsync(consumer, messages[3]);
        await CheckStoneContractRejectionAsync(consumer, messages[3]);
        await CheckMaterialConversionIdentityRejectionAsync(
            consumer,
            messages[4]);
        await CheckMaterialConversionFamilyRejectionAsync(
            consumer,
            messages[5]);
        await CheckContractRejectionAsync(consumer, messages[0]);
    }

    private static OutboxEventMessage[] CreateCompatibleSequence() =>
    [
        CreateGrantMessage(
            revision: 1,
            DeveloperItemGrantPersistenceCodec.LegacyMaterialEventType),
        CreateGrantMessage(
            revision: 2,
            DeveloperItemGrantPersistenceCodec.EventType),
        CreateBagClearMessage(revision: 3),
        CreateMakeAttributeStoneMessage(revision: 4),
        CreateMaterialConversionMessage(
            revision: 5,
            CommandFamily.GearMentorTransformCrystal),
        CreateMaterialConversionMessage(
            revision: 6,
            CommandFamily.GearMentorCombineGemPieces)
    ];

    private static OutboxEventMessage CreateGrantMessage(
        long revision,
        string eventType)
    {
        var eventId = Guid.NewGuid();
        var receipt = new DeveloperItemGrantExecutionReceipt(
            CharacterId,
            itemId: 4230,
            grantedQuantity: 1,
            inventoryRevision: revision,
            auditReference: $"inventory-check-{revision}",
            outboxEventId: eventId);
        return CreateMessage(
            eventId,
            revision,
            eventType,
            DeveloperItemGrantPersistenceCodec.ContractVersion,
            DeveloperItemGrantPersistenceCodec.Encode(receipt));
    }

    private static OutboxEventMessage CreateBagClearMessage(long revision)
    {
        var eventId = Guid.NewGuid();
        var receipt = new DeveloperBagClearExecutionReceipt(
            CharacterId,
            removedSlots: [0, 2],
            inventoryRevision: revision,
            auditReference: $"inventory-check-{revision}",
            outboxEventId: eventId);
        return CreateMessage(
            eventId,
            revision,
            DeveloperBagClearPersistenceCodec.EventType,
            DeveloperBagClearPersistenceCodec.ContractVersion,
            DeveloperBagClearPersistenceCodec.Encode(receipt));
    }

    private static OutboxEventMessage CreateMakeAttributeStoneMessage(
        long revision)
    {
        var eventId = Guid.NewGuid();
        var receipt = new MakeAttributeStoneExecutionReceipt(
            CharacterId,
            MakeAttributeStoneResultStatus.Succeeded,
            MakeAttributeStoneNativeResults.SucceededSubId,
            selectedKitBagSlot: 7,
            sourceDustItemId: 9900,
            outputStoneItemId: 9930,
            isBound: true,
            inventoryRevision: revision,
            auditReference: $"inventory-check-{revision}",
            outboxEventId: eventId);
        return CreateMessage(
            eventId,
            revision,
            MakeAttributeStonePersistenceCodec.EventType,
            MakeAttributeStonePersistenceCodec.ContractVersion,
            MakeAttributeStonePersistenceCodec.Encode(receipt));
    }

    private static OutboxEventMessage CreateMaterialConversionMessage(
        long revision,
        CommandFamily family)
    {
        var eventId = Guid.NewGuid();
        var isTransform =
            family == CommandFamily.GearMentorTransformCrystal;
        var receipt =
            new GearMentorMaterialConversionExecutionReceipt(
                family,
                CharacterId,
                GearMentorMaterialConversionResultStatus.Succeeded,
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    family,
                    GearMentorMaterialConversionResultStatus.Succeeded),
                selectedKitBagSlot: 8,
                sourceItemId: isTransform ? 4234u : 4216u,
                outputItemId: isTransform ? 4233u : 4215u,
                outputQuantity: isTransform ? 2 : 1,
                isBound: true,
                inventoryRevision: revision,
                auditReference: $"inventory-check-{revision}",
                outboxEventId: eventId);
        return CreateMessage(
            eventId,
            revision,
            GearMentorMaterialConversionPersistenceCodec.EventType(
                family),
            GearMentorMaterialConversionPersistenceCodec.ContractVersion,
            GearMentorMaterialConversionPersistenceCodec.Encode(receipt));
    }

    private static OutboxEventMessage CreateMessage(
        Guid eventId,
        long revision,
        string eventType,
        int schemaVersion,
        ReadOnlyMemory<byte> payload,
        string? aggregateKey = null) =>
        new(
            eventId,
            DeveloperItemGrantPersistenceCodec.ConsumerKey,
            DeveloperItemGrantPersistenceCodec.AggregateType,
            aggregateKey ??
                DeveloperItemGrantPersistenceCodec.AggregateKey(CharacterId),
            revision,
            eventType,
            schemaVersion,
            DateTimeOffset.UtcNow,
            payload);

    private static async Task CheckIdentityRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage bagClear)
    {
        var inconsistent = CreateMessage(
            bagClear.EventId,
            bagClear.AggregateRevision,
            bagClear.EventType,
            bagClear.SchemaVersion,
            bagClear.Payload,
            aggregateKey: "character:999:inventory");
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "bag-clear payload identity mismatch is rejected");
    }

    private static async Task CheckContractRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage legacyGrant)
    {
        var unsupported = CreateMessage(
            legacyGrant.EventId,
            legacyGrant.AggregateRevision,
            eventType: "inventory.unsupported",
            legacyGrant.SchemaVersion,
            legacyGrant.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(unsupported).AsTask(),
            "unknown inventory event type is rejected");
    }

    private static async Task CheckStoneIdentityRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage makeAttributeStone)
    {
        var inconsistent = CreateMessage(
            Guid.NewGuid(),
            makeAttributeStone.AggregateRevision,
            makeAttributeStone.EventType,
            makeAttributeStone.SchemaVersion,
            makeAttributeStone.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "Make Attribute Stone event identity mismatch is rejected");
    }

    private static async Task CheckStoneContractRejectionAsync(
        CharacterInventoryOutboxConsumer consumer,
        OutboxEventMessage makeAttributeStone)
    {
        var unsupported = CreateMessage(
            makeAttributeStone.EventId,
            makeAttributeStone.AggregateRevision,
            makeAttributeStone.EventType,
            MakeAttributeStonePersistenceCodec.ContractVersion + 1,
            makeAttributeStone.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(unsupported).AsTask(),
            "unsupported Make Attribute Stone schema is rejected");
    }

    private static async Task
        CheckMaterialConversionIdentityRejectionAsync(
            CharacterInventoryOutboxConsumer consumer,
            OutboxEventMessage conversion)
    {
        var inconsistent = CreateMessage(
            Guid.NewGuid(),
            conversion.AggregateRevision,
            conversion.EventType,
            conversion.SchemaVersion,
            conversion.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "material-conversion event identity mismatch is rejected");
    }

    private static async Task
        CheckMaterialConversionFamilyRejectionAsync(
            CharacterInventoryOutboxConsumer consumer,
            OutboxEventMessage combine)
    {
        var inconsistent = CreateMessage(
            combine.EventId,
            combine.AggregateRevision,
            GearMentorMaterialConversionPersistenceCodec
                .TransformEventType,
            combine.SchemaVersion,
            combine.Payload);
        await CheckThrowsAsync<InvalidDataException>(
            () => consumer.ConsumeAsync(inconsistent).AsTask(),
            "material-conversion event/family mismatch is rejected");
    }

    private static async Task CheckThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }
}
