using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckElementalLivePvpAsync()
    {
        await CheckPvpDirectStatusSelectionAsync();
        await CheckPvpResonanceTransactionAsync();
        await CheckPvpShockControlAuthorityAsync();
        await CheckPvpIncomingCadencesAsync();
        await CheckPvpCreditedKillRecoveryAsync();
    }

    private static async Task CheckPvpDirectStatusSelectionAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var attackerOwnership = ElementalPvpOwnership(1);
        var targetOwnership = ElementalPvpOwnership(2);
        var attacker = ElementalPvpCharacter(
            1_501,
            51,
            GameDefaults.SpartaCamp,
            attackerOwnership);
        var target = ElementalPvpCharacter(
            1_502,
            52,
            GameDefaults.AthensCamp,
            targetOwnership);
        BindElementalLiveSession(
            registry,
            attackerSocket.Session,
            attacker,
            attackerOwnership);
        BindElementalLiveSession(
            registry,
            targetSocket.Session,
            target,
            targetOwnership);
        var at = new DateTimeOffset(
            2026, 8, 14, 1, 0, 0, TimeSpan.Zero);

        SetElementalProfile(
            attacker,
            LiveProfile(
                (ElementKind.Light, 0, new(1_000, 0, 10_000)),
                (ElementKind.Earth, 0, new(900, 0, 10_000))));
        var dazzleRevision = FindElementalPvpRevision(
            attacker,
            target,
            ElementKind.Light,
            static value => value.Hit);
        var dazzle = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            dazzleRevision,
            at,
            CancellationToken.None);
        Check.True(
            dazzle.Accepted &&
            dazzle.ElementalApplications is
                [{ Element: ElementKind.Light,
                   Effect: ElementalEffectKind.Dazzle }],
            "live PvP selects only the strongest server-owned direct-hit element");

        var targetFence = new ElementalCombatSessionFence(
            target.Id,
            target.CurrentMap,
            targetOwnership);
        Check.True(
            registry.TryGetElementalStatusAdjustment(
                targetSocket.Session,
                targetFence,
                at.AddMilliseconds(1).ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: 1_000,
                magicDefense: 1_000,
                hitRating: 1_000,
                healingReceived: 1_000,
                out var dazzled) &&
            dazzled.HitRating == 900 &&
            dazzled.PhysicalDefense == 1_000,
            "committed Dazzle is active before the next shared PvP resolution");

        var targetCombat = CombatCharacterStatsAdapter.ToTarget(
            attacker.Level,
            attacker.CalculatedStats!);
        var targetAttackerStats = CombatCharacterStatsAdapter
            .FromCharacter(target);
        var dazzledAttacker = targetAttackerStats with
        {
            Hit = checked((int)ElementalBasisPointMath.ScaleDown(
                targetAttackerStats.Hit,
                1_000))
        };
        var reverseRevision = FindElementalPvpRevision(
            target,
            attacker,
            element: null,
            resolution => PlayerCombatRules.ResolveBasicAttack(
                    dazzledAttacker,
                    targetCombat,
                    resolution.EventId)
                .Hit);
        var reverseEventId = CombatEventIdentity.ForPlayerBasicAttack(
            target.Id,
            attacker.Id,
            target.VitalsRevision,
            attacker.VitalsRevision,
            reverseRevision);
        var expectedDazzled = PlayerCombatRules.ResolveBasicAttack(
            dazzledAttacker,
            targetCombat,
            reverseEventId);
        var reverse = await registry.ResolvePvpBasicAttackAsync(
            targetSocket.Session,
            WorldObjectIds.ForPlayer(attacker.Id),
            target.PositionX,
            target.PositionZ,
            reverseRevision,
            at.AddMilliseconds(2),
            CancellationToken.None);
        Check.True(
            reverse.Resolution.Rolls.HitChanceBasisPoints ==
                expectedDazzled.Rolls.HitChanceBasisPoints &&
            reverse.Resolution.Rolls.HitRollBasisPoints ==
                expectedDazzled.Rolls.HitRollBasisPoints,
            "Dazzle-adjusted Hit feeds the authoritative shared resolver");

        SetElementalProfile(
            attacker,
            LiveProfile((
                ElementKind.Earth,
                0,
                new ElementalEffectTotals(1_000, 0, 10_000))));
        var fractureAt = at.AddMilliseconds(10);
        var fractureRevision = FindElementalPvpRevision(
            attacker,
            target,
            ElementKind.Earth,
            static value => value.Hit);
        var fracture = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            fractureRevision,
            fractureAt,
            CancellationToken.None);
        Check.True(
            fracture.ElementalApplications is
                [{ Effect: ElementalEffectKind.Fracture }],
            "committed Earth hit applies one Fracture status");

        var fracturedRevision = FindElementalPvpRevision(
            attacker,
            target,
            ElementKind.Earth,
            static value => value.Hit);
        var fractured = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            fracturedRevision,
            fractureAt.AddMilliseconds(1),
            CancellationToken.None);
        Check.Equal(
            900,
            fractured.Resolution.Evidence.EffectiveDefense,
            "Fracture-adjusted defense feeds the authoritative shared resolver");

        SetElementalProfile(
            attacker,
            LiveProfile((
                ElementKind.Lightning,
                0,
                new ElementalEffectTotals(1_000, 0, 10_000))));
        var shockAt = at.AddMilliseconds(20);
        var shockRevision = FindElementalPvpRevision(
            attacker,
            target,
            ElementKind.Lightning,
            static value => value.Hit);
        var shock = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            shockRevision,
            shockAt,
            CancellationToken.None);
        Check.True(
            shock.ElementalApplications is
                [{ Effect: ElementalEffectKind.Shock }],
            "committed Lightning hit applies Shock");

        var callbackCalls = 0;
        long NextRevision()
        {
            callbackCalls++;
            return 99_999;
        }

        var blocked = await registry.ResolvePvpBasicAttackAsync(
            targetSocket.Session,
            WorldObjectIds.ForPlayer(attacker.Id),
            target.PositionX,
            target.PositionZ,
            NextRevision,
            shockAt.AddMilliseconds(1),
            CancellationToken.None);
        Check.True(
            !blocked.Accepted &&
            blocked.RejectionReason ==
                PvpBasicAttackRejectionReason.ElementalControl &&
            callbackCalls == 0,
            "Shock rejects PvP action before consuming an admitted combat revision");

        RemoveElementalPvpPlayers(
            registry,
            (attackerSocket.Session, attacker),
            (targetSocket.Session, target));
    }

    private static async Task CheckPvpResonanceTransactionAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var firstSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var secondSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_511, 61, GameDefaults.SpartaCamp, ElementalPvpOwnership(11));
        var target = ElementalPvpCharacter(
            1_512, 62, GameDefaults.AthensCamp, ElementalPvpOwnership(12));
        var first = ElementalPvpCharacter(
            1_513, 63, GameDefaults.AthensCamp, ElementalPvpOwnership(13));
        var second = ElementalPvpCharacter(
            1_514, 64, GameDefaults.AthensCamp, ElementalPvpOwnership(14));
        target.PositionX = 1f;
        first.PositionX = 2f;
        second.PositionX = 3f;
        source.CurrentHp = 90_000;
        source.MaxHp = 100_000;
        target.CurrentHp = target.MaxHp = 100_000;
        first.CurrentHp = first.MaxHp = 100_000;
        second.CurrentHp = second.MaxHp = 100_000;
        SetElementalProfile(
            source,
            LiveProfile(
                (ElementKind.Lightning, 10, default),
                (ElementKind.Dark, 3, default)));
        SetElementalProfile(
            target,
            LiveProfile((ElementKind.Earth, 10, default)));
        BindPvpFixture(registry, sourceSocket.Session, source);
        BindPvpFixture(registry, targetSocket.Session, target);
        BindPvpFixture(registry, firstSocket.Session, first);
        BindPvpFixture(registry, secondSocket.Session, second);
        var at = new DateTimeOffset(
            2026, 8, 14, 2, 0, 0, TimeSpan.Zero);
        PvpBasicAttackDecision decision = null!;
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var revision = FindElementalPvpRevision(
                source,
                target,
                element: null,
                static value => value.Hit);
            decision = await registry.ResolvePvpBasicAttackAsync(
                sourceSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                source.PositionX,
                source.PositionZ,
                revision,
                at.AddMilliseconds(ordinal),
                CancellationToken.None);
        }

        var kinds = decision.ElementalDamageCommits
            .Select(static value => value.Kind)
            .ToHashSet();
        Check.True(
            decision.Accepted &&
            kinds.SetEquals(
            [
                ResonanceDamageKind.ZeusBolt,
                ResonanceDamageKind.ZeusChain,
                ResonanceDamageKind.ZeusStormCrown,
                ResonanceDamageKind.GaiaReflection
            ]) &&
            decision.ElementalControlCommits is [{ StunMilliseconds: 1_000 }] &&
            decision.ElementalHealthRecovery > 0 &&
            decision.ChangedVitals.Select(static value => value.CharacterId)
                .Order()
                .SequenceEqual(new[] { source.Id, target.Id, first.Id, second.Id }),
            "one PvP transaction commits terminal 3/6/10 resonance damage, stun, reflection, and lifesteal once");
        Check.True(
            decision.ElementalDamageCommits.All(static value =>
                value.SourceEventId != 0 && value.AppliedDamage > 0) &&
            first.CurrentHp < first.MaxHp &&
            second.CurrentHp < second.MaxHp &&
            source.CurrentHp < 90_000,
            "terminal resonance damage uses authoritative additional targets and cannot recurse into healing/reflection");

        RemoveElementalPvpPlayers(
            registry,
            (sourceSocket.Session, source),
            (targetSocket.Session, target),
            (firstSocket.Session, first),
            (secondSocket.Session, second));
    }

    private static GameSessionRegistry ElementalPvpRegistry() =>
        new(gameplayCatalogs: GameplayContentTestFixtures.Runtime);

    private static PlayerOwnershipFence ElementalPvpOwnership(int ordinal) =>
        new(new Guid(ordinal, 0, 0, new byte[8]), 1);

    private static GameCharacter ElementalPvpCharacter(
        int characterId,
        int accountId,
        byte camp,
        PlayerOwnershipFence ownership,
        int physicalAttack = 2_000)
    {
        var character = ElementalLiveCharacter(
            characterId,
            accountId,
            ownership);
        character.CurrentMap = 7;
        character.Camp = camp;
        character.Level = 120;
        character.CurrentHp = 100_000;
        character.MaxHp = 100_000;
        character.CurrentMp = 1_000;
        character.MaxMp = 1_000;
        character.CalculatedStats = new CharacterStats
        {
            CharacterId = characterId,
            AccountId = accountId,
            Name = character.Name,
            Profession = character.Profession,
            Level = character.Level,
            CurrentHp = character.CurrentHp,
            MaxHp = character.MaxHp,
            CurrentMp = character.CurrentMp,
            MaxMp = character.MaxMp,
            PhysicalAttack = physicalAttack,
            PhysicalDefense = 1_000,
            Hit = 5_000,
            Dodge = 100,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 10f
        };
        return character;
    }

    private static long FindElementalPvpRevision(
        GameCharacter attacker,
        GameCharacter target,
        ElementKind? element,
        Func<CombatResolution, bool> predicate)
    {
        var targetStats = CombatCharacterStatsAdapter.ToTarget(
            target.Level,
            target.CalculatedStats!);
        for (var revision = 1L; revision <= 100_000; revision++)
        {
            var eventId = CombatEventIdentity.ForPlayerBasicAttack(
                attacker.Id,
                target.Id,
                attacker.VitalsRevision,
                target.VitalsRevision,
                revision);
            var resolution = PlayerCombatRules.ResolveBasicAttack(
                CombatCharacterStatsAdapter.FromCharacter(attacker),
                targetStats,
                eventId);
            if (!predicate(resolution))
            {
                continue;
            }

            var combatEvent = new DeterministicCombatEventContext(
                eventId,
                attacker.CurrentMap,
                attacker.Id,
                target.Id,
                0,
                CombatEventProvenance.DirectBasicAttack,
                Committed: true,
                IsPvp: false,
                default);
            if (!element.HasValue ||
                ElementalEffectExecutionPolicy
                    .DeterministicRollBasisPoints(
                        combatEvent,
                        element.Value) < 2_000)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "No deterministic elemental PvP sample matched the predicate.");
    }

    private static void BindPvpFixture(
        GameSessionRegistry registry,
        Godswar.Server.Networking.ClientSession session,
        GameCharacter character,
        DateTimeOffset? joinedAt = null) =>
        BindElementalLiveSession(
            registry,
            session,
            character,
            new PlayerOwnershipFence(
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration),
            joinedAt);

    private static void RemoveElementalPvpPlayers(
        GameSessionRegistry registry,
        params (Godswar.Server.Networking.ClientSession Session,
            GameCharacter Character)[] players)
    {
        foreach (var player in players)
        {
            registry.Remove(player.Session);
            registry.RemoveAccountSession(
                player.Character.AccountId,
                player.Session);
        }
    }
}
