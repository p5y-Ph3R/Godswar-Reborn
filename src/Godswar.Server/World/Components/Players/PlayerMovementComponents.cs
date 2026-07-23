namespace Godswar.Server.World.Components.Players;

internal enum PlayerMovementRejectionReason : byte
{
    None = 0,
    IntentOutOfOrder = 1,
    SourceObjectMismatch = 2,
    IdentityMismatch = 3,
    InvalidCoordinates = 4
}

/// <summary>
/// Stable session identity captured when a character enters the movement
/// boundary. Client-supplied movement state never replaces these values.
/// </summary>
internal readonly record struct PlayerMovementIdentityComponent(
    int AccountId,
    int CharacterId,
    uint SourceObjectId);

/// <summary>
/// ECS-owned movement projection. Current and target coincide after an
/// accepted walk; rejected intents leave both and the revision unchanged.
/// </summary>
internal struct PlayerMovementTransformComponent
{
    public PlayerMovementTransformComponent(
        byte mapId,
        float currentX,
        float currentZ)
    {
        MapId = mapId;
        CurrentX = currentX;
        CurrentZ = currentZ;
        TargetX = currentX;
        TargetZ = currentZ;
        ProjectionRevision = 0;
        LastIntentSequence = 0;
    }

    public byte MapId;

    public float CurrentX;

    public float CurrentZ;

    public float TargetX;

    public float TargetZ;

    public ulong ProjectionRevision;

    public ulong LastIntentSequence;
}

/// <summary>
/// One boundary-decoded walk request. Sequence is adapter-owned rather than
/// client-owned and exists only to make projection order deterministic. The
/// source is null until packet evidence identifies a trustworthy inbound
/// source-object field; opaque movement-state bits are never reinterpreted.
/// </summary>
internal readonly record struct PlayerMovementIntentComponent(
    ulong Sequence,
    uint? VerifiedSourceObjectId,
    int SessionAccountId,
    int CharacterAccountId,
    int CharacterId,
    byte MapId,
    float TargetX,
    float TargetZ);
