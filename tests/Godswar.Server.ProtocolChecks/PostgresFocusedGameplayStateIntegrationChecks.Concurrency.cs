using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.World;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    private static async Task AssertWorldBossActivationRacesAsync(
        string connectionString,
        NpgsqlDataSource verificationDataSource,
        Fixture fixture,
        string gameplayContentRevision)
    {
        // Separate providers guarantee separate pools and physical database
        // sessions. The release gate makes both calls contend in PostgreSQL.
        await using var firstProvider =
            NpgsqlDataSource.Create(connectionString);
        await using var secondProvider =
            NpgsqlDataSource.Create(connectionString);
        await WarmProviderAsync(firstProvider);
        await WarmProviderAsync(secondProvider);

        var firstStore =
            new PostgresWorldBossAreaControlStore(
                firstProvider,
                gameplayContentRevision);
        var secondStore =
            new PostgresWorldBossAreaControlStore(
                secondProvider,
                gameplayContentRevision);

        await AssertNewerEventWinsAsync(
            verificationDataSource,
            firstStore,
            secondStore,
            fixture);
        await AssertDeathTokenHasOneMapOwnerAsync(
            verificationDataSource,
            firstStore,
            secondStore,
            fixture);
    }

    private static async Task AssertNewerEventWinsAsync(
        NpgsqlDataSource verificationDataSource,
        PostgresWorldBossAreaControlStore firstStore,
        PostgresWorldBossAreaControlStore secondStore,
        Fixture fixture)
    {
        var older = new WorldBossAreaActivation(
            fixture.ConfiguredMapId,
            fixture.BossTemplateKey,
            0,
            fixture.KilledAtUtc.AddMinutes(2),
            $"race-older:{fixture.Token}");
        var newer = older with
        {
            ControllingCamp = 1,
            KilledAtUtc = fixture.KilledAtUtc.AddMinutes(3),
            DeathToken = $"race-newer:{fixture.Token}"
        };

        var release = NewRaceSignal();
        var olderTask = ActivateAfterReleaseAsync(
            release.Task,
            firstStore,
            older);
        var newerTask = ActivateAfterReleaseAsync(
            release.Task,
            secondStore,
            newer);
        release.SetResult();
        var results = await Task.WhenAll(olderTask, newerTask)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Committed,
            (int)results[1].Disposition,
            "newer concurrent world-boss event commits");
        Check.True(
            results[0].Disposition is
                WorldBossAreaActivationDisposition.Committed or
                WorldBossAreaActivationDisposition.Stale,
            "older concurrent event either commits first or observes the newer event");

        var durable = await ReadDurableControlAsync(
            verificationDataSource,
            fixture.ConfiguredMapId);
        Check.Equal(
            newer.DeathToken,
            durable.DeathToken,
            "concurrent activation leaves the newest death token durable");
        Check.Equal(
            newer.KilledAtUtc,
            durable.ActivatedAtUtc,
            "concurrent activation leaves the newest event time durable");
        Check.Equal(
            newer.ControllingCamp,
            durable.ControllingCamp,
            "concurrent activation leaves the newest controlling camp durable");
    }

    private static async Task AssertDeathTokenHasOneMapOwnerAsync(
        NpgsqlDataSource verificationDataSource,
        PostgresWorldBossAreaControlStore firstStore,
        PostgresWorldBossAreaControlStore secondStore,
        Fixture fixture)
    {
        var sharedDeathToken = $"race-shared:{fixture.Token}";
        var firstActivation = new WorldBossAreaActivation(
            fixture.ConfiguredMapId,
            fixture.BossTemplateKey,
            0,
            fixture.KilledAtUtc.AddMinutes(4),
            sharedDeathToken);
        var secondActivation = firstActivation with
        {
            MapId = fixture.SecondConfiguredMapId,
            BossTemplateKey = fixture.SecondBossTemplateKey,
            ControllingCamp = 1
        };

        var release = NewRaceSignal();
        var firstTask = ActivateAfterReleaseAsync(
            release.Task,
            firstStore,
            firstActivation);
        var secondTask = ActivateAfterReleaseAsync(
            release.Task,
            secondStore,
            secondActivation);
        release.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                WorldBossAreaActivationDisposition.Committed),
            "one map wins concurrent ownership of a global death token");
        Check.Equal(
            1,
            results.Count(static result =>
                result.Disposition ==
                WorldBossAreaActivationDisposition.Invalid),
            "cross-map death-token loser is rejected as invalid");

        var winnerIndex = results[0].Disposition ==
            WorldBossAreaActivationDisposition.Committed
            ? 0
            : 1;
        var winningActivation = winnerIndex == 0
            ? firstActivation
            : secondActivation;
        Check.True(
            results[1 - winnerIndex].Control is null,
            "invalid cross-map contender exposes no control projection");

        var durableOwner = await ReadDeathTokenOwnerAsync(
            verificationDataSource,
            sharedDeathToken);
        Check.Equal(
            winningActivation.MapId,
            durableOwner.MapId,
            "global death token is durable only on the winning map");
        Check.Equal(
            winningActivation.ControllingCamp,
            durableOwner.ControllingCamp,
            "global death token preserves the winning camp");
    }

    private static async Task<WorldBossAreaActivationResult>
        ActivateAfterReleaseAsync(
            Task release,
            PostgresWorldBossAreaControlStore store,
            WorldBossAreaActivation activation)
    {
        await release;
        return await store.ActivateAsync(activation);
    }

    private static TaskCompletionSource NewRaceSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WarmProviderAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1;",
            connection);
        await command.ExecuteScalarAsync();
    }

    private static async Task<DurableWorldBossControl>
        ReadDurableControlAsync(
            NpgsqlDataSource dataSource,
            short mapId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT death_token, controlling_camp, activated_at
            FROM public.faction_area_experience_control
            WHERE map_id = @mapId;
            """);
        command.Parameters.AddWithValue("mapId", mapId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The durable world-boss control is missing.");
        }

        return new DurableWorldBossControl(
            mapId,
            checked((byte)reader.GetInt16(1)),
            reader.GetString(0),
            AsUtc(reader.GetDateTime(2)));
    }

    private static async Task<DurableWorldBossControl>
        ReadDeathTokenOwnerAsync(
            NpgsqlDataSource dataSource,
            string deathToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT map_id, controlling_camp, activated_at
            FROM public.faction_area_experience_control
            WHERE death_token = @deathToken;
            """);
        command.Parameters.AddWithValue("deathToken", deathToken);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The durable world-boss death-token owner is missing.");
        }

        var owner = new DurableWorldBossControl(
            reader.GetInt16(0),
            checked((byte)reader.GetInt16(1)),
            deathToken,
            AsUtc(reader.GetDateTime(2)));
        Check.True(
            !await reader.ReadAsync(),
            "global death token has exactly one durable map owner");
        return owner;
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record DurableWorldBossControl(
        short MapId,
        byte ControllingCamp,
        string DeathToken,
        DateTimeOffset ActivatedAtUtc);
}
