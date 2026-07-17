namespace Godswar.Server.State;

internal readonly record struct CompactItemEntry(
    uint Id,
    int? Attribute1,
    int? Attribute2,
    int? Attribute3,
    int? Attribute4,
    int? Attribute5,
    short Quality,
    short Grade,
    short Bound,
    short Stack,
    int Exp,
    int HolySuitCode,
    short? AttributeLevel1,
    short? AttributeLevel2,
    short? AttributeLevel3,
    short? AttributeLevel4,
    short? AttributeLevel5,
    short SocketCount,
    short? Socket1EffectId,
    short? Socket1Level,
    short? Socket2EffectId,
    short? Socket2Level,
    short? Socket3EffectId,
    short? Socket3Level,
    short? Socket4EffectId,
    short? Socket4Level,
    short? Socket5EffectId,
    short? Socket5Level,
    short? Socket6EffectId,
    short? Socket6Level)
{
    private const int MaxSockets = 4;

    public bool IsEmpty => Id == 0;

    public short HolySuitType => HolySuitCode <= 0 ? (short)0 : (short)Math.Clamp(HolySuitCode / 100, 0, 7);

    public short HolySuitLevel => HolySuitCode <= 0 ? (short)0 : (short)Math.Clamp(HolySuitCode % 100, 0, 10);

    public static CompactItemEntry Parse(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry == "[]")
        {
            return Empty;
        }

        var clean = entry.Trim('[', ']');
        var parts = clean.Split(',', StringSplitOptions.None);
        var id = ParseUInt(parts, 0);
        var defaultQuality = id == 0 ? (short)0 : (short)1;
        var defaultGrade = id == 0 ? (short)0 : (short)1;

        return new CompactItemEntry(
            id,
            ParseNullableInt(parts, 1),
            ParseNullableInt(parts, 2),
            ParseNullableInt(parts, 3),
            ParseNullableInt(parts, 4),
            ParseNullableInt(parts, 5),
            ParseInt16(parts, 6, defaultQuality),
            ParseInt16(parts, 7, defaultGrade),
            ParseInt16(parts, 8, 0),
            ParseInt16(parts, 9, 1),
            ParseInt32(parts, 10, 0),
            ParseInt32(parts, 11, 0),
            ParseNullableInt16(parts, 12),
            ParseNullableInt16(parts, 13),
            ParseNullableInt16(parts, 14),
            ParseNullableInt16(parts, 15),
            ParseNullableInt16(parts, 16),
            ParseInt16(parts, 17, 0),
            ParseNullableInt16(parts, 18),
            ParseNullableInt16(parts, 19),
            ParseNullableInt16(parts, 20),
            ParseNullableInt16(parts, 21),
            ParseNullableInt16(parts, 22),
            ParseNullableInt16(parts, 23),
            ParseNullableInt16(parts, 24),
            ParseNullableInt16(parts, 25),
            ParseNullableInt16(parts, 26),
            ParseNullableInt16(parts, 27),
            ParseNullableInt16(parts, 28),
            ParseNullableInt16(parts, 29));
    }

    public static CompactItemEntry Empty => new(
        0,
        null,
        null,
        null,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    public string ToCompactString()
    {
        if (IsEmpty)
        {
            return "[]";
        }

        return '[' + string.Join(
            ',',
            Id.ToString(),
            Format(Attribute1),
            Format(Attribute2),
            Format(Attribute3),
            Format(Attribute4),
            Format(Attribute5),
            Quality.ToString(),
            Grade.ToString(),
            Bound.ToString(),
            Stack.ToString(),
            Exp.ToString(),
            HolySuitCode.ToString(),
            Format(AttributeLevel1),
            Format(AttributeLevel2),
            Format(AttributeLevel3),
            Format(AttributeLevel4),
            Format(AttributeLevel5),
            Math.Clamp(SocketCount, (short)0, (short)MaxSockets).ToString(),
            Format(Socket1EffectId),
            Format(Socket1Level),
            Format(Socket2EffectId),
            Format(Socket2Level),
            Format(Socket3EffectId),
            Format(Socket3Level),
            Format(Socket4EffectId),
            Format(Socket4Level),
            Format(Socket5EffectId),
            Format(Socket5Level),
            Format(Socket6EffectId),
            Format(Socket6Level)) + ']';
    }

    private static string Format(int? value)
    {
        return value.HasValue ? value.Value.ToString() : string.Empty;
    }

    private static string Format(short? value)
    {
        return value.HasValue ? value.Value.ToString() : string.Empty;
    }

    private static uint ParseUInt(string[] parts, int index)
    {
        return index < parts.Length && uint.TryParse(parts[index], out var value) ? value : 0;
    }

    private static int? ParseNullableInt(string[] parts, int index)
    {
        if (index >= parts.Length || string.IsNullOrWhiteSpace(parts[index]))
        {
            return null;
        }

        return int.TryParse(parts[index], out var value) ? value : null;
    }

    private static short ParseInt16(string[] parts, int index, short fallback)
    {
        return index < parts.Length && short.TryParse(parts[index], out var value) ? value : fallback;
    }

    private static short? ParseNullableInt16(string[] parts, int index)
    {
        if (index >= parts.Length || string.IsNullOrWhiteSpace(parts[index]))
        {
            return null;
        }

        return short.TryParse(parts[index], out var value) ? value : null;
    }

    private static int ParseInt32(string[] parts, int index, int fallback)
    {
        return index < parts.Length && int.TryParse(parts[index], out var value) ? value : fallback;
    }
}
