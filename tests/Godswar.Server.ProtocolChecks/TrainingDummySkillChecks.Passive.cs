using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static async Task CheckPassiveCounterDamageAsync()
    {
        await CheckPassiveScalarCounterDamageAsync();
        await CheckPassiveAreaCounterDamageAsync();
    }

    private static async Task CheckPassiveScalarCounterDamageAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var target = Dummy();
        ConfigureCounterDamage(target);
        var attacker = Player(8001, 8001, "Tester", 0, 0);
        await using var fixture = await Fixture.CreateAsync(
            attacker,
            target,
            bindElementalOwnership: true);
        var attackerHealth = fixture.Attacker.CurrentHp;
        var revision = FindHittingRevision(
            fixture.Attacker,
            fixture.Target);

        var decision = await fixture.ResolveAsync(revision, now);

        Check.True(
            decision.Accepted &&
            decision.Combat.Eligibility.EntitlementKind ==
                PvpEntitlementKind.TrainingDummy &&
            decision.Combat.Resolution.Hit &&
            decision.Combat.Attacker?.CharacterId == fixture.Attacker.Id &&
            decision.Combat.Target?.CharacterId == fixture.Target.Id,
            "passive scalar keeps exact training admission and combat direction");
        Check.True(
            decision.Combat.ReboundDamage == 0 &&
            decision.Combat.ElementalDamageCommits.All(commit =>
                commit.Source.CharacterId != fixture.Target.Id ||
                commit.Target.CharacterId != fixture.Attacker.Id) &&
            fixture.Attacker.CurrentHp == attackerHealth,
            "passive scalar suppresses stat and elemental target counter-damage");
        var baseDamage = BaseSkillDamage(
            fixture.Attacker,
            fixture.Target,
            SpearHit(),
            decision.Combat.Resolution.EventId);
        Check.True(
            decision.Combat.AppliedDamage > 0 &&
            decision.Combat.AppliedDamage ==
                decision.Combat.Resolution.Damage &&
            decision.Combat.Resolution.Damage < baseDamage,
            "passive scalar preserves target defense and Gaia mitigation " +
            $"(applied={decision.Combat.AppliedDamage}, " +
            $"resolved={decision.Combat.Resolution.Damage}, " +
            $"base={baseDamage}, " +
            $"earth={fixture.Target.ElementalEquipment.CountFor(ElementKind.Earth)})");

        var attackerVisual =
            await fixture.AttackerSocket.ReadPacketAsync(40);
        var attackerDamage =
            await fixture.AttackerSocket.ReadPacketAsync(30);
        var attackerImpact =
            await fixture.AttackerSocket.ReadPacketAsync(24);
        var targetVisual = await fixture.TargetSocket.ReadPacketAsync(40);
        var targetDamage = await fixture.TargetSocket.ReadPacketAsync(30);
        var targetImpact = await fixture.TargetSocket.ReadPacketAsync(24);
        AssertAnimationDirection(
            attackerVisual,
            attackerImpact,
            LocalPlayerObjectId,
            fixture.TargetObjectId,
            "scalar attacker view");
        AssertAnimationDirection(
            targetVisual,
            targetImpact,
            fixture.AttackerObjectId,
            LocalPlayerObjectId,
            "scalar dummy view");
        AssertDamageDirection(
            attackerDamage,
            LocalPlayerObjectId,
            fixture.TargetObjectId,
            "scalar attacker view");
        AssertDamageDirection(
            targetDamage,
            fixture.AttackerObjectId,
            LocalPlayerObjectId,
            "scalar dummy view");
        await ReadVitalsAsync(
            fixture.AttackerSocket,
            decision.Combat.ChangedVitals.Count);
        await ReadVitalsAsync(
            fixture.TargetSocket,
            decision.Combat.ChangedVitals.Count);
        Check.True(
            fixture.AttackerSocket.Available == 0 &&
            fixture.TargetSocket.Available == 0,
            "scalar publishes one directed cast and no dummy-to-player damage packet");

        var committedEvent = new DeterministicCombatEventContext(
            decision.Combat.Resolution.EventId,
            fixture.Attacker.CurrentMap,
            fixture.Attacker.Id,
            fixture.Target.Id,
            now.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectSkill,
            Committed: true,
            IsPvp: true,
            decision.Combat.Eligibility);
        var deferredReflection = ElementalResonanceExecutionPolicy
            .PlanCommittedReflection(
                committedEvent,
                fixture.Target.ElementalEquipment,
                ElementalState(
                    fixture.Registry,
                    fixture.TargetSocket.Session).Resonance,
                decision.Combat.AppliedDamage,
                fixture.Attacker.MaxHp);
        Check.True(
            deferredReflection is
            {
                Kind: ResonanceDamageKind.GaiaReflection,
                SourceCharacterId: 7001,
                TargetId: 8001
            },
            "passive scalar does not consume the dummy's Gaia reflection replay state");
    }

    private static async Task CheckPassiveAreaCounterDamageAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-16T07:00:00Z");
        await using var fixture = await AreaFixture.CreateAsync(
            bindElementalOwnership: true);
        foreach (var dummy in fixture.Dummies)
        {
            ConfigureCounterDamage(dummy);
        }
        var attackerHealth = fixture.Attacker.CurrentHp;
        var revision = FindAreaHittingRevision(
            fixture.Attacker,
            fixture.Dummies,
            AreaSkill());

        var decision = await fixture.ResolveAsync(
            AreaSkill(),
            () => revision,
            now);

        Check.True(
            decision.Accepted &&
            decision.Combats.Count == 2 &&
            decision.Combats.All(combat =>
                combat.Resolution.Hit &&
                combat.Attacker?.CharacterId == fixture.Attacker.Id &&
                fixture.Dummies.Any(dummy =>
                    dummy.Id == combat.Target?.CharacterId) &&
                combat.ReboundDamage == 0 &&
                combat.AppliedDamage > 0 &&
                combat.AppliedDamage == combat.Resolution.Damage &&
                combat.Resolution.Damage < BaseSkillDamage(
                    fixture.Attacker,
                    combat.Target!.Character,
                    AreaSkill(),
                    combat.Resolution.EventId) &&
                combat.ElementalDamageCommits.All(commit =>
                    commit.Source.CharacterId != combat.Target?.CharacterId ||
                    commit.Target.CharacterId != fixture.Attacker.Id)) &&
            fixture.Attacker.CurrentHp == attackerHealth,
            "exact-dummy area keeps per-target mitigation and suppresses every counter-hit");

        var areaVisual = await fixture.AttackerSocket.ReadPacketAsync(40);
        var areaImpact = await fixture.AttackerSocket.ReadPacketAsync(24);
        var dummyAreaVisual =
            await fixture.FirstDummySocket.ReadPacketAsync(40);
        var dummyAreaImpact =
            await fixture.FirstDummySocket.ReadPacketAsync(24);
        var observerAreaVisual =
            await fixture.ObserverSocket.ReadPacketAsync(40);
        var observerAreaImpact =
            await fixture.ObserverSocket.ReadPacketAsync(24);
        var attackerWorldId = fixture.AttackerObjectId;
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                areaVisual.AsSpan(2, 2)) == 0x2738 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                areaVisual.AsSpan(4, 4)) == LocalPlayerObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                areaVisual.AsSpan(16, 4)) == LocalPlayerObjectId &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                areaImpact.AsSpan(2, 2)) == 0x273E &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                areaImpact.AsSpan(4, 4)) == LocalPlayerObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                areaImpact.AsSpan(8, 4)) == uint.MaxValue,
            "self-area publishes one local cast and impact before target damage");
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(
                dummyAreaVisual.AsSpan(4, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                dummyAreaVisual.AsSpan(16, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                dummyAreaImpact.AsSpan(4, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                dummyAreaImpact.AsSpan(8, 4)) == uint.MaxValue,
            "self-area translates the caster/self target for remote viewers");
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerAreaVisual.AsSpan(4, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerAreaVisual.AsSpan(16, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerAreaImpact.AsSpan(4, 4)) == attackerWorldId &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerAreaImpact.AsSpan(8, 4)) == uint.MaxValue,
            "self-area translates caster/self IDs for an ordinary observer");
        foreach (var combat in decision.Combats)
        {
            var damage = await fixture.AttackerSocket.ReadPacketAsync(30);
            AssertDamageDirection(
                damage,
                LocalPlayerObjectId,
                combat.Target!.ObjectId,
                "area attacker view");
            await ReadVitalsAsync(
                fixture.AttackerSocket,
                combat.ChangedVitals
                    .DistinctBy(static value => value.CharacterId)
                    .Count());
        }
        Check.Equal(
            0,
            fixture.AttackerSocket.Available,
            "area publishes one animation pair plus player-to-dummy damage and committed vitals");
    }

    private static void ConfigureCounterDamage(GameCharacter target)
    {
        var source = target.CalculatedStats ??
            throw new InvalidOperationException("Target stats are required.");
        target.CalculatedStats = new CharacterStats
        {
            CharacterId = target.Id,
            AccountId = target.AccountId,
            Name = target.Name,
            Profession = target.Profession,
            Level = target.Level,
            CurrentHp = target.CurrentHp,
            MaxHp = target.MaxHp,
            CurrentMp = target.CurrentMp,
            MaxMp = target.MaxMp,
            PhysicalAttack = source.PhysicalAttack,
            PhysicalDefense = source.PhysicalDefense,
            Hit = source.Hit,
            Dodge = source.Dodge,
            DamageRebound = 76_061,
            DamageReboundFlat = 7,
            BasicAttackIntervalMilliseconds =
                source.BasicAttackIntervalMilliseconds,
            BasicAttackRange = source.BasicAttackRange
        };
        SetElementalProfile(
            target,
            ElementalProfile((ElementKind.Earth, 10)));
    }

    private static void PrepareElementalOwnership(GameCharacter character)
    {
        character.CheckpointOwnerId = new Guid(
            character.Id,
            0,
            0,
            new byte[8]);
        character.CheckpointOwnerGeneration = 1;
    }

    private static void BindElementalOwnership(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character)
    {
        PrepareElementalOwnership(character);
        var ownership = new PlayerOwnershipFence(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
        registry.ReplaceAccountSession(character.AccountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                session,
                ownership),
            "training fixture binds authoritative elemental ownership");
    }

    private static long FindAreaHittingRevision(
        GameCharacter attacker,
        IReadOnlyList<GameCharacter> targets,
        SkillCombatDefinition definition)
    {
        var source = CombatCharacterStatsAdapter.FromCharacter(attacker);
        var skill = TrainingDummyDamageSkillPolicy.Snapshot(definition);
        for (var revision = 1L; revision <= 1_000; revision++)
        {
            if (targets.All(target =>
            {
                var targetStats = CombatCharacterStatsAdapter.ToTarget(
                    target.Level,
                    target.CalculatedStats!);
                var eventId = CombatEventIdentity.ForPlayerSkill(
                    attacker.Id,
                    target.Id,
                    attacker.VitalsRevision,
                    target.VitalsRevision,
                    revision,
                    skill.SkillId);
                return PlayerCombatRules.ResolvePvpSkillDamage(
                    source,
                    targetStats,
                    skill,
                    eventId).Hit;
            }))
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "Expected one all-hit area revision within 1,000 attempts.");
    }

    private static uint BaseSkillDamage(
        GameCharacter attacker,
        GameCharacter target,
        SkillCombatDefinition definition,
        ulong eventId) =>
        PlayerCombatRules.ResolvePvpSkillDamage(
            CombatCharacterStatsAdapter.FromCharacter(attacker),
            CombatCharacterStatsAdapter.ToTarget(
                target.Level,
                target.CalculatedStats!),
            TrainingDummyDamageSkillPolicy.Snapshot(definition),
            eventId).Damage;

    private static ElementalEquipmentProfile ElementalProfile(
        params (ElementKind Element, int Pieces)[] values)
    {
        var totals = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            static _ => 0);
        foreach (var value in values)
        {
            counts[value.Element] = value.Pieces;
        }

        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(
                value,
                counts[value]));
        return new(totals, counts, active);
    }

    private static void SetElementalProfile(
        GameCharacter character,
        ElementalEquipmentProfile profile)
    {
        var property = typeof(GameCharacter).GetProperty(
            nameof(GameCharacter.ElementalEquipment),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "GameCharacter.ElementalEquipment was not found.");
        property.SetValue(character, profile);
    }

    private static GameSessionRegistry.ElementalCombatSessionState
        ElementalState(
            GameSessionRegistry registry,
            ClientSession session)
    {
        var field = typeof(GameSessionRegistry).GetField(
            "_elementalCombatSessions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Elemental combat session storage was not found.");
        var states = field.GetValue(registry) as ConcurrentDictionary<
            ClientSession,
            GameSessionRegistry.ElementalCombatSessionState>
            ?? throw new InvalidOperationException(
                "Elemental combat session storage has an unexpected type.");
        return states[session];
    }

    private static async Task ReadVitalsAsync(
        RuntimePolicySessionSocket socket,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            var packet = await socket.ReadPacketAsync(16);
            Check.Equal(
                (ushort)0x2771,
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)),
                "passive training damage publishes captured vitals packets");
        }
    }

    private static void AssertDamageDirection(
        byte[] packet,
        uint expectedSource,
        uint expectedTarget,
        string scope)
    {
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)) ==
                0x272A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)) ==
                expectedSource &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)) ==
                expectedTarget,
            $"{scope} preserves player-to-dummy source/target ordering");
    }

    private static void AssertAnimationDirection(
        byte[] visual,
        byte[] impact,
        uint expectedSource,
        uint expectedTarget,
        string scope)
    {
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(
                visual.AsSpan(2, 2)) == 0x2738 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                visual.AsSpan(4, 4)) == expectedSource &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                visual.AsSpan(16, 4)) == expectedTarget &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                impact.AsSpan(2, 2)) == 0x273E &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(4, 4)) == expectedSource &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                impact.AsSpan(8, 4)) == expectedTarget,
            $"{scope} translates animation source and target identities");
    }
}
