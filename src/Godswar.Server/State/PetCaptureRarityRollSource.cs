using System.Security.Cryptography;

namespace Godswar.Server.State;

internal interface IPetCaptureRarityRollSource
{
    int NextRoll();
}

internal sealed class CryptographicPetCaptureRarityRollSource :
    IPetCaptureRarityRollSource
{
    public const int BasisPointCount = 10_000;

    public static CryptographicPetCaptureRarityRollSource Instance { get; } =
        new();

    private CryptographicPetCaptureRarityRollSource()
    {
    }

    public int NextRoll() =>
        RandomNumberGenerator.GetInt32(BasisPointCount);
}
