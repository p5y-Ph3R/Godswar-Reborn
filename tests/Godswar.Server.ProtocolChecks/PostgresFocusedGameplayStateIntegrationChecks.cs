using Godswar.Server.Application.Progression;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.World;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;
using AppBoostKinds =
    Godswar.Server.Application.Progression.ExperienceBoostKinds;
using AppStatusIds =
    Godswar.Server.Application.Progression.ExperienceStatusIds;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Mandatory disposable-PostgreSQL coverage for the B20C focused runtime
/// progression and world-boss persistence boundaries.
/// </summary>
internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL focused progression and world-boss persistence";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationRunner =
            new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeGodswarSchemaAsync();
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        var gameplayPublication =
            await PostgresGameplayContentPublisher.EnsurePublishedAsync(
                connectionString);

        Fixture? fixture = null;
        try
        {
            fixture = await CreateFixtureAsync(
                dataSource,
                gameplayPublication.Revision);
            var boostReader =
                new PostgresExperienceBoostStateReader(
                    dataSource,
                    gameplayPublication.Revision);
            var worldBossStore =
                new PostgresWorldBossAreaControlStore(
                    dataSource,
                    RealmId.Tempest,
                    gameplayPublication.Revision);

            await AssertInvalidAndNotConfiguredAsync(
                dataSource,
                worldBossStore,
                fixture);
            var committed = await AssertWorldBossLifecycleAsync(
                dataSource,
                worldBossStore,
                fixture);
            await AssertRealmIsolationAsync(
                dataSource,
                boostReader,
                worldBossStore,
                fixture,
                gameplayPublication.Revision);
            await AssertBoostCompositionAndOwnershipAsync(
                boostReader,
                fixture,
                committed);
            await AssertDeletedCharacterIsExcludedAsync(
                dataSource,
                boostReader,
                fixture);
            await AssertWorldBossActivationRacesAsync(
                connectionString,
                dataSource,
                fixture,
                gameplayPublication.Revision);
        }
        finally
        {
            if (fixture is not null)
            {
                await DeleteFixtureAsync(dataSource, fixture);
            }
        }
    }

    private static async Task AssertInvalidAndNotConfiguredAsync(
        NpgsqlDataSource dataSource,
        PostgresWorldBossAreaControlStore store,
        Fixture fixture)
    {
        var invalid = await store.ActivateAsync(
            new WorldBossAreaActivation(
                fixture.ConfiguredMapId,
                fixture.BossTemplateKey,
                9,
                fixture.KilledAtUtc,
                $"invalid:{fixture.Token}"));
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Invalid,
            (int)invalid.Disposition,
            "invalid camp is rejected before PostgreSQL mutation");
        Check.True(
            invalid.Control is null,
            "invalid activation has no control projection");

        var notConfigured = await store.ActivateAsync(
            new WorldBossAreaActivation(
                fixture.UnconfiguredMapId,
                fixture.BossTemplateKey,
                0,
                fixture.KilledAtUtc,
                $"unconfigured:{fixture.Token}"));
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.NotConfigured,
            (int)notConfigured.Disposition,
            "map without a world-boss policy is not configured");
        Check.True(
            notConfigured.Control is null,
            "not-configured activation has no control projection");
        Check.Equal(
            0L,
            await CountControlsAsync(dataSource, fixture),
            "rejected world-boss requests create no control rows");

        var absent = await store.ReadActiveAsync(
            new WorldBossRespawnReadRequest(
                fixture.ConfiguredMapId,
                fixture.ReadAtUtc));
        Check.True(
            absent is null,
            "configured boss without a kill has no respawn suppression");
    }

    private static async Task<WorldBossAreaControlSnapshot>
        AssertWorldBossLifecycleAsync(
            NpgsqlDataSource dataSource,
            PostgresWorldBossAreaControlStore store,
            Fixture fixture)
    {
        var activation = new WorldBossAreaActivation(
            fixture.ConfiguredMapId,
            fixture.BossTemplateKey,
            0,
            fixture.KilledAtUtc,
            fixture.DeathToken);
        var committed = await store.ActivateAsync(activation);
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Committed,
            (int)committed.Disposition,
            "configured world-boss activation commits");
        Check.True(
            committed.Control is not null,
            "committed activation returns its durable projection");
        var control = committed.Control!;
        Check.Equal(
            fixture.ConfiguredMapId,
            control.MapId,
            "committed control preserves the configured map");
        Check.Equal(
            fixture.BossTemplateKey,
            control.BossTemplateKey,
            "committed control preserves the configured boss");
        Check.Equal(
            fixture.DeathToken,
            control.DeathToken,
            "committed control preserves the death token");
        Check.Equal(
            fixture.BonusBasisPoints,
            control.BonusBasisPoints,
            "committed control uses the configured area bonus");
        Check.Equal(
            fixture.KilledAtUtc,
            control.ActivatedAtUtc,
            "committed control records the authoritative kill time");
        Check.Equal(
            fixture.KilledAtUtc.AddSeconds(
                fixture.RespawnIntervalSeconds),
            control.ExpiresAtUtc,
            "committed control applies the configured respawn interval");

        var duplicate = await store.ActivateAsync(
            activation with
            {
                ControllingCamp = 1,
                KilledAtUtc = fixture.KilledAtUtc.AddMinutes(1)
            });
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Duplicate,
            (int)duplicate.Disposition,
            "same death token is idempotent");
        Check.Equal(
            fixture.DeathToken,
            duplicate.Control!.DeathToken,
            "duplicate returns the original durable control");
        Check.Equal(
            (byte)0,
            duplicate.Control.ControllingCamp,
            "duplicate cannot change the controlling camp");

        var stale = await store.ActivateAsync(
            activation with
            {
                ControllingCamp = 1,
                KilledAtUtc = fixture.KilledAtUtc.AddSeconds(-1),
                DeathToken = $"stale:{fixture.Token}"
            });
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Stale,
            (int)stale.Disposition,
            "older delayed kill is classified as stale");
        Check.Equal(
            fixture.DeathToken,
            stale.Control!.DeathToken,
            "stale kill returns the newer durable control");
        Check.Equal(
            fixture.DeathToken,
            await ReadDeathTokenAsync(dataSource, fixture.ConfiguredMapId),
            "stale kill cannot overwrite PostgreSQL state");

        var respawn = await store.ReadActiveAsync(
            new WorldBossRespawnReadRequest(
                fixture.ConfiguredMapId,
                fixture.ReadAtUtc));
        Check.True(
            respawn is not null,
            "active control suppresses the killed world boss");
        Check.Equal(
            control.ExpiresAtUtc,
            respawn!.RespawnAtUtc,
            "respawn projection preserves the durable expiry");

        var expired = await store.ReadActiveAsync(
            new WorldBossRespawnReadRequest(
                fixture.ConfiguredMapId,
                control.ExpiresAtUtc));
        Check.True(
            expired is null,
            "respawn suppression ends at the exact expiry boundary");
        return control;
    }

    private static async Task AssertRealmIsolationAsync(
        NpgsqlDataSource dataSource,
        PostgresExperienceBoostStateReader boostReader,
        PostgresWorldBossAreaControlStore tempestStore,
        Fixture fixture,
        string gameplayContentRevision)
    {
        var dwargonStore = new PostgresWorldBossAreaControlStore(
            dataSource,
            RealmId.Dwargon,
            gameplayContentRevision);
        var request = new ExperienceBoostReadRequest(
            fixture.OtherAccountId,
            fixture.DwargonCharacterId,
            0,
            fixture.ConfiguredMapId,
            fixture.ReadAtUtc);
        var before = await boostReader.ReadAsync(request);
        Check.True(
            before.ActiveBoosts.All(static boost =>
                boost.Kind != AppBoostKinds.FactionArea),
            "Tempest area control cannot leak into a Dwargon character read");
        Check.True(
            await dwargonStore.ReadActiveAsync(
                new WorldBossRespawnReadRequest(
                    fixture.ConfiguredMapId,
                    fixture.ReadAtUtc)) is null,
            "Tempest respawn suppression cannot leak into Dwargon");

        var dwargonActivation = await dwargonStore.ActivateAsync(
            new WorldBossAreaActivation(
                fixture.ConfiguredMapId,
                fixture.BossTemplateKey,
                1,
                fixture.KilledAtUtc.AddMinutes(1),
                $"dwargon:{fixture.Token}"));
        Check.Equal(
            (int)WorldBossAreaActivationDisposition.Committed,
            (int)dwargonActivation.Disposition,
            "Dwargon independently controls the same configured map");

        var after = await boostReader.ReadAsync(
            request with { Camp = 1 });
        Check.Equal(
            1,
            after.ActiveBoosts.Count(static boost =>
                boost.Kind == AppBoostKinds.FactionArea),
            "Dwargon character receives only Dwargon area control");
        Check.Equal(
            fixture.DeathToken,
            await ReadDeathTokenAsync(
                dataSource,
                fixture.ConfiguredMapId,
                RealmId.Tempest),
            "Dwargon activation preserves Tempest control");
        Check.Equal(
            $"dwargon:{fixture.Token}",
            await ReadDeathTokenAsync(
                dataSource,
                fixture.ConfiguredMapId,
                RealmId.Dwargon),
            "Dwargon activation owns its realm control row");

        var tempestRespawn = await tempestStore.ReadActiveAsync(
            new WorldBossRespawnReadRequest(
                fixture.ConfiguredMapId,
                fixture.ReadAtUtc));
        Check.True(
            tempestRespawn is not null,
            "Dwargon activation preserves Tempest respawn suppression");
    }

    private static async Task AssertBoostCompositionAndOwnershipAsync(
        PostgresExperienceBoostStateReader reader,
        Fixture fixture,
        WorldBossAreaControlSnapshot control)
    {
        var request = new ExperienceBoostReadRequest(
            fixture.PrimaryAccountId,
            fixture.CharacterId,
            0,
            fixture.ConfiguredMapId,
            fixture.ReadAtUtc);
        var snapshot = await reader.ReadAsync(request);
        Check.Equal(
            4,
            snapshot.ActiveBoosts.Length,
            "one repeatable snapshot composes personal, Talent, VIP, and area boosts");
        Check.True(
            snapshot.ActiveBoosts
                .Select(static boost => boost.Kind)
                .SequenceEqual(
                [
                    AppBoostKinds.Consumable,
                    AppBoostKinds.Talent,
                    AppBoostKinds.Vip,
                    AppBoostKinds.FactionArea
                ]),
            "composed boosts are stable and ordered by kind");

        var personal = snapshot.ActiveBoosts.Single(
            static boost =>
                boost.Kind == AppBoostKinds.Consumable);
        Check.Equal(
            fixture.ReadAtUtc.AddMinutes(30),
            personal.ExpiresAtUtc!.Value,
            "online-only personal duration is projected from read time");
        var vip = snapshot.ActiveBoosts.Single(
            static boost => boost.Kind == AppBoostKinds.Vip);
        Check.Equal(
            AppStatusIds.VipGold,
            vip.StatusId,
            "Gold VIP membership selects the Gold status");
        Check.Equal(
            fixture.VipExpiresAtUtc,
            vip.ExpiresAtUtc!.Value,
            "VIP projection preserves its calendar expiry");
        var area = snapshot.ActiveBoosts.Single(
            static boost =>
                boost.Kind == AppBoostKinds.FactionArea);
        Check.Equal(
            control.ExpiresAtUtc,
            area.ExpiresAtUtc!.Value,
            "area boost shares the world-boss control expiry");
        Check.Equal(
            2_500 + fixture.BonusBasisPoints,
            snapshot.TotalBonusBasisPoints,
            "fighter EXP stacks personal, VIP, and area bonuses");
        Check.Equal(
            2_000,
            snapshot.TotalTalentBonusBasisPoints,
            "Talent EXP remains a separate bonus channel");
        Check.Equal(
            (100 * (12_500 + fixture.BonusBasisPoints)) / 10_000,
            snapshot.ApplyTo(100),
            "composed fighter EXP multiplier is deterministic");
        Check.Equal(
            120,
            snapshot.ApplyToTalent(100),
            "composed Talent EXP multiplier is deterministic");

        var crossAccount = await reader.ReadAsync(
            request with { AccountId = fixture.OtherAccountId });
        Check.Equal(
            0,
            crossAccount.ActiveBoosts.Length,
            "another account cannot read a character's boosts or VIP projection");
    }

    private static async Task AssertDeletedCharacterIsExcludedAsync(
        NpgsqlDataSource dataSource,
        PostgresExperienceBoostStateReader reader,
        Fixture fixture)
    {
        await SoftDeleteCharacterAsync(dataSource, fixture);
        var snapshot = await reader.ReadAsync(
            new ExperienceBoostReadRequest(
                fixture.PrimaryAccountId,
                fixture.CharacterId,
                0,
                fixture.ConfiguredMapId,
                fixture.ReadAtUtc));
        Check.Equal(
            0,
            snapshot.ActiveBoosts.Length,
            "deleted character cannot expose personal, account, or area boosts");
    }
}
