using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperItemGrantProjectionChecks
{
    public static void Run()
    {
        var live = new GameCharacter
        {
            Id = 71,
            AccountId = 7,
            KitBag = "live-bag",
            Equipment = "live-equipment",
            CurrentMap = 9,
            PositionX = 123.5f,
            PositionZ = -88.25f,
            CurrentHp = 777,
            CurrentMp = 333,
            Level = 80,
            Experience = 456_789,
            Silver = 1_234,
            Gold = 567
        };
        var persisted = new GameCharacter
        {
            Id = live.Id,
            AccountId = live.AccountId,
            KitBag = "authoritative-new-bag",
            Equipment = "stale-equipment",
            CurrentMap = 1,
            PositionX = -1,
            PositionZ = -2,
            CurrentHp = 1,
            CurrentMp = 2,
            Level = 3,
            Experience = 4,
            Silver = 5,
            Gold = 6
        };

        GameClientHandler.ApplyDeveloperItemGrantProjection(
            live,
            persisted);

        Check.True(
            live.KitBag == persisted.KitBag &&
            live.Equipment == "live-equipment" &&
            live.CurrentMap == 9 &&
            Math.Abs(live.PositionX - 123.5f) < 0.001f &&
            Math.Abs(live.PositionZ + 88.25f) < 0.001f &&
            live.CurrentHp == 777 &&
            live.CurrentMp == 333 &&
            live.Level == 80 &&
            live.Experience == 456_789 &&
            live.Silver == 1_234 &&
            live.Gold == 567,
            "durable material grant refresh changes only the live bag " +
            "projection");

        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ApplyDeveloperItemGrantProjection(
                live,
                new GameCharacter
                {
                    Id = live.Id + 1,
                    AccountId = live.AccountId,
                    KitBag = "wrong-character"
                }),
            "durable material grant projection rejects identity changes");
    }
}
