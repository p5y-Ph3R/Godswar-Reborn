using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Npcs;

internal sealed record NpcEcsSnapshot(
    NpcIdentityComponent Identity,
    NpcTransformComponent Transform,
    NpcAppearanceComponent Appearance,
    NpcFunctionComponent Function,
    ImmutableArray<byte> Detail10077,
    ImmutableArray<byte> Detail10080);

internal static class NpcEcsSnapshotAdapter
{
    public static NpcEcsSnapshot Capture(
        EcsWorld world,
        EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        var dialog = world.Get<NpcDialogComponent>(entity);
        return new NpcEcsSnapshot(
            world.Get<NpcIdentityComponent>(entity),
            world.Get<NpcTransformComponent>(entity),
            world.Get<NpcAppearanceComponent>(entity),
            world.Get<NpcFunctionComponent>(entity),
            dialog.Detail10077,
            dialog.Detail10080);
    }

    public static NpcSpawnDefinition ToSpawnDefinition(NpcEcsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new NpcSpawnDefinition(
            snapshot.Identity.MapId,
            snapshot.Identity.SceneKey,
            snapshot.Identity.NpcKey,
            snapshot.Identity.TemplateKey,
            snapshot.Identity.ObjectId,
            snapshot.Transform.X,
            snapshot.Transform.Z,
            snapshot.Function.InteractionId,
            snapshot.Appearance.AppearanceType,
            snapshot.Transform.Facing,
            snapshot.Detail10077.ToArray(),
            snapshot.Detail10080.ToArray());
    }
}
