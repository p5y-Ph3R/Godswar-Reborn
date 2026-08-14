using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CombatSecondaryProjectionChecks
{
    private static void CheckWitheredLifeAbsorption()
    {
        var character = new GameCharacter
        {
            Id = 70,
            CurrentHp = 50,
            MaxHp = 100,
            CalculatedStats = new CharacterStats
            {
                LifeAbsorption = 5_000
            }
        };
        var committer = new PveLifeAbsorptionCommitter(
            ledgerCapacity: 8);
        var hits = new[]
        {
            new PveCommittedMonsterDamage(101, 901, 1, 11),
            new PveCommittedMonsterDamage(102, 902, 1, 11)
        };

        var withered = committer.Commit(
            character,
            hits,
            healingReceivedBasisPoints: 5_000);
        Check.True(
            withered is
            {
                ClaimedHitCount: 2,
                RequestedHealing: 10,
                AdjustedRequestedHealing: 4,
                AppliedHealing: 4,
                BeforeHealth: 50,
                AfterHealth: 54,
                BeforeVitalsRevision: 0,
                AfterVitalsRevision: 1
            },
            "Wither scales each rounded life-absorption hit before aggregation");

        var replay = committer.Commit(
            character,
            hits,
            healingReceivedBasisPoints: 5_000);
        Check.True(
            replay.ClaimedHitCount == 0 &&
            replay.AppliedHealing == 0 &&
            character.CurrentHp == 54 &&
            character.VitalsRevision == 1,
            "Withered life absorption retains its exact-once hit fence");

        character.CurrentHp = 99;
        var capped = committer.Commit(
            character,
            [
                new PveCommittedMonsterDamage(103, 903, 1, 11),
                new PveCommittedMonsterDamage(104, 904, 1, 11)
            ],
            healingReceivedBasisPoints: 5_000);
        Check.True(
            capped.AdjustedRequestedHealing == 4 &&
            capped.AppliedHealing == 1 &&
            character.CurrentHp == 100 &&
            character.VitalsRevision == 2,
            "Withered life absorption remains capped by missing HP");

        character.CurrentHp = 0;
        var dead = committer.Commit(
            character,
            [new PveCommittedMonsterDamage(105, 905, 1, 11)],
            healingReceivedBasisPoints: 5_000);
        Check.True(
            dead.ClaimedHitCount == 1 &&
            dead.AdjustedRequestedHealing == 2 &&
            dead.AppliedHealing == 0 &&
            character.CurrentHp == 0 &&
            character.VitalsRevision == 2,
            "Withered life absorption cannot revive a dead attacker");

        var root = FindRepositoryRoot();
        var seam = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.CombatSecondaryEffects.cs"));
        Check.True(
            seam.Contains(
                "_registry.AdjustElementalHealingReceived(",
                StringComparison.Ordinal) &&
            seam.Contains(
                "ElementalBasisPointMath.Portion(",
                StringComparison.Ordinal),
            "all centralized Legacy/ECS PVE life absorption uses the Wither seam");
    }
}
