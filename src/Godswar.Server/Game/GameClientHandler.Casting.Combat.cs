using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task BeginIntonedCombatSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        var manaCost = Math.Max(0, combat.Mp);
        if (currentMana < manaCost)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    currentMana),
                cancellationToken,
                "IntonedSkillManaRejected");
            Console.WriteLine(
                $"[skill] rejected intonation for insufficient MP " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"mp={currentMana} cost={manaCost}");
            return;
        }

        uint? expectedTargetSpawnGeneration = null;
        Func<CancellationToken, Task> publishStartAsync;
        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
        {
            var worldObjectId =
                WorldObjectIds.ForPlayer(character.Id);
            publishStartAsync = async token =>
            {
                await _session.SendAsync(
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    token,
                    "IntonedAreaSkillCastSelf");
                await _registry.BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        worldObjectId),
                    token,
                    _session,
                    "IntonedAreaSkillCastWorld");
            };
        }
        else
        {
            if (!_registry.TryGetMonsterSnapshot(
                    _session,
                    character.CurrentMap,
                    cast.TargetObjectId,
                    out var target) ||
                !_registry.IsMonsterVisibleTo(
                    _session,
                    cast.TargetObjectId,
                    target.SpawnGeneration) ||
                !target.IsSpawned ||
                !target.IsAlive ||
                !SkillCombatResolver.IsWithinRange(
                    character.PositionX,
                    character.PositionZ,
                    target.X,
                    target.Z,
                    combat))
            {
                Console.WriteLine(
                    $"[skill] rejected invalid intonation target " +
                    $"character={character.Name} skill={cast.SkillId} " +
                    $"target={cast.TargetObjectId}");
                return;
            }

            expectedTargetSpawnGeneration = target.SpawnGeneration;
            var worldObjectId =
                WorldObjectIds.ForPlayer(character.Id);
            publishStartAsync = async token =>
            {
                await _registry.DeliverMonsterPacketToViewerAsync(
                    _session,
                    character.CurrentMap,
                    cast.TargetObjectId,
                    PacketBuilder.SkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    target.SpawnGeneration,
                    token,
                    "IntonedSkillCastSelf");
                await _registry.BroadcastToMonsterViewersAsync(
                    character.CurrentMap,
                    cast.TargetObjectId,
                    PacketBuilder.SkillCastVisual(
                        packet.Buffer,
                        worldObjectId),
                    token,
                    _session,
                    "IntonedSkillCastWorld",
                    expectedSpawnGeneration:
                        target.SpawnGeneration);
            };
        }

        var started = await TryBeginPendingSkillCastAsync(
            cast.SkillId,
            combat.CastTime,
            "combat",
            publishStartAsync,
            token => HandleSkillCastAsync(
                packet,
                token,
                intonationCompleted: true,
                expectedTargetSpawnGeneration),
            cancellationToken,
            () => IsIntonedCombatCompletionStillValid(
                cast,
                combat,
                expectedTargetSpawnGeneration));
        if (!started)
        {
            Console.WriteLine(
                $"[skill] rejected intonation while another cast is " +
                $"pending character={character.Name} " +
                $"skill={cast.SkillId}");
        }
    }

    private bool IsIntonedCombatCompletionStillValid(
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        uint? expectedTargetSpawnGeneration)
    {
        var character = _character;
        if (character is null)
        {
            return false;
        }

        lock (character.VitalsSync)
        {
            if (character.CurrentMp < Math.Max(0, combat.Mp))
            {
                return false;
            }
        }

        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
        {
            return true;
        }

        return _registry.TryGetMonsterSnapshot(
                   _session,
                   character.CurrentMap,
                   cast.TargetObjectId,
                   out var target) &&
               expectedTargetSpawnGeneration is { } expectedGeneration &&
               target.SpawnGeneration == expectedGeneration &&
               _registry.IsMonsterVisibleTo(
                   _session,
                   cast.TargetObjectId,
                   target.SpawnGeneration) &&
               target.IsSpawned &&
               target.IsAlive &&
               SkillCombatResolver.IsWithinRange(
                   character.PositionX,
                   character.PositionZ,
                   target.X,
                   target.Z,
                   combat);
    }
}
