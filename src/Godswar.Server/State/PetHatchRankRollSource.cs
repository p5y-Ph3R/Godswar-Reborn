using System.Security.Cryptography;

namespace Godswar.Server.State;

internal interface IPetHatchRankRollSource
{
    int NextRoll();
}

internal sealed class CryptographicPetHatchRankRollSource :
    IPetHatchRankRollSource
{
    public static CryptographicPetHatchRankRollSource Instance { get; } =
        new();

    private CryptographicPetHatchRankRollSource()
    {
    }

    public int NextRoll() => RandomNumberGenerator.GetInt32(100);
}
