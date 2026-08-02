using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed Class Suit material identities captured from the original client
/// item catalog. These entries are durable inventory values and therefore
/// belong in the immutable PostgreSQL item-content publication.
/// </summary>
internal static class ClassSuitItemContentBaseline
{
    public static IReadOnlyList<ItemTemplateSeed>
        PromotionalInsignias { get; } =
        Array.AsReadOnly<ItemTemplateSeed>(
        [
            Insignia(
                id: 3931,
                nameKey: "Earphone3931",
                displayName: "Promotional Insignia I",
                icon: "792,288"),
            Insignia(
                id: 3962,
                nameKey: "Earphone3962",
                displayName: "Promotional Insignia II",
                icon: "756,288"),
            Insignia(
                id: 14069,
                nameKey: "Earphone14069",
                displayName: "Promotional Insignia III",
                icon: "720,288"),
            Insignia(
                id: 14073,
                nameKey: "Earphone14073",
                displayName: "Promotional Insignia IV",
                icon: "828,288")
        ]);

    private const string Texture =
        "./Localization/en_us/UI/Texture/Icon.gwo";

    private static ItemTemplateSeed Insignia(
        int id,
        string nameKey,
        string displayName,
        string icon) =>
        new(
            id,
            "consume item",
            nameKey,
            displayName,
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            Texture,
            icon,
            $$"""
            {"ID":"{{id}}","Type":"consume item","Texture":"{{Texture}}","Icon":"{{icon}}","Random":"0","Distribution":"0,0","Money":"0","Overlap":"99"}
            """);
}
