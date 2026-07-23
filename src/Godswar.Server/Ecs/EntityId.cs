namespace Godswar.Server.Ecs;

/// <summary>
/// An opaque, generation-checked handle to an entity.
/// </summary>
internal readonly struct EntityId :
    IEquatable<EntityId>,
    IComparable<EntityId>
{
    private readonly ulong _value;

    private EntityId(int index, uint generation)
    {
        _value = ((ulong)generation << 32) | (uint)index;
    }

    public static EntityId None => default;

    public bool IsValid => Generation != 0;

    internal int Index => IsValid
        ? checked((int)(_value & uint.MaxValue))
        : throw new InvalidOperationException("The null entity has no index.");

    internal uint Generation => (uint)(_value >> 32);

    internal static EntityId FromParts(int index, uint generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Entity generations start at one.");
        }

        return new EntityId(index, generation);
    }

    public int CompareTo(EntityId other)
    {
        var indexComparison = ((uint)_value).CompareTo((uint)other._value);
        return indexComparison != 0
            ? indexComparison
            : Generation.CompareTo(other.Generation);
    }

    public bool Equals(EntityId other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is EntityId other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => IsValid
        ? $"Entity({(uint)_value}:{Generation})"
        : "Entity(None)";

    public static bool operator ==(EntityId left, EntityId right) =>
        left.Equals(right);

    public static bool operator !=(EntityId left, EntityId right) =>
        !left.Equals(right);
}
