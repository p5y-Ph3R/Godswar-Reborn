namespace Godswar.Server.ProtocolChecks;

internal static class CoreRuntimeCheckCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Strongly typed ECS kernel", EcsKernelChecks.RunAsync),
        ("Player runtime ECS shadow parity", PlayerRuntimeEcsShadowChecks.RunAsync),
        ("Reversible player runtime ECS cutover", PlayerRuntimeEcsCutoverChecks.RunAsync),
        ("Player and NPC ECS hydration parity", PlayerNpcEcsHydrationChecks.RunAsync),
        ("Per-map player and NPC ECS runtime cutover", MapEcsShadowChecks.RunAsync),
        ("Atomic map ECS publication and rollback", MapEcsRuntimeCutoverChecks.RunAsync),
        ("Online NPC revision and object-ID collision rollback", NpcCatalogRevisionChecks.RunAsync),
        ("Cross-map ECS transfer rollback state", MapEcsTransferRollbackChecks.RunAsync)
    ];
}
