using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.State;

/// <summary>
/// Resolves permanent Medusa-title combat attributes from durable ownership.
/// Display selection is intentionally irrelevant: owning several titles never
/// stacks their bonuses, and the strongest authored definition always wins.
/// </summary>
internal static class MedusaTitleAttributePolicy
{
    public static CharacterStats ApplyStrongestOwned(
        IReadOnlyCollection<uint> ownedTitleIds,
        CharacterStats baseline)
    {
        ArgumentNullException.ThrowIfNull(ownedTitleIds);
        ArgumentNullException.ThrowIfNull(baseline);
        if (!TryResolveStrongestOwned(
                ownedTitleIds,
                out var definition))
        {
            return baseline;
        }

        var attributes = definition.Attributes;
        return baseline.WithCoreCombatAttributeBonus(
            attributes.PhysicalAttackBasisPoints,
            attributes.MagicAttackBasisPoints,
            attributes.PhysicalDefenseBasisPoints,
            attributes.MagicDefenseBasisPoints);
    }

    public static bool TryResolveStrongestOwned(
        IReadOnlyCollection<uint> ownedTitleIds,
        out MedusaTitleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(ownedTitleIds);
        definition = default;
        var strongest = 0;
        foreach (var ownedTitleId in ownedTitleIds)
        {
            foreach (var candidate in MedusaTitleAwardPolicy.Titles)
            {
                if (candidate.ClientTitleId != ownedTitleId ||
                    candidate.Attributes.StrengthBasisPoints <= strongest)
                {
                    continue;
                }

                definition = candidate;
                strongest = candidate.Attributes.StrengthBasisPoints;
            }
        }

        return strongest > 0;
    }
}
