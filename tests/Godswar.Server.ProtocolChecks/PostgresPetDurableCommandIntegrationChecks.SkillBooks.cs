using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    public const string SkillBookCheckName =
        "PostgreSQL authoritative pet skill-book activation";

    private const short SkillBookSlot = 89;

    public static async Task RunSkillBookOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {SkillBookCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {SkillBookCheckName} requires a disposable " +
                $"B03/B12 database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }

        var learned =
            await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
                connectionString);
        var ownerMerge =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var options = new PostgresOutboxDispatcherOptions();
        var executor = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            learned,
            new FixedPetHatchRankRollSource(89));
        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            learned,
            new ThrowingPetHatchRankRollSource());
        var fixture = await CreateFixtureAsync(connectionString);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var hatch = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId),
                        fixture.EggSlot))));
        Check.True(
            hatch is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.EggHatched
            },
            "skill-book fixture hatches one carried pet");

        var petId = hatch.Receipt!.PetId;
        var bookInstanceId = await PrepareSkillBookAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var operationId = Guid.NewGuid();
        var envelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    SkillBookSlot)));
        var results = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            restarted.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            results,
            PetDurableReceiptStatus.PetSkillLearned,
            "concurrent Wild Bump II activation " +
            string.Join(",", results.Select(result =>
                $"{result.Disposition}:{result.Receipt?.Status}")));
        var receipt = results.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var evidence = receipt.SkillLearn ??
            throw new InvalidDataException(
                "The skill-book receipt omitted learn evidence.");
        Check.True(
            evidence.IsValid &&
            evidence.PetId == petId &&
            evidence.ItemInstanceId == bookInstanceId &&
            evidence.ItemTemplateId == 10_465 &&
            evidence.SpeciesId == 25 &&
            evidence.FamilyType == 408 &&
            evidence.PreviousPriority == 1 &&
            evidence.LearnedPriority == 2 &&
            evidence.PreviousRuntimeSkillId == 3_900 &&
            evidence.LearnedRuntimeSkillId == 3_904 &&
            evidence.SkillSlot == 0 &&
            evidence.TraitRequirement.Strength == 64m &&
            evidence.TraitsAtLearnTime.Strength == 64m &&
            evidence.ItemContentRevision ==
                itemContent.Templates.Revision.Sha256 &&
            evidence.LearnedSkillContentRevision == learned.Revision.Sha256,
            "receipt pins item instance, species, family, tier, slot, Trait, and both content revisions");

        var state = await ReadSkillBookStateAsync(
            dataSource,
            fixture.CharacterId,
            petId,
            receipt);
        Check.True(
            state.SkillId == 3_904 &&
            state.SkillRank == 2 &&
            state.SkillSlot == 0 &&
            state.SkillExperience == 0 &&
            state.SkillRevision == 1 &&
            state.BookStack == 1 &&
            state.PetRevision == receipt.PetRevision &&
            state.InventoryRevision == 2 &&
            state.SkillBookLedgerCount == 1 &&
            state.AuditEvidenceCount == 1,
            "one transaction upgrades the exact family row, consumes one exact book, and records durable audit/ledger evidence");

        var replay = await restarted.ExecuteAsync(envelope);
        var replayed = await ReadSkillBookStateAsync(
            dataSource,
            fixture.CharacterId,
            petId,
            receipt);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == receipt &&
            replayed == state,
            "restart replay cannot consume the book or advance the tier twice");

        var wrongSpeciesInstance = await SeedBagItemAsync(
            dataSource,
            fixture.CharacterId,
            bagSlot: SkillBookSlot - 1,
            itemId: 10_511,
            stack: 2);
        var rejected = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.RawLocalServer(
                            Guid.NewGuid(),
                            correlation.ConnectionId),
                        SkillBookSlot - 1))));
        Check.True(
            rejected is
            {
                Disposition:
                    PetDurableExecutionDisposition.TerminalRejected,
                Receipt.Status:
                    PetDurableReceiptStatus.PetSkillBookWrongSpecies
            } &&
            await ReadItemStackAsync(dataSource, wrongSpeciesInstance) == 2,
            "wrong-species packet selection is rejected without consuming its exact bag item");
        await AssertPlatypusFocusActivationAsync(
            executor,
            dataSource,
            subject,
            correlation,
            fixture.CharacterId,
            petId);
        await AssertLearnedSkillOwnerStatsAsync(
            dataSource,
            itemContent,
            learned,
            fixture.CharacterId,
            petId);
    }

    private static async Task<long> PrepareSkillBookAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET species_id = 25,
                has_soul_contract = true,
                soul_contract_stage = 6,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND is_carried;
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(1, await pet.ExecuteNonQueryAsync(),
                "skill-book fixture selects one Cretan Bull");
        }
        await using (var skill = new NpgsqlCommand(
            """
            UPDATE public.character_pet_skills
            SET skill_id = 3900,
                skill_rank = 1,
                skill_experience = 0,
                revision = 0
            WHERE pet_id = @petId
              AND slot_index = 0;
            """,
            connection,
            transaction))
        {
            skill.Parameters.AddWithValue("petId", petId);
            Check.Equal(1, await skill.ExecuteNonQueryAsync(),
                "skill-book fixture pins the species starter family");
        }
        await using (var trait = new NpgsqlCommand(
            """
            UPDATE public.character_pet_stat_values
            SET initial_savvy = 56
            WHERE pet_id = @petId
              AND stat_code = 2;
            """,
            connection,
            transaction))
        {
            trait.Parameters.AddWithValue("petId", petId);
            Check.Equal(1, await trait.ExecuteNonQueryAsync(),
                "Soul Contract +8 satisfies the Strength-64 threshold");
        }
        var itemId = await SeedBagItemAsync(
            connection,
            transaction,
            characterId,
            SkillBookSlot,
            itemId: 10_465,
            stack: 2);
        await transaction.CommitAsync();
        return itemId;
    }

    private static async Task<long> SeedBagItemAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int bagSlot,
        int itemId,
        short stack)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await SeedBagItemAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            itemId,
            stack);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<long> SeedBagItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        int itemId,
        short stack)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @bagSlot, @itemId,
                1, 1, 1, @stack, 0, 0
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", checked((short)bagSlot));
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("stack", stack);
        return await command.ExecuteScalarAsync() is long instanceId
            ? instanceId
            : throw new InvalidDataException(
                "The skill-book fixture item was not inserted.");
    }

    private static async Task<SkillBookState> ReadSkillBookStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId,
        PetDurableReceipt receipt)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                skill.skill_id,
                skill.skill_rank,
                skill.slot_index,
                skill.skill_experience,
                skill.revision,
                item.stack,
                pet.revision,
                character.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'pet_skill_book_learn'
                ),
                (
                    SELECT count(*)
                    FROM public.command_audit
                    WHERE id = @auditId
                      AND detail_payload -> 'pet_skill_learn'
                          ->> 'ItemTemplateId' = '10465'
                )
            FROM public.character_pets pet
            JOIN public.character_pet_skills skill
              ON skill.pet_id = pet.id
             AND skill.slot_index = 0
            JOIN public.character_items item
              ON item.user_id = pet.user_id
             AND item.item_location = 1
             AND item.slot_index = @bookSlot
            JOIN public.character_base character
              ON character.id = pet.user_id
            WHERE pet.id = @petId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "auditId",
            long.Parse(
                receipt.AuditReference,
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("bookSlot", SkillBookSlot);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The committed skill-book projection disappeared.");
        }
        return new SkillBookState(
            reader.GetInt32(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetInt16(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));
    }

    private static async Task<short> ReadItemStackAsync(
        NpgsqlDataSource dataSource,
        long itemInstanceId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT stack
            FROM public.character_items
            WHERE id = @itemInstanceId;
            """);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        return await command.ExecuteScalarAsync() is short stack
            ? stack
            : throw new InvalidDataException(
                "The rejected skill book disappeared.");
    }

    private sealed record SkillBookState(
        int SkillId,
        short SkillRank,
        short SkillSlot,
        int SkillExperience,
        long SkillRevision,
        short BookStack,
        long PetRevision,
        long InventoryRevision,
        long SkillBookLedgerCount,
        long AuditEvidenceCount);
}
