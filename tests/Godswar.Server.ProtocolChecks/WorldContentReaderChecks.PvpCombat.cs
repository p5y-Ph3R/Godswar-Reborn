using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static void CheckPvpWorldAuthority()
    {
        var combatMap = new GameplayMapDefinition(
            7,
            "combat",
            "Combat",
            7,
            0);
        var safeMap = new GameplayMapDefinition(
            8,
            "safe",
            "Safe",
            8,
            5);
        var content = GameplayContentCatalog.Empty with
        {
            Maps = [combatMap, safeMap]
        };
        var catalog = PvpWorldAuthorityCatalog.Create(content);
        var now = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        var spartan = Player(100, 7, GameDefaults.SpartaCamp);
        var athenian = Player(200, 7, GameDefaults.AthensCamp);

        var allowed = catalog.EvaluateOpposingFaction(
            spartan,
            athenian,
            now);
        Check.True(
            allowed.Allowed &&
            allowed.EntitlementKind ==
                PvpEntitlementKind.OpposingFaction &&
            allowed.Admits(spartan.Id, athenian.Id, 7),
            "published mode-zero map admits only identity-bound opposing-faction PvP");
        Check.True(
            !catalog.EvaluateOpposingFaction(
                spartan,
                Player(201, 7, GameDefaults.SpartaCamp),
                now).Allowed,
            "same-faction targets are denied");

        spartan.CurrentMap = 8;
        athenian.CurrentMap = 8;
        Check.True(
            catalog.IsSafeZone(8) &&
            !catalog.EvaluateOpposingFaction(
                spartan,
                athenian,
                now).Allowed &&
            PvpWorldAuthorityCatalog.Empty.IsSafeZone(7),
            "safe and unpublished maps fail closed");

        var changed = content with
        {
            Maps = [combatMap with { MapMode = 5 }, safeMap]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(content).Sha256 !=
            WorldContentRevisionHasher.HashGameplay(changed).Sha256,
            "map PvP mode participates in the sealed gameplay revision");
    }

    private static GameCharacter Player(
        int id,
        byte map,
        byte camp) =>
        new()
        {
            Id = id,
            AccountId = id,
            Name = $"Player{id}",
            CurrentMap = map,
            Camp = camp,
            Level = 120,
            CurrentHp = 10_000,
            MaxHp = 10_000
        };
}
