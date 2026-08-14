namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog
{
    private static void ValidateMagicJadeAppearances(
        IReadOnlyList<PetSpeciesContentDefinition> species,
        IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition> factors)
    {
        var ordered = species.OrderBy(static value => value.SpeciesId).ToArray();
        if (ordered.Length != 45 ||
            ordered.Select(static value => value.MagicJadeItemId)
                .Distinct().Count() != ordered.Length)
        {
            throw new InvalidOperationException(
                "Published Magic Jade appearances are incomplete or ambiguous.");
        }

        var factorsBySpecies = factors.ToDictionary(
            static value => value.SpeciesId);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var expectedSpecies = checked((short)(index + 1));
            var expectedItemId = checked((uint)(11050 + index));
            if (value.MagicJadeItemId != expectedItemId ||
                value.SpeciesId != expectedSpecies ||
                string.IsNullOrWhiteSpace(value.DisplayName) ||
                !factorsBySpecies.ContainsKey(value.SpeciesId))
            {
                throw new InvalidOperationException(
                    $"Published Magic Jade appearance {value.MagicJadeItemId} is invalid.");
            }
        }
    }
}
