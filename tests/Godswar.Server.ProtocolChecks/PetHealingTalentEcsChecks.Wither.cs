namespace Godswar.Server.ProtocolChecks;

internal static partial class PetHealingTalentEcsChecks
{
    private static void CheckWitheredHealing()
    {
        var reduced = CreateFixture(currentHp: 50, maximumHp: 100);
        Queue(
            reduced,
            eventId: 1,
            damage: 10,
            resolvedAt: Start,
            healingReceivedBasisPoints: 5_000);
        reduced.Scheduler.RunTick(TimeSpan.Zero);
        var reducedHealing = Heals(reduced).Single();
        Check.True(
            reducedHealing is
            {
                ResolvedHealing: 12,
                AppliedHealing: 12,
                BeforeHealth: 40,
                AfterHealth: 52
            } &&
            reducedHealing.CooldownReadyAt == Start.AddSeconds(180),
            "Wither scales pet Healing before its unchanged 180-second cooldown");

        var suppressed = CreateFixture(currentHp: 50, maximumHp: 100);
        Queue(
            suppressed,
            eventId: 1,
            damage: 10,
            resolvedAt: Start,
            healingReceivedBasisPoints: 0);
        suppressed.Scheduler.RunTick(TimeSpan.Zero);
        Check.True(
            Heals(suppressed).Length == 0 &&
            suppressed.World.Get<
                Godswar.Server.World.Components.Players
                    .PlayerVitalsComponent>(
                    suppressed.Player.Entity).CurrentHp == 40,
            "fully suppressed pet Healing does not mutate HP");

        Queue(
            suppressed,
            eventId: 2,
            damage: 1,
            resolvedAt: Start.AddSeconds(1));
        suppressed.Scheduler.RunTick(TimeSpan.Zero);
        var admitted = Heals(suppressed).Single();
        Check.True(
            admitted.AppliedHealing == 25 &&
            admitted.AfterHealth == 64 &&
            admitted.CooldownReadyAt == Start.AddSeconds(181),
            "suppressed Healing does not consume the pet cooldown");

        var root = FindPetHealingRepositoryRoot();
        var monsterAttack = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "GameSessionRegistry.MonsterAttacksEcs.cs"));
        var adjustment = monsterAttack.IndexOf(
            "AdjustMonsterIncomingElementalHealingLocked(",
            StringComparison.Ordinal);
        var request = monsterAttack.IndexOf(
            "new PlayerMonsterDamageEcsRequest(",
            StringComparison.Ordinal);
        var adapter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "PlayerVitalsDamageEcsAdapter.cs"));
        var system = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "World",
            "Systems",
            "Combat",
            "PetHealingTalentSystem.cs"));
        Check.True(
            adjustment >= 0 &&
            request > adjustment &&
            monsterAttack.Contains(
                "petHealingReceivedBasisPoints",
                StringComparison.Ordinal) &&
            adapter.Contains(
                "request.HealingReceivedBasisPoints",
                StringComparison.Ordinal) &&
            system.Contains(
                "intent.HealingReceivedBasisPoints",
                StringComparison.Ordinal),
            "live ECS monster attacks carry authoritative Wither into pet Healing");
    }

    private static string FindPetHealingRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Reborn.sln")) ||
                File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for Pet Healing checks.");
    }
}
