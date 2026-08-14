using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSkillBookActivationPolicyChecks
{
    public const string CheckName =
        "Authoritative reviewed pet skill-book policy";

    private static readonly ReviewedBook[] Expected =
    [
        .. Family(10_464, 408, [3_900, 3_904, 3_908, 3_912, 3_916, 3_920]),
        .. Family(10_510, 412, [4_500, 4_503, 4_507, 4_511, 4_515, 4_519]),
        .. Family(10_530, 413, [4_600, 4_604, 4_608, 4_612, 4_616, 4_620]),
        .. Family(10_590, 419, [5_200, 5_204, 5_208, 5_212, 5_216, 5_220]),
        .. Family(10_700, 423, [5_600, 5_604, 5_608, 5_612, 5_616, 5_620])
    ];

    public static Task RunAsync()
    {
        var items = TestItemContent.Catalog;
        var skills = PetLearnedSkillContentBaseline.Create();
        Check.Equal(30, Expected.Length, "reviewed skill-book count");
        foreach (var expected in Expected)
        {
            Check.True(
                PetSkillBookActivationPolicy.IsReviewedItem(expected.ItemId) &&
                PetSkillBookActivationPolicy.TryResolve(
                    items,
                    skills,
                    expected.ItemId,
                    out var actual) &&
                actual.ItemId == expected.ItemId &&
                actual.FamilyType == expected.FamilyType &&
                actual.Priority == expected.Priority &&
                actual.RuntimeSkillId == expected.RuntimeSkillId &&
                actual.TraitRequirement == ExpectedTrait(
                    expected.FamilyType,
                    expected.Priority),
                $"reviewed skill book {expected.ItemId} is pinned exactly");
        }

        Check.True(
            !PetSkillBookActivationPolicy.IsReviewedItem(10_463) &&
            !PetSkillBookActivationPolicy.IsReviewedItem(10_470) &&
            !PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_470,
                out _),
            "unreviewed neighboring items fail closed");
        CheckTamperedMetadataFailsClosed(items, skills);
        CheckReceiptRoundTrip(items, skills);

        Check.True(
            PetSkillFamilyCatalog.TryGetByInitialRuntimeSkillId(
                3_900,
                out var wildBump) &&
            wildBump.HasSkillBooks &&
            PetSkillFamilyCatalog.BookBackedFamilyCount == 58,
            "Wild Bump is included in the reviewed book-backed catalog");
        return Task.CompletedTask;
    }

    private static void CheckTamperedMetadataFailsClosed(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        var tamperedDefinitions = items.All.Select(definition =>
            definition.Id == 10_465
                ? definition with
                {
                    StatsJson = definition.StatsJson.Replace(
                        "3904",
                        "3905",
                        StringComparison.Ordinal)
                }
                : definition).ToArray();
        var tampered = PinnedItemTemplateCatalog.Create(
            "tampered-pet-skill-book-check",
            tamperedDefinitions);
        Check.True(
            !PetSkillBookActivationPolicy.TryResolve(
                tampered,
                skills,
                10_465,
                out _),
            "published item metadata cannot redirect an allow-listed book");
    }

    private static void CheckReceiptRoundTrip(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog skills)
    {
        Check.True(
            PetSkillBookActivationPolicy.TryResolve(
                items,
                skills,
                10_465,
                out var book),
            "receipt fixture resolves the reviewed book");
        var evidence = new PetSkillLearnEvidence(
            PetId: 7,
            ItemInstanceId: 11,
            ItemTemplateId: book.ItemId,
            SpeciesId: 25,
            FamilyType: book.FamilyType,
            PreviousPriority: 1,
            LearnedPriority: book.Priority,
            PreviousRuntimeSkillId: 3_900,
            LearnedRuntimeSkillId: book.RuntimeSkillId,
            SkillSlot: 0,
            TraitRequirement: book.TraitRequirement,
            TraitsAtLearnTime: new PetContentStatVector(
                0m,
                64m,
                0m,
                0m,
                0m,
                0m),
            ItemContentRevision: items.Revision.Sha256,
            LearnedSkillContentRevision: skills.Revision.Sha256);
        var receipt = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PetSkillLearned,
            AccountId: 1,
            CharacterId: 2,
            KitBagSlot: 25,
            EquipmentSlot: -1,
            PetId: 7,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: 3,
            IsCarried: true,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 4,
            AuditReference: "pet-skill-book-policy-check",
            OutboxEventId: Guid.NewGuid(),
            SkillLearn: evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var decoded = PetDurablePersistenceCodec.Decode(payload);
        Check.True(
            PetDurablePersistenceCodec.ReadContractVersion(payload) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion &&
            decoded == receipt,
            "bag activation v3 durably round-trips exact skill-learn evidence");
    }

    private static IEnumerable<ReviewedBook> Family(
        uint firstItemId,
        int familyType,
        IReadOnlyList<int> runtimeSkillIds) =>
        runtimeSkillIds.Select((runtimeSkillId, index) => new ReviewedBook(
            checked(firstItemId + (uint)index),
            familyType,
            checked((short)(index + 1)),
            runtimeSkillId));

    private static PetSkillTraitRequirement ExpectedTrait(
        int familyType,
        short priority)
    {
        var threshold = priority switch
        {
            1 => 0m,
            2 => 64m,
            3 => 192m,
            4 => 235m,
            5 => 270m,
            6 => 305m,
            _ => throw new ArgumentOutOfRangeException(nameof(priority))
        };
        return familyType switch
        {
            408 or 419 => new(0m, threshold, 0m, 0m, 0m, 0m),
            412 or 413 => new(0m, 0m, threshold, 0m, 0m, 0m),
            423 => new(0m, 0m, 0m, 0m, threshold, 0m),
            _ => throw new ArgumentOutOfRangeException(nameof(familyType))
        };
    }

    private sealed record ReviewedBook(
        uint ItemId,
        int FamilyType,
        short Priority,
        int RuntimeSkillId);
}
