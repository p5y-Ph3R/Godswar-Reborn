using System.Security.Cryptography;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed class CryptographicHolySpiritEffectivenessRandomSource :
    IHolySpiritEffectivenessRandomSource
{
    public int NextInclusive(
        int minimumInclusive,
        int maximumInclusive)
    {
        if (minimumInclusive > maximumInclusive ||
            maximumInclusive == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInclusive));
        }

        return RandomNumberGenerator.GetInt32(
            minimumInclusive,
            maximumInclusive + 1);
    }
}
