using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task AssertCrossFamilyStreamAsync(
        string connectionString)
    {
        var transformFixture = await CreateFixtureAsync(
            connectionString,
            "mixed",
            CommandFamily.GearMentorTransformCrystal,
            sourceItemId: 4234,
            sourceStack: 1,
            outputItemId: 4233,
            outputQuantity: 2,
            isBound: true,
            additionalItemId: 4216,
            additionalItemStack: 99,
            additionalItemSlot: 1,
            additionalItemBound: 1);
        var combineExpected = CompactItemEntry.Parse(
            "[4216,,,,,,1,1,1,99,0]").ToCompactString();
        var combineFixture = new ConversionFixture(
            transformFixture.AccountId,
            transformFixture.CharacterId,
            transformFixture.Username,
            CommandFamily.GearMentorCombineGemPieces,
            SelectedSlot: 1,
            combineExpected,
            SourceItemId: 4216,
            InitialSourceStack: 99,
            OutputItemId: 4215,
            OutputQuantity: 1,
            IsBound: true);
        var sharedClientOperationId = Guid.NewGuid();

        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            var executor = CreateExecutor(source);
            var transform = RequireReceipt(
                await ExecuteAsync(
                    executor,
                    transformFixture,
                    sharedClientOperationId),
                GearMentorMaterialConversionExecutionDisposition
                    .Committed,
                "cross-family Transform");
            var combine = RequireReceipt(
                await ExecuteAsync(
                    executor,
                    combineFixture,
                    sharedClientOperationId),
                GearMentorMaterialConversionExecutionDisposition
                    .Committed,
                "cross-family Combine");
            Check.True(
                transform.InventoryRevision == 1 &&
                combine.InventoryRevision == 2,
                "different families sharing a client UUID advance one " +
                "strict inventory stream");
        }

        var transformState = await ReadStateAsync(
            connectionString,
            transformFixture);
        var combineState = await ReadStateAsync(
            connectionString,
            combineFixture);
        Check.True(
            transformState.InventoryRevision == 2 &&
            combineState.InventoryRevision == 2 &&
            transformState.AuditCount == 1 &&
            transformState.InboxCount == 1 &&
            transformState.LedgerCount == 1 &&
            transformState.OutboxCount == 1 &&
            combineState.AuditCount == 1 &&
            combineState.InboxCount == 1 &&
            combineState.LedgerCount == 1 &&
            combineState.OutboxCount == 1 &&
            transformState.IsReconciled &&
            combineState.IsReconciled,
            "cross-family commands keep separate evidence and one value " +
            "revision");

        var events = await ReadConversionEventsAsync(
            connectionString,
            transformFixture.CharacterId);
        Check.True(
            events.Count == 2 &&
            events[0].AggregateRevision == 1 &&
            events[1].AggregateRevision == 2 &&
            events[0].EventType ==
                GearMentorMaterialConversionPersistenceCodec
                    .TransformEventType &&
            events[1].EventType ==
                GearMentorMaterialConversionPersistenceCodec
                    .CombineEventType,
            "cross-family outbox revisions are contiguous and typed");
        var consumer = new CharacterInventoryOutboxConsumer();
        var currentRevision = 0L;
        foreach (var message in events)
        {
            Check.True(
                OutboxOrderingRules.Decide(
                    consumer.OrderingPolicy,
                    currentRevision,
                    message) == OutboxOrderingDecision.Deliver,
                $"mixed conversion revision {message.AggregateRevision} " +
                "is deliverable");
            await consumer.ConsumeAsync(message);
            currentRevision = message.AggregateRevision;
        }
    }

    private static async Task<IReadOnlyList<OutboxEventMessage>>
        ReadConversionEventsAsync(
            string connectionString,
            int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                event_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                created_at,
                payload::text
            FROM public.outbox_events
            WHERE aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey
              AND event_type IN (@transformEvent, @combineEvent)
            ORDER BY aggregate_version;
            """,
            connection);
        command.Parameters.AddWithValue(
            "aggregateType",
            GearMentorMaterialConversionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            GearMentorMaterialConversionPersistenceCodec.AggregateKey(
                characterId));
        command.Parameters.AddWithValue(
            "transformEvent",
            GearMentorMaterialConversionPersistenceCodec
                .TransformEventType);
        command.Parameters.AddWithValue(
            "combineEvent",
            GearMentorMaterialConversionPersistenceCodec
                .CombineEventType);
        var messages = new List<OutboxEventMessage>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            messages.Add(
                new OutboxEventMessage(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetInt16(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    Encoding.UTF8.GetBytes(reader.GetString(8))));
        }

        return messages;
    }
}
