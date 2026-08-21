using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertOwnerMergeRevisionSafetyAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent,
        IPetOwnerMergeContentCatalog ownerMergeContent)
    {
        var fixture = await CreateOwnerMergeRevisionFixtureAsync(
            connectionString,
            itemContent);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var hatch = await executor.ExecuteAsync(
            CreateOwnerMergeActivationEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                fixture.EggSlot));
        Check.True(
            hatch.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "owner-Merge provenance fixture hatches one pet");
        var petId = hatch.Receipt!.PetId;
        await SeedOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);

        var baselineStats = await ReadProjectedMergeStatsAsync(
            connectionString,
            fixture,
            itemContent);
        var staleRevision = await InsertStaleOwnerMergeRevisionAsync(
            dataSource,
            ownerMergeContent.Revision);

        foreach (var corruption in Enum.GetValues<
                     OwnerMergeBalanceCorruption>())
        {
            await AssertOwnerMergeRevisionRepairAsync(
                connectionString,
                dataSource,
                executor,
                itemContent,
                ownerMergeContent.Revision.Sha256,
                staleRevision,
                fixture,
                subject,
                correlation,
                petId,
                baselineStats,
                corruption);
        }
    }

    private static async Task AssertOwnerMergeRevisionRepairAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        GameplayItemContent itemContent,
        string pinnedRevision,
        string staleRevision,
        PetFixture fixture,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId,
        OwnerMergeProjectedStats baselineStats,
        OwnerMergeBalanceCorruption corruption)
    {
        var merged = await executor.ExecuteAsync(
            CreateOwnerMergeToggleEnvelope(
                subject,
                correlation,
                Guid.NewGuid()));
        Check.True(
            merged.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            merged.Receipt?.Status == PetDurableReceiptStatus.OwnerMerged,
            $"owner-Merge {corruption} fixture merges once");

        var stamped = await ReadOwnerMergeRevisionStampAsync(
            dataSource,
            petId,
            pinnedRevision);
        Check.True(
            stamped.RowCount == 18 &&
            stamped.PinnedRowCount == 18 &&
            stamped.DistinctRevisionCount == 1 &&
            stamped.NullRevisionCount == 0,
            "a fresh owner-Merge stamps all 18 rows with exactly the " +
            "process-pinned balance revision");

        await CorruptOwnerMergeBalanceAsync(
            dataSource,
            petId,
            staleRevision,
            corruption);
        var beforeRepair = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var repaired = await executor.ExecuteAsync(
            CreateOwnerMergeToggleEnvelope(
                subject,
                correlation,
                Guid.NewGuid()));
        var afterRepair = await ReadOwnerMergeStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var repairedStats = await ReadProjectedMergeStatsAsync(
            connectionString,
            fixture,
            itemContent);

        Check.True(
            repaired.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            repaired.Receipt?.Status ==
                PetDurableReceiptStatus.OwnerUnmerged &&
            repaired.Receipt.PetId == petId &&
            repaired.Receipt.PetRevision == beforeRepair.PetRevision + 1 &&
            !afterRepair.Contributes &&
            afterRepair.PetRevision == beforeRepair.PetRevision + 1 &&
            afterRepair.BonusCount == 0 &&
            repairedStats == baselineStats,
            $"owner unmerge repairs {corruption} non-authoritative rows " +
            "without leaking their values into the resulting projection");
    }

    private static async Task<OwnerMergeRevisionStamp>
        ReadOwnerMergeRevisionStampAsync(
            NpgsqlDataSource dataSource,
            long petId,
            string pinnedRevision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (
                    WHERE balance_revision = @pinnedRevision
                ),
                count(DISTINCT balance_revision),
                count(*) FILTER (WHERE balance_revision IS NULL)
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("pinnedRevision", pinnedRevision);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The owner-Merge revision stamp query returned no row.");
        }

        return new OwnerMergeRevisionStamp(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static async Task<string> InsertStaleOwnerMergeRevisionAsync(
        NpgsqlDataSource dataSource,
        PetOwnerMergeContentRevision pinned)
    {
        var staleRevision = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.pet_owner_merge_content_revisions (
                revision, policy_version, effect_base_count,
                band_count, rate_count, source
            )
            VALUES (
                @revision, 'integration-stale-fixture', @effectBaseCount,
                @bandCount, @rateCount, 'protocol-check-stale-fixture'
            );
            """);
        command.Parameters.AddWithValue("revision", staleRevision);
        command.Parameters.AddWithValue(
            "effectBaseCount",
            checked((short)pinned.EffectBaseCount));
        command.Parameters.AddWithValue(
            "bandCount",
            checked((short)pinned.BandCount));
        command.Parameters.AddWithValue(
            "rateCount",
            checked((short)pinned.RateCount));
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "owner-Merge stale fixture inserts one alternate revision");
        return staleRevision;
    }

    private static async Task CorruptOwnerMergeBalanceAsync(
        NpgsqlDataSource dataSource,
        long petId,
        string staleRevision,
        OwnerMergeBalanceCorruption corruption)
    {
        if (corruption == OwnerMergeBalanceCorruption.MissingRow)
        {
            await DeleteOneOwnerMergeRowAsync(dataSource, petId);
            return;
        }

        var sql = corruption switch
        {
            OwnerMergeBalanceCorruption.StaleRevision =>
                """
                UPDATE public.character_pet_character_bonuses
                SET effect_value = 999999,
                    balance_revision = @staleRevision
                WHERE pet_id = @petId;
                """,
            OwnerMergeBalanceCorruption.NullRevision =>
                """
                UPDATE public.character_pet_character_bonuses
                SET effect_value = 999999,
                    balance_revision = NULL
                WHERE pet_id = @petId;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("staleRevision", staleRevision);
        var affected = await command.ExecuteNonQueryAsync();
        Check.Equal(
            PetOwnerMergeStoredBonusCodec.TotalCount,
            affected,
            $"owner-Merge {corruption} fixture corrupts the expected rows");
    }

    private static async Task DeleteOneOwnerMergeRowAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand(
                         """
                         DELETE FROM public.character_pet_character_bonuses
                         WHERE pet_id = @petId
                           AND effect_code = (
                               SELECT min(effect_code)
                               FROM public.character_pet_character_bonuses
                               WHERE pet_id = @petId
                           );
                         """,
                         connection,
                         transaction))
        {
            delete.Parameters.AddWithValue("petId", petId);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "owner-Merge missing-row fixture deletes one row");
        }
        await using (var poison = new NpgsqlCommand(
                         """
                         UPDATE public.character_pet_character_bonuses
                         SET effect_value = 999999
                         WHERE pet_id = @petId;
                         """,
                         connection,
                         transaction))
        {
            poison.Parameters.AddWithValue("petId", petId);
            Check.Equal(
                PetOwnerMergeStoredBonusCodec.TotalCount - 1,
                await poison.ExecuteNonQueryAsync(),
                "owner-Merge missing-row fixture poisons remaining rows");
        }
        await transaction.CommitAsync();
    }

    private static async Task<PetFixture>
        CreateOwnerMergeRevisionFixtureAsync(
            string connectionString,
            GameplayItemContent itemContent)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var store = new PostgresGameStore(
            connectionString,
            itemContent);
        var account = await store.LoginOrCreateAccountAsync(
            $"b12_merge_revision_{token}",
            string.Empty);
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = $"Merge{token}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 80
            });
        const int eggSlot = 90;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var egg = new NpgsqlCommand(
                         """
                         INSERT INTO public.character_items (
                             user_id, item_location, slot_index, prop_id,
                             item_quality, item_grade, bound, stack,
                             item_exp, holy_suit_code
                         )
                         VALUES (
                             @characterId, 1, @slot, 10150,
                             @eggAptitude, 1, 1, 1, 0, 0
                         );
                         """,
                         connection))
        {
            egg.Parameters.AddWithValue("characterId", character.Id);
            egg.Parameters.AddWithValue("slot", (short)eggSlot);
            egg.Parameters.AddWithValue(
                "eggAptitude",
                (short)PetAptitude.Smart);
            Check.Equal(
                1,
                await egg.ExecuteNonQueryAsync(),
                "owner-Merge revision fixture inserts one egg");
        }

        await using var transaction =
            await connection.BeginTransactionAsync();
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            account.Id,
            character.Id);
        await transaction.CommitAsync();
        return new PetFixture(account.Id, character.Id, eggSlot);
    }

    private enum OwnerMergeBalanceCorruption
    {
        StaleRevision,
        NullRevision,
        MissingRow
    }

    private readonly record struct OwnerMergeRevisionStamp(
        long RowCount,
        long PinnedRowCount,
        long DistinctRevisionCount,
        long NullRevisionCount);
}
