using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ItemTemplateContentArchitectureChecks
{
    public const string CheckName =
        "Pinned PostgreSQL item-template content boundary";

    private static readonly string[] RuntimeConsumers =
    [
        "src/Godswar.Server/State/DeveloperMountCatalog.cs",
        "src/Godswar.Server/State/EquipmentEligibility.cs",
        "src/Godswar.Server/State/EquipmentSlots.cs",
        "src/Godswar.Server/State/GearEnhancementPlanner.cs",
        "src/Godswar.Server/State/GearMentorPlanner.cs",
        "src/Godswar.Server/State/MountCatalog.cs"
    ];

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260801_038_item_template_content_release");
        AssertContains(
            migration.Sql,
            "item_template_content_revisions",
            "item_template_content_definitions",
            "item_template_content_publication",
            "sealed_at",
            "guard_item_template_content_insert",
            "FOR UPDATE",
            "validate_item_template_content_publication",
            "trg_item_template_content_publication_no_delete");
        var projectionMigration =
            PostgresSchemaMigrationCatalog.All.Single(
                static migration => migration.Id ==
                    "20260801_040_item_runtime_projection_cutover");
        AssertContains(
            projectionMigration.Sql,
            "official_item_template_content",
            "sealed_at IS NOT NULL",
            "pg_depend",
            "public.item_templates",
            "CREATE OR REPLACE VIEW");
        var policyMigration =
            PostgresSchemaMigrationCatalog.All.Single(
                static migration => migration.Id ==
                    "20260801_041_item_policy_content_release");
        AssertContains(
            policyMigration.Sql,
            "manifest_version",
            "item_attribute_content_definitions",
            "equipment_rank_content_definitions",
            "holy_suit_effect_content_definitions",
            "official_item_attribute_content",
            "official_equipment_rank_content",
            "official_holy_suit_effect_content",
            "pg_depend");
        var materialMigration =
            PostgresSchemaMigrationCatalog.All.Single(
                static migration => migration.Id ==
                    "20260801_044_item_material_content_release");
        AssertContains(
            materialMigration.Sql,
            "material_policy_count",
            "manifest_version IN (1, 2, 3)",
            "manifest_version = 3",
            "item_material_content_definitions",
            "fk_item_material_content_template",
            "guard_item_material_content_insert",
            "FOR UPDATE",
            "material policy is incomplete",
            "NEW.material_policy_count",
            "official_item_material_content");
        var recipeMigration =
            PostgresSchemaMigrationCatalog.All.Single(
                static migration => migration.Id ==
                    "20260801_045_item_material_recipe_content_release");
        AssertContains(
            recipeMigration.Sql,
            "material_recipe_count",
            "manifest_version IN (1, 2, 3, 4)",
            "manifest_version = 4",
            "recipe_kind",
            "source_quantity",
            "target_quantity",
            "crystal_transform",
            "gem_piece_combination",
            "policy_kind = 'forging'",
            "NEW.material_recipe_count",
            "material recipes are incomplete",
            "manifest_version IN (2, 3, 4)",
            "manifest_version IN (3, 4)",
            "official_item_material_content");

        foreach (var relativePath in RuntimeConsumers)
        {
            Check.True(
                !Read(root, relativePath).Contains(
                    "ItemTemplateSeeds",
                    StringComparison.Ordinal),
                $"{relativePath} receives published item content");
        }

        var publisher = Read(
            root,
            "src/Godswar.Server/Infrastructure/Items/" +
            "PostgresItemTemplateBaselinePublisher.cs");
        Check.True(
            publisher.Contains(
                "ItemTemplateSeeds.All",
                StringComparison.Ordinal) &&
            publisher.IndexOf(
                "TryReadPublishedRevisionAsync",
                StringComparison.Ordinal) <
            publisher.IndexOf(
                "UpsertReviewedBaselineAsync",
                StringComparison.Ordinal),
            "compiled item seeds exist only behind a publication-absent check");

        AssertNoMutableRuntimeTemplateReads(root);
        AssertNoCompiledMaterialRuntimeConsumers(root);
        AssertNoUnpinnedCharacterItemViews(root);

        var projection = Read(
            root,
            "src/Godswar.Server/State/" +
            "PostgresCharacterItemProjectionSql.cs");
        Check.True(
            projection.Contains(
                "item_template_content_definitions",
                StringComparison.Ordinal) &&
            projection.Contains(
                "@itemContentRevision",
                StringComparison.Ordinal) &&
            !projection.Contains(
                "JOIN item_templates",
                StringComparison.OrdinalIgnoreCase),
            "character item projections bind the exact pinned revision");

        var runtimeProjection = Read(
            root,
            "src/Godswar.Server/State/" +
            "PostgresCharacterRuntimeItemProjectionSql.cs");
        var rankProjection = Read(
            root,
            "src/Godswar.Server/State/" +
            "PostgresCharacterRuntimeItemRankSql.cs");
        AssertContains(
            runtimeProjection,
            "@itemContentRevision",
            "item_template_content_definitions",
            "item_attribute_content_definitions",
            "holy_suit_effect_content_definitions");
        AssertContains(
            rankProjection,
            "@itemContentRevision",
            "item_template_content_definitions",
            "equipment_rank_content_definitions");

        var loader = Read(
            root,
            "src/Godswar.Server/Infrastructure/Items/" +
            "PostgresItemTemplateCatalogLoader.cs");
        Check.True(
            loader.Contains(
                "IsolationLevel.RepeatableRead",
                StringComparison.Ordinal) &&
            loader.Contains(
                "PinnedItemTemplateCatalog.Create",
                StringComparison.Ordinal) &&
            loader.Contains(
                "publication.ManifestVersion != 4",
                StringComparison.Ordinal) &&
            loader.Contains(
                "ReadMaterialPoliciesAsync",
                StringComparison.Ordinal),
            "loader pins one complete v4 revision and validates its hash");

        AssertPinnedSnapshotIsImmutable();
        return Task.CompletedTask;
    }

    private static void AssertNoMutableRuntimeTemplateReads(string root)
    {
        var sourceRoot = Path.Combine(root, "src", "Godswar.Server");
        string[] mutableTables =
        [
            "item_templates",
            "item_attribute_templates",
            "equipment_rank_rules",
            "holy_suit_effect_templates"
        ];
        var offenders = Directory.EnumerateFiles(
                sourceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => mutableTables.Any(table =>
                File.ReadAllText(path).Contains(
                    table,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path)
                .Replace('\\', '/'))
            .Where(path =>
                !path.StartsWith(
                    "src/Godswar.Server/State/DatabaseMigrations/",
                    StringComparison.Ordinal) &&
                !path.StartsWith(
                    "src/Godswar.Server/Infrastructure/Items/" +
                    "PostgresItemTemplateBaselinePublisher",
                    StringComparison.Ordinal) &&
                !path.EndsWith(
                    "PostgresRelationalContentBaselineBootstrapper.cs",
                    StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            offenders.Length == 0,
            "mutable item staging/policy tables have no runtime readers: " +
            string.Join(", ", offenders));
    }

    private static void AssertNoUnpinnedCharacterItemViews(string root)
    {
        var sourceRoot = Path.Combine(root, "src", "Godswar.Server");
        string[] compatibilityViews =
        [
            "character_rank_summary",
            "character_stat_summary"
        ];
        var offenders = Directory.EnumerateFiles(
                sourceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => compatibilityViews.Any(view =>
                File.ReadAllText(path).Contains(
                    view,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path)
                .Replace('\\', '/'))
            .Where(path => !path.StartsWith(
                "src/Godswar.Server/State/DatabaseMigrations/",
                StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            offenders.Length == 0,
            "runtime character item projections do not follow mutable " +
            "official pointers through compatibility views: " +
            string.Join(", ", offenders));
    }

    private static void AssertNoCompiledMaterialRuntimeConsumers(string root)
    {
        string[] compiledCatalogs =
        [
            "ForgingMaterialCatalog",
            "GearEnhancementMaterialCatalog",
            "GearMentorMaterialCatalog"
        ];
        var sourceRoot = Path.Combine(root, "src", "Godswar.Server");
        var offenders = Directory.EnumerateFiles(
                sourceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => compiledCatalogs.Any(catalog =>
                File.ReadAllText(path).Contains(
                    catalog,
                    StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path)
                .Replace('\\', '/'))
            .Where(path =>
                !path.StartsWith(
                    "src/Godswar.Server/Infrastructure/Items/" +
                    "PostgresItemTemplateBaselinePublisher",
                    StringComparison.Ordinal) &&
                path is not
                    "src/Godswar.Server/State/ForgingMaterialCatalog.cs" and not
                    "src/Godswar.Server/State/GearEnhancementMaterialCatalog.cs" and not
                    "src/Godswar.Server/State/GearMentorMaterialCatalog.cs" and not
                    "src/Godswar.Server/State/MaterialItemTemplateSeedExtensions.cs")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Check.True(
            offenders.Length == 0,
            "runtime material policy uses only the pinned v4 catalog: " +
            string.Join(", ", offenders));
    }

    private static void AssertPinnedSnapshotIsImmutable()
    {
        short[] classes = [0, 1];
        var source = new List<ItemTemplateDefinition>
        {
            new(
                1,
                "weapon",
                "TestWeapon",
                "Test Weapon",
                EquipmentSlots.Weapon,
                classes,
                1,
                120,
                1,
                0,
                "test.gwo",
                "0,0",
                "{\"Attack\":\"1\"}")
        };
        var pinned = PinnedItemTemplateCatalog.Create(
            "architecture-check",
            source);
        classes[0] = 99;
        source.Clear();
        Check.True(
            pinned.All.Count == 1 &&
            pinned.All[0].ClassIds[0] == 0,
            "pinned item content owns defensive immutable copies");

        try
        {
            _ = PinnedItemTemplateCatalog.Create(
                "architecture-check",
                pinned.All,
                new string('0', 64));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Pinned item content accepted a mismatched revision hash.");
    }

    private static void AssertContains(
        string value,
        params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            Check.True(
                value.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"item-template migration contains {fragment}");
        }
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for item-content checks.");
    }
}
