using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

/// <summary>
/// Deterministically validates and projects player walk intents. This system
/// deliberately has no distance, speed, cadence, or wall-clock rule because
/// the legacy movement path has none.
/// </summary>
internal sealed class PlayerMovementProjectionSystem : IEcsSystem
{
    private const double WorldCellSize = 32d;

    public const int SystemOrder = 300;

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var entity in context.World.Query<
                     PlayerMovementIdentityComponent,
                     PlayerMovementTransformComponent,
                     PlayerMovementIntentComponent>())
        {
            var identity = context.World
                .Get<PlayerMovementIdentityComponent>(entity);
            ref var transform = ref context.World
                .Get<PlayerMovementTransformComponent>(entity);
            var intent = context.World
                .Get<PlayerMovementIntentComponent>(entity);
            context.Commands.Remove<
                PlayerMovementIntentComponent>(entity);

            if (intent.Sequence <= transform.LastIntentSequence)
            {
                Reject(
                    context,
                    entity,
                    intent,
                    transform,
                    PlayerMovementRejectionReason.IntentOutOfOrder);
                continue;
            }

            transform.LastIntentSequence = intent.Sequence;
            if (intent.VerifiedSourceObjectId is { } sourceObjectId &&
                sourceObjectId != identity.SourceObjectId)
            {
                Reject(
                    context,
                    entity,
                    intent,
                    transform,
                    PlayerMovementRejectionReason
                        .SourceObjectMismatch);
                continue;
            }

            if (intent.SessionAccountId != identity.AccountId ||
                intent.CharacterAccountId != identity.AccountId ||
                intent.CharacterId != identity.CharacterId ||
                intent.MapId != transform.MapId)
            {
                Reject(
                    context,
                    entity,
                    intent,
                    transform,
                    PlayerMovementRejectionReason.IdentityMismatch);
                continue;
            }

            if (!HasValidWorldCoordinates(
                    transform.CurrentX,
                    transform.CurrentZ) ||
                !HasValidWorldCoordinates(
                    intent.TargetX,
                    intent.TargetZ))
            {
                Reject(
                    context,
                    entity,
                    intent,
                    transform,
                    PlayerMovementRejectionReason.InvalidCoordinates);
                continue;
            }

            var previousX = transform.CurrentX;
            var previousZ = transform.CurrentZ;
            transform.TargetX = intent.TargetX;
            transform.TargetZ = intent.TargetZ;
            transform.CurrentX = transform.TargetX;
            transform.CurrentZ = transform.TargetZ;
            transform.ProjectionRevision = checked(
                transform.ProjectionRevision + 1);
            context.Events.Publish(
                new PlayerMovementProjectedEvent(
                    entity,
                    intent.Sequence,
                    transform.ProjectionRevision,
                    transform.MapId,
                    previousX,
                    previousZ,
                    transform.TargetX,
                    transform.TargetZ,
                    transform.CurrentX,
                    transform.CurrentZ));
        }
    }

    internal static bool HasValidWorldCoordinates(
        float x,
        float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        var cellX = Math.Floor((double)x / WorldCellSize);
        var cellZ = Math.Floor((double)z / WorldCellSize);
        return cellX is >= int.MinValue and <= int.MaxValue &&
               cellZ is >= int.MinValue and <= int.MaxValue;
    }

    private static void Reject(
        EcsSystemContext context,
        EntityId entity,
        in PlayerMovementIntentComponent intent,
        in PlayerMovementTransformComponent transform,
        PlayerMovementRejectionReason reason)
    {
        context.Events.Publish(
            new PlayerMovementRejectedEvent(
                entity,
                intent.Sequence,
                transform.ProjectionRevision,
                reason,
                transform.MapId,
                intent.TargetX,
                intent.TargetZ,
                transform.CurrentX,
                transform.CurrentZ));
    }
}
