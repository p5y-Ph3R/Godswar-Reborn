namespace Godswar.Server.Application.Characters;

/// <summary>
/// Process-local ownership bridge used only by the JSON development adapter.
/// It is not a distributed ownership fence and must not be used by PostgreSQL.
/// </summary>
internal interface ILocalPlayerOwnershipRegistry
{
    Task BindAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);
}
