namespace Godswar.Server.Game;

internal sealed record WarehouseAccessContext(
    int AccountId,
    int CharacterId,
    int RealmId,
    byte MapId,
    uint NpcInteractionId,
    DateTimeOffset ExpiresAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public bool Matches(
        int accountId,
        int characterId,
        int realmId,
        byte mapId,
        DateTimeOffset now) =>
        AccountId == accountId &&
        CharacterId == characterId &&
        RealmId == realmId &&
        MapId == mapId &&
        now < ExpiresAt;
}
