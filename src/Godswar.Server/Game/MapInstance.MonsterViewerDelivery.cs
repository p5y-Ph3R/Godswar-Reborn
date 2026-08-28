namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private IReadOnlyList<uint> ResolveTerminalMonsterMutations(
        IReadOnlyList<MonsterHealthMutation> mutations)
    {
        if (mutations.Count == 0)
        {
            return [];
        }

        var afterVersions = mutations.ToDictionary(
            static mutation => mutation.ObjectId,
            static mutation => mutation.AfterVersion);
        return SnapshotMonsters()
            .Where(monster =>
                !monster.IsAlive &&
                afterVersions.TryGetValue(
                    monster.ObjectId,
                    out var afterVersion) &&
                monster.AppearanceVersion == afterVersion)
            .Select(static monster => monster.ObjectId)
            .Order()
            .ToArray();
    }
}
