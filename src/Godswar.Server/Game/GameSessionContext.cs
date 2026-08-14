using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record GameSessionContext(
    ClientSession Session,
    int AccountId,
    int CharacterId,
    string CharacterName,
    RealmId RealmId,
    WorldInstanceId WorldInstanceId,
    byte MapId,
    uint ObjectId,
    GameCharacter Character,
    bool WorldReady,
    long WorldRevision)
{
    public PlayerOwnershipFence Ownership { get; init; }

    public bool PetOwnerMergeActive { get; init; }

    public Func<MonsterDamageResult,
        Task<PreparedPveMonsterKillReward?>>?
        PreparePveMonsterKillReward { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(CharacterName)
        ? $"character:{CharacterId}"
        : CharacterName;
}

internal sealed class PreparedPveMonsterKillReward
{
    private readonly Func<CancellationToken, Task> _publish;

    public PreparedPveMonsterKillReward(
        Func<CancellationToken, Task> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(CancellationToken cancellationToken) =>
        _publish(cancellationToken);
}
