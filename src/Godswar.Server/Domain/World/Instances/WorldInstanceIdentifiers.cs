using System.Globalization;

namespace Godswar.Server.Domain.World.Instances;

/// <summary>
/// Durable logical realm identity. The legacy PostgreSQL <c>server</c> row is
/// the source of this value; it is not a process or container identifier.
/// </summary>
internal readonly record struct RealmId
{
    public static readonly RealmId Tempest = new(1);

    public static readonly RealmId Dwargon = new(2);

    public RealmId(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Realm IDs must be positive.");
        }

        Value = value;
    }

    public int Value { get; }

    public bool IsValid => Value > 0;

    public override string ToString() =>
        Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Identifies one running server process. This identity is intentionally
/// separate from <see cref="RealmId"/> because a realm may use many nodes.
/// </summary>
internal readonly record struct ServerNodeId
{
    public const int MaximumLength = 64;

    public static readonly ServerNodeId Local = new("local-node");

    public ServerNodeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLength ||
            !value.All(IsAllowedCharacter))
        {
            throw new ArgumentException(
                "Server node IDs must contain at most 64 ASCII letters, " +
                "digits, periods, underscores, or hyphens.",
                nameof(value));
        }

        Value = value;
    }

    public string? Value { get; }

    public bool IsValid => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value ?? string.Empty;

    private static bool IsAllowedCharacter(char value) =>
        value is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '.' or
            '_' or
            '-';
}

/// <summary>
/// Globally unique identity of one live open-world, battlefield, or dungeon
/// simulation. Multiple world instances may share the same map definition.
/// </summary>
internal readonly record struct WorldInstanceId
{
    public WorldInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "World instance IDs cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;

    public static WorldInstanceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Identity of a reusable map/content definition. The current client protocol
/// uses one byte, while the content schema already uses a signed short.
/// </summary>
internal readonly record struct MapId
{
    public MapId(short value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Map IDs cannot be negative.");
        }

        Value = value;
    }

    public short Value { get; }

    public bool IsValid => Value >= 0;

    public static MapId FromLegacy(byte value) => new(value);

    public bool TryGetLegacyValue(out byte value)
    {
        if (Value <= byte.MaxValue)
        {
            value = checked((byte)Value);
            return true;
        }

        value = default;
        return false;
    }

    public override string ToString() =>
        Value.ToString(CultureInfo.InvariantCulture);
}
