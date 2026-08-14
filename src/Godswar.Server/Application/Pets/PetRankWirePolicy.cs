namespace Godswar.Server.Application.Pets;

/// <summary>
/// Native pet rank is an unsigned 16-bit integer expressed in hundredths.
/// Invalid authoritative state must fail before packet projection rather than
/// being rounded or clamped into a different rank.
/// </summary>
internal static class PetRankWirePolicy
{
    public const decimal MaximumRank = 655.35m;

    public static bool IsRepresentable(decimal rank) =>
        rank is >= 0m and <= MaximumRank &&
        rank * 100m == decimal.Truncate(rank * 100m);

    public static ushort Encode(decimal rank)
    {
        if (!IsRepresentable(rank))
        {
            throw new InvalidDataException(
                "Pet rank is not representable by the native hundredths wire field.");
        }

        return checked((ushort)(rank * 100m));
    }
}
