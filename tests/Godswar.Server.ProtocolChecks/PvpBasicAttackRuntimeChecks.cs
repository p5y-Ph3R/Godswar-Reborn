using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string CheckName =
        "Default-deny authoritative PvP basic attack";

    public static async Task RunAsync()
    {
        Check.True(
            SkillCombatResolver.MustRejectHostilePlayerTarget(
                selectedTargetIsOtherPlayer: true) &&
            !SkillCombatResolver.MustRejectHostilePlayerTarget(
                selectedTargetIsOtherPlayer: false),
            "hostile PvP skills remain capture-gated while PvP basic attacks are live");
        await CheckCommittedHitAsync();
        await CheckStatReboundPacketAsync();
        await CheckMissAndAdmissionDenialAsync();
        await CheckPostCommitCancellationDurabilityAsync();
    }

    private static async Task CheckStatReboundPacketAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            500,
            GameDefaults.SpartaCamp,
            physicalAttack: 1_000,
            hit: 5_000);
        var target = Player(
            600,
            GameDefaults.AthensCamp,
            physicalDefense: 100,
            dodge: 0,
            damageRebound: 1_000);
        var registry = Registry();
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);
        var revision = FindRevision(
            attacker,
            target,
            static resolution => resolution.Hit);

        var decision = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            revision,
            DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            CancellationToken.None);
        Check.True(
            decision.Accepted && decision.ReboundDamage > 0,
            "PvP stat Rebound commits terminal attacker damage");
        _ = await attackerSocket.ReadPacketAsync(30);
        _ = await targetSocket.ReadPacketAsync(30);
        var attackerRebound = await attackerSocket.ReadPacketAsync(30);
        var targetRebound = await targetSocket.ReadPacketAsync(30);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(
                attackerRebound.AsSpan(4)) ==
                    WorldObjectIds.ForPlayer(target.Id) &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                attackerRebound.AsSpan(20)) == 0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                attackerRebound.AsSpan(24)) ==
                    decision.ReboundDamage &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                targetRebound.AsSpan(4)) == 0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                targetRebound.AsSpan(20)) ==
                    WorldObjectIds.ForPlayer(attacker.Id),
            "stat Rebound publishes one target-to-attacker native damage packet with local identity mapping");
        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }

    private static async Task CheckCommittedHitAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            100,
            GameDefaults.SpartaCamp,
            physicalAttack: 1_000,
            hit: 5_000);
        var target = Player(
            200,
            GameDefaults.AthensCamp,
            physicalDefense: 100,
            dodge: 0);
        var registry = Registry();
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);
        var revision = FindRevision(
            attacker,
            target,
            static resolution => resolution.Hit);
        var beforeRevision = target.VitalsRevision;

        var decision = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            revision,
            DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            CancellationToken.None);

        Check.True(
            decision.Accepted &&
            decision.Eligibility.Allowed &&
            decision.Resolution.Hit &&
            decision.AppliedDamage > 0 &&
            target.CurrentHp < target.MaxHp &&
            target.VitalsRevision == beforeRevision + 1,
            "opposing factions on a published combat map commit one authoritative hit");

        var attackerPacket = await attackerSocket.ReadPacketAsync(30);
        var targetPacket = await targetSocket.ReadPacketAsync(30);
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(attackerPacket.AsSpan(2)) ==
                Opcodes.BasicAttack &&
            BinaryPrimitives.ReadUInt32LittleEndian(attackerPacket.AsSpan(4)) ==
                0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(attackerPacket.AsSpan(20)) ==
                WorldObjectIds.ForPlayer(target.Id),
            "attacker receives local-source and authoritative world-target identities");
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(targetPacket.AsSpan(4)) ==
                WorldObjectIds.ForPlayer(attacker.Id) &&
            BinaryPrimitives.ReadUInt32LittleEndian(targetPacket.AsSpan(20)) ==
                0x1448 &&
            targetPacket[29] == (byte)decision.Resolution.Outcome,
            "target receives world-source and local-target identities with the resolved outcome");
        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }

    private static async Task CheckMissAndAdmissionDenialAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            300,
            GameDefaults.SpartaCamp,
            physicalAttack: 1_000,
            hit: 0);
        var target = Player(
            400,
            GameDefaults.AthensCamp,
            physicalDefense: 100,
            dodge: 50_000);
        var registry = Registry();
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);
        var revision = FindRevision(
            attacker,
            target,
            static resolution => !resolution.Hit);
        var beforeHealth = target.CurrentHp;
        var beforeRevision = target.VitalsRevision;

        var miss = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            revision,
            DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
            CancellationToken.None);
        Check.True(
            miss.Accepted &&
            miss.Resolution.Outcome == CombatHitOutcome.Miss &&
            miss.Resolution.CapturedDamageValue == uint.MaxValue &&
            target.CurrentHp == beforeHealth &&
            target.VitalsRevision == beforeRevision,
            "PvP miss uses the captured sentinel and cannot mutate target vitals");
        var missPacket = await attackerSocket.ReadPacketAsync(30);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(missPacket.AsSpan(24)) ==
                uint.MaxValue &&
            missPacket[29] == (byte)CombatHitOutcome.Miss,
            "PvP miss publishes exact captured damage/outcome bytes");

        target.Camp = GameDefaults.SpartaCamp;
        var admittedRevisionCalls = 0;
        long NextAdmittedRevision()
        {
            admittedRevisionCalls++;
            return revision + 1;
        }

        PvpBasicAttackDecision? denied = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            denied = await registry.ResolvePvpBasicAttackAsync(
                attackerSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                attacker.PositionX,
                attacker.PositionZ,
                NextAdmittedRevision,
                DateTimeOffset.Parse("2026-08-14T00:00:01Z"),
                CancellationToken.None);
        }

        Check.True(
            denied is { Accepted: false } &&
            denied.RejectionReason ==
                PvpBasicAttackRejectionReason.AdmissionDenied &&
            admittedRevisionCalls == 0 &&
            target.CurrentHp == beforeHealth &&
            target.VitalsRevision == beforeRevision,
            "rejected PvP spam cannot consume an admitted combat revision");

        target.Camp = GameDefaults.AthensCamp;
        var admitted = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            NextAdmittedRevision,
            DateTimeOffset.Parse("2026-08-14T00:00:02Z"),
            CancellationToken.None);
        Check.True(admitted.Accepted && admittedRevisionCalls == 1,
            "one admitted PvP attempt consumes exactly one combat revision");
        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }

    private static long FindRevision(
        GameCharacter attacker,
        GameCharacter target,
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
            if (predicate(resolution))
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "No deterministic PvP combat sample matched the requested outcome.");
    }

    private static GameSessionRegistry Registry() =>
        new(gameplayCatalogs: GameplayContentTestFixtures.Runtime);

    private static void Join(
        GameSessionRegistry registry,
        RuntimePolicySessionSocket socket,
        GameCharacter character) =>
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));

    private static GameCharacter Player(
        int id,
        byte camp,
        int physicalAttack = 500,
        int physicalDefense = 100,
        int hit = 1_000,
        int dodge = 100,
        int damageRebound = 0)
    {
        var character = new GameCharacter
        {
            Id = id,
            AccountId = id,
            Name = $"Player{id}",
            CurrentMap = 7,
            Camp = camp,
            Profession = 0,
            Level = 120,
            PositionX = 0,
            PositionZ = 0,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 1_000,
            MaxMp = 1_000
        };
        character.CalculatedStats = new CharacterStats
        {
            CharacterId = id,
            AccountId = id,
            Name = character.Name,
            Profession = character.Profession,
            Level = character.Level,
            CurrentHp = character.CurrentHp,
            MaxHp = character.MaxHp,
            CurrentMp = character.CurrentMp,
            MaxMp = character.MaxMp,
            PhysicalAttack = physicalAttack,
            PhysicalDefense = physicalDefense,
            Hit = hit,
            Dodge = dodge,
            DamageRebound = damageRebound,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return character;
    }
}
