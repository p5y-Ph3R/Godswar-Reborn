using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const ushort PlayerExtendedStatusOpcode = 0x27B7;
    private const int PlayerStatusEffectsLength = 340;
    private const int PlayerStatusEffectsMaximumCount = 20;
    private const int PlayerStatusEffectsCountOffset = 8;
    private const int PlayerStatusEffectsIdsOffset = 12;
    private const int PlayerStatusEffectsTimesOffset = 92;
    private const int PlayerStatusEffectsStatusDataOffset = 172;
    private const int PlayerStatusEffectsStatusDataLength = 168;
    private const int PlayerStatusEffectsMaximumHpOffset = 172;
    private const int PlayerStatusEffectsMaximumMpOffset = 176;
    private const int PlayerStatusEffectsHpRecoveryOffset = 180;
    private const int PlayerStatusEffectsMpRecoveryOffset = 184;
    private const int PlayerStatusEffectsPhysicalAttackOffset = 188;
    private const int PlayerStatusEffectsPhysicalDefenseOffset = 192;
    private const int PlayerStatusEffectsMagicAttackOffset = 196;
    private const int PlayerStatusEffectsMagicDefenseOffset = 200;
    private const int PlayerStatusEffectsHitBonusOffset = 204;
    private const int PlayerStatusEffectsDodgeOffset = 208;
    private const int PlayerStatusEffectsCriticalAppendBonusOffset = 212;
    private const int PlayerStatusEffectsCriticalResistanceOffset = 216;
    private const int PlayerStatusEffectsPhysicalDamageBonusOffset = 220;
    private const int PlayerStatusEffectsMagicDamageBonusOffset = 224;
    private const int PlayerStatusEffectsDamageAbsorbOffset = 228;
    private const int PlayerStatusEffectsBeCureBonusOffset = 232;
    private const int PlayerStatusEffectsCureBonusOffset = 236;
    private const int PlayerStatusEffectsExperienceBonusOffset = 300;
    private const int PlayerStatusEffectsMovementSpeedMultiplierOffset = 324;
    private const int PlayerStatusEffectsRidingFlagOffset = 328;

    public static byte[] PlayerExtendedStatus(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return PlayerStatusEffects(
            character,
            LocalPlayerObjectId,
            [],
            ClientStatusAggregate.Empty);
    }

    /// <summary>
    /// Builds this client revision's complete status snapshot (10167 / 0x27B7).
    /// Its 32-bit timers and expanded StatusData differ from the preserved R3
    /// server declaration.
    /// </summary>
    public static byte[] PlayerStatusEffects(
        GameCharacter character,
        IReadOnlyList<ClientStatusEffect> effects,
        ClientStatusAggregate aggregate)
    {
        return PlayerStatusEffects(character, LocalPlayerObjectId, effects, aggregate);
    }

    public static byte[] PlayerStatusEffects(
        GameCharacter character,
        uint objectId,
        IReadOnlyList<ClientStatusEffect> effects,
        ClientStatusAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Count > PlayerStatusEffectsMaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effects),
                effects.Count,
                $"The client status packet supports at most {PlayerStatusEffectsMaximumCount} entries.");
        }

        if (!float.IsFinite(aggregate.ExperienceBonus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.ExperienceBonus,
                "The total experience bonus must be finite.");
        }

        if (!float.IsFinite(aggregate.MovementSpeedMultiplier) ||
            aggregate.MovementSpeedMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregate),
                aggregate.MovementSpeedMultiplier,
                "The movement-speed multiplier must be finite and positive.");
        }

        var packet = new byte[PlayerStatusEffectsLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerExtendedStatusOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsCountOffset, 4),
            (uint)effects.Count);

        // The preserved server stores statuses in std::map<statusId, ...>, so its
        // wire order is ascending by ID rather than activation order.
        var orderedEffects = effects.OrderBy(static effect => effect.StatusId).ToArray();
        for (var index = 0; index < orderedEffects.Length; index++)
        {
            var effect = orderedEffects[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(PlayerStatusEffectsIdsOffset + (index * sizeof(uint)), sizeof(uint)),
                effect.StatusId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(PlayerStatusEffectsTimesOffset + (index * sizeof(uint)), sizeof(uint)),
                effect.RemainingSeconds);
        }

        // StatusData occupies exactly 42 dwords in the bundled client. Working
        // captures show that opcode 10167 carries the complete derived data,
        // not merely each status's delta. Keep the unimplemented tail zeroed,
        // while preserving the composed movement-speed multiplier.
        packet.AsSpan(PlayerStatusEffectsStatusDataOffset, PlayerStatusEffectsStatusDataLength).Clear();
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsMovementSpeedMultiplierOffset, sizeof(float)),
            aggregate.MovementSpeedMultiplier);
        // Status.ini effect 33 is the aggregate Riding state. The status ID
        // selects the Ride.ini model, but the stock client does not switch the
        // character into its mounted render state unless this dword is set.
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsRidingFlagOffset, sizeof(uint)),
            aggregate.IsRiding ? 1u : 0u);

        var stats = character.CalculatedStats ?? CharacterStats.FromCharacter(character);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsMaximumHpOffset, sizeof(int)),
            character.MaxHp);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsMaximumMpOffset, sizeof(int)),
            character.MaxMp);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsHpRecoveryOffset, sizeof(int)),
            PlayerRecoveryCatalog.GetTotalHp(character));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsMpRecoveryOffset, sizeof(int)),
            PlayerRecoveryCatalog.GetTotalMp(character));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsPhysicalAttackOffset, sizeof(int)),
            stats.PhysicalAttack);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsPhysicalDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.PhysicalDefense,
                aggregate.PhysicalDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsMagicAttackOffset, sizeof(int)),
            stats.MagicAttack);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsMagicDefenseOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.MagicDefense,
                aggregate.MagicDefense));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsDodgeOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.Dodge,
                aggregate.Dodge));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsCriticalResistanceOffset, sizeof(int)),
            SaturatingStatusValue(
                stats.CriticalResistance,
                aggregate.CriticalResistance));
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsPhysicalDamageBonusOffset, sizeof(float)),
            ToClientPercent(stats.PhysicalDamageBonus));
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsMagicDamageBonusOffset, sizeof(float)),
            ToClientPercent(stats.MagicDamageBonus));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsDamageAbsorbOffset, sizeof(int)),
            stats.DamageAbsorb);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsBeCureBonusOffset, sizeof(float)),
            ToClientPercent(stats.BeCureBonus));
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsCureBonusOffset, sizeof(float)),
            ToClientPercent(stats.CureBonus));
        aggregate = aggregate with
        {
            Hit = (int)Math.Clamp(
                (long)stats.Hit + aggregate.Hit,
                int.MinValue,
                int.MaxValue),
            CriticalAppend = (int)Math.Clamp(
                (long)stats.Critical + aggregate.CriticalAppend,
                int.MinValue,
                int.MaxValue)
        };

        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsHitBonusOffset, sizeof(int)),
            aggregate.Hit);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsCriticalAppendBonusOffset, sizeof(int)),
            aggregate.CriticalAppend);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsExperienceBonusOffset, sizeof(float)),
            aggregate.ExperienceBonus);
        return packet;
    }

    private static int SaturatingStatusValue(
        int baseValue,
        int modifier) =>
        (int)Math.Clamp(
            (long)baseValue + modifier,
            int.MinValue,
            int.MaxValue);

    /// <summary>
    /// Builds the same complete status-map envelope for a non-player world
    /// object. Monsters do not expose player-derived StatusData, so that block
    /// remains zeroed apart from the protocol's baseline movement multiplier.
    /// </summary>
    public static byte[] WorldObjectStatusEffects(
        uint objectId,
        IReadOnlyList<ClientStatusEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (objectId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectId),
                objectId,
                "A status snapshot requires a non-zero world object ID.");
        }

        if (effects.Count > PlayerStatusEffectsMaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effects),
                effects.Count,
                $"The client status packet supports at most {PlayerStatusEffectsMaximumCount} entries.");
        }

        var packet = new byte[PlayerStatusEffectsLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerExtendedStatusOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerStatusEffectsCountOffset, 4),
            (uint)effects.Count);

        var orderedEffects = effects.OrderBy(static effect => effect.StatusId).ToArray();
        for (var index = 0; index < orderedEffects.Length; index++)
        {
            var effect = orderedEffects[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(PlayerStatusEffectsIdsOffset + (index * sizeof(uint)), sizeof(uint)),
                effect.StatusId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(PlayerStatusEffectsTimesOffset + (index * sizeof(uint)), sizeof(uint)),
                effect.RemainingSeconds);
        }

        packet.AsSpan(PlayerStatusEffectsStatusDataOffset, PlayerStatusEffectsStatusDataLength).Clear();
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(PlayerStatusEffectsMovementSpeedMultiplierOffset, sizeof(float)),
            1f);
        return packet;
    }
}
