namespace Godswar.Server.ProtocolChecks;

internal static class HolySpiritPersistenceSurfaceChecks
{
    public const string CheckName =
        "Holy Spirit effectiveness persistence surfaces";

    private static readonly string[] SqlSurfaces =
    [
        "PostgresEquipmentBagTransferCommandExecutor.Locking.cs",
        "PostgresEquipmentForgeCommandExecutor.Locking.cs",
        "PostgresGearEnhancementCommandExecutor.Locking.cs",
        "PostgresGearMentorDecomposeCommandExecutor.Mutation.cs",
        "PostgresGearMentorMaterialConversionCommandExecutor.Mutation.cs",
        "PostgresGearMentorMaterialConversionCommandExecutor.ClassSuitLocking.cs",
        "PostgresHolyStoneCommandExecutor.Locking.cs",
        "PostgresHolyStoneCommandExecutor.Mutation.cs",
        "PostgresHolySuitCommandExecutor.Locking.cs",
        "PostgresKitBagItemDeleteCommandExecutor.Mutation.cs",
        "PostgresKitBagItemMoveCommandExecutor.Locking.cs",
        "PostgresMakeAttributeStoneCommandExecutor.Mutation.cs"
    ];

    private static readonly string[] ItemCodecs =
    [
        "PostgresEquipmentBagTransferCommandExecutor.ItemCodec.cs",
        "PostgresEquipmentForgeCommandExecutor.ItemCodec.cs",
        "PostgresGearEnhancementCommandExecutor.ItemCodec.cs",
        "PostgresGearMentorDecomposeCommandExecutor.ItemCodec.cs",
        "PostgresGearMentorMaterialConversionCommandExecutor.ItemCodec.cs",
        "PostgresHolyStoneCommandExecutor.ItemCodec.cs",
        "PostgresHolySuitCommandExecutor.Locking.cs",
        "PostgresKitBagItemDeleteCommandExecutor.ItemCodec.cs",
        "PostgresKitBagItemMoveCommandExecutor.ItemCodec.cs",
        "PostgresMakeAttributeStoneCommandExecutor.ItemCodec.cs"
    ];

    private static readonly string[] WriteSurfaces =
    [
        "PostgresEquipmentForgeCommandExecutor.Items.cs",
        "PostgresGearEnhancementCommandExecutor.Items.cs",
        "PostgresGearMentorDecomposeCommandExecutor.Items.cs",
        "PostgresGearMentorMaterialConversionCommandExecutor.Items.cs",
        "PostgresMakeAttributeStoneCommandExecutor.Items.cs"
    ];

    public static Task RunAsync()
    {
        var inventory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Inventory");
        foreach (var file in SqlSurfaces)
        {
            AssertAllSocketValues(
                File.ReadAllText(Path.Combine(inventory, file)),
                "holy_socket{0}_value",
                $"{file} SQL");
        }
        foreach (var file in ItemCodecs)
        {
            AssertAllSocketValues(
                File.ReadAllText(Path.Combine(inventory, file)),
                "Socket{0}Value",
                $"{file} codec");
        }
        foreach (var file in WriteSurfaces)
        {
            AssertAllSocketValues(
                File.ReadAllText(Path.Combine(inventory, file)),
                "@holySocket{0}Value",
                $"{file} mutation");
        }

        return Task.CompletedTask;
    }

    private static void AssertAllSocketValues(
        string source,
        string tokenFormat,
        string description)
    {
        for (var socket = 1; socket <= 4; socket++)
        {
            Check.True(
                source.Contains(
                    string.Format(tokenFormat, socket),
                    StringComparison.Ordinal),
                $"{description} preserves Holy Spirit value {socket}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(current.FullName, "GodswarServer.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for Holy Spirit checks.");
    }
}
