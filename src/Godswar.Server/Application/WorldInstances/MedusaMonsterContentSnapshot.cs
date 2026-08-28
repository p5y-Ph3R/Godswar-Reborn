using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaMonsterRule(
    MedusaEncounterDifficulty Difficulty,
    string TemplateAlias,
    uint Level,
    uint MaximumHealth,
    int Score,
    int MovementSpeedBasisPoints,
    int? CorpseWithoutLootMilliseconds,
    int? CorpseWithLootMilliseconds,
    int PetExperience);

internal readonly record struct MedusaMonsterLootRule(
    MedusaEncounterDifficulty Difficulty,
    string TemplateAlias,
    int LootIndex,
    uint ItemId,
    int ChanceBasisPoints,
    int MinimumQuantity,
    int MaximumQuantity);

internal readonly record struct MedusaRolledLoot(
    int LootIndex,
    uint ItemId,
    int Quantity);

/// <summary>
/// Process-pinned Medusa monster and drop configuration loaded from PostgreSQL.
/// A server-management page can safely edit the backing rows; a restart pins
/// one coherent revision for newly created instances.
/// </summary>
internal sealed class MedusaMonsterContentSnapshot
{
    private readonly FrozenDictionary<
        (MedusaEncounterDifficulty Difficulty, string TemplateAlias),
        MedusaMonsterRule> _monsters;
    private readonly FrozenDictionary<
        (MedusaEncounterDifficulty Difficulty, string TemplateAlias),
        MedusaMonsterLootRule[]> _loot;

    public MedusaMonsterContentSnapshot(
        IReadOnlyCollection<MedusaMonsterRule> monsters,
        IReadOnlyCollection<MedusaMonsterLootRule> loot)
    {
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(loot);
        Validate(monsters, loot);
        _monsters = monsters.ToFrozenDictionary(
            static rule => (rule.Difficulty, rule.TemplateAlias));
        _loot = loot
            .GroupBy(static rule =>
                (rule.Difficulty, rule.TemplateAlias))
            .ToFrozenDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static rule => rule.LootIndex)
                    .ToArray());
    }

    public IReadOnlyCollection<MedusaMonsterRule> Monsters =>
        _monsters.Values;

    public IReadOnlyCollection<MedusaMonsterLootRule> Loot =>
        _loot.Values.SelectMany(static rules => rules).ToArray();

    public bool TryGetMonster(
        MedusaEncounterDifficulty difficulty,
        string? templateAlias,
        out MedusaMonsterRule rule)
    {
        if (string.IsNullOrWhiteSpace(templateAlias))
        {
            rule = default;
            return false;
        }

        return _monsters.TryGetValue(
            (difficulty, templateAlias),
            out rule);
    }

    public IReadOnlyList<MedusaRolledLoot> RollLoot(
        MedusaEncounterDifficulty difficulty,
        string templateAlias,
        Guid deathEventId)
    {
        if (deathEventId == Guid.Empty ||
            !_loot.TryGetValue((difficulty, templateAlias), out var rules))
        {
            return [];
        }

        var rolled = new List<MedusaRolledLoot>(rules.Length);
        Span<byte> input = stackalloc byte[20];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        deathEventId.TryWriteBytes(input[..16]);
        foreach (var rule in rules)
        {
            BitConverter.TryWriteBytes(input[16..], rule.LootIndex);
            SHA256.HashData(input, hash);
            var chanceRoll = BitConverter.ToUInt32(hash) % 10_000;
            if (chanceRoll >= rule.ChanceBasisPoints)
            {
                continue;
            }

            var range = checked(
                rule.MaximumQuantity - rule.MinimumQuantity + 1);
            var quantity = checked(
                rule.MinimumQuantity +
                (int)(BitConverter.ToUInt32(hash[4..]) % range));
            rolled.Add(new(rule.LootIndex, rule.ItemId, quantity));
        }
        return rolled.AsReadOnly();
    }

    private static void Validate(
        IReadOnlyCollection<MedusaMonsterRule> monsters,
        IReadOnlyCollection<MedusaMonsterLootRule> loot)
    {
        var expected = Enum.GetValues<MedusaEncounterDifficulty>()
            .SelectMany(difficulty => MedusaIslandRosterPolicy.Templates
                .Select(template => (difficulty, template.Alias)))
            .ToHashSet();
        var actual = monsters.Select(static rule =>
                (rule.Difficulty, rule.TemplateAlias))
            .ToHashSet();
        if (!actual.SetEquals(expected) || actual.Count != monsters.Count)
        {
            throw new InvalidDataException(
                "Medusa monster rules must cover every difficulty/template exactly once.");
        }

        foreach (var rule in monsters)
        {
            if (!Enum.IsDefined(rule.Difficulty) ||
                string.IsNullOrWhiteSpace(rule.TemplateAlias) ||
                rule.Level is < 1 or > 200 ||
                rule.MaximumHealth == 0 ||
                rule.Score is < 0 or > 10_000 ||
                rule.MovementSpeedBasisPoints is < 1 or > 10_000 ||
                rule.PetExperience is < 0 or > 100_000_000 ||
                !ValidCorpseDelay(rule.CorpseWithoutLootMilliseconds) ||
                !ValidCorpseDelay(rule.CorpseWithLootMilliseconds) ||
                rule.CorpseWithLootMilliseconds is { } withLoot &&
                rule.CorpseWithoutLootMilliseconds is { } withoutLoot &&
                withLoot < withoutLoot)
            {
                throw new InvalidDataException(
                    $"Medusa monster rule {rule.Difficulty}/{rule.TemplateAlias} is invalid.");
            }
        }

        var duplicateLoot = loot.GroupBy(static rule =>
                (rule.Difficulty, rule.TemplateAlias, rule.LootIndex))
            .Any(static group => group.Count() != 1);
        if (duplicateLoot)
        {
            throw new InvalidDataException("Medusa loot indexes are duplicated.");
        }
        foreach (var rule in loot)
        {
            if (!actual.Contains((rule.Difficulty, rule.TemplateAlias)) ||
                rule.LootIndex is < 0 or >= 32 ||
                rule.ItemId == 0 ||
                rule.ChanceBasisPoints is < 1 or > 10_000 ||
                rule.MinimumQuantity is < 1 or > 255 ||
                rule.MaximumQuantity < rule.MinimumQuantity ||
                rule.MaximumQuantity > 255)
            {
                throw new InvalidDataException("A Medusa loot rule is invalid.");
            }
        }
    }

    private static bool ValidCorpseDelay(int? milliseconds) =>
        milliseconds is null or >= 1_000 and <= 300_000;
}

internal static class MedusaMonsterContentCatalog
{
    private static MedusaMonsterContentSnapshot? _current;

    public static MedusaMonsterContentSnapshot Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException(
            "The database-owned Medusa monster content has not been loaded.");

    public static void Install(MedusaMonsterContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
