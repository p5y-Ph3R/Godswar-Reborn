using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetHealingTalentLiveAdapterChecks
{
    private static async Task CheckWitheredHealingAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = Character();
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start);
        Check.True(registry.UpdateActivePetHealingRuntime(
                socket.Session,
                [Pet(petId: 700, summoned: true)]),
            "Wither fixture accepts its active Healing pet");

        var applied = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                    eventId: 701,
                    character,
                    objectId,
                    resolvedAt: Start,
                    damage: 10) with
                {
                    HealingReceivedBasisPoints = 5_000
                });
        Check.True(
            applied.PetHealing is
            {
                ResolvedHealing: 12,
                AppliedHealing: 12,
                BeforeHealth: 40,
                AfterHealth: 52
            } &&
            applied.FinalHealth == 52 &&
            character.CurrentHp == 52 &&
            character.VitalsRevision == 2,
            "live damage adapter applies Wither to pet Healing after admitted damage");

        var replay = registry.ResolvePlayerVitalsDamageEcs(
            socket.Session,
            character,
            objectId,
            Request(
                    eventId: 701,
                    character,
                    objectId,
                    resolvedAt: Start,
                    damage: 10) with
                {
                    HealingReceivedBasisPoints = 5_000
                });
        Check.True(
            !replay.Applied &&
            replay.PetHealing is null &&
            character.CurrentHp == 52 &&
            character.VitalsRevision == 2,
            "replayed Withered hit cannot damage or trigger pet Healing twice");

        registry.Remove(socket.Session);
    }
}
