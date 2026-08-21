using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Coordination;

/// <summary>
/// Process-local proof that the current session still owns its disposable
/// cross-process route. Durable authority remains the PostgreSQL fence.
/// </summary>
internal interface IPlayerCoordinationLease : IAsyncDisposable
{
    PlayerOwnershipFence Ownership { get; }

    Guid LeaseToken { get; }

    bool IsCurrent { get; }

    /// <summary>
    /// Stops renewal and releases the exact disposable lease. The result is
    /// true only when this lease removed the current coordination record.
    /// Repeated calls return the first release result.
    /// </summary>
    ValueTask<bool> ReleaseAsync();

    ValueTask<bool> PublishEnteringAsync(
        CoordinatedWorldRoute route,
        CancellationToken cancellationToken = default);

    ValueTask<bool> PublishOnlineAsync(
        CoordinatedWorldRoute route,
        CancellationToken cancellationToken = default);
}

internal interface IPlayerCoordinationLeaseIssuer
{
    bool IsEnabled { get; }

    ServerNodeId NodeId { get; }

    bool TryResolveRoute(
        byte legacyMapId,
        out CoordinatedWorldRoute route);

    ValueTask<IPlayerCoordinationLease?> AcquireAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CoordinatedWorldRoute route,
        Action ownershipLost,
        CancellationToken cancellationToken = default);
}
