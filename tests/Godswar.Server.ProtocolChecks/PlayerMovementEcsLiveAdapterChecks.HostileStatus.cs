using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerMovementEcsLiveAdapterChecks
{
    private static async Task CheckHostileStatusMovementControlsAsync()
    {
        foreach (var (skillId, profession, label) in new[]
                 {
                     (350, (byte)1, "Frozen"),
                     (70, (byte)0, "Stuned"),
                     (790, (byte)2, "Caged")
                 })
        {
            await CheckLegacyStatusMovementAsync(
                skillId,
                profession,
                label);
        }
    }

    private static async Task CheckLegacyStatusMovementAsync(
        int skillId,
        byte profession,
        string label)
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var store = new RecordingPositionStore();
        var attacker = TrainingDummyHostileStatusTestFixture
            .CreateAttacker(
                profession,
                id: 9_000 + skillId,
                name: $"Movement{label}Caster");
        var character = TrainingDummyHostileStatusTestFixture
            .CreateDummy();
        var registry = TrainingDummyHostileStatusTestFixture
            .CreateRegistry(PlayerRuntimeMode.Ecs);
        registry.JoinPlayerMap(
            attackerSocket.Session,
            attacker.AccountId,
            attacker);
        registry.JoinPlayerMap(
            targetSocket.Session,
            character.AccountId,
            character);
        var handler = CreateHandler(
            targetSocket.Session,
            store,
            registry,
            character,
            configureVisibility: false);
        var now = DateTimeOffset.UtcNow;
        var applied = await TrainingDummyHostileStatusTestFixture.ApplyAsync(
            registry,
            attackerSocket.Session,
            attacker,
            targetSocket.Session,
            character,
            skillId,
            now,
            shouldApply: true);
        Check.True(
            applied.Accepted &&
            applied.Targets.Single().Application.Applied,
            $"{label} status is applied for movement check");

        await InvokePacketAsync(
            handler,
            CreateWalkPacket(
                opaqueMovementState: 0xCAFE_0001u,
                targetX: 3f,
                targetZ: 4f));

        Check.True(
            character.PositionX == 148f &&
            character.PositionZ == -154f &&
            store.SaveAttempts == 0,
            $"{label} blocks authoritative legacy movement");
        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }
}
