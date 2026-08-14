using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum PetTalentKind
{
    RandomEvent,
    QuestDispatch,
    Work,
    Healing,
    Merge
}

internal sealed record PetTalentDefinition(
    PetTalentKind Talent,
    string DisplayName,
    byte MaskBit);

/// <summary>
/// Stable innate pet-talent bits. Aptitude content decides which bits a pet
/// receives; inventory items are not a talent source. Bit 32 is part of the
/// native six-bit field but remains reserved until its meaning is captured.
/// </summary>
internal static class PetTalentCatalog
{
    public const byte SupportedMask = 1 | 2 | 4 | 8 | 16;
    public const byte ReservedMaskBit = 32;
    public const byte NativeMask = SupportedMask | ReservedMaskBit;

    public static IReadOnlyList<PetTalentDefinition> All { get; } =
        Array.AsReadOnly(
        [
            D(
                PetTalentKind.RandomEvent,
                "Random Event",
                1),
            D(
                PetTalentKind.QuestDispatch,
                "Quest Dispatch",
                2),
            D(
                PetTalentKind.Work,
                "Work",
                4),
            D(
                PetTalentKind.Healing,
                "Healing",
                8),
            D(
                PetTalentKind.Merge,
                "Merge",
                16)
        ]);

    private static readonly FrozenDictionary<
        PetTalentKind,
        PetTalentDefinition> ByTalent =
        All.ToFrozenDictionary(static value => value.Talent);

    private static readonly FrozenDictionary<byte, PetTalentDefinition>
        ByMaskBit =
        All.ToFrozenDictionary(static value => value.MaskBit);

    public static PetTalentDefinition Merge =>
        ByTalent[PetTalentKind.Merge];

    static PetTalentCatalog()
    {
        if (All.Count != 5 ||
            All.Select(static value => value.Talent).Distinct().Count() !=
                All.Count ||
            All.Select(static value => value.MaskBit).Distinct().Count() !=
                All.Count ||
            All.Any(static value =>
                value.MaskBit == 0 ||
                (value.MaskBit & (value.MaskBit - 1)) != 0 ||
                string.IsNullOrWhiteSpace(value.DisplayName)) ||
            All.Aggregate(
                (byte)0,
                static (mask, value) => (byte)(mask | value.MaskBit)) !=
                SupportedMask ||
            (SupportedMask & ReservedMaskBit) != 0)
        {
            throw new InvalidDataException(
                "The stock pet-talent catalog is incomplete or ambiguous.");
        }
    }

    public static bool TryGet(
        PetTalentKind talent,
        out PetTalentDefinition definition) =>
        ByTalent.TryGetValue(talent, out definition!);

    public static bool TryGetByMaskBit(
        byte maskBit,
        out PetTalentDefinition definition) =>
        ByMaskBit.TryGetValue(maskBit, out definition!);

    public static bool IsSupportedMask(byte mask) =>
        (mask & ~SupportedMask) == 0;

    private static PetTalentDefinition D(
        PetTalentKind talent,
        string displayName,
        byte maskBit) =>
        new(talent, displayName, maskBit);
}
