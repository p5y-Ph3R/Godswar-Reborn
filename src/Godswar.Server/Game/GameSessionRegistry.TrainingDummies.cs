using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private PvpEligibilityResult EvaluatePvpBasicAttack(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        if (_trainingDummies.TryGetCoreIdentity(attacker, out _))
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.MissingEntitlement);
        }
        if (!_trainingDummies.Contains(target))
        {
            return _gameplayCatalogs.PvpWorldAuthority
                .EvaluateOpposingFaction(attacker, target, now);
        }

        return EvaluateTrainingDummyTarget(attacker, target, now);
    }

    private PvpEligibilityResult EvaluateTrainingDummyTarget(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        var inCapital = attacker.CurrentMap is 0 or 1 &&
            attacker.CurrentMap == target.CurrentMap &&
            _gameplayCatalogs.Content.Maps.Any(map =>
                map.MapId == attacker.CurrentMap &&
                map.MapMode == 5);
        var validFactions = attacker.Camp is
                GameDefaults.SpartaCamp or GameDefaults.AthensCamp &&
            target.Camp is
                GameDefaults.SpartaCamp or GameDefaults.AthensCamp;
        if (!inCapital || !validFactions)
        {
            return PvpEligibilityResult.Denied(
                PvpEligibilityFailure.SafeZone);
        }

        var attackerParticipant = new PvpCombatParticipant(
            attacker.Id,
            attacker.CurrentMap,
            attacker.CurrentHp > 0,
            IsInSafeZone: false,
            attacker.Camp);
        var targetParticipant = new PvpCombatParticipant(
            target.Id,
            target.CurrentMap,
            target.CurrentHp > 0,
            IsInSafeZone: false,
            target.Camp);
        var entitlement = new PvpCombatEntitlement(
            CreateTrainingDummyEntitlementId(
                attacker.Id,
                target.Id,
                attacker.CurrentMap),
            PvpEntitlementKind.TrainingDummy,
            attacker.Id,
            target.Id,
            attacker.CurrentMap,
            now.AddSeconds(-1),
            now.AddSeconds(5),
            attacker.Camp,
            target.Camp);
        return PvpCombatEligibilityPolicy.Evaluate(
            attackerParticipant,
            targetParticipant,
            entitlement,
            now);
    }

    internal bool IsTrainingDummy(GameCharacter? character) =>
        character is not null && _trainingDummies.Contains(character);

    internal bool IsTrainingDummyCore(GameCharacter? character) =>
        character is not null &&
        _trainingDummies.TryGetCoreIdentity(character, out _);

    internal byte? TrainingDummySpawnPkMode(GameCharacter? character) =>
        IsTrainingDummy(character) ? (byte)1 : null;

    internal bool TryRestoreTrainingDummyEntryState(
        GameCharacter? character)
    {
        if (character is null ||
            !_trainingDummies.TryGetCoreIdentity(
                character,
                out var identity))
        {
            return false;
        }

        character.CurrentMap = identity.MapId;
        character.PositionX = identity.PositionX;
        character.PositionZ = identity.PositionZ;
        character.MarkPositionChanged();
        lock (character.VitalsSync)
        {
            character.CurrentHp = character.MaxHp;
            character.CurrentMp = character.MaxMp;
            character.MarkVitalsChanged();
        }
        return true;
    }

    private static Guid CreateTrainingDummyEntitlementId(
        int attackerId,
        int targetId,
        byte mapId)
    {
        Span<byte> source = stackalloc byte[13];
        BinaryPrimitives.WriteInt32LittleEndian(source, attackerId);
        BinaryPrimitives.WriteInt32LittleEndian(source[4..], targetId);
        source[8] = mapId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            source[9..],
            0x31444D54); // "TMD1"
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(source, digest);
        return new Guid(digest[..16]);
    }
}
