using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SkillCombatTimingCatalogChecks
{
    private static async Task
        CheckRegistryCooldownSurvivesReconnectAsync()
    {
        const int accountId = 61_530;
        const int characterId = 62_530;
        const uint skillId = 530;
        var observedAt = new DateTimeOffset(
            2035,
            4,
            5,
            6,
            7,
            8,
            TimeSpan.Zero);
        var cooldown = TimeSpan.FromSeconds(22);
        var registry = new GameSessionRegistry();

        await using var first =
            await RuntimePolicySessionSocket.CreateAsync();
        var firstCharacter = CreateCooldownCharacter(
            accountId,
            characterId,
            "CooldownReconnectFirst");
        registry.JoinMap(
            first.Session,
            accountId,
            firstCharacter,
            WorldObjectIds.ForPlayer(characterId));
        Check.True(
            registry.TryClaimHostileSkillCooldown(
                first.Session,
                firstCharacter,
                skillId,
                cooldown,
                observedAt,
                out var firstLease,
                out var readyAt),
            "the first connection claims the character cooldown");
        registry.Remove(first.Session);

        await using var replacement =
            await RuntimePolicySessionSocket.CreateAsync();
        var replacementCharacter = CreateCooldownCharacter(
            accountId,
            characterId,
            "CooldownReconnectReplacement");
        registry.JoinMap(
            replacement.Session,
            accountId,
            replacementCharacter,
            WorldObjectIds.ForPlayer(characterId));
        Check.True(
            !registry.TryClaimHostileSkillCooldown(
                replacement.Session,
                replacementCharacter,
                skillId,
                cooldown,
                observedAt + TimeSpan.FromSeconds(1),
                out _,
                out var replacementReadyAt),
            "same-character reconnect cannot bypass a live cooldown");
        Check.Equal(
            readyAt,
            replacementReadyAt,
            "replacement observes the original authoritative ready time");

        Check.Equal(
            1,
            registry.PruneHostileSkillCooldowns(readyAt),
            "the heartbeat prune removes a character that never casts again");
        Check.Equal(
            0,
            registry.HostileSkillCooldownOwnerCount,
            "expired cooldown owner storage is bounded");
        Check.True(
            registry.TryClaimHostileSkillCooldown(
                replacement.Session,
                replacementCharacter,
                skillId,
                cooldown,
                readyAt,
                out var replacementLease,
                out _),
            "replacement can claim after the authored cooldown expires");
        Check.True(
            !registry.ReleaseHostileSkillCooldown(firstLease),
            "a pre-prune connection lease cannot erase a newer claim");
        Check.True(
            registry.ReleaseHostileSkillCooldown(replacementLease),
            "the exact replacement claim remains releasable");
        Check.Equal(
            1,
            registry.PruneHostileSkillCooldowns(readyAt),
            "released owner state is pruned on the next heartbeat");
        registry.Remove(replacement.Session);
    }

    private static GameCharacter CreateCooldownCharacter(
        int accountId,
        int characterId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 0,
            Level = 30,
            CurrentHp = 100,
            MaxHp = 100,
            CurrentMp = 100,
            MaxMp = 100
        };
}
