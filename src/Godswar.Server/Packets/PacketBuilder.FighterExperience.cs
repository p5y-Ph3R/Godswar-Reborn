using System.Buffers.Binary;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // The stock client's CLevelExp::Update signature is Update(unsigned int,
    // unsigned int). Preserve the four-byte legacy layout while allowing the
    // complete UInt32 range. Values outside that range indicate authoritative
    // state corruption and are rejected instead of silently wrapping/clamping.
    internal const long MaximumLegacyFighterExperience = uint.MaxValue;

    private static void WriteLegacyFighterExperience(
        Span<byte> destination,
        long value,
        string parameterName)
    {
        if (value < 0 || value > MaximumLegacyFighterExperience)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Legacy fighter EXP must be between 0 and {MaximumLegacyFighterExperience}.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value);
    }
}
