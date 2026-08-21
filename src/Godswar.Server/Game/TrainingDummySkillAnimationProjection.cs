namespace Godswar.Server.Game;

internal readonly record struct TrainingDummySkillAnimationProjection(
    byte[] ClientSkillCastPacket,
    uint SkillId,
    bool SelfArea)
{
    public bool ImpactBeforeDamage => SelfArea;

    public static TrainingDummySkillAnimationProjection Create(
        ReadOnlyMemory<byte> clientSkillCastPacket,
        uint expectedCasterObjectId,
        uint expectedTargetObjectId,
        uint expectedSkillId,
        bool selfArea)
    {
        if (!SkillCastRequest.TryParse(
                clientSkillCastPacket.Span,
                out var cast) ||
            cast.CasterObjectId != expectedCasterObjectId ||
            cast.TargetObjectId != expectedTargetObjectId ||
            cast.SkillId != expectedSkillId)
        {
            throw new ArgumentException(
                "The training-skill animation packet does not match the " +
                "admitted cast.",
                nameof(clientSkillCastPacket));
        }

        return new(
            clientSkillCastPacket[..checked((int)ReadDeclaredLength(
                clientSkillCastPacket.Span))].ToArray(),
            expectedSkillId,
            selfArea);
    }

    private static ushort ReadDeclaredLength(ReadOnlySpan<byte> packet) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet);
}
