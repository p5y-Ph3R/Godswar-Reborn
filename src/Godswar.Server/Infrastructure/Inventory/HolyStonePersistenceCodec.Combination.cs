using System.Text.Json;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.Infrastructure.Inventory;

internal static partial class HolyStonePersistenceCodec
{
    private static void WriteCombinationEvidence(
        Utf8JsonWriter writer,
        HolyStoneExecutionReceipt receipt)
    {
        if (receipt.Operation != HolyStoneCommandOperation.Combine)
        {
            return;
        }

        var evidence = receipt.CombinationEvidence ??
            throw new InvalidDataException(
                "A Combination receipt has no third-material evidence.");
        writer.WriteNumber(
            "thirdMaterialKitBagSlot",
            evidence.ThirdMaterialKitBagSlot);
        WriteNullableNumber(
            writer,
            "thirdMaterialItemInstanceId",
            evidence.ThirdMaterialItemInstanceId);
        writer.WriteString(
            "expectedThirdMaterialCompactItemState",
            evidence.ExpectedThirdMaterialCompactItemState);
        writer.WriteString(
            "authoritativeThirdMaterialBeforeCompactItemState",
            evidence.AuthoritativeThirdMaterialBeforeCompactItemState);
        writer.WriteString(
            "authoritativeThirdMaterialAfterCompactItemState",
            evidence.AuthoritativeThirdMaterialAfterCompactItemState);
    }

    private static HolyStoneCombinationReceiptEvidence?
        DecodeCombinationEvidence(
            JsonElement root,
            bool combination) =>
        combination
            ? new HolyStoneCombinationReceiptEvidence(
                root.GetProperty("thirdMaterialKitBagSlot").GetInt32(),
                NullableInt64(root, "thirdMaterialItemInstanceId"),
                RequiredString(
                    root,
                    "expectedThirdMaterialCompactItemState"),
                RequiredString(
                    root,
                    "authoritativeThirdMaterialBeforeCompactItemState"),
                RequiredString(
                    root,
                    "authoritativeThirdMaterialAfterCompactItemState"))
            : null;
}
