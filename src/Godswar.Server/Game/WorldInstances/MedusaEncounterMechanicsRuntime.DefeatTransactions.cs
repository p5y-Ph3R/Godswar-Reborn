namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    internal sealed class PreparedSourceRetirement
    {
        internal readonly MedusaEncounterMechanicsRuntime _owner;
        internal readonly MonsterState _monster;

        internal PreparedSourceRetirement(
            MedusaEncounterMechanicsRuntime owner,
            MonsterState monster,
            DateTimeOffset retiredAt)
        {
            _owner = owner;
            _monster = monster;
            RetiredAt = retiredAt;
        }

        internal DateTimeOffset RetiredAt { get; }

        internal bool Completed { get; set; }

#if DEBUG
        internal bool ProtocolCheckRetireInvalid { get; set; }

        internal bool ProtocolCheckTerminalClearInvalid { get; set; }
#endif
    }

    internal bool TryPrepareSourceRetirement(
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset retiredAt,
        out PreparedSourceRetirement? prepared,
        out MedusaMechanicSourceRetireResult rejection)
    {
        var authoritativeAt = retiredAt.ToUniversalTime();
        var preview = PreviewRetireMonster(
            sourceObjectId,
            sourceSpawnGeneration,
            authoritativeAt);
        if (preview != MedusaMechanicSourceRetireOutcome.Retired)
        {
            prepared = null;
            rejection = new(preview, PeriodicDamage: null);
            return false;
        }

        rejection = default;
        prepared = new PreparedSourceRetirement(
            this,
            _monstersByObjectId[sourceObjectId],
            authoritativeAt);
        return true;
    }

    internal bool CanCompletePreparedSourceRetirement(
        PreparedSourceRetirement? prepared,
        bool terminal) =>
        prepared is not null &&
        ReferenceEquals(prepared._owner, this) &&
        !prepared.Completed &&
#if DEBUG
        !prepared.ProtocolCheckRetireInvalid &&
        (!terminal || !prepared.ProtocolCheckTerminalClearInvalid) &&
#endif
        _pendingPeriodicDamage is null &&
        _lastObservedAt == prepared.RetiredAt &&
        !prepared._monster.Retired &&
        _monstersByObjectId.TryGetValue(
            prepared._monster.Spawn.ObjectId,
            out var current) &&
        ReferenceEquals(current, prepared._monster);

    internal MedusaMechanicSourceRetireResult
        CompletePreparedSourceRetirement(
            PreparedSourceRetirement prepared,
            bool terminal)
    {
        prepared.Completed = true;
        prepared._monster.Retired = true;
        if (terminal)
        {
            foreach (var character in _orderedCharacters)
            {
                character.Effects.Clear();
            }
        }

        return new(
            MedusaMechanicSourceRetireOutcome.Retired,
            PeriodicDamage: null);
    }

#if DEBUG
    internal static void InvalidatePreparedRetirementForProtocolCheck(
        PreparedSourceRetirement prepared) =>
        prepared.ProtocolCheckRetireInvalid = true;

    internal static void InvalidatePreparedTerminalClearForProtocolCheck(
        PreparedSourceRetirement prepared) =>
        prepared.ProtocolCheckTerminalClearInvalid = true;
#endif
}
