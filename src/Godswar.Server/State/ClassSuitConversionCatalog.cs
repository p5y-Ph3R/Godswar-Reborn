namespace Godswar.Server.State;

internal enum ClassSuitTier : byte
{
    Common = 0,
    TierI = 1,
    TierII = 2,
    TierIII = 3,
    TierIV = 4
}

internal sealed record ClassSuitConversionBranch(
    string Key,
    byte Profession,
    IReadOnlyList<uint> CommonItemIds,
    uint TierIItemId,
    uint TierIIItemId,
    uint TierIIIItemId,
    uint TierIVItemId,
    int InsigniaCost)
{
    public uint ItemIdFor(ClassSuitTier tier) => tier switch
    {
        ClassSuitTier.TierI => TierIItemId,
        ClassSuitTier.TierII => TierIIItemId,
        ClassSuitTier.TierIII => TierIIIItemId,
        ClassSuitTier.TierIV => TierIVItemId,
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
}

internal sealed record ClassSuitForwardRule(
    ClassSuitConversionBranch Branch,
    ClassSuitTier SourceTier,
    ClassSuitTier TargetTier,
    uint TargetItemId,
    uint InsigniaItemId,
    int InsigniaQuantity);

internal sealed record ClassSuitReverseRule(
    ClassSuitConversionBranch Branch,
    ClassSuitTier SourceTier,
    uint CommonItemId,
    IReadOnlyList<ClassSuitRefundDefinition> Refunds);

internal sealed record ClassSuitRefundDefinition(
    uint ItemId,
    int Quantity);

/// <summary>
/// The 62 shipped Class Suit equipment branches. The mapping comes from the
/// original client item table and dialogue-37 slot/cost descriptions. It is
/// deliberately explicit about accepted common inputs, because an item ID is
/// client-controlled and its visual slot is not conversion authority.
/// </summary>
internal static class ClassSuitConversionCatalog
{
    public const uint PromotionalInsigniaI = 3931;
    public const uint PromotionalInsigniaII = 3962;
    public const uint PromotionalInsigniaIII = 14069;
    public const uint PromotionalInsigniaIV = 14073;
    public const int BranchCount = 62;

    private static readonly IReadOnlyList<ClassSuitConversionBranch> BranchTable =
        Array.AsReadOnly(CreateBranches().ToArray());

    private static readonly IReadOnlyDictionary<(byte Profession, uint ItemId),
        (ClassSuitConversionBranch Branch, ClassSuitTier Tier)> SuitIndex =
        CreateSuitIndex();

    private static readonly IReadOnlyDictionary<uint,
        (ClassSuitConversionBranch Branch, ClassSuitTier Tier)> AnySuitIndex =
        SuitIndex.ToDictionary(
            static value => value.Key.ItemId,
            static value => value.Value);

    public static IReadOnlyList<ClassSuitConversionBranch> Branches => BranchTable;

    static ClassSuitConversionCatalog()
    {
        ValidateCatalog();
    }

    public static bool TryResolveForward(
        byte profession,
        uint sourceItemId,
        ClassSuitTier targetTier,
        out ClassSuitForwardRule rule)
    {
        rule = default!;
        if (profession > 3 || targetTier is < ClassSuitTier.TierI or > ClassSuitTier.TierIV)
        {
            return false;
        }

        var sourceTier = targetTier - 1;
        ClassSuitConversionBranch? branch;
        if (sourceTier == ClassSuitTier.Common)
        {
            branch = BranchTable.FirstOrDefault(value =>
                value.Profession == profession &&
                value.CommonItemIds.Contains(sourceItemId));
        }
        else
        {
            branch = BranchTable.FirstOrDefault(value =>
                value.Profession == profession &&
                value.ItemIdFor(sourceTier) == sourceItemId);
        }

        if (branch is null)
        {
            return false;
        }

        rule = new ClassSuitForwardRule(
            branch,
            sourceTier,
            targetTier,
            branch.ItemIdFor(targetTier),
            InsigniaFor(targetTier),
            branch.InsigniaCost);
        return true;
    }

    public static bool TryResolveReverse(
        byte profession,
        uint sourceItemId,
        out ClassSuitReverseRule rule)
    {
        rule = default!;
        if (!TryResolveSuit(
                profession,
                sourceItemId,
                out var branch,
                out var tier) ||
            tier is < ClassSuitTier.TierI or > ClassSuitTier.TierIV)
        {
            return false;
        }

        var refunds = Enumerable
            .Range((int)ClassSuitTier.TierI, (int)tier)
            .Select(targetTier => new ClassSuitRefundDefinition(
                InsigniaFor((ClassSuitTier)targetTier),
                branch.InsigniaCost))
            .ToArray();
        rule = new ClassSuitReverseRule(
            branch,
            tier,
            ReverseCommonItemId(branch),
            Array.AsReadOnly(refunds));
        return true;
    }

    public static bool TryResolveSuit(
        byte profession,
        uint itemId,
        out ClassSuitConversionBranch branch,
        out ClassSuitTier tier)
    {
        if (SuitIndex.TryGetValue((profession, itemId), out var value))
        {
            branch = value.Branch;
            tier = value.Tier;
            return true;
        }

        branch = default!;
        tier = default;
        return false;
    }

    public static bool TryResolveSuit(
        uint itemId,
        out ClassSuitConversionBranch branch,
        out ClassSuitTier tier)
    {
        if (AnySuitIndex.TryGetValue(itemId, out var value))
        {
            branch = value.Branch;
            tier = value.Tier;
            return true;
        }

        branch = default!;
        tier = default;
        return false;
    }

    public static bool IsTierIII(uint itemId) =>
        BranchTable.Any(branch => branch.TierIIIItemId == itemId);

    public static bool IsTierThreeOrFourItem(uint itemId) =>
        AnySuitIndex.TryGetValue(itemId, out var value) &&
        value.Tier is ClassSuitTier.TierIII or ClassSuitTier.TierIV;

    public static uint InsigniaFor(ClassSuitTier targetTier) => targetTier switch
    {
        ClassSuitTier.TierI => PromotionalInsigniaI,
        ClassSuitTier.TierII => PromotionalInsigniaII,
        ClassSuitTier.TierIII => PromotionalInsigniaIII,
        ClassSuitTier.TierIV => PromotionalInsigniaIV,
        _ => throw new ArgumentOutOfRangeException(nameof(targetTier))
    };

    private static uint ReverseCommonItemId(
        ClassSuitConversionBranch branch)
    {
        // Gloves and boots accept both shipped common families. Their Class
        // Suit ID cannot remember the input, so reverse deterministically to
        // the physical family for professions 0/1 and magic family for 2/3.
        return branch.CommonItemIds.Count == 1 || branch.Profession < 2
            ? branch.CommonItemIds[0]
            : branch.CommonItemIds[1];
    }

    private static List<ClassSuitConversionBranch> CreateBranches()
    {
        var branches = new List<ClassSuitConversionBranch>(BranchCount);
        AddFamily(branches, "weapon", 1013, 1032, 3, profession: 0);
        AddFamily(branches, "weapon", 1413, 1432, 3, profession: 1);
        AddFamily(branches, "weapon", 1713, 1732, 3, profession: 2);
        AddFamily(branches, "weapon", 1813, 1832, 3, profession: 3);

        AddFamily(branches, "shield", 2013, 2031, 1, profession: 0);
        AddFamily(branches, "shield", 2013, 2041, 1, profession: 2);

        AddProfessionFamily(branches, "armor", [2113], 2131, 3);
        AddProfessionFamily(branches, "cloth", [2213], 2231, 3);

        AddFamily(branches, "head-offensive", 2313, 2331, 3, profession: 0);
        AddFamily(branches, "head-offensive", 2313, 2341, 3, profession: 1);
        AddFamily(branches, "head-offensive", 2413, 2431, 3, profession: 2);
        AddFamily(branches, "head-offensive", 2413, 2441, 3, profession: 3);

        AddProfessionFamily(branches, "head-firm", [2513], 2531, 3);
        AddProfessionFamily(branches, "cuff-physical", [2613], 2631, 2);
        AddProfessionFamily(branches, "cuff-magic", [3313], 3331, 2);
        AddProfessionFamily(branches, "legs-physical", [2713], 2731, 2);
        AddProfessionFamily(branches, "legs-magic", [3413], 3431, 2);
        AddProfessionFamily(branches, "gloves", [2813, 3513], 2831, 1);
        AddProfessionFamily(branches, "boots", [2913, 3613], 2931, 1);
        AddProfessionFamily(branches, "girdle", [3013], 3031, 2);
        AddProfessionFamily(branches, "amulet", [3113], 3131, 1);

        for (byte profession = 0; profession < 4; profession++)
        {
            var targetBase = checked((uint)(3230 + profession * 10));
            AddBranch(
                branches,
                $"ring-hp-p{profession}",
                profession,
                [3209],
                [targetBase, targetBase + 1, targetBase + 2, targetBase + 6],
                cost: 2);
            AddBranch(
                branches,
                $"ring-mp-p{profession}",
                profession,
                [3210],
                [targetBase + 3, targetBase + 4, targetBase + 5, targetBase + 7],
                cost: 2);
        }

        return branches;
    }

    private static void AddProfessionFamily(
        List<ClassSuitConversionBranch> branches,
        string key,
        uint[] commonItemIds,
        uint firstTierIItemId,
        int cost)
    {
        for (byte profession = 0; profession < 4; profession++)
        {
            AddBranch(
                branches,
                $"{key}-p{profession}",
                profession,
                commonItemIds,
                [
                    firstTierIItemId + checked((uint)(profession * 10)),
                    firstTierIItemId + checked((uint)(profession * 10 + 1)),
                    firstTierIItemId + checked((uint)(profession * 10 + 2)),
                    firstTierIItemId + checked((uint)(profession * 10 + 3))
                ],
                cost);
        }
    }

    private static void AddFamily(
        List<ClassSuitConversionBranch> branches,
        string key,
        uint commonItemId,
        uint tierIItemId,
        int cost,
        byte profession)
    {
        AddBranch(
            branches,
            $"{key}-p{profession}",
            profession,
            [commonItemId],
            [tierIItemId, tierIItemId + 1, tierIItemId + 2, tierIItemId + 3],
            cost);
    }

    private static void AddBranch(
        List<ClassSuitConversionBranch> branches,
        string key,
        byte profession,
        uint[] commonItemIds,
        uint[] tiers,
        int cost)
    {
        if (tiers.Length != 4)
        {
            throw new ArgumentException("A Class Suit branch requires four tiers.", nameof(tiers));
        }

        branches.Add(new ClassSuitConversionBranch(
            key,
            profession,
            Array.AsReadOnly(commonItemIds.ToArray()),
            tiers[0],
            tiers[1],
            tiers[2],
            tiers[3],
            cost));
    }

    private static IReadOnlyDictionary<(byte, uint),
        (ClassSuitConversionBranch, ClassSuitTier)> CreateSuitIndex()
    {
        var index = new Dictionary<(byte, uint),
            (ClassSuitConversionBranch, ClassSuitTier)>();
        foreach (var branch in BranchTable)
        {
            foreach (var tier in new[]
                     {
                         ClassSuitTier.TierI,
                         ClassSuitTier.TierII,
                         ClassSuitTier.TierIII,
                         ClassSuitTier.TierIV
                     })
            {
                if (!index.TryAdd(
                        (branch.Profession, branch.ItemIdFor(tier)),
                        (branch, tier)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate Class Suit mapping for profession {branch.Profession}, item {branch.ItemIdFor(tier)}.");
                }
            }
        }

        return index;
    }

    private static void ValidateCatalog()
    {
        if (BranchTable.Count != BranchCount ||
            BranchTable.Select(static branch => branch.Key).Distinct(StringComparer.Ordinal).Count() != BranchCount)
        {
            throw new InvalidOperationException("The Class Suit branch table is incomplete or contains duplicate keys.");
        }

        if (BranchTable.Any(static branch =>
                branch.Profession > 3 ||
                branch.CommonItemIds.Count is < 1 or > 2 ||
                branch.CommonItemIds.Distinct().Count() != branch.CommonItemIds.Count ||
                branch.InsigniaCost is < 1 or > 3))
        {
            throw new InvalidOperationException("The Class Suit branch table contains an invalid rule.");
        }

        var tierIds = BranchTable
            .SelectMany(static branch => new[]
            {
                branch.TierIItemId,
                branch.TierIIItemId,
                branch.TierIIIItemId,
                branch.TierIVItemId
            })
            .Order()
            .ToArray();
        if (tierIds.Length != ClassSuitItemCatalog.ShippedItemCount ||
            tierIds.Distinct().Count() != tierIds.Length ||
            !tierIds.SequenceEqual(ClassSuitItemCatalog.AllItemIds))
        {
            throw new InvalidOperationException(
                "The conversion table does not cover the canonical 248 Class Suit items exactly once.");
        }
    }
}
