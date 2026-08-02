using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySuitExecutionContractChecks
{
    private static void CheckProjectionPreservesLiveVitals()
    {
        var live = new GameCharacter
        {
            Id = 13,
            AccountId = 13,
            Experience = 900_000_000,
            HolySuitPoints = 14,
            Equipment = "live-equipment",
            KitBag = "live-bag",
            MaxHp = 50_000,
            CurrentHp = 42_000,
            MaxMp = 8_000,
            CurrentMp = 7_000,
            VitalsRevision = 88
        };
        var persisted = new GameCharacter
        {
            Id = 13,
            AccountId = 13,
            Experience = 500_000_000,
            HolySuitPoints = 21,
            Equipment = "persisted-equipment",
            KitBag = "authoritative-bag",
            MaxHp = 5_470,
            CurrentHp = 5_470,
            MaxMp = 1_000,
            CurrentMp = 1_000,
            VitalsRevision = 12,
            CalculatedStats = new CharacterStats
            {
                CharacterId = 13,
                AccountId = 13,
                MaxHp = 5_470,
                CurrentHp = 5_470,
                MaxMp = 1_000,
                CurrentMp = 1_000
            }
        };

        GameClientHandler.ApplyDurableHolySuitProjection(live, persisted);

        Check.Equal(
            "authoritative-bag",
            live.KitBag,
            "Holy Suit projection refreshes the authoritative bag");
        Check.Equal(
            500_000_000L,
            live.Experience,
            "Holy Suit projection refreshes authoritative fighter EXP");
        Check.Equal(
            21,
            live.HolySuitPoints,
            "Holy Suit projection refreshes authoritative suit points");
        Check.Equal(
            "live-equipment",
            live.Equipment,
            "Holy Suit projection does not replace equipped items");
        Check.Equal(
            50_000,
            live.MaxHp,
            "Holy Suit projection preserves live maximum HP");
        Check.Equal(
            42_000,
            live.CurrentHp,
            "Holy Suit projection preserves live current HP");
        Check.Equal(
            8_000,
            live.MaxMp,
            "Holy Suit projection preserves live maximum MP");
        Check.Equal(
            7_000,
            live.CurrentMp,
            "Holy Suit projection preserves live current MP");
        Check.Equal(
            88L,
            live.VitalsRevision,
            "Holy Suit projection does not create a vitals mutation");
    }
}
