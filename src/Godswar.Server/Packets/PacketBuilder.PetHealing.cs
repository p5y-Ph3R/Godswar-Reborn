namespace Godswar.Server.Packets;

internal readonly record struct PetHealingPacketSequence(
    byte[] CombatText,
    byte[] AuthoritativeVitals);

internal static partial class PacketBuilder
{
    /// <summary>
    /// Constructs the native green healing number followed by the final
    /// authoritative HP/MP projection. Callers must preserve this order.
    /// </summary>
    public static PetHealingPacketSequence PetHealingTalentResult(
        uint petObjectId,
        uint ownerObjectId,
        int appliedHealing,
        uint skillId,
        float ownerX,
        float ownerZ,
        int currentHp,
        int currentMp)
    {
        if (petObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(petObjectId));
        }
        if (ownerObjectId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerObjectId));
        }
        if (appliedHealing <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedHealing));
        }
        if (currentHp <= 0 || currentMp < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHp));
        }

        return new PetHealingPacketSequence(
            SkillHealing(
                petObjectId,
                ownerObjectId,
                appliedHealing,
                skillId,
                ownerX,
                ownerZ),
            PlayerVitalsUpdate(
                ownerObjectId,
                currentHp,
                currentMp));
    }
}
