using System.Security.Cryptography;

namespace Godswar.Server.Infrastructure.Inventory;

internal interface IHolyStoneUpgradeRandomSource
{
    int NextRoll();
}

internal sealed class CryptographicHolyStoneUpgradeRandomSource :
    IHolyStoneUpgradeRandomSource
{
    public int NextRoll() => RandomNumberGenerator.GetInt32(100);
}
