using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertRawLocalUpgradeAtomicPathAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "upraw",
            target: SimpleItem(9030, grade: 4),
            stone: SimpleItem(9041, stack: 2));
        var serverOperationId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var identity = HolyStoneOperationIdentity.RawLocalServer(
            serverOperationId,
            connectionId);
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                identity,
                HolyStoneCommandOperation.Upgrade,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                fixture.TargetSlot,
                fixture.TargetState,
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                fixture.StoneSlot,
                fixture.StoneState,
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                "[]",
                out var command),
            "raw-local Upgrade creates a bounded canonical command");

        var correlation = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var envelope = PlayerOwnershipTestFences.Bind(
            HolyStoneCommandEnvelope.CreateRawLocal(
                fixture.Subject,
                correlation,
                DateTimeOffset.UtcNow,
                command));
        Check.True(
            envelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            envelope.Connection.Transport == CommandTransportKind.LegacyTcp &&
            envelope.Command.Identity.RawLocalConnectionId == connectionId &&
            HolyStoneCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "PostgreSQL receives a valid connection-scoped raw Upgrade envelope");

        var sameServerIdOnAnotherConnection =
            HolyStoneOperationIdentity.RawLocalServer(
                serverOperationId,
                Guid.NewGuid());
        Check.True(
            !string.Equals(
                envelope.OperationId,
                HolyStoneCommandEnvelope.CreateOperationId(
                    fixture.Subject,
                    HolyStoneCommandOperation.Upgrade,
                    sameServerIdOnAnotherConnection),
                StringComparison.Ordinal),
            "raw-local durable identity includes the legacy connection scope");

        var random = new FixedUpgradeRandomSource(0);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(
            dataSource,
            upgradeRandomSource: random);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Upgraded,
            "raw-local Upgrade");
        var duplicate = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Upgraded,
            "raw-local Upgrade duplicate");
        Check.Equal(
            committed,
            duplicate,
            "raw-local duplicate returns the atomically stored receipt");
        Check.Equal(
            1,
            random.CallCount,
            "raw-local duplicate neither resamples nor consumes twice");

        var target = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        var eclipse = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value.Item;
        Check.True(
            target.Grade == 5 && eclipse.Stack == 1,
            "raw-local Upgrade atomically raises the stone and consumes one Eclipse");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Upgrade);
        AssertCommittedEvidence(
            state,
            expectedLedger: 2,
            "raw-local Upgrade");
        Check.True(
            state.InventoryRevision == 1 &&
            state.DuplicateCount == 1 &&
            state.ConflictCount == 0,
            "raw-local duplicate retains one inventory transition and one inbox replay");
        Check.True(
            await HasRawOperationEvidenceAsync(
                connectionString,
                fixture,
                envelope.OperationId),
            "raw-local operation identity binds the inbox and audit evidence");

        var audit = await ReadUpgradeAuditAsync(connectionString, fixture);
        Check.True(
            audit.Roll == 0 &&
            audit.Rate == 25 &&
            audit.CatalystSlot ==
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            "raw-local Upgrade uses the shared rate policy and audit payload");
    }

    private static async Task<bool> HasRawOperationEvidenceAsync(
        string connectionString,
        HolyFixture fixture,
        string operationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM public.command_inbox
                    WHERE principal_key = @principalKey
                      AND aggregate_key = @aggregateKey
                      AND command_family = @commandFamily
                      AND operation_id = @operationId),
                EXISTS (
                    SELECT 1
                    FROM public.command_audit
                    WHERE principal_key = @principalKey
                      AND aggregate_key = @aggregateKey
                      AND command_family = @commandFamily
                      AND operation_id = @operationId);
            """,
            connection);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            HolyStonePersistenceCodec.AggregateKey(fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            HolyStonePersistenceCodec.CommandFamilyCode(
                HolyStoneCommandOperation.Upgrade));
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = Convert.FromHexString(operationId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() &&
               reader.GetBoolean(0) &&
               reader.GetBoolean(1);
    }
}
