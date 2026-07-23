using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Npcs;

/// <summary>
/// Copies a catalog/capture-backed NPC definition into packet-independent ECS
/// values. Detail frames are copied so later capture-buffer mutations cannot
/// alter the world snapshot.
/// </summary>
internal static class NpcSpawnDefinitionEcsHydrator
{
    public static void RegisterComponents(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RegisterComponent<NpcIdentityComponent>();
        world.RegisterComponent<NpcTransformComponent>();
        world.RegisterComponent<NpcAppearanceComponent>();
        world.RegisterComponent<NpcFunctionComponent>();
        world.RegisterComponent<NpcDialogComponent>();
    }

    public static EntityId Hydrate(
        EcsWorld world,
        NpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.ObjectId == 0 ||
            definition.InteractionId == 0)
        {
            throw new ArgumentException(
                "An NPC ECS entity requires non-zero object and interaction IDs.",
                nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.SceneKey) ||
            string.IsNullOrWhiteSpace(definition.NpcKey) ||
            string.IsNullOrWhiteSpace(definition.TemplateKey))
        {
            throw new ArgumentException(
                "An NPC ECS entity requires scene, NPC, and template keys.",
                nameof(definition));
        }

        if (definition.AppearanceType == 0 ||
            !float.IsFinite(definition.X) ||
            !float.IsFinite(definition.Z) ||
            !float.IsFinite(definition.Facing))
        {
            throw new ArgumentException(
                "The NPC definition contains invalid appearance values.",
                nameof(definition));
        }

        ArgumentNullException.ThrowIfNull(definition.Detail10077);
        ArgumentNullException.ThrowIfNull(definition.Detail10080);
        var detail10077 = ImmutableArray.CreateRange(definition.Detail10077);
        var detail10080 = ImmutableArray.CreateRange(definition.Detail10080);

        RegisterComponents(world);
        var entity = world.CreateEntity();
        world.Add(
            entity,
            new NpcIdentityComponent(
                definition.MapId,
                definition.SceneKey,
                definition.NpcKey,
                definition.TemplateKey,
                definition.ObjectId));
        world.Add(
            entity,
            new NpcTransformComponent(
                definition.X,
                definition.Z,
                definition.Facing));
        world.Add(
            entity,
            new NpcAppearanceComponent(definition.AppearanceType));
        world.Add(
            entity,
            new NpcFunctionComponent(definition.InteractionId));
        world.Add(
            entity,
            new NpcDialogComponent(detail10077, detail10080));
        return entity;
    }
}
