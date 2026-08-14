using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SkillCombatTimingCatalogChecks
{
    private static void CheckAuthoritativeCooldownLedger()
    {
        var observedAt = new DateTimeOffset(
            2035,
            4,
            5,
            6,
            7,
            8,
            TimeSpan.Zero);
        var cooldown = TimeSpan.FromSeconds(22);
        var ledger = new HostileSkillCooldownLedger();

        Check.True(
            ledger.TryClaim(
                0,
                TimeSpan.FromSeconds(10),
                observedAt,
                out _,
                out _),
            "skill ID zero participates in hostile-skill cooldown admission");

        Check.True(
            ledger.TryClaim(
                530,
                cooldown,
                observedAt,
                out var first,
                out var firstReadyAt),
            "first hostile-skill cooldown claim is admitted");
        Check.Equal(
            observedAt + cooldown,
            firstReadyAt,
            "hostile-skill cooldown uses the authored duration");
        Check.True(
            !ledger.TryClaim(
                530,
                cooldown,
                observedAt + TimeSpan.FromSeconds(1),
                out _,
                out var rejectedReadyAt),
            "an accepted hostile cast blocks replay inside cooldown");
        Check.Equal(
            firstReadyAt,
            rejectedReadyAt,
            "cooldown rejection reports the authoritative ready time");

        Check.True(
            ledger.TryRelease(first),
            "a rejected downstream reservation releases its own claim");
        Check.True(
            ledger.TryClaim(
                530,
                cooldown,
                observedAt + TimeSpan.FromSeconds(1),
                out var replacement,
                out _),
            "released hostile-skill cooldown can be retried");
        Check.True(
            !ledger.TryRelease(first),
            "a stale lease cannot erase a newer cooldown claim");
        Check.True(
            ledger.TryRelease(replacement),
            "the current replacement lease remains releasable");

        var contenders = Enumerable.Range(0, 32)
            .Select(_index => Task.Run(() => ledger.TryClaim(
                570,
                TimeSpan.FromSeconds(2),
                observedAt,
                out _,
                out _)))
            .ToArray();
        Task.WaitAll(contenders);
        Check.Equal(
            1,
            contenders.Count(static contender => contender.Result),
            "concurrent hostile-skill replay admits exactly one cast");
    }
}
