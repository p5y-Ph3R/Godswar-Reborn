using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishPetHealingTalentAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        GameCharacter owner,
        uint ownerObjectId,
        PlayerPetHealingEcsDecision healing,
        string audience,
        CancellationToken cancellationToken)
    {
        int currentHp;
        int currentMp;
        lock (owner.VitalsSync)
        {
            currentHp = owner.CurrentHp;
            currentMp = owner.CurrentMp;
        }

        var packets = PacketBuilder.PetHealingTalentResult(
            checked((uint)healing.PetId),
            ownerObjectId,
            healing.AppliedHealing,
            PetHealingTalentPolicy.CombatTextSkillId,
            owner.PositionX,
            owner.PositionZ,
            currentHp,
            currentMp);
        await TrySendWorldInstancePacketAsync(
            runtime,
            recipient,
            packets.CombatText,
            cancellationToken,
            $"PetHealingCombatText{audience}");
        await TrySendWorldInstancePacketAsync(
            runtime,
            recipient,
            packets.AuthoritativeVitals,
            cancellationToken,
            $"PetHealingVitals{audience}");
    }
}
