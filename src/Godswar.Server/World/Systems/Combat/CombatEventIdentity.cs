namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Stable event identities composed only from server-owned revisions and
/// authenticated identities. A retransmitted packet against the same state
/// therefore cannot fish for a different hit or critical roll.
/// </summary>
internal static class CombatEventIdentity
{
    public static ulong ForMonsterAttack(
        int attackerCharacterId,
        uint targetObjectId,
        uint spawnGeneration,
        ulong healthRevision) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            targetObjectId,
            spawnGeneration,
            healthRevision,
            0x4D4F4E5354455201UL);

    public static ulong ForPlayerMonsterBasicAttack(
        int attackerCharacterId,
        uint targetObjectId,
        uint spawnGeneration,
        ulong healthRevision,
        ulong admittedCombatRevision) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            targetObjectId,
            spawnGeneration,
            healthRevision,
            admittedCombatRevision,
            0x504D4F4E42415301UL);

    public static ulong ForPlayerMonsterSkill(
        int attackerCharacterId,
        uint targetObjectId,
        uint spawnGeneration,
        ulong healthRevision,
        ulong admittedCombatRevision,
        uint skillId,
        int targetOrder) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            targetObjectId,
            spawnGeneration,
            healthRevision,
            admittedCombatRevision,
            skillId,
            (ulong)(uint)targetOrder,
            0x504D4F4E534B4C01UL);

    public static ulong ForPlayerAttack(
        int attackerCharacterId,
        int targetCharacterId,
        long attackerVitalsRevision,
        long targetVitalsRevision) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            (ulong)(uint)targetCharacterId,
            (ulong)attackerVitalsRevision,
            (ulong)targetVitalsRevision,
            0x5056504154544B01UL);

    public static ulong ForPlayerBasicAttack(
        int attackerCharacterId,
        int targetCharacterId,
        long attackerVitalsRevision,
        long targetVitalsRevision,
        long admittedCombatRevision) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            (ulong)(uint)targetCharacterId,
            (ulong)attackerVitalsRevision,
            (ulong)targetVitalsRevision,
            (ulong)admittedCombatRevision,
            0x5056504154544B02UL);

    public static ulong ForPlayerSkill(
        int attackerCharacterId,
        int targetCharacterId,
        long attackerVitalsRevision,
        long targetVitalsRevision,
        long admittedCombatRevision,
        uint skillId) =>
        Mix(
            (ulong)(uint)attackerCharacterId,
            (ulong)(uint)targetCharacterId,
            (ulong)attackerVitalsRevision,
            (ulong)targetVitalsRevision,
            (ulong)admittedCombatRevision,
            skillId,
            0,
            0x505650534B494C01UL);

    public static ulong ForMonsterCounterattack(
        uint monsterObjectId,
        uint spawnGeneration,
        int targetCharacterId,
        long targetVitalsRevision) =>
        Mix(
            monsterObjectId,
            spawnGeneration,
            (ulong)(uint)targetCharacterId,
            (ulong)targetVitalsRevision,
            0x4D4F4E4154544B01UL);

    private static ulong Mix(
        ulong first,
        ulong second,
        ulong third,
        ulong fourth,
        ulong domain)
    {
        var value = domain;
        value = Round(value ^ first);
        value = Round(value ^ second);
        value = Round(value ^ third);
        value = Round(value ^ fourth);
        return value == 0 ? domain : value;
    }

    private static ulong Mix(
        ulong first,
        ulong second,
        ulong third,
        ulong fourth,
        ulong fifth,
        ulong sixth,
        ulong seventh,
        ulong domain)
    {
        var value = domain;
        value = Round(value ^ first);
        value = Round(value ^ second);
        value = Round(value ^ third);
        value = Round(value ^ fourth);
        value = Round(value ^ fifth);
        value = Round(value ^ sixth);
        value = Round(value ^ seventh);
        return value == 0 ? domain : value;
    }

    private static ulong Mix(
        ulong first,
        ulong second,
        ulong third,
        ulong fourth,
        ulong fifth,
        ulong domain)
    {
        var value = domain;
        value = Round(value ^ first);
        value = Round(value ^ second);
        value = Round(value ^ third);
        value = Round(value ^ fourth);
        value = Round(value ^ fifth);
        return value == 0 ? domain : value;
    }

    private static ulong Round(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
