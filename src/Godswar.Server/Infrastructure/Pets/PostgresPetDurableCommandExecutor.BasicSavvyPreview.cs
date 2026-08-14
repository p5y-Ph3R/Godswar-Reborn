using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    // Retained only so migration-080 preview rows can be read/discarded while
    // rolling deployments drain older server processes. New Fairy resets do
    // not create pending previews.
    private static readonly TimeSpan PetBasicSavvyPreviewLifetime =
        TimeSpan.FromMinutes(2);

    private async Task<PetTransition> ExecutePetBasicSavvyCommitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedPetForBasicSavvyResetAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return new(PetDurableReceiptStatus.PetNotTaken);
        }

        var feather = await LockFirstFairyFeatherAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (feather is null)
        {
            return FromBasicSavvyResetPet(
                PetDurableReceiptStatus.FairyFeatherNotFound,
                pet);
        }

        var stats = await LockPetBasicSavvyStatsAsync(
            connection,
            transaction,
            pet,
            cancellationToken);
        var current = ToBasicSavvyVector(stats);
        var roll = PetBasicSavvyRedistributionPolicy.Redistribute(
            current,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        if (roll.TotalSavvy != ToBasicSavvyArray(current).Sum())
        {
            throw new InvalidDataException(
                "The Fairy preview did not preserve total Basic Savvy.");
        }

        var values = ToBasicSavvyArray(roll.BasicSavvy);
        var nextStats = new BasicSavvyResetStat[stats.Count];
        for (var index = 0; index < stats.Count; index++)
        {
            var currentStat = stats[index];
            nextStats[index] = currentStat with
            {
                InitialSavvy = values[index],
                Revision = checked(currentStat.Revision + 1)
            };
            await UpdatePetBasicSavvyStatAsync(
                connection,
                transaction,
                pet.PetId,
                currentStat,
                nextStats[index],
                cancellationToken);
        }

        if (nextStats.Sum(static value => value.InitialSavvy) !=
            stats.Sum(static value => value.InitialSavvy))
        {
            throw new InvalidDataException(
                "The Fairy reset changed total Basic Savvy.");
        }

        var nextPetRevision = await AdvanceBasicSavvyPetRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            cancellationToken);

        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            feather.BagSlot,
            feather.Item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            character.InventoryRevision,
            cancellationToken);
        await DeleteAnyBasicSavvyPreviewAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        await WriteBasicSavvyResetAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            feather,
            stats,
            nextStats,
            roll,
            cancellationToken);

        return new PetTransition(
            PetDurableReceiptStatus.PetBasicSavvyAccepted,
            KitBagSlot: feather.BagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(
                    feather.Item.ItemId,
                    consumed.MutationKind,
                    feather.Item.BeforeState,
                    consumed.AfterState,
                    "pet_basic_savvy_reset",
                    inventoryRevision)
            ],
            BasicSavvyPreview: new PetBasicSavvyPreviewSnapshot(
                envelope.Command.Identity.OperationId,
                pet.PetId,
                pet.Level,
                nextPetRevision,
                roll.BasicSavvy,
                DateTimeOffset.MaxValue));
    }
}
