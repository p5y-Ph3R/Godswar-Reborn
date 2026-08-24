using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Grants the stock warehouse manager's finite dialog-106 capability at both
/// capital endpoints. The menu advertises only action 100; current capacity
/// and every result are resolved from authoritative character state.
/// </summary>
internal static class NpcDialogueBaselineV8
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 8;
    public const int ExpectedRouteCount = 16;
    public const int ExpectedMenuEntryCount = 47;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV5.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "A4D40EC92EC6B637C2743A402DC38D3D4BA45E20AB3124C1D6FCEF284852ABB4";
    public const string Source =
        "reviewed-published-npc-dialogue-warehouse-v1";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV5.Profiles,
        new(
            "warehouse_manager",
            WarehouseNpcProtocol.ManagerDialogIndex,
            NpcDialogueBehavior.WarehouseManager,
            WarehouseNpcProtocol.ManagerInitialRequestSubId,
            WarehouseNpcProtocol.ManagerInitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV5.Bindings,
        new("Athens_134", "Athens_134", "warehouse_manager"),
        new("Sparta_134", "Sparta_134", "warehouse_manager")
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey,
            StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RouteOrder)
            .Select(binding =>
            {
                if (!profiles.TryGetValue(binding.ProfileKey, out var profile))
                {
                    throw new InvalidDataException(
                        $"Unknown NPC dialogue profile '{binding.ProfileKey}'.");
                }

                return new NpcDialogueRouteDefinition(
                    binding.NpcKey,
                    binding.ClientScriptKey,
                    profile.DialogIndex,
                    profile.Behavior,
                    profile.InitialMenuSubIds)
                {
                    RouteOrder = binding.RouteOrder
                };
            })
            .ToArray();
    }
}
