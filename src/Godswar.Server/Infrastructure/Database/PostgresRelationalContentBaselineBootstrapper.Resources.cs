namespace Godswar.Server.Infrastructure.Database;

internal static partial class PostgresRelationalContentBaselineBootstrapper
{
    private static readonly BaselineResource ItemAttributesResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.005_item_attributes.sql",
        "2CE8B2539589D3666599C87B50106CE6D52C1C0576EDA227D9551045197E3EE0");

    private static readonly BaselineResource SkillsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.006_skills_and_talents.sql",
        "47CCE1F6514EEA9A969638AAE25BA8B21AE5098A2412A88E54EDF791AD8EF505");

    private static readonly BaselineResource NpcsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.007_npcs.sql",
        "3CE8A37430F0A0E10CF607781790B19EB26BD9F25872AC7CC11459F1BFC824F8");

    private static readonly BaselineResource MapsResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.008_maps.sql",
        "A287CB08131DBB55E3A834E28789D38819A474FEEE72E2586ACF31C922BFF3BE");

    private static readonly BaselineResource MonstersResource = new(
        "Godswar.Server.Infrastructure.DatabaseBaselines.009_monsters.sql",
        "65AADCEED4C30291D4C2250F4AA2907ED2447644A66A6D454805E7EE5687E9A5");
}
