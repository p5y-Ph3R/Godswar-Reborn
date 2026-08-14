using System.Buffers;
using System.Text.Json;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

/// <summary>
/// A bounded, process-one-shot capture for an unresolved stock-client frame.
/// It is callable only by a raw LocalDevelopment session which owns the
/// validated legacy-authentication capability. Production and secure
/// sessions never serialize or write the native argument payload.
/// </summary>
internal static class PetManagerRejectedShapeDiagnostic
{
    internal const string CapturePath =
        "/tmp/godswar-pet-appearance-frame.json";
    internal const string EnabledEnvironmentVariable =
        "GODSWAR_LOCAL_PET_APPEARANCE_FRAME_DIAGNOSTICS";
    private const int MaximumPayloadBytes = 512;
    private static int _captureClaimed;

    public static void TryCapture(
        bool isSecureSession,
        bool hasLocalDevelopmentCapability,
        bool isExactFrame,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ShouldCapture(
                IsExplicitlyEnabled(Environment.GetEnvironmentVariable(
                    EnabledEnvironmentVariable)),
                isSecureSession,
                hasLocalDevelopmentCapability,
                isExactFrame,
                npcId,
                dialogIndex,
                subId,
                arguments.Count) ||
            Interlocked.CompareExchange(
                ref _captureClaimed,
                1,
                comparand: 0) != 0)
        {
            return;
        }

        try
        {
            var payload = BuildPayload(
                npcId,
                dialogIndex,
                subId,
                arguments);
            var stagingPath = CapturePath +
                $".{Environment.ProcessId}.tmp";
            File.WriteAllBytes(stagingPath, payload);
            File.Move(stagingPath, CapturePath, overwrite: true);
        }
        catch (IOException)
        {
            Volatile.Write(ref _captureClaimed, 0);
        }
        catch (UnauthorizedAccessException)
        {
            Volatile.Write(ref _captureClaimed, 0);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _captureClaimed, 0);
        }
    }

    internal static bool ShouldCapture(
        bool diagnosticsEnabled,
        bool isSecureSession,
        bool hasLocalDevelopmentCapability,
        bool isExactFrame,
        uint npcId,
        int dialogIndex,
        int subId,
        int argumentCount) =>
        diagnosticsEnabled &&
        !isSecureSession &&
        hasLocalDevelopmentCapability &&
        isExactFrame &&
        IsPetManagerNpc(npcId) &&
        dialogIndex == PetManagerProtocol.DialogIndex &&
        subId == PetManagerProtocol.AppearanceChangeMenuSubId &&
        argumentCount == PetManagerProtocol.FunctionArgumentCount;

    internal static bool IsExplicitlyEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal static byte[] BuildPayload(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != PetManagerProtocol.FunctionArgumentCount)
        {
            throw new ArgumentException(
                "A Pet Manager shape capture requires all 18 arguments.",
                nameof(arguments));
        }

        var output = new ArrayBufferWriter<byte>(MaximumPayloadBytes);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("npc_id", npcId);
        writer.WriteNumber("dialog_index", dialogIndex);
        writer.WriteNumber("sub_id", subId);
        writer.WriteStartArray("arguments");
        foreach (var argument in arguments)
        {
            writer.WriteNumberValue(argument);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        if (output.WrittenCount > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The Pet Manager shape diagnostic exceeded its byte bound.");
        }
        return output.WrittenSpan.ToArray();
    }

    private static bool IsPetManagerNpc(uint npcId) =>
        npcId is PetManagerProtocol.AthensNpcId or
            PetManagerProtocol.PublishedSpartaNpcId or
            PetManagerProtocol.SourceSpartaNpcId;
}
