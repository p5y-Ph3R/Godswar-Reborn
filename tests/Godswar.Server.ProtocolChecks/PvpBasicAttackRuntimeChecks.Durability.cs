using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    private static async Task CheckPostCommitCancellationDurabilityAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            700,
            GameDefaults.SpartaCamp,
            physicalAttack: 100_000,
            hit: 5_000);
        var target = Player(
            800,
            GameDefaults.AthensCamp,
            physicalDefense: 0,
            dodge: 0,
            damageRebound: 1_000);
        target.CurrentHp = 100;

        var store = new PvpVitalsRecordingStore();
        var registry = new GameSessionRegistry(
            store: store,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);
        var now = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        Check.True(
            await registry.SetPersistentRuntimeStatusAndPublishAsync(
                targetSocket.Session,
                MountCatalog.RuntimeStatusKind,
                statusId: 1,
                priority: 1,
                beneficial: false,
                movementSpeedBonus: 0.25f,
                active: true,
                now,
                "pvp-death-cancellation-fixture",
                CancellationToken.None),
            "PvP cancellation fixture starts with a live Ride status");

        var revision = FindRevision(
            attacker,
            target,
            static resolution => resolution.Hit);
        using var publicationCancellation = new CancellationTokenSource();
        var barrierCalled = false;
        Task? CancelBeforeFirstPublication()
        {
            barrierCalled = true;
            publicationCancellation.Cancel();
            return null;
        }

        var publicationCanceled = false;
        try
        {
            _ = await registry.ResolvePvpBasicAttackAsync(
                attackerSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                attacker.PositionX,
                attacker.PositionZ,
                () => revision,
                now,
                publicationCancellation.Token,
                CancelBeforeFirstPublication);
        }
        catch (OperationCanceledException)
        {
            publicationCanceled = true;
        }

        var saves = store.Saves;
        Check.True(
            barrierCalled && publicationCanceled,
            "caller cancellation is injected at the first PvP publication boundary");
        Check.True(
            target.CurrentHp == 0 && attacker.CurrentHp < attacker.MaxHp,
            "terminal primary damage and Rebound mutate both PvP participants");
        Check.True(
            saves.Count == 2 &&
            saves.All(static save => !save.CancellationCanBeRequested) &&
            saves.Single(save => save.CharacterId == attacker.Id).CurrentHp ==
                attacker.CurrentHp &&
            saves.Single(save => save.CharacterId == target.Id).CurrentHp ==
                target.CurrentHp,
            "both committed PvP vitals checkpoints precede caller-cancellable publication");
        Check.True(
            !registry.IsRuntimeStatusActive(
                targetSocket.Session,
                MountCatalog.RuntimeStatusKind,
                now),
            "caller cancellation cannot skip committed PvP death-status cleanup");

        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }

    private readonly record struct SavedPvpVitals(
        int CharacterId,
        int CurrentHp,
        int CurrentMp,
        long VitalsRevision,
        bool CancellationCanBeRequested);

    private sealed class PvpVitalsRecordingStore : GameStoreTestStub
    {
        private readonly List<SavedPvpVitals> _saves = [];

        public IReadOnlyList<SavedPvpVitals> Saves => _saves.ToArray();

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            _ = accountId;
            _saves.Add(new(
                characterId,
                currentHp,
                currentMp,
                vitalsRevision,
                cancellationToken.CanBeCanceled));
            return Task.CompletedTask;
        }
    }
}
