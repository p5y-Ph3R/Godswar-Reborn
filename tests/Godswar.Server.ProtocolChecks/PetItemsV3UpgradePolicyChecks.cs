using System.Reflection;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class PetItemsV3UpgradePolicyChecks
{
    public const string CheckName =
        "Exact pets-v3 through capture-tool publication lineage";

    public static Task RunAsync()
    {
        const BindingFlags flags =
            BindingFlags.NonPublic | BindingFlags.Static;
        var type = typeof(PostgresItemTemplateBaselinePublisher);
        Check.Equal(
            "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057",
            type.GetField("OfficialPetItemsV3Revision", flags)
                ?.GetRawConstantValue() as string ?? string.Empty,
            "only the exact sealed pets-v3 SHA is an upgrade predecessor");
        Check.Equal(
            "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
            "zephyr-v1+mount-speed-v3+pets-v3",
            type.GetField("OfficialPetItemsV3Source", flags)
                ?.GetRawConstantValue() as string ?? string.Empty,
            "pets-v3 predecessor also pins its exact source label");
        Check.Equal(
            "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
            "zephyr-v1+mount-speed-v3+pets-v5+nameplates-v1+warehouse-v1",
            type.GetField("PublicationSource", flags)
                ?.GetRawConstantValue() as string ?? string.Empty,
            "the capture-tool release retains the reviewed warehouse lineage");
        return Task.CompletedTask;
    }
}
