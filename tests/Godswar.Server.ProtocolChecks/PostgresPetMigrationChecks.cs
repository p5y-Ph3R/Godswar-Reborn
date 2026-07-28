using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetMigrationChecks
{
    private static readonly IReadOnlyList<(
        PetAptitude Aptitude,
        decimal Minimum,
        decimal Maximum)> V1GrowthBrackets =
    [
        (PetAptitude.Weak, 2m, 3m),
        (PetAptitude.Fool, 3m, 4m),
        (PetAptitude.Cowish, 4m, 5m),
        (PetAptitude.Moderate, 5m, 7m),
        (PetAptitude.Rational, 7m, 9m),
        (PetAptitude.Calm, 9m, 11m),
        (PetAptitude.Smart, 11m, 14m),
        (PetAptitude.Zealous, 14m, 17m),
        (PetAptitude.Grumpy, 17m, 21m),
        (PetAptitude.Brave, 21m, 26m),
        (PetAptitude.Overbearing, 26m, 32m),
        (PetAptitude.Ferocious, 32m, 39m),
        (PetAptitude.Almighty, 39m, 47m),
        (PetAptitude.Godly, 47m, 56m),
        (PetAptitude.Celestial, 56m, 67m),
        (PetAptitude.Transcendent, 67m, 80m)
    ];

    public static Task RunAsync()
    {
        CheckPresenceMigrations();
        CheckGrowthPolicyMigration();
        CheckMidpointBackfillMigration();
        CheckMidpointDistribution();
        CheckGrowthPolicyV2Migration();
        return Task.CompletedTask;
    }

    private static void CheckPresenceMigrations()
    {
        var presence = Find("20260728_014_pet_presence_protocol");
        Check.True(
            presence.Sql.Contains(
                "ux_character_pets_one_carried",
                StringComparison.Ordinal) &&
            presence.Sql.Contains(
                "NOT is_summoned OR is_carried",
                StringComparison.Ordinal) &&
            presence.Sql.Contains(
                "NOT is_carried OR activity_state = 'owned'",
                StringComparison.Ordinal) &&
            presence.Sql.Contains(
                "WHERE is_summoned",
                StringComparison.Ordinal) &&
            presence.Sql.Contains("10239", StringComparison.Ordinal) &&
            presence.Sql.Contains("10244", StringComparison.Ordinal),
            "pet presence migration bounds selection and records the native protocol");

        var audit = Find(
            "20260728_015_pet_presence_audit_operation");
        Check.True(
            audit.Sql.Contains("'take'", StringComparison.Ordinal) &&
            audit.Sql.Contains("NOT VALID", StringComparison.Ordinal) &&
            audit.Sql.Contains(
                "VALIDATE CONSTRAINT",
                StringComparison.Ordinal) &&
            audit.Sql.Contains(
                "DROP CONSTRAINT",
                StringComparison.Ordinal),
            "pet Take auditing is added through a staged forward-only constraint");
    }

    private static void CheckGrowthPolicyMigration()
    {
        var growth = Find("20260728_016_pet_growth_policy");
        Check.True(
            growth.Sql.Contains(
                "minimum_total_growth",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "maximum_total_growth",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "maximum_stat_deviation",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "base_growth_rate",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "'project-v1'",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "generate_series(10150, 10193)",
                StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "'display-name-species-v1'",
                StringComparison.Ordinal) &&
            growth.Sql.Contains("'hatch'", StringComparison.Ordinal) &&
            growth.Sql.Contains(
                "ck_pet_operation_audit_operation_v3",
                StringComparison.Ordinal),
            "pet growth, egg templates, and hatch auditing are persisted");

        foreach (var bracket in V1GrowthBrackets)
        {
            var expectedRow = FormattableString.Invariant(
                $"({(short)bracket.Aptitude}::smallint, {bracket.Minimum:0.00}::numeric, {bracket.Maximum:0.00}::numeric)");
            Check.True(
                growth.Sql.Contains(expectedRow, StringComparison.Ordinal),
                $"{bracket.Aptitude} frozen v1 bracket remains immutable");
        }
    }

    private static void CheckMidpointBackfillMigration()
    {
        var backfill = Find(
            "20260728_017_pet_growth_midpoint_backfill");
        var sql = backfill.Sql;
        Check.True(
            sql.Contains(
                "WITH eligible_pets AS MATERIALIZED",
                StringComparison.Ordinal) &&
            sql.Contains(
                "existing.base_growth_rate <> 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE NOT EXISTS",
                StringComparison.Ordinal) &&
            sql.Contains(
                "round(",
                StringComparison.Ordinal) &&
            sql.Contains(
                "midpoint_total_growth * 100",
                StringComparison.Ordinal) &&
            sql.Contains(
                "generate_series(1, 6)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "midpoint.total_hundredths / 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "mod(midpoint.total_hundredths, 6)",
                StringComparison.Ordinal),
            "legacy backfill selects only all-zero pets and distributes midpoint hundredths");
        Check.True(
            sql.Contains(
                "INSERT INTO public.character_pet_stat_values",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ON CONFLICT (pet_id, stat_code) DO UPDATE",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE character_pet_stat_values.base_growth_rate = 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ALTER COLUMN base_growth_rate DROP DEFAULT",
                StringComparison.Ordinal),
            "legacy backfill repairs zero rows then prevents implicit growth creation");
        Check.True(
            !sql.Contains("random(", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "SET initial_savvy",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "SET added_savvy",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "SET growth_acceleration",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase),
            "legacy growth backfill is deterministic and preserves unrelated pet state");
    }

    private static void CheckMidpointDistribution()
    {
        foreach (var bracket in V1GrowthBrackets)
        {
            var midpoint = decimal.Round(
                (
                    bracket.Minimum +
                    bracket.Maximum
                ) / 2m,
                2,
                MidpointRounding.AwayFromZero);
            var totalHundredths =
                decimal.ToInt32(midpoint * 100m);
            var baseHundredths = totalHundredths / 6;
            var remainder = totalHundredths % 6;
            var distribution = Enumerable.Range(1, 6)
                .Select(statCode =>
                    baseHundredths +
                    (statCode <= remainder ? 1 : 0))
                .ToArray();

            Check.Equal(
                totalHundredths,
                distribution.Sum(),
                $"{bracket.Aptitude} frozen v1 midpoint remains exact");
            Check.True(
                distribution.Max() - distribution.Min() <= 1,
                $"{bracket.Aptitude} frozen v1 midpoint differs by at most one hundredth");
            Check.True(
                distribution.SequenceEqual(
                    Enumerable.Range(1, 6)
                        .Select(statCode =>
                            baseHundredths +
                            (statCode <= remainder ? 1 : 0))),
                $"{bracket.Aptitude} frozen v1 midpoint allocation is deterministic");
        }
    }

    private static void CheckGrowthPolicyV2Migration()
    {
        var migration = Find(
            "20260728_018_pet_growth_policy_v2");
        var sql = migration.Sql;
        var normalizedSql = string.Join(
            ' ',
            sql.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));
        Check.Equal(
            "project-v2",
            PetGrowthPolicy.Version,
            "runtime uses the forward-only v2 growth policy");
        foreach (var bracket in PetGrowthPolicy.All)
        {
            var expectedRow = FormattableString.Invariant(
                $"({bracket.AptitudeValue}::smallint, {bracket.MinimumTotalGrowth:0.00}::numeric, {bracket.MaximumTotalGrowth:0.00}::numeric)");
            Check.True(
                sql.Contains(expectedRow, StringComparison.Ordinal),
                $"{bracket.Aptitude} v2 runtime and migration brackets match");
        }

        Check.True(
            sql.Contains(
                "growth_policy_version = 'project-v2'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "GET DIAGNOSTICS updated_aptitudes = ROW_COUNT",
                StringComparison.Ordinal) &&
            sql.Contains(
                "IF updated_aptitudes <> 16",
                StringComparison.Ordinal),
            "v2 migration updates every aptitude and fails closed on an incomplete catalog");
        Check.True(
            sql.Contains(
                "HAVING count(*) = 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "count(DISTINCT stat.stat_code) = 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sum(stat.base_growth_rate)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "< aptitude.minimum_total_growth",
                StringComparison.Ordinal) &&
            sql.Contains(
                "> aptitude.maximum_total_growth",
                StringComparison.Ordinal),
            "v2 reconciliation selects only complete pets outside the new inclusive bracket");
        Check.True(
            sql.Contains(
                "midpoint_total_growth * 1000000",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                ") / 2, 2 ) AS midpoint_total_growth",
                StringComparison.Ordinal) &&
            sql.Contains(
                "midpoint.total_microunits / 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "mod(midpoint.total_microunits, 6)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UPDATE public.character_pet_stat_values existing",
                StringComparison.Ordinal),
            "v2 reconciliation assigns exact deterministic six-decimal midpoints");
        Check.True(
            !sql.Contains(
                "INSERT INTO public.character_pet_stat_values",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("random(", StringComparison.OrdinalIgnoreCase),
            "v2 reconciliation neither fills partial pets nor randomizes or deletes state");
        Check.True(
            sql.Contains(
                "DO $validate_pet_growth_policy_v2$",
                StringComparison.Ordinal) &&
            sql.Contains(
                "LEFT JOIN public.character_pet_stat_values stat",
                StringComparison.Ordinal) &&
            sql.Contains(
                "HAVING count(stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "count(DISTINCT stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE stat.base_growth_rate > 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "coalesce(sum(stat.base_growth_rate), 0)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal),
            "v2 migration fails before commit when any pet violates the complete positive bracket invariant");

        var godly = PetGrowthPolicy.All.Single(
            bracket => bracket.Aptitude == PetAptitude.Godly);
        Check.True(
            51.50m >= godly.MinimumTotalGrowth &&
            51.50m <= godly.MaximumTotalGrowth,
            "live test2 Godly midpoint remains inside v2 and is preserved");

        foreach (var bracket in PetGrowthPolicy.All)
        {
            var midpoint = decimal.Round(
                (
                    bracket.MinimumTotalGrowth +
                    bracket.MaximumTotalGrowth
                ) / 2m,
                2,
                MidpointRounding.AwayFromZero);
            var totalMicrounits =
                decimal.ToInt64(midpoint * 1_000_000m);
            var baseMicrounits = totalMicrounits / 6;
            var remainder = totalMicrounits % 6;
            var distribution = Enumerable.Range(1, 6)
                .Select(statCode =>
                    baseMicrounits +
                    (statCode <= remainder ? 1L : 0L))
                .ToArray();

            Check.Equal(
                totalMicrounits,
                distribution.Sum(),
                $"{bracket.Aptitude} v2 midpoint remains exact");
            Check.Equal(
                0L,
                totalMicrounits % 10_000L,
                $"{bracket.Aptitude} v2 target total has hundredth precision");
            Check.True(
                distribution.Max() - distribution.Min() <= 1L,
                $"{bracket.Aptitude} v2 allocation differs by at most one microunit");
        }

        Check.Equal(
            0.06m,
            RoundedMidpoint(PetAptitude.Weak),
            "Weak midpoint rounds from 0.055 to 0.06");
        Check.Equal(
            0.18m,
            RoundedMidpoint(PetAptitude.Fool),
            "Fool midpoint rounds from 0.175 to 0.18");
        Check.Equal(
            0.38m,
            RoundedMidpoint(PetAptitude.Cowish),
            "Cowish midpoint rounds from 0.375 to 0.38");
    }

    private static decimal RoundedMidpoint(PetAptitude aptitude)
    {
        var bracket = PetGrowthPolicy.All.Single(
            candidate => candidate.Aptitude == aptitude);
        return decimal.Round(
            (
                bracket.MinimumTotalGrowth +
                bracket.MaximumTotalGrowth
            ) / 2m,
            2,
            MidpointRounding.AwayFromZero);
    }

    private static PostgresSchemaMigration Find(string id) =>
        PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == id);
}
