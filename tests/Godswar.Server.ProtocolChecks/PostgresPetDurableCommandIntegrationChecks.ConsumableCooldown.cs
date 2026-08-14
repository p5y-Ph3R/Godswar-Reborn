using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertImmediateConsumableCooldownAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId,
        PetExperienceItemState expectedState)
    {
        var natural = await ReadConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4721);
        Check.True(
            natural is { } started &&
            started.ReadyAt - started.UpdatedAt ==
                TimeSpan.FromSeconds(1),
            "committed Morning Dew starts exactly one second of authoritative group-4721 cooldown");

        // Keep the rejection assertion deterministic on slow CI workers. The
        // preceding assertion proves the committed stock duration; this
        // fixture extension only ensures that it is still active while a
        // second operation and its replay are exercised.
        await ExtendConsumableCooldownForAssertionAsync(
            dataSource,
            subject.CharacterId,
            4721);
        var before = await ReadConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4721);

        var envelope = CreatePetExperienceItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            RestrictedMorningDewSlot);
        var rejected = await executor.ExecuteAsync(envelope);
        var replayed = await restarted.ExecuteAsync(envelope);
        var after = await ReadConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4721);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status ==
                PetDurableReceiptStatus.ConsumableCooldownActive &&
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == rejected.Receipt &&
            after == before &&
            await ReadPetExperienceItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId) == expectedState,
            "active cooldown rejects before pet/item mutation, remains monotonic, and replays without extending its deadline");

        await ExpireConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4721);
    }

    private static async Task
        ExtendConsumableCooldownForAssertionAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int cooldownGroup)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_bag_consumable_cooldowns
            SET ready_at = transaction_timestamp() + interval '1 minute',
                updated_at = transaction_timestamp()
            WHERE character_id = @characterId
              AND cooldown_group = @cooldownGroup;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("cooldownGroup", cooldownGroup);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "cooldown fixture keeps one group active during rejection assertions");
    }

    private static async Task ExpireConsumableCooldownAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int cooldownGroup)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_bag_consumable_cooldowns
            SET ready_at = '-infinity'::timestamptz,
                updated_at = '-infinity'::timestamptz
            WHERE character_id = @characterId
              AND cooldown_group = @cooldownGroup;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("cooldownGroup", cooldownGroup);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "cooldown fixture expires one authoritative group deadline");
    }

    private static async Task<ConsumableCooldownState?>
        ReadConsumableCooldownAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            int cooldownGroup)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT ready_at, updated_at
            FROM public.character_bag_consumable_cooldowns
            WHERE character_id = @characterId
              AND cooldown_group = @cooldownGroup;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("cooldownGroup", cooldownGroup);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    private sealed record ConsumableCooldownState(
        DateTimeOffset ReadyAt,
        DateTimeOffset UpdatedAt);
}
