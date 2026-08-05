using System.Globalization;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct HolyStoneCompactItemEvidence
{
    private readonly string[] _fields;

    private HolyStoneCompactItemEvidence(string[] fields)
    {
        _fields = fields;
        Id = uint.Parse(fields[0], CultureInfo.InvariantCulture);
        Grade = short.Parse(fields[7], CultureInfo.InvariantCulture);
        Bound = short.Parse(fields[8], CultureInfo.InvariantCulture);
        Stack = short.Parse(fields[9], CultureInfo.InvariantCulture);
        SocketCount = ParseInt16(fields, 17);
        Socket1EffectId = ParseNullableInt16(fields, 18);
        Socket1Level = ParseNullableInt16(fields, 19);
        Socket1Value = ParseNullableInt16(fields, 34);
    }

    public bool IsEmpty => _fields is null;
    public uint Id { get; }
    public short Grade { get; }
    public short Bound { get; }
    public short Stack { get; }
    public short SocketCount { get; }
    public short? Socket1EffectId { get; }
    public short? Socket1Level { get; }
    public short? Socket1Value { get; }

    public static HolyStoneCompactItemEvidence Parse(string state)
    {
        if (state == "[]")
        {
            return default;
        }
        if (string.IsNullOrWhiteSpace(state) ||
            state[0] != '[' ||
            state[^1] != ']')
        {
            throw new FormatException(
                "The compact Holy Stone evidence is malformed.");
        }

        var fields = state[1..^1].Split(',', StringSplitOptions.None);
        if (fields.Length < 10)
        {
            throw new FormatException(
                "The compact Holy Stone evidence is truncated.");
        }
        return new HolyStoneCompactItemEvidence(fields);
    }

    public string WithGrade(short grade) =>
        WithGradeAndBound(grade, Bound);

    public string WithGradeAndBound(short grade, short bound)
    {
        RequirePresent();
        var fields = (string[])_fields.Clone();
        fields[7] = grade.ToString(CultureInfo.InvariantCulture);
        fields[8] = bound.ToString(CultureInfo.InvariantCulture);
        return '[' + string.Join(',', fields) + ']';
    }

    public string ConsumeOne()
    {
        RequirePresent();
        if (Stack <= 0)
        {
            throw new InvalidOperationException(
                "An empty Holy Stone stack cannot be consumed.");
        }
        if (Stack == 1)
        {
            return "[]";
        }

        var fields = (string[])_fields.Clone();
        fields[9] = checked((short)(Stack - 1)).ToString(
            CultureInfo.InvariantCulture);
        return '[' + string.Join(',', fields) + ']';
    }

    private void RequirePresent()
    {
        if (IsEmpty || _fields is null)
        {
            throw new InvalidOperationException(
                "Empty Holy Stone evidence cannot be mutated.");
        }
    }

    private static short ParseInt16(string[] fields, int index) =>
        index < fields.Length &&
        short.TryParse(
            fields[index],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : (short)0;

    private static short? ParseNullableInt16(
        string[] fields,
        int index) =>
        index < fields.Length &&
        short.TryParse(
            fields[index],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
}
