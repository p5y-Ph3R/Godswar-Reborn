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

    /// <summary>
    /// Class Suit III/IV attributes are stored separately from the five
    /// ordinary appended attributes. Their effective values are selected by
    /// the equipment grade, so they do not need independent level fields.
    /// </summary>
    public int? ClassAttribute1 { get; init; }

    public int? ClassAttribute2 { get; init; }

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

        var parsed = new CompactItemEntry(
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
            ParseNullableInt16(parts, 29))
        {
            ClassAttribute1 = ParseNullableInt(parts, 30),
            ClassAttribute2 = ParseNullableInt(parts, 31)
        };
        return parsed.NormalizeClassAttributes();
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

        var nativeFields = string.Join(
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
            Format(Socket6Level));
        if (!ClassAttribute1.HasValue && !ClassAttribute2.HasValue)
        {
            return '[' + nativeFields + ']';
        }

        return '[' + nativeFields + ',' +
            Format(ClassAttribute1) + ',' +
            Format(ClassAttribute2) + ']';
    }

    internal CompactItemEntry NormalizeClassAttributes()
    {
        var attributes = new[]
        {
            Attribute1,
            Attribute2,
            Attribute3,
            Attribute4,
            Attribute5
        };
        var levels = new[]
        {
            AttributeLevel1,
            AttributeLevel2,
            AttributeLevel3,
            AttributeLevel4,
            AttributeLevel5
        };
        var classAttributes = new List<int>(2);
        if (!TryAddDistinctClassAttribute(classAttributes, ClassAttribute1) ||
            !TryAddDistinctClassAttribute(classAttributes, ClassAttribute2))
        {
            return this;
        }

        var legacyClassAttributeFound = false;
        for (var index = 0; index < attributes.Length; index++)
        {
            if (!IsLegacyClassAttribute(attributes[index]))
            {
                continue;
            }

            legacyClassAttributeFound = true;
            if (!TryAddDistinctClassAttribute(
                    classAttributes,
                    attributes[index]))
            {
                return this;
            }
            attributes[index] = null;
            levels[index] = null;
        }

        if (classAttributes.Count > 2)
        {
            return this;
        }

        var normalizedClassAttribute1 = classAttributes.Count > 0
            ? classAttributes[0]
            : (int?)null;
        var normalizedClassAttribute2 = classAttributes.Count > 1
            ? classAttributes[1]
            : (int?)null;
        if (!legacyClassAttributeFound &&
            ClassAttribute1 == normalizedClassAttribute1 &&
            ClassAttribute2 == normalizedClassAttribute2)
        {
            return this;
        }

        if (legacyClassAttributeFound)
        {
            var ordinaryAttributes = new List<int?>(5);
            var ordinaryLevels = new List<short?>(5);
            for (var index = 0; index < attributes.Length; index++)
            {
                if (!attributes[index].HasValue)
                {
                    continue;
                }

                ordinaryAttributes.Add(attributes[index]);
                ordinaryLevels.Add(levels[index]);
            }
            while (ordinaryAttributes.Count < 5)
            {
                ordinaryAttributes.Add(null);
                ordinaryLevels.Add(null);
            }

            attributes = ordinaryAttributes.ToArray();
            levels = ordinaryLevels.ToArray();
        }

        return this with
        {
            Attribute1 = attributes[0],
            Attribute2 = attributes[1],
            Attribute3 = attributes[2],
            Attribute4 = attributes[3],
            Attribute5 = attributes[4],
            AttributeLevel1 = levels[0],
            AttributeLevel2 = levels[1],
            AttributeLevel3 = levels[2],
            AttributeLevel4 = levels[3],
            AttributeLevel5 = levels[4],
            ClassAttribute1 = normalizedClassAttribute1,
            ClassAttribute2 = normalizedClassAttribute2
        };
    }

    private static bool TryAddDistinctClassAttribute(
        List<int> attributes,
        int? attribute)
    {
        if (!attribute.HasValue)
        {
            return true;
        }

        if (attributes.Contains(attribute.Value))
        {
            return false;
        }

        attributes.Add(attribute.Value);
        return true;
    }

    private static bool IsLegacyClassAttribute(int? attribute) =>
        attribute is 200 or 201 or 210 or 211 or 220 or 221 or 230 or 231;

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
