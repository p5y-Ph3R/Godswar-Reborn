using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetManagerUtilityAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            PetAptitude.Smart);
        var outsider = await CreateFixtureAsync(connectionString);
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
                "Pet Manager utility fixture failed to hatch.");
        var summoned = await executor.ExecuteAsync(
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
            summoned.Receipt is
            {
                Status: PetDurableReceiptStatus.PresenceChanged,
                IsCarried: true,
                IsSummoned: true
            },
            "Pet Manager utility fixture summons one authoritative pet");
        await SeedPetManagerUtilityAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var initial = await ReadPetManagerUtilityStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var firstGrowth = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.CheckGrowth);
        var concurrentGrowth = await Task.WhenAll(
            executor.ExecuteAsync(firstGrowth),
            restarted.ExecuteAsync(firstGrowth));
        AssertCommitAndDuplicate(
            concurrentGrowth,
            PetDurableReceiptStatus.PetGrowthChecked,
            "concurrent Pixie Tear Growth check");
        var firstGrowthReceipt = concurrentGrowth.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        Check.True(
            firstGrowthReceipt.PetManagerUtility is
            {
                Operation: PetManagerUtilityOperation.CheckGrowth,
                ItemTemplateId: 10106,
                Growth: { IsValid: true },
                BeforePetState: { GrowthRevealed: false } before,
                AfterPetState: { GrowthRevealed: true } after
            } &&
            firstGrowthReceipt.PetRevision == after.Revision &&
            after.Revision == before.Revision + 1,
            "Growth receipt pins six effective rates and exact before/after revision evidence");

        var secondGrowth = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.CheckGrowth);
        var secondGrowthResult = await executor.ExecuteAsync(secondGrowth);
        var secondGrowthReplay = await restarted.ExecuteAsync(secondGrowth);
        Check.True(
            secondGrowthResult.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            secondGrowthReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            secondGrowthReplay.Receipt == secondGrowthResult.Receipt &&
            secondGrowthResult.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthChecked &&
            secondGrowthResult.Receipt.PetManagerUtility is
            {
                BeforePetState.GrowthRevealed: true,
                AfterPetState.GrowthRevealed: true
            },
            "a distinct Growth check consumes another Tear even after reveal while replay consumes none");
        var afterGrowth = await ReadPetManagerUtilityStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            afterGrowth.PetRevision == initial.PetRevision + 2 &&
            afterGrowth.PixieTears == 0 &&
            afterGrowth.InventoryRevision ==
                initial.InventoryRevision + 2,
            "two successful Growth operations advance pet and inventory exactly twice");

        var sealEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.Seal);
        var sealedResult = await executor.ExecuteAsync(sealEnvelope);
        var sealedReplay = await restarted.ExecuteAsync(sealEnvelope);
        var sealEvidence = sealedResult.Receipt?.PetManagerUtility;
        Check.True(
            sealedResult.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            sealedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            sealedReplay.Receipt == sealedResult.Receipt &&
            sealedResult.Receipt?.Status ==
                PetDurableReceiptStatus.PetSealed &&
            sealEvidence is
            {
                ItemTemplateId: 10109,
                ItemInstanceId: > 0,
                KitBagSlot: >= 0,
                BeforePetState:
                {
                    ActivityState: "owned",
                    SoulContractStage: 6
                },
                AfterPetState:
                {
                    ActivityState: "sealed",
                    IsCarried: false,
                    IsSummoned: false,
                    HasSoulContract: false,
                    SoulContractStage: 0
                }
            } &&
            sealedResult.Receipt.PetRevision ==
                sealEvidence.AfterPetState.Revision &&
            sealEvidence.AfterPetState.Revision ==
                sealEvidence.BeforePetState.Revision + 1,
            "Seal commits once, clears stage six, and replays exact evidence");
        var committedSealEvidence = sealEvidence ??
            throw new InvalidDataException(
                "Seal receipt has no utility evidence.");
        var packedSlot = committedSealEvidence.KitBagSlot;
        if (packedSlot < 0)
        {
            throw new InvalidDataException(
                "Seal receipt has no packed-item slot.");
        }
        var sealedState = await ReadPetManagerUtilityStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            sealedState.ActivityState == "sealed" &&
            sealedState.CurrentEnergy == 31 &&
            sealedState.MaximumEnergy == 100 &&
            sealedState.EmptySealJades == 1 &&
            sealedState.PackedSealJades == 1 &&
            sealedState.SealedLinks == 1 &&
            sealedState.PackedItemId ==
                committedSealEvidence.ItemInstanceId &&
            sealedState.PackedSlot == packedSlot,
            "a stacked empty Seal Jade decrements and creates a separately linked packed item");

        await using var snapshotReader =
            new PostgresCharacterSnapshotReader(
                connectionString,
                itemContent.Templates);
        var authorized = await snapshotReader
            .ReadAuthorizedSealedPetAsync(
                fixture.AccountId,
                fixture.CharacterId,
                petId);
        var unauthorized = await snapshotReader
            .ReadAuthorizedSealedPetAsync(
                outsider.AccountId,
                outsider.CharacterId,
                petId);
        var ownedWhileSealed = await snapshotReader.ReadOwnedPetsAsync(
            fixture.AccountId,
            fixture.CharacterId);
        Check.True(
            authorized?.PetId == petId &&
            authorized.ActivityState == "sealed" &&
            unauthorized is null &&
            ownedWhileSealed.All(pet => pet.PetId != petId),
            "packed detail authorizes only the linked owner and owned-pet lists exclude sealed pets");

        await SeedFullPetShedAsync(
            dataSource,
            fixture.CharacterId);
        var rejectedUnseal = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.Unseal,
            packedSlot);
        var fullResult = await executor.ExecuteAsync(rejectedUnseal);
        var fullReplay = await restarted.ExecuteAsync(rejectedUnseal);
        var stillSealed = await ReadPetManagerUtilityStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            fullResult.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            fullResult.Receipt?.Status ==
                PetDurableReceiptStatus.PetManagerPetUnavailable &&
            fullReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            fullReplay.Receipt == fullResult.Receipt &&
            stillSealed.ActivityState == "sealed" &&
            stillSealed.CurrentEnergy == 31 &&
            stillSealed.MaximumEnergy == 100 &&
            stillSealed.PackedSealJades == 1 &&
            stillSealed.SealedLinks == 1,
            "a full shed rejects and replays Unseal without consuming the link or packed item");
        var displaced = await PrepareDisplacedUnsealPetAsync(
            dataSource,
            fixture.CharacterId);

        var unsealEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.Unseal,
            packedSlot);
        var unsealedResult = await executor.ExecuteAsync(unsealEnvelope);
        var unsealedReplay = await restarted.ExecuteAsync(unsealEnvelope);
        var unsealEvidence = unsealedResult.Receipt?.PetManagerUtility;
        var unsealedState = await ReadPetManagerUtilityStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var displacedAfter = await ReadUnsealPresenceAsync(
            dataSource,
            displaced.PetId);
        Check.True(
            unsealedResult.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            unsealedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            unsealedReplay.Receipt == unsealedResult.Receipt &&
            unsealedResult.Receipt?.Status ==
                PetDurableReceiptStatus.PetUnsealed &&
            unsealEvidence?.BeforePetState?.ActivityState == "sealed" &&
            unsealEvidence.BeforePetState.CurrentEnergy == 31 &&
            unsealEvidence.BeforePetState.MaximumEnergy == 100 &&
            unsealEvidence.AfterPetState?.ActivityState == "owned" &&
            unsealEvidence.AfterPetState.CurrentEnergy == 100 &&
            unsealEvidence.AfterPetState.MaximumEnergy == 100 &&
            unsealedResult.Receipt.PetRevision ==
                unsealEvidence.AfterPetState.Revision &&
            unsealEvidence.AfterPetState.Revision ==
                unsealEvidence.BeforePetState.Revision + 1 &&
            unsealedState.ActivityState == "owned" &&
            unsealedState.IsCarried &&
            unsealedState.IsSummoned &&
            unsealedState.CurrentEnergy == 100 &&
            unsealedState.MaximumEnergy == 100 &&
            unsealedResult.Receipt.IsCarried &&
            unsealedResult.Receipt.IsSummoned &&
            unsealEvidence.AfterPetState.IsCarried &&
            unsealEvidence.AfterPetState.IsSummoned &&
            !displacedAfter.IsCarried &&
            !displacedAfter.IsSummoned &&
            displacedAfter.Revision == displaced.Revision + 1 &&
            unsealedState.PackedSealJades == 0 &&
            unsealedState.SealedLinks == 0,
            "Unseal consumes once, recalls the previous companion, and " +
            "atomically carries, summons, and fully energizes the restored pet");

        await AssertPetManagerGenderAndClaimsAsync(
            dataSource,
            executor,
            restarted,
            subject,
            correlation,
            petId);
        await AssertPetManagerUtilityAuditAsync(
            dataSource,
            fixture.CharacterId);
    }

    private static CommandEnvelope<PetManagerUtilityCommand>
        CreatePetManagerUtilityEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            PetManagerUtilityOperation operation,
            int kitBagSlot = -1) =>
        PlayerOwnershipTestFences.Bind(
            PetManagerUtilityCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetManagerUtilityCommand(
                    PetCommandOperationIdentity.SecureClient(
                        Guid.NewGuid()),
                    operation,
                    kitBagSlot)));
}
