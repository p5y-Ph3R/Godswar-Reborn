namespace Godswar.Server.Ecs;

/// <summary>
/// Refers to an entity that will be created when its owning command buffer is
/// played back. Tokens cannot be reused after playback or discard.
/// </summary>
internal readonly struct EcsDeferredEntity : IEquatable<EcsDeferredEntity>
{
    internal EcsDeferredEntity(long ownerId, uint epoch, int ordinal)
    {
        OwnerId = ownerId;
        Epoch = epoch;
        Ordinal = ordinal;
    }

    internal long OwnerId { get; }

    internal uint Epoch { get; }

    internal int Ordinal { get; }

    public bool IsValid => OwnerId != 0 && Epoch != 0 && Ordinal >= 0;

    public bool Equals(EcsDeferredEntity other) =>
        OwnerId == other.OwnerId &&
        Epoch == other.Epoch &&
        Ordinal == other.Ordinal;

    public override bool Equals(object? obj) =>
        obj is EcsDeferredEntity other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(OwnerId, Epoch, Ordinal);

    public override string ToString() => IsValid
        ? $"DeferredEntity({OwnerId}:{Epoch}:{Ordinal})"
        : "DeferredEntity(None)";
}
