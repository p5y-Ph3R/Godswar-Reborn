using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private const int SharedPveIdentity = 1_430;

    private static async Task CheckCrossDomainCombatIdentityAsync()
    {
        CheckCrossDomainEventContracts();
        await CheckSameNumberPvePrimaryAndKillAsync();
        await CheckSameNumberPveChainTargetAsync();
    }

    private static void CheckCrossDomainEventContracts()
    {
        var at = new DateTimeOffset(
            2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        var pveDirect = new DeterministicCombatEventContext(
            EventId: 430_001,
            MapId: 0,
            SourceCharacterId: SharedPveIdentity,
            TargetCharacterId: SharedPveIdentity,
            at.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectBasicAttack,
            Committed: true,
            IsPvp: false,
            default);
        var syntheticSelfAdmission = new PvpEligibilityResult(
            Allowed: true,
            PvpEligibilityFailure.None,
            PvpEntitlementKind.MutualDuel,
            PvpCombatCaps.Current,
            Guid.Parse("14301430-1430-1430-1430-143014301430"),
            SharedPveIdentity,
            SharedPveIdentity,
            MapId: 0);
        var pvpSelf = pveDirect with
        {
            EventId = 430_002,
            IsPvp = true,
            PvpEligibility = syntheticSelfAdmission
        };
        var creditedPveKill = AuthoredElementalCombatV1
            .CreditedPveKillEvent(
                430_001,
                SharedPveIdentity,
                SharedPveIdentity,
                mapId: 0,
                killOrdinal: 1,
                at);
        var monsterCandidate = new ResonanceTargetCandidate(
            SharedPveIdentity,
            MapId: 0,
            DistanceMillimeters: 0,
            IsAlive: true,
            IsBoss: false,
            ResonanceTargetAuthority.AuthoritativeMonster,
            default);
        var playerCandidate = monsterCandidate with
        {
            Authority = ResonanceTargetAuthority.AdmittedPlayer,
            PvpAdmission = syntheticSelfAdmission
        };

        Check.True(
            pveDirect.IsCommittedDirectHit && creditedPveKill.IsValid,
            "PvE accepts equal numeric character and monster IDs from distinct identity domains");
        Check.True(
            !pvpSelf.IsValid &&
            monsterCandidate.IsAdmitted(SharedPveIdentity) &&
            !playerCandidate.IsAdmitted(SharedPveIdentity),
            "same-entity rejection is confined to PvP player authority");
    }

    private static async Task CheckSameNumberPvePrimaryAndKillAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var source = ElementalLiveCharacter(
            SharedPveIdentity,
            accountId: 70,
            ownership);
        source.CurrentHp = 8_000;
        source.CurrentMp = 800;
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Dark, 10, default)));
        var at = DateTimeOffset.UtcNow;
        registry.InitializeMapMonsters(
            source.CurrentMap,
            [ElementalReachMonster(
                SharedPveIdentity,
                "ElementalSharedPrimary")],
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
                "same-number primary fixture captured no authority");
        var primary = ApplyTransactionDamage(
            registry,
            socket.Session,
            source,
            SharedPveIdentity,
            damage: 1_000,
            at);
        var committed = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectBasicAttack,
            [new(430_101, 0, primary)],
            at);

        Check.True(
            primary.Killed &&
            committed.SourceRecovery.Applied &&
            source.CurrentHp > 8_000 &&
            source.CurrentMp > 800,
            "same-number PvE direct kill reaches credited-kill restoration");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(source.AccountId, socket.Session);
    }

    private static async Task CheckSameNumberPveChainTargetAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        const int sourceCharacterId = SharedPveIdentity + 1;
        const uint primaryObjectId = 9_431;
        var source = ElementalLiveCharacter(
            sourceCharacterId,
            accountId: 71,
            ownership);
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Lightning, 6, default)));
        var at = DateTimeOffset.UtcNow;
        registry.InitializeMapMonsters(
            source.CurrentMap,
            [
                ElementalReachMonster(
                    primaryObjectId,
                    "ElementalChainPrimary"),
                ElementalReachMonster(
                    sourceCharacterId,
                    "ElementalSharedChainTarget")
            ],
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
                "same-number chain fixture captured no authority");
        var primary = ApplyTransactionDamage(
            registry,
            socket.Session,
            source,
            primaryObjectId,
            damage: 100,
            at);
        var committed = registry.CommitPveElementalHits(
            authority,
            CombatEventProvenance.DirectBasicAttack,
            [new(431_101, 0, primary)],
            at);

        Check.True(
            committed.DamageCommits is
            [
                {
                    Kind: ResonanceDamageKind.ZeusChain,
                    DamageResult.ObjectId: sourceCharacterId
                }
            ],
            "Zeus chain retains a monster whose object ID equals the source character ID");

        registry.Remove(socket.Session);
        registry.RemoveAccountSession(source.AccountId, socket.Session);
    }
}
