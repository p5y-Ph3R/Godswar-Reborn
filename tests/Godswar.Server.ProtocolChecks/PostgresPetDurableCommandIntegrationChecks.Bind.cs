using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetBindAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        IPetContentCatalog petContent)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            PetAptitude.Smart);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.SecureClient(
                            Guid.NewGuid()),
                        fixture.EggSlot))));
        var petId = hatch.Receipt?.PetId ??
            throw new InvalidDataException(
                "Pet bind fixture failed to hatch.");
        var callOut = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetPresenceTransitionCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetPresenceTransitionCommand(
                        PetCommandOperationIdentity.SecureClient(
                            Guid.NewGuid()),
                        petId,
                        PetPresenceCommandOperation.CallOut))));
        Check.True(
            callOut.Receipt is
            {
                Status: PetDurableReceiptStatus.PresenceChanged,
                IsSummoned: true
            },
            "pet bind fixture summons its target");
        await SetFixturePetBoundAsync(dataSource, petId, isBound: false);

        var before = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            !before.IsBound && before.IsSummoned,
            "pet bind fixture starts with one unbound summoned pet");
        var operationId = Guid.NewGuid();
        var envelope = PlayerOwnershipTestFences.Bind(
            PetBindCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetBindCommand(
                    PetCommandOperationIdentity.SecureClient(
                        operationId))));
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetBound,
            "concurrent summoned-pet bind");
        var receipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var bound = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            receipt.PetId == petId &&
            receipt.PetRevision == before.PetRevision + 1 &&
            receipt.IsCarried && receipt.IsSummoned &&
            bound.IsBound &&
            bound.PetRevision == before.PetRevision + 1 &&
            SameBindPayload(before, bound) &&
            bound.InventoryRevision == before.InventoryRevision &&
            bound.InventoryLedgerCount == before.InventoryLedgerCount &&
            bound.InventoryOutboxCount == before.InventoryOutboxCount &&
            bound.CommandAuditCount == 1 &&
            bound.InboxCount == 1 &&
            bound.PetOutboxCount == 1 &&
            bound.CommittedOperationAuditCount == 1 &&
            bound.DuplicateCount == 1,
            "bind atomically changes only bound/revision with no inventory mutation");
        await AssertPetBindAuditEvidenceAsync(
            dataSource,
            receipt,
            operationId,
            petContent.Revision.Sha256);

        var replay = await restarted.ExecuteAsync(envelope);
        var replayed = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            SameBindCommit(bound, replayed) &&
            replayed.DuplicateCount == 2,
            "bind replay cannot increment revision or mutate the pet twice");

        var already = await executor.ExecuteAsync(
            CreatePetBindEnvelope(subject, correlation));
        var afterAlready = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            already.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            already.Receipt?.Status ==
                PetDurableReceiptStatus.PetAlreadyBound &&
            SameBindCommit(replayed, afterAlready) &&
            afterAlready.RejectedOperationAuditCount == 1,
            "already-bound pet returns 1072 semantics without mutation");

        await SetFixturePetMergedAndUnboundAsync(dataSource, petId);
        var mergedBefore = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var unavailable = await executor.ExecuteAsync(
            CreatePetBindEnvelope(subject, correlation));
        var mergedAfter = await ReadPetBindStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            unavailable.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            unavailable.Receipt?.Status ==
                PetDurableReceiptStatus.PetBindPetNotSummoned &&
            !mergedAfter.IsBound &&
            SameBindCommit(mergedBefore, mergedAfter) &&
            mergedAfter.RejectedOperationAuditCount == 2,
            "owner-merged pet is unavailable to Bind and remains unbound");
    }

    private static CommandEnvelope<PetBindCommand> CreatePetBindEnvelope(
        CommandSubject subject,
        CommandConnectionCorrelation correlation) =>
        PlayerOwnershipTestFences.Bind(
            PetBindCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetBindCommand(
                    PetCommandOperationIdentity.SecureClient(
                        Guid.NewGuid()))));

    private static bool SameBindPayload(
        PetBindDatabaseState before,
        PetBindDatabaseState after) =>
        before.ImmutablePetJson == after.ImmutablePetJson &&
        before.StatValuesJson == after.StatValuesJson &&
        before.SkillsJson == after.SkillsJson &&
        before.CharacterBonusesJson == after.CharacterBonusesJson;

    private static bool SameBindCommit(
        PetBindDatabaseState expected,
        PetBindDatabaseState actual) =>
        expected.IsBound == actual.IsBound &&
        expected.PetRevision == actual.PetRevision &&
        expected.InventoryRevision == actual.InventoryRevision &&
        expected.InventoryLedgerCount == actual.InventoryLedgerCount &&
        expected.InventoryOutboxCount == actual.InventoryOutboxCount &&
        expected.PetOutboxCount == actual.PetOutboxCount &&
        expected.CommittedOperationAuditCount ==
            actual.CommittedOperationAuditCount &&
        SameBindPayload(expected, actual);
}
