using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal static readonly TimeSpan CharacterUiStatsV1ProbeInterval =
        TimeSpan.FromSeconds(1);

    internal bool TryAcceptCharacterUiStatsV1CapabilityProbe(
        ClientSession session,
        DateTimeOffset observedAt)
    {
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        lock (state.CharacterUiStatsGate)
        {
            if (state.LastCharacterUiStatsV1ProbeAt is { } previous &&
                observedAt - previous < CharacterUiStatsV1ProbeInterval)
            {
                return false;
            }

            if (!state.CharacterUiStatsV1Enabled)
            {
                state.CharacterUiStatsV1Enabled = true;
            }

            state.LastCharacterUiStatsV1ProbeAt = observedAt;
            return true;
        }
    }

    internal bool IsCharacterUiStatsV1Enabled(ClientSession session)
    {
        if (!TryGetCharacterUiStatsState(session, out var state))
        {
            return false;
        }

        lock (state.CharacterUiStatsGate)
        {
            return state.CharacterUiStatsV1Enabled;
        }
    }

    internal static CharacterUiStatsV1Projection ProjectCharacterUiStatsV1(
        GameCharacter character,
        in ClientStatusAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!float.IsFinite(aggregate.MovementSpeedMultiplier) ||
            aggregate.MovementSpeedMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregate));
        }

        var speed = (int)Math.Round(
            Math.Clamp(
                (double)aggregate.MovementSpeedMultiplier *
                    PacketBuilder.CharacterUiStatsBasisPointScale,
                PacketBuilder.CharacterUiStatsMinimumSpeedBasisPoints,
                PacketBuilder.CharacterUiStatsMaximumSpeedBasisPoints),
            MidpointRounding.AwayFromZero);
        var stats = character.CalculatedStats ??
            CharacterStats.FromCharacter(character);
        return PacketBuilder.NormalizeCharacterUiStatsV1(
            new CharacterUiStatsV1Projection(
                speed,
                Math.Clamp(
                    stats.IgnorePhysicalDefense,
                    0,
                    AuthoredCombatFormula.MaximumIgnoreDefenseBasisPoints),
                Math.Clamp(
                    stats.IgnoreMagicDefense,
                    0,
                    AuthoredCombatFormula.MaximumIgnoreDefenseBasisPoints)));
    }

    private bool TryGetCharacterUiStatsState(
        ClientSession session,
        out PlayerStatusState state)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(session) ||
                !_playerStatusStates.TryGetValue(session, out var existing))
            {
                state = null!;
                return false;
            }

            state = existing;
            return true;
        }
    }
}
