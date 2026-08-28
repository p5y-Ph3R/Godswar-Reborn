using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaTitleAttributePolicyChecks
{
    public const string CheckName =
        "Medusa strongest-owned title attributes";

    public static Task RunAsync()
    {
        var baseline = new CharacterStats
        {
            CharacterId = 71,
            AccountId = 81,
            Name = "TitleAttributes",
            Level = 120,
            PhysicalAttack = 10_000,
            MagicAttack = 20_000,
            PhysicalDefense = 30_000,
            MagicDefense = 40_000,
            Hit = 777,
            Dodge = 555
        };
        var character = new GameCharacter
        {
            Id = baseline.CharacterId,
            AccountId = baseline.AccountId,
            Name = baseline.Name,
            Level = baseline.Level,
            CalculatedStats = baseline
        };

        Check.True(
            ReferenceEquals(
                baseline,
                CharacterStats.FromCharacter(character)),
            "a character without a Medusa title keeps exact base stats");

        var expected = new Dictionary<uint, int>
        {
            [5009] = 100,
            [5010] = 200,
            [5011] = 300,
            [5154] = 400,
            [5153] = 500,
            [5152] = 600
        };
        foreach (var (titleId, basisPoints) in expected)
        {
            character.OwnedTitleIds = [titleId];
            var effective = CharacterStats.FromCharacter(character);
            Check.Equal(
                Scale(baseline.PhysicalAttack, basisPoints),
                effective.PhysicalAttack,
                $"title {titleId} scales physical attack");
            Check.Equal(
                Scale(baseline.MagicAttack, basisPoints),
                effective.MagicAttack,
                $"title {titleId} scales magical attack");
            Check.Equal(
                Scale(baseline.PhysicalDefense, basisPoints),
                effective.PhysicalDefense,
                $"title {titleId} scales physical defense");
            Check.Equal(
                Scale(baseline.MagicDefense, basisPoints),
                effective.MagicDefense,
                $"title {titleId} scales magical defense");
            Check.Equal(
                baseline.Hit,
                effective.Hit,
                $"title {titleId} does not alter unauthored attributes");
        }

        character.OwnedTitleIds = [5009, 5011, 5154, 5152];
        character.SelectedTitleId = 5009;
        var strongest = CharacterStats.FromCharacter(character);
        Check.Equal(
            10_600,
            strongest.PhysicalAttack,
            "owned title attributes do not stack and ignore display selection");
        Check.True(
            MedusaTitleAttributePolicy.TryResolveStrongestOwned(
                character.OwnedTitleIds,
                out var definition) &&
            definition.ClientTitleId == 5152 &&
            definition.Attributes.StrengthBasisPoints == 600,
            "Heir of Perseus is the strongest owned definition");

        var combat = CombatCharacterStatsAdapter.FromCharacter(character);
        var target = CombatCharacterStatsAdapter.ToTarget(
            character.Level,
            strongest);
        Check.Equal(
            strongest.PhysicalAttack,
            combat.PhysicalAttack,
            "authoritative outgoing combat consumes the title bonus");
        Check.Equal(
            strongest.PhysicalDefense,
            target.PhysicalDefense,
            "authoritative incoming combat consumes the title bonus");
        return Task.CompletedTask;
    }

    private static int Scale(int value, int basisPoints) =>
        checked((value * (10_000 + basisPoints) + 5_000) / 10_000);
}
