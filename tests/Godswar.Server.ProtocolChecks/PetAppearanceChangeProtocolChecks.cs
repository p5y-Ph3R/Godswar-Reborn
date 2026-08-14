using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class PetAppearanceChangeProtocolChecks
{
    public const string CheckName =
        "Stock Magic Jade appearance-change protocol";

    public static Task RunAsync()
    {
        Check.True(
            PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                out var page) &&
            page.SequenceEqual(
                [PetManagerProtocol.AppearanceChangeDescriptionSubId]),
            "Pet Manager choice 8 opens stock page 113");

        var navigation = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        Check.True(
            PetManagerProtocol.IsExactNavigationArguments(navigation) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                navigation,
                out _),
            "page navigation is not classified as Magic Jade consumption");

        (int Coordinate, int AbsoluteSlot)[] coordinates =
        [
            (0, 0),
            (23, 23),
            (100, 24),
            (123, 47),
            (300, 72),
            (323, 95)
        ];
        foreach (var (coordinate, expectedSlot) in coordinates)
        {
            var arguments = navigation.ToArray();
            arguments[0] =
                PetManagerProtocol.AppearanceChangeActionArgumentValue;
            arguments[PetManagerProtocol.AppearanceChangeItemArgumentIndex] =
                coordinate;
            Check.True(
                PetManagerProtocol.TryResolveAppearanceChangeMutation(
                    PetManagerProtocol.DialogIndex,
                    PetManagerProtocol.AppearanceChangeMenuSubId,
                    arguments,
                    out var absoluteSlot) &&
                absoluteSlot == expectedSlot,
                $"Magic Jade coordinate {coordinate} maps bag slot {expectedSlot}");
        }

        foreach (var coordinate in new[] { -2, 24, 99, 124, 299, 324, 400 })
        {
            var malformed = navigation.ToArray();
            malformed[0] =
                PetManagerProtocol.AppearanceChangeActionArgumentValue;
            malformed[PetManagerProtocol.AppearanceChangeItemArgumentIndex] =
                coordinate;
            Check.True(
                !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                    PetManagerProtocol.DialogIndex,
                    PetManagerProtocol.AppearanceChangeMenuSubId,
                    malformed,
                    out _),
                $"invalid Magic Jade coordinate {coordinate} fails closed");
        }

        var valid = navigation.ToArray();
        valid[0] = PetManagerProtocol.AppearanceChangeActionArgumentValue;
        valid[PetManagerProtocol.AppearanceChangeItemArgumentIndex] = 205;
        valid[PetManagerProtocol.AppearanceChangeFirstScratchArgumentIndex] =
            int.MinValue;
        valid[PetManagerProtocol.AppearanceChangeFirstScratchArgumentIndex + 1] =
            0;
        valid[PetManagerProtocol.AppearanceChangeLastScratchArgumentIndex] =
            int.MaxValue;
        var extraArgument = valid.ToArray();
        extraArgument[1] = 0;
        var missingActionMarker = valid.ToArray();
        missingActionMarker[0] = -1;
        var descriptionPageMarker = valid.ToArray();
        descriptionPageMarker[0] =
            PetManagerProtocol.AppearanceChangeDescriptionSubId;
        for (var index = 1; index < valid.Length; index++)
        {
            if (index ==
                    PetManagerProtocol.AppearanceChangeItemArgumentIndex ||
                index is >=
                    PetManagerProtocol
                        .AppearanceChangeFirstScratchArgumentIndex and <=
                    PetManagerProtocol
                        .AppearanceChangeLastScratchArgumentIndex)
            {
                continue;
            }

            var forbidden = valid.ToArray();
            forbidden[index] = 0;
            Check.True(
                !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                    PetManagerProtocol.DialogIndex,
                    PetManagerProtocol.AppearanceChangeMenuSubId,
                    forbidden,
                    out _),
                $"appearance mutation rejects non--1 argument {index}");
        }
        Check.True(
            PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                valid,
                out var selectedSlot) &&
            selectedSlot == 53 &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                valid,
                out _) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId + 1,
                valid,
                out _) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                extraArgument,
                out _) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                missingActionMarker,
                out _) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                descriptionPageMarker,
                out _) &&
            !PetManagerProtocol.TryResolveAppearanceChangeMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                valid[..^1],
                out _),
            "appearance mutation requires the literal-zero A1 action, ignores only native args 10-12 scratch, and rejects page, padding, and length confusion");

        var terminalResults = new[]
        {
            PetManagerProtocol.AppearanceChangeSucceededResultSubId,
            PetManagerProtocol.AppearanceChangeMissingJadeResultSubId,
            PetManagerProtocol.AppearanceChangeIncompatibleJadeResultSubId,
            PetManagerProtocol.AppearanceChangeNoPetResultSubId,
            PetManagerProtocol.AppearanceChangeUnboundPetResultSubId
        };
        Check.True(
            terminalResults.SequenceEqual([130, 137, 138, 139, 140]),
            "stock appearance result sub-IDs remain exact");
        foreach (var result in terminalResults)
        {
            var response = PacketBuilder.NpcFunctionActionResponse(
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                result);
            Check.True(
                response.Length == 16 &&
                BinaryPrimitives.ReadUInt16LittleEndian(response) == 16 &&
                BinaryPrimitives.ReadUInt16LittleEndian(
                    response.AsSpan(2)) ==
                    Opcodes.NpcFunctionActionResponse &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    response.AsSpan(4)) ==
                    PetManagerProtocol.AthensNpcId &&
                BinaryPrimitives.ReadInt32LittleEndian(
                    response.AsSpan(8)) ==
                    PetManagerProtocol.DialogIndex &&
                BinaryPrimitives.ReadInt32LittleEndian(
                    response.AsSpan(12)) == result,
                $"appearance result {result} retains the stock 10070 frame");
        }

        CheckRejectedShapeDiagnostic();

        return Task.CompletedTask;
    }

    private static void CheckRejectedShapeDiagnostic()
    {
        var arguments = new[]
        {
            0, -1, int.MinValue, 0, 1, 23, 205, 323, -1,
            int.MaxValue, 80_556_148, 0, 344, -1, -1, -1, -1, -1
        };
        var payload = PetManagerRejectedShapeDiagnostic.BuildPayload(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.DialogIndex,
            PetManagerProtocol.AppearanceChangeMenuSubId,
            arguments);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var capturedArguments = root.GetProperty("arguments")
            .EnumerateArray()
            .Select(static value => value.GetInt32())
            .ToArray();
        Check.True(
            payload.Length <= 512 &&
            root.EnumerateObject().Count() == 4 &&
            root.GetProperty("npc_id").GetUInt32() ==
                PetManagerProtocol.AthensNpcId &&
            root.GetProperty("dialog_index").GetInt32() ==
                PetManagerProtocol.DialogIndex &&
            root.GetProperty("sub_id").GetInt32() ==
                PetManagerProtocol.AppearanceChangeMenuSubId &&
            capturedArguments.SequenceEqual(arguments),
            "LocalDevelopment rejected-shape artifact contains only endpoint metadata and all 18 signed arguments");

        Check.True(
            PetManagerRejectedShapeDiagnostic.ShouldCapture(
                diagnosticsEnabled: true,
                isSecureSession: false,
                hasLocalDevelopmentCapability: true,
                isExactFrame: true,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                PetManagerProtocol.FunctionArgumentCount) &&
            !PetManagerRejectedShapeDiagnostic.ShouldCapture(
                diagnosticsEnabled: true,
                isSecureSession: true,
                hasLocalDevelopmentCapability: true,
                isExactFrame: true,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                PetManagerProtocol.FunctionArgumentCount) &&
            !PetManagerRejectedShapeDiagnostic.ShouldCapture(
                diagnosticsEnabled: true,
                isSecureSession: false,
                hasLocalDevelopmentCapability: false,
                isExactFrame: true,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                PetManagerProtocol.FunctionArgumentCount) &&
            !PetManagerRejectedShapeDiagnostic.ShouldCapture(
                diagnosticsEnabled: true,
                isSecureSession: false,
                hasLocalDevelopmentCapability: true,
                isExactFrame: false,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                PetManagerProtocol.FunctionArgumentCount) &&
            !PetManagerRejectedShapeDiagnostic.ShouldCapture(
                diagnosticsEnabled: false,
                isSecureSession: false,
                hasLocalDevelopmentCapability: true,
                isExactFrame: true,
                PetManagerProtocol.AthensNpcId,
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.AppearanceChangeMenuSubId,
                PetManagerProtocol.FunctionArgumentCount) &&
            PetManagerRejectedShapeDiagnostic.IsExplicitlyEnabled("true") &&
            PetManagerRejectedShapeDiagnostic.IsExplicitlyEnabled("TRUE") &&
            !PetManagerRejectedShapeDiagnostic.IsExplicitlyEnabled(null) &&
            !PetManagerRejectedShapeDiagnostic.IsExplicitlyEnabled("1"),
            "rejected-shape capture requires its explicit flag and an exact raw LocalDevelopment frame");
    }
}
