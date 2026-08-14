using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task
        CheckHolyWardMitigatesLiveMonsterAttackAsync()
    {
        const uint monsterObjectId = 9_100;
        var unwardedDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId,
            status: null);
        Check.True(
            SkillStatusEffectCatalog.TryGet(94, out var holyWard),
            "live mitigation fixture resolves Holy Ward 5");
        var wardedDamage = await ObserveFirstMonsterHitAsync(
            monsterObjectId + 1,
            holyWard);

        Check.Equal(
            354,
            unwardedDamage,
            "unwarded live monster hit preserves the authoritative tier damage");
        Check.Equal(
            265,
            wardedDamage,
            "Holy Ward 5 reduces the authoritative live monster hit by twenty-five percent");
    }

    private static async Task<int> ObserveFirstMonsterHitAsync(
        uint monsterObjectId,
        SkillStatusEffectDefinition? status)
    {
        var activeAt = DateTimeOffset.UtcNow;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = CreateCharacter();
        character.Id = checked(
            character.Id + (int)(monsterObjectId - 9_100));
        character.Name = $"HolyWardHero{monsterObjectId}";
        character.CurrentHp = 1_000;
        character.MaxHp = 1_000;
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ,
                tier: 100)],
            activeAt);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: activeAt);

        if (status is { } definition)
        {
            Check.True(
                await registry.ApplyRuntimeStatusAndPublishAsync(
                    socket.Session,
                    definition,
                    activeAt,
                    "holy-ward-live-hit",
                    CancellationToken.None),
                "live Holy Ward status is accepted");
            await socket.ReadPacketAsync(340);
            Check.Equal(
                definition.PhysicalDamageReduction,
                registry.GetRuntimePhysicalDamageReduction(
                    socket.Session,
                    activeAt),
                "live Holy Ward mitigation remains in the " +
                "authoritative runtime status state");
        }

        Check.True(
            registry.TryApplyMonsterDamage(
                character.CurrentMap,
                monsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: activeAt,
                out _),
            "live Holy Ward fixture establishes monster aggro");
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt,
            CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(
            activeAt + MonsterMapRuntime.TickInterval,
            CancellationToken.None);

        var damage = 1_000 - character.CurrentHp;
        registry.Remove(socket.Session);
        return damage;
    }
}
