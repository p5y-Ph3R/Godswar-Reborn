using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>
/// Short-lived proof that this authenticated character reached the Medusa
/// difficulty page from the advertised NPC in the same world instance.
/// </summary>
internal sealed record InstanceCallerPageContext(
    int AccountId,
    int CharacterId,
    string NpcKey,
    uint NpcInteractionId,
    int DialogIndex,
    WorldInstanceId SourceWorldInstanceId,
    Guid PageNonce,
    DateTimeOffset ExpiresAt);
