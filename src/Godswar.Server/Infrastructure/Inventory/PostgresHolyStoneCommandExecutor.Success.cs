using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task<HolyStoneExecutionResult> PersistSuccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        LockedCharacter character,
        LockedCommandItems locked,
        HolyStonePlan plan,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var target = locked.Target ??
            throw new InvalidDataException(
                "A successful Holy Stone operation has no target.");
        long? outputItemInstanceId = null;
        if (plan.Status == HolyStoneCommandResultStatus.Removed)
        {
            outputItemInstanceId = await ReserveItemInstanceIdAsync(
                connection,
                transaction,
                cancellationToken);
        }

        var nextRevision = checked(character.InventoryRevision + 1);
        var chargesGold = plan.GoldSpent > 0;
        var nextWalletRevision = chargesGold
            ? checked(character.WalletRevision + 1)
            : character.WalletRevision;
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            character,
            locked,
            plan,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.AuditInserted,
            -1,
            cancellationToken);
        var receipt = CreateReceipt(
            context,
            locked,
            plan,
            character.Gold,
            nextWalletRevision,
            nextRevision,
            auditId,
            eventId,
            outputItemInstanceId);
        var payload = HolyStonePersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            context.Command.Operation,
            plan.Status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.InboxInserted,
            -1,
            cancellationToken);

        var mutations = new List<InventoryMutation>(4)
        {
            await UpdateTargetAsync(
                connection,
                transaction,
                context,
                target,
                plan.TargetAfter,
                cancellationToken)
        };
        await ReachAsync(
            PostgresHolyStoneCommandStage.TargetMutated,
            0,
            cancellationToken);
        if (chargesGold)
        {
            await UpdateGoldWalletAsync(
                connection,
                transaction,
                context,
                character,
                checked(character.Gold - plan.GoldSpent),
                nextWalletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresHolyStoneCommandStage.WalletUpdated,
                -1,
                cancellationToken);
        }
        if (plan.Status == HolyStoneCommandResultStatus.Mounted ||
            context.Command.Operation ==
                HolyStoneCommandOperation.AdvancedDrill ||
            context.Command.Operation == HolyStoneCommandOperation.Upgrade ||
            context.Command.Operation == HolyStoneCommandOperation.Combine ||
            context.Command.Operation ==
                HolyStoneCommandOperation.ImplementSpirit)
        {
            var stone = locked.Stone ??
                throw new InvalidDataException(
                    "A successful material-consuming Holy Stone " +
                    "operation has no source material.");
            mutations.Add(await ConsumeStoneAsync(
                connection,
                transaction,
                context,
                stone,
                plan.StoneAfter,
                cancellationToken));
            await ReachAsync(
                PostgresHolyStoneCommandStage.StoneMutated,
                1,
                cancellationToken);
            if ((context.Command.Operation is
                    HolyStoneCommandOperation.Upgrade or
                    HolyStoneCommandOperation.Combine or
                    HolyStoneCommandOperation.ImplementSpirit) &&
                locked.Catalyst is not null)
            {
                mutations.Add(await ConsumeStoneAsync(
                    connection,
                    transaction,
                    context,
                    locked.Catalyst,
                    plan.CatalystAfter,
                    cancellationToken));
                await ReachAsync(
                    PostgresHolyStoneCommandStage.StoneMutated,
                    2,
                    cancellationToken);
            }
            if (context.Command.Operation ==
                HolyStoneCommandOperation.Combine)
            {
                var thirdMaterial = locked.ThirdMaterial ??
                    throw new InvalidDataException(
                        "A successful Holy Stone Combination has no " +
                        "third material.");
                mutations.Add(await ConsumeStoneAsync(
                    connection,
                    transaction,
                    context,
                    thirdMaterial,
                    plan.ThirdMaterialAfter,
                    cancellationToken));
                await ReachAsync(
                    PostgresHolyStoneCommandStage.StoneMutated,
                    3,
                    cancellationToken);
            }
        }
        else if (plan.Status == HolyStoneCommandResultStatus.Removed)
        {
            mutations.Add(await InsertOutputAsync(
                connection,
                transaction,
                context,
                plan,
                outputItemInstanceId!.Value,
                cancellationToken));
            await ReachAsync(
                PostgresHolyStoneCommandStage.OutputInserted,
                1,
                cancellationToken);
        }

        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context,
            character.InventoryRevision,
            nextRevision,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.InventoryRevisionAdvanced,
            -1,
            cancellationToken);
        if (chargesGold)
        {
            await InsertGoldLedgerAsync(
                connection,
                transaction,
                inboxId,
                context,
                character,
                checked(character.Gold - plan.GoldSpent),
                nextWalletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresHolyStoneCommandStage.CurrencyLedgerInserted,
                -1,
                cancellationToken);
        }
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            context,
            nextRevision,
            mutations,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.InventoryLedgerInserted,
            -1,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            nextRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.OutboxInserted,
            -1,
            cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return HolyStoneExecutionResult.Committed(receipt);
    }
}
