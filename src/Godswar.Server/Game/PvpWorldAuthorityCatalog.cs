using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using Godswar.Server.Application.World;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// Process-pinned PvP map authority. Stock map mode 0 is treated as an
/// opposing-faction combat map; every other or unknown mode fails closed as a
/// safe zone. Individual duel authority can be added without weakening this
/// default-deny boundary.
/// </summary>
internal sealed class PvpWorldAuthorityCatalog
{
    internal const string PolicyVersion = "reborn-pvp-map-v1";

    private readonly FrozenDictionary<short, short?> _mapModes;

    private PvpWorldAuthorityCatalog(
        FrozenDictionary<short, short?> mapModes)
    {
        _mapModes = mapModes;
    }

    public static PvpWorldAuthorityCatalog Empty { get; } = Create(
        GameplayContentCatalog.Empty);

    public static PvpWorldAuthorityCatalog Create(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        return new PvpWorldAuthorityCatalog(
            gameplay.Maps.ToFrozenDictionary(
                static map => map.MapId,
                static map => map.MapMode));
    }

    public bool IsSafeZone(byte mapId) =>
        !_mapModes.TryGetValue(mapId, out var mode) || mode != 0;

    public PvpEligibilityResult EvaluateOpposingFaction(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        var safe = IsSafeZone(attacker.CurrentMap);
        var attackerParticipant = new PvpCombatParticipant(
            attacker.Id,
            attacker.CurrentMap,
            attacker.CurrentHp > 0,
            safe,
            attacker.Camp);
        var targetParticipant = new PvpCombatParticipant(
            target.Id,
            target.CurrentMap,
            target.CurrentHp > 0,
            IsSafeZone(target.CurrentMap),
            target.Camp);
        var entitlement = safe ||
            attacker.Camp == target.Camp ||
            attacker.Camp is not (
                GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            target.Camp is not (
                GameDefaults.SpartaCamp or GameDefaults.AthensCamp)
            ? (PvpCombatEntitlement?)null
            : new PvpCombatEntitlement(
                CreateEntitlementId(
                    attacker.Id,
                    target.Id,
                    attacker.CurrentMap),
                PvpEntitlementKind.OpposingFaction,
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

    private static Guid CreateEntitlementId(
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
            0x31505650); // "PVP1"
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(source, digest);
        return new Guid(digest[..16]);
    }
}
