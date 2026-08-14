using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetTalentCatalogChecks
{
    public static Task RunAsync()
    {
        var expected = new[]
        {
            new PetTalentDefinition(
                PetTalentKind.RandomEvent,
                "Random Event",
                1),
            new PetTalentDefinition(
                PetTalentKind.QuestDispatch,
                "Quest Dispatch",
                2),
            new PetTalentDefinition(
                PetTalentKind.Work,
                "Work",
                4),
            new PetTalentDefinition(
                PetTalentKind.Healing,
                "Healing",
                8),
            new PetTalentDefinition(
                PetTalentKind.Merge,
                "Merge",
                16)
        };

        Check.True(
            PetTalentCatalog.All.SequenceEqual(expected),
            "stock pet talents retain their exact innate mask bits");
        Check.Equal(
            (byte)31,
            PetTalentCatalog.SupportedMask,
            "five implemented talent bits compose the supported mask");
        Check.Equal(
            (byte)32,
            PetTalentCatalog.ReservedMaskBit,
            "the sixth native talent bit remains reserved");
        Check.Equal(
            (byte)63,
            PetTalentCatalog.NativeMask,
            "the complete native six-bit field is documented");

        foreach (var definition in expected)
        {
            Check.True(
                PetTalentCatalog.TryGet(
                    definition.Talent,
                    out var byTalent) &&
                byTalent == definition,
                $"{definition.DisplayName} resolves by talent");
            Check.True(
                PetTalentCatalog.TryGetByMaskBit(
                    definition.MaskBit,
                    out var byBit) &&
                byBit == definition,
                $"{definition.DisplayName} resolves by mask bit");
        }

        foreach (var itemId in Enumerable.Range(10110, 5))
        {
            Check.True(
                PetItemCatalog.TryGetCore(
                    checked((uint)itemId),
                    out var item) &&
                item.Purpose == PetItemPurpose.LegacyTalentArtifact,
                $"legacy talent-stick item {itemId} cannot author a talent");
        }

        Check.True(
            PetTalentCatalog.IsSupportedMask(0) &&
            PetTalentCatalog.IsSupportedMask(31),
            "empty and complete implemented talent masks are valid");
        Check.True(
            !PetTalentCatalog.IsSupportedMask(32) &&
            !PetTalentCatalog.IsSupportedMask(63),
            "the reserved talent bit cannot be treated as implemented");
        Check.True(
            !PetTalentCatalog.TryGetByMaskBit(0, out _) &&
            !PetTalentCatalog.TryGetByMaskBit(3, out _) &&
            !PetTalentCatalog.TryGetByMaskBit(32, out _) &&
            !PetTalentCatalog.TryGet((PetTalentKind)99, out _),
            "unknown, combined, and reserved talent identities fail closed");

        return Task.CompletedTask;
    }
}
