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
    public const string BoundSealCheckName =
        "PostgreSQL bound-pet Seal Jade inheritance";

    public static async Task RunBoundSealOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {BoundSealCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {BoundSealCheckName} requires a disposable " +
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
            PetLearnedSkillContentBaseline.Create(),
            new FixedPetHatchRankRollSource(89));
        var restarted = new PostgresPetDurableCommandExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            PetLearnedSkillContentBaseline.Create(),
            new ThrowingPetHatchRankRollSource());

        await AssertSealBindingCaseAsync(
            connectionString, dataSource, executor, restarted,
            petIsBound: false, emptyStack: 1, fillBag: false);
        await AssertSealBindingCaseAsync(
            connectionString, dataSource, executor, restarted,
            petIsBound: true, emptyStack: 1, fillBag: true);
        await AssertSealBindingCaseAsync(
            connectionString, dataSource, executor, restarted,
            petIsBound: false, emptyStack: 2, fillBag: false);
        await AssertSealBindingCaseAsync(
            connectionString, dataSource, executor, restarted,
            petIsBound: true, emptyStack: 2, fillBag: false);
    }

    private static async Task AssertSealBindingCaseAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        bool petIsBound,
        short emptyStack,
        bool fillBag)
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
        Check.True(
            hatch is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.EggHatched
            },
            "bound-Seal fixture hatches one pet");
        var petId = hatch.Receipt!.PetId;
        var summon = await executor.ExecuteAsync(
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
            summon is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.PresenceChanged,
                Receipt.IsCarried: true,
                Receipt.IsSummoned: true
            },
            "bound-Seal fixture summons its authoritative pet");

        var emptyItemId = await PrepareSealBindingCaseAsync(
            dataSource,
            fixture.CharacterId,
            petId,
            petIsBound,
            emptyStack,
            fillBag);
        var seal = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.Seal);
        var sealResults = await Task.WhenAll(
            executor.ExecuteAsync(seal),
            restarted.ExecuteAsync(seal));
        AssertCommitAndDuplicate(
            sealResults,
            PetDurableReceiptStatus.PetSealed,
            $"{SealCaseName(petIsBound, emptyStack)} Seal");
        var sealReceipt = sealResults.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var sealedState = await ReadSealBindingStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var expectedPackedBound = checked((short)(petIsBound ? 1 : 0));
        var replacesInPlace = emptyStack == 1;
        Check.True(
            sealedState.PetIsBound == petIsBound &&
            sealedState.ActivityState == "sealed" &&
            !sealedState.IsCarried &&
            !sealedState.IsSummoned &&
            !sealedState.ContributesToCharacter &&
            !sealedState.HasSoulContract &&
            sealedState.SoulContractStage == 0 &&
            sealedState.EmptySealJades == emptyStack - 1 &&
            sealedState.PackedSealJades == 1 &&
            sealedState.SealedLinks == 1 &&
            sealedState.PetBoundSnapshot == petIsBound &&
            sealedState.PackedBound == expectedPackedBound &&
            sealedState.PackedItemId ==
                sealReceipt.PetManagerUtility?.ItemInstanceId &&
            sealedState.PackedSlot ==
                sealReceipt.PetManagerUtility.KitBagSlot &&
            (replacesInPlace
                ? sealedState.PackedItemId == emptyItemId &&
                  sealedState.PackedSlot == SealJadeSlot &&
                  sealedState.PackMutationKind == "update" &&
                  sealedState.EmptyMutationCount == 0
                : sealedState.PackedItemId != emptyItemId &&
                  sealedState.PackedSlot != SealJadeSlot &&
                  sealedState.PackMutationKind == "add" &&
                  sealedState.EmptyMutationCount == 1),
            $"{SealCaseName(petIsBound, emptyStack)} Seal inherits " +
            "pet binding and uses the correct atomic item shape");

        var unseal = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.Unseal,
            sealedState.PackedSlot);
        var unsealResult = await executor.ExecuteAsync(unseal);
        var unsealReplay = await restarted.ExecuteAsync(unseal);
        var unsealedState = await ReadUnsealedBindingStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            unsealResult is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.PetUnsealed
            } &&
            unsealReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            unsealReplay.Receipt == unsealResult.Receipt &&
            unsealedState.PetIsBound == petIsBound &&
            unsealedState.ActivityState == "owned" &&
            unsealedState.PackedSealJades == 0 &&
            unsealedState.SealedLinks == 0,
            $"{SealCaseName(petIsBound, emptyStack)} Unseal preserves " +
            "the pet binding and consumes its packed jade once");
    }

    private const short SealJadeSlot = 80;

    private static string SealCaseName(bool petIsBound, short emptyStack) =>
        $"{(petIsBound ? "bound" : "unbound")} pet, " +
        $"empty-jade stack {emptyStack}";

    private static async Task<long> PrepareSealBindingCaseAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId,
        bool petIsBound,
        short emptyStack,
        bool fillBag)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET bound = @petBound,
                contributes_to_character = false,
                has_soul_contract = true,
                soul_contract_stage = 6,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned;
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("petBound", petIsBound);
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await pet.ExecuteNonQueryAsync(),
                "bound-Seal fixture pins pet binding and soul stage");
        }

        long emptyItemId;
        await using (var item = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack
            )
            VALUES (
                @characterId, 1, @bagSlot, 10108,
                1, 1, @materialBound, @emptyStack
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            item.Parameters.AddWithValue("characterId", characterId);
            item.Parameters.AddWithValue("bagSlot", SealJadeSlot);
            item.Parameters.AddWithValue(
                "materialBound",
                checked((short)(petIsBound ? 0 : 1)));
            item.Parameters.AddWithValue("emptyStack", emptyStack);
            emptyItemId = (long)(await item.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "bound-Seal fixture created no empty jade."));
        }

        if (fillBag)
        {
            await using var fill = new NpgsqlCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack
                )
                SELECT
                    @characterId, 1, candidate.slot_index, 10104,
                    1, 1, 0, 1
                FROM generate_series(0, 95) candidate(slot_index)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM public.character_items item
                    WHERE item.user_id = @characterId
                      AND item.item_location = 1
                      AND item.slot_index = candidate.slot_index
                );
                """,
                connection,
                transaction);
            fill.Parameters.AddWithValue("characterId", characterId);
            await fill.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return emptyItemId;
    }

    private static async Task<SealBindingState> ReadSealBindingStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.bound, pet.activity_state, pet.is_carried,
                pet.is_summoned, pet.contributes_to_character,
                pet.has_soul_contract, pet.soul_contract_stage,
                packed.id, packed.bound, packed.slot_index,
                link.pet_bound_snapshot,
                (SELECT COALESCE(sum(item.stack), 0)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10108),
                (SELECT count(*)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10109),
                (SELECT count(*)::integer
                 FROM public.sealed_pet_items owned_link
                 WHERE owned_link.owner_character_id = @characterId),
                (SELECT ledger.mutation_kind
                 FROM public.character_inventory_ledger ledger
                 WHERE ledger.character_id = @characterId
                   AND ledger.item_instance_id = packed.id
                   AND ledger.reason_code = 'pet_sealed_into_jade'
                 ORDER BY ledger.id DESC
                 LIMIT 1),
                (SELECT count(*)::integer
                 FROM public.character_inventory_ledger ledger
                 WHERE ledger.character_id = @characterId
                   AND ledger.reason_code = 'pet_empty_seal_consumed')
            FROM public.character_pets pet
            JOIN public.sealed_pet_items link ON link.pet_id = pet.id
            JOIN public.character_items packed
              ON packed.id = link.item_instance_id
             AND packed.prop_id = 10109
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The sealed binding state is missing.");
        }
        return new SealBindingState(
            reader.GetBoolean(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            checked((byte)reader.GetInt16(6)),
            reader.GetInt64(7),
            reader.GetInt16(8),
            reader.GetInt16(9),
            reader.GetBoolean(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetString(14),
            reader.GetInt32(15));
    }

    private static async Task<UnsealedBindingState>
        ReadUnsealedBindingStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.bound,
                pet.activity_state,
                (SELECT count(*)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10109),
                (SELECT count(*)::integer
                 FROM public.sealed_pet_items link
                 WHERE link.owner_character_id = @characterId)
            FROM public.character_pets pet
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new UnsealedBindingState(
                reader.GetBoolean(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3))
            : throw new InvalidDataException(
                "The unsealed binding state is missing.");
    }

    private sealed record SealBindingState(
        bool PetIsBound,
        string ActivityState,
        bool IsCarried,
        bool IsSummoned,
        bool ContributesToCharacter,
        bool HasSoulContract,
        byte SoulContractStage,
        long PackedItemId,
        short PackedBound,
        int PackedSlot,
        bool PetBoundSnapshot,
        int EmptySealJades,
        int PackedSealJades,
        int SealedLinks,
        string PackMutationKind,
        int EmptyMutationCount);

    private sealed record UnsealedBindingState(
        bool PetIsBound,
        string ActivityState,
        int PackedSealJades,
        int SealedLinks);
}
