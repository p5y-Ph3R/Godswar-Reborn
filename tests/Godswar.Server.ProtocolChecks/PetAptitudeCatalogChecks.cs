using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetAptitudeCatalogChecks
{
    public static Task RunAsync()
    {
        var expectedNames = new[]
        {
            "Weak",
            "Fool",
            "Cowish",
            "Moderate",
            "Rational",
            "Calm",
            "Grumpy",
            "Brave",
            "Zealous",
            "Smart",
            "Overbearing",
            "Ferocious",
            "Almighty",
            "Godly",
            "Celestial",
            "Transcendent"
        };

        Check.Equal(
            PetAptitudeCatalog.Count,
            PetAptitudeCatalog.All.Count,
            "all authoritative pet aptitude tiers are cataloged");
        Check.True(
            PetAptitudeCatalog.All
                .Select(static definition => (int)definition.Value)
                .SequenceEqual(Enumerable.Range(1, PetAptitudeCatalog.Count)),
            "pet aptitude values remain contiguous from 1 through 16");
        Check.True(
            PetAptitudeCatalog.All
                .Select(static definition => definition.DisplayName)
                .SequenceEqual(expectedNames),
            "pet aptitude names preserve the client ladder and named extensions");
        Check.True(
            PetAptitudeCatalog.TryGet(PetAptitude.Calm, out var calm) &&
            calm.Value == 6 &&
            PetAptitudeCatalog.TryGet(PetAptitude.Grumpy, out var grumpy) &&
            grumpy.Value == 7 &&
            PetAptitudeCatalog.TryGet(PetAptitude.Brave, out var brave) &&
            brave.Value == 8 &&
            PetAptitudeCatalog.TryGet(PetAptitude.Zealous, out var zealous) &&
            zealous.Value == 9 &&
            PetAptitudeCatalog.TryGet(PetAptitude.Smart, out var smart) &&
            smart.Value == 10 &&
            !calm.IsServerExtension &&
            !grumpy.IsServerExtension &&
            !brave.IsServerExtension &&
            !zealous.IsServerExtension &&
            !smart.IsServerExtension,
            "aptitudes 6-10 retain their corrected fixed-ID names");
        Check.True(
            PetAptitudeCatalog.TryGet(PetAptitude.Ferocious, out var ferocious) &&
            ferocious.Value == 12 &&
            !ferocious.IsServerExtension,
            "Ferocious remains stock aptitude 12");
        Check.True(
            PetAptitudeCatalog.TryGet(PetAptitude.Godly, out var godly) &&
            godly.Value == 14 &&
            !godly.IsServerExtension,
            "Godly remains stock aptitude 14");
        Check.True(
            PetAptitudeCatalog.TryGet(15, out var celestial) &&
            celestial.DisplayName == "Celestial" &&
            celestial.IsServerExtension,
            "aptitude 15 is the Celestial extension");
        Check.True(
            PetAptitudeCatalog.TryGet(16, out var transcendent) &&
            transcendent.DisplayName == "Transcendent" &&
            transcendent.IsServerExtension,
            "aptitude 16 is the Transcendent extension");
        Check.True(
            !PetAptitudeCatalog.TryGet((short)0, out _) &&
            !PetAptitudeCatalog.TryGet(17, out _),
            "values outside the authoritative aptitude ladder are rejected");

        var migration = PostgresSchemaMigrationCatalog.All.Single(candidate =>
            candidate.Id == "20260728_012_pet_aptitude_catalog");
        Check.True(
            migration.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS public.pet_aptitude_templates",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "(15, 'PETAPTITUDE15', 'Celestial', true",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "(16, 'PETAPTITUDE16', 'Transcendent', true",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ALTER COLUMN aptitude SET NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "fk_character_pets_aptitude_templates",
                StringComparison.Ordinal),
            "database migration seeds and references the authoritative ladder");

        return Task.CompletedTask;
    }
}
