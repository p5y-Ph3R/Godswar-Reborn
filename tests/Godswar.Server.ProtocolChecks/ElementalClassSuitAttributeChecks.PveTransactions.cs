using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckPveElementalTransactionAuthorityAsync()
    {
        await CheckDisconnectCannotCancelCommittedElementalHitAsync();
        await CheckDeathCannotCancelCommittedElementalDamageAsync();
        await CheckCapturedResourceCapsAsync();
        CheckPveHandlerTransactionOrdering();
    }

    private static async Task
        CheckDisconnectCannotCancelCommittedElementalHitAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var sourceOwnership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var observerOwnership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var source = ElementalLiveCharacter(1_420, 60, sourceOwnership);
        var observer = ElementalLiveCharacter(1_421, 61, observerOwnership);
        SetElementalProfile(
            source,
            LiveProfile((
                ElementKind.Earth,
                1,
                new ElementalEffectTotals(1_000, 0, 10_000))));
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_420;
        registry.InitializeMapMonsters(
            source.CurrentMap,
            [ElementalReachMonster(objectId, "ElementalTxnDisconnect")],
            at);
        BindElementalLiveSession(
            registry,
            sourceSocket.Session,
            source,
            sourceOwnership,
            at);
        BindElementalLiveSession(
            registry,
            observerSocket.Session,
            observer,
            observerOwnership,
            at);

        using var authority = registry.CapturePveElementalCommitAuthority(
                sourceSocket.Session,
                source)
            ?? throw new InvalidOperationException(
                "Disconnect transaction fixture captured no authority.");
        var primary = ApplyTransactionDamage(
            registry,
            sourceSocket.Session,
            source,
            objectId,
            damage: 1,
            at);
        registry.Remove(sourceSocket.Session);
        registry.RemoveAccountSession(source.AccountId, sourceSocket.Session);

        var committed = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectBasicAttack,
            [new(420_001, 0, primary)],
            at);
        Check.True(
            committed.Applications is
            [{ Effect: ElementalEffectKind.Fracture }],
            "disconnect after the primary mutation cannot cancel its captured target status");
        Check.True(
            registry.TryGetMonsterSnapshot(
                observerSocket.Session,
                observer.CurrentMap,
                objectId,
                out var observedTarget),
            "observer retains the disconnected source's world instance");
        var adjusted = registry.AdjustPveMonsterTargetStats(
            observerSocket.Session,
            observedTarget,
            at.AddMilliseconds(1),
            new CombatTargetStats
            {
                PhysicalDefense = 1_000,
                MagicDefense = 1_000
            });
        Check.True(
            adjusted.PhysicalDefense == 900 &&
            adjusted.MagicDefense == 900,
            "captured Fracture remains target-owned after source disconnect");

        registry.Remove(observerSocket.Session);
        registry.RemoveAccountSession(
            observer.AccountId,
            observerSocket.Session);
    }

    private static async Task
        CheckDeathCannotCancelCommittedElementalDamageAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var source = ElementalLiveCharacter(1_422, 62, ownership);
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Lightning, 3, default)));
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_421;
        registry.InitializeMapMonsters(
            source.CurrentMap,
            [ElementalReachMonster(objectId, "ElementalTxnDeath")],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            source,
            ownership,
            at);

        for (var ordinal = 1; ordinal <= 3; ordinal++)
        {
            using var primeAuthority =
                registry.CapturePveElementalCommitAuthority(
                    socket.Session,
                    source)
                ?? throw new InvalidOperationException(
                    "Zeus cadence fixture captured no authority.");
            var primary = ApplyTransactionDamage(
                registry,
                socket.Session,
                source,
                objectId,
                damage: 1,
                at.AddMilliseconds(ordinal));
            var primed = registry.CommitPveElementalHits(
                primeAuthority,
                CombatEventProvenance.DirectBasicAttack,
                [new(422_000UL + (ulong)ordinal, 0, primary)],
                at.AddMilliseconds(ordinal));
            Check.True(
                primed.DamageCommits.Count == 0,
                $"Zeus pre-trigger hit {ordinal} has no derived damage");
        }

        using var authority = registry.CapturePveElementalCommitAuthority(
                socket.Session,
                source)
            ?? throw new InvalidOperationException(
                "Terminal Zeus fixture captured no authority.");
        var finalPrimary = ApplyTransactionDamage(
            registry,
            socket.Session,
            source,
            objectId,
            damage: 900,
            at.AddMilliseconds(4));
        lock (source.VitalsSync)
        {
            source.CurrentHp = 0;
            source.MarkVitalsChanged();
        }
        registry.AdvancePlayerLifeRevision(
            socket.Session,
            at.AddMilliseconds(4));

        var committed = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectBasicAttack,
            [new(422_004, 0, finalPrimary)],
            at.AddMilliseconds(4));
        Check.True(
            committed.DamageCommits is
            [{ Kind: ResonanceDamageKind.ZeusBolt,
               DamageResult.Killed: true }] &&
            source.CurrentHp == 0 &&
            !committed.SourceRecovery.Applied,
            "source death after primary commit preserves terminal Zeus damage without proc revival");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(source.AccountId, socket.Session);
    }

    private static async Task CheckCapturedResourceCapsAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var source = ElementalLiveCharacter(1_423, 63, ownership);
        source.CurrentHp = 9_000;
        source.CurrentMp = 900;
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Dark, 10, default)));
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_422;
        registry.InitializeMapMonsters(
            source.CurrentMap,
            [ElementalReachMonster(objectId, "ElementalTxnCaps")],
            at);
        BindElementalLiveSession(
            registry,
            socket.Session,
            source,
            ownership,
            at);

        using var authority = registry.CapturePveElementalCommitAuthority(
                socket.Session,
                source)
            ?? throw new InvalidOperationException(
                "Resource-cap fixture captured no authority.");
        var primary = ApplyTransactionDamage(
            registry,
            socket.Session,
            source,
            objectId,
            damage: 1_000,
            at);
        lock (source.VitalsSync)
        {
            source.MaxHp = 100_000;
            source.MaxMp = 10_000;
        }

        var committed = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectSkill,
            [new(423_001, 0, primary)],
            at);
        Check.True(
            committed.SourceRecovery.BeforeHealth == 9_000 &&
            committed.SourceRecovery.AfterHealth == 9_820 &&
            committed.SourceRecovery.BeforeMana == 900 &&
            committed.SourceRecovery.AfterMana == 980,
            "Hades lifesteal and kill restoration use pre-progression captured resource caps");
        var replay = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectSkill,
            [new(423_001, 0, primary)],
            at);
        Check.True(
            replay == PveElementalCommitResult.Empty &&
            source.CurrentHp == 9_820 &&
            source.CurrentMp == 980,
            "one captured elemental authority cannot replay resource restoration");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(source.AccountId, socket.Session);
    }

    private static MonsterDamageResult ApplyTransactionDamage(
        GameSessionRegistry registry,
        Godswar.Server.Networking.ClientSession routingSession,
        GameCharacter source,
        uint objectId,
        uint damage,
        DateTimeOffset at)
    {
        Check.True(
            registry.TryGetMonsterSnapshot(
                routingSession,
                source.CurrentMap,
                objectId,
                out var target),
            "elemental transaction fixture resolves its target");
        Check.True(
            registry.TryApplyMonsterDamageGuarded(
                source.CurrentMap,
                objectId,
                damage,
                source.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                at,
                out var result),
            "elemental transaction fixture commits guarded primary damage");
        return result;
    }

    private static void CheckPveHandlerTransactionOrdering()
    {
        var root = FindPveTransactionRepositoryRoot();
        var handlers = new[]
        {
            new HandlerOrder(
                "GameClientHandler.MovementCombat.cs",
                "TryApplyMonsterDamage(",
                "DeliverMonsterHealthPacketToViewerAsync"),
            new HandlerOrder(
                "GameClientHandler.CombatEcsBasic.cs",
                "ResolvePlayerCombatEcs(",
                "DeliverMonsterHealthPacketToViewerAsync"),
            new HandlerOrder(
                "GameClientHandler.CombatSkill.cs",
                "TryReserveLegacyHostileSkill(",
                "PublishLegacyHostileMonsterSkillHitAsync"),
            new HandlerOrder(
                "GameClientHandler.CombatEcsSkill.cs",
                "TryClaimHostileSkillCooldown(",
                "DeliverMonsterPacketToViewerAsync"),
            new HandlerOrder(
                "GameClientHandler.CombatArea.cs",
                "TryReserveLegacyHostileSkill(",
                "await _session.SendAsync"),
            new HandlerOrder(
                "GameClientHandler.CombatEcsArea.cs",
                "TryClaimHostileSkillCooldown(",
                "await _session.SendAsync")
        };

        foreach (var handler in handlers)
        {
            var source = File.ReadAllText(Path.Combine(
                root,
                "src",
                "Godswar.Server",
                "Game",
                handler.FileName));
            var capture = source.IndexOf(
                "using var elementalAuthority",
                StringComparison.Ordinal);
            var admission = source.IndexOf(
                handler.AdmissionOrMutation,
                StringComparison.Ordinal);
            var commit = source.IndexOf(
                "var elementalCommit =",
                StringComparison.Ordinal);
            var derivedReward = source.IndexOf(
                "PreparePveElementalKillRewardsAsync",
                commit,
                StringComparison.Ordinal);
            var primaryReward = source.IndexOf(
                "PrepareMonsterKillRewardAsync",
                commit,
                StringComparison.Ordinal);
            var publication = source.IndexOf(
                handler.FirstPublication,
                commit,
                StringComparison.Ordinal);
            Check.True(
                capture >= 0 &&
                capture < admission &&
                admission < commit &&
                commit < derivedReward &&
                derivedReward < publication &&
                commit < primaryReward &&
                primaryReward < derivedReward &&
                primaryReward < publication,
                $"{handler.FileName} captures authority before admission/mutation and prepares primary then derived kill rewards before packet I/O");
            if (handler.FileName is
                "GameClientHandler.MovementCombat.cs" or
                "GameClientHandler.CombatEcsBasic.cs")
            {
                var interruptionBarrier = source.IndexOf(
                    "await InterruptPendingSkillCastAsync",
                    commit,
                    StringComparison.Ordinal);
                Check.True(
                    derivedReward < interruptionBarrier &&
                    interruptionBarrier < publication,
                    $"{handler.FileName} commits and durably prepares secondary effects before awaiting cast interruption, then publishes interruption before damage");
            }
        }
    }

    private static string FindPveTransactionRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate GodswarServer.sln for transaction checks.");
    }

    private readonly record struct HandlerOrder(
        string FileName,
        string AdmissionOrMutation,
        string FirstPublication);
}
