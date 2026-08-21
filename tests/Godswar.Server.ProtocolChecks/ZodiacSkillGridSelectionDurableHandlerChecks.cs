using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridSelectionDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommittedSelectionAsync();
        await CheckReplaySuppressesNativeAnimationAsync();
        await CheckSecureTokenlessFailsClosedAsync();
        await CheckNonzeroTailFailsClosedAsync();
        await CheckLocalRawAttackAndDefenseSelectionAsync();
    }

    private static async Task CheckCommittedSelectionAsync()
    {
        var receipt = SuccessfulReceipt();
        await using var fixture = CreateFixture(
            ZodiacSkillGridSelectionExecutionResult.Committed(receipt));

        await InvokeAsync(
            fixture.Handler,
            CreateSelectionPacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor.Count,
            "secure SID102 executes once");
        var envelope = fixture.Executor.LastEnvelope ??
            throw new InvalidOperationException(
                "SID102 handler did not capture its envelope.");
        Check.True(
            envelope.Command.ClientOperationId == OperationId &&
            envelope.Command.GridIndex == GridIndex &&
            envelope.Command.SelectedSkillKind == SelectedKind &&
            envelope.Subject ==
                new CommandSubject(AccountId, CharacterId) &&
            ZodiacSkillGridSelectionCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "SID102 handler preserves UUID, owner, and canonical intent");
        AssertProjection(fixture, SelectedKind, "committed SID102");
        AssertResponse(
            fixture,
            SecureLegacyCommandDisposition.Applied,
            expectedSelectionAcknowledgements: 1,
            "committed SID102");
    }

    private static async Task
        CheckReplaySuppressesNativeAnimationAsync()
    {
        var receipt = SuccessfulReceipt();
        var replay =
            ZodiacSkillGridSelectionExecutionResult.Duplicate(
                receipt,
                currentLevel: 1,
                selectedSkillKind: SelectedKind,
                currentRevision: 1);
        await using var fixture = CreateFixture(replay);

        await InvokeAsync(
            fixture.Handler,
            CreateSelectionPacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor.Count,
            "secure SID102 replay executes one inbox lookup");
        AssertProjection(fixture, SelectedKind, "replayed SID102");
        AssertResponse(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedSelectionAcknowledgements: 0,
            "replayed SID102");
    }

    private static async Task CheckSecureTokenlessFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            ZodiacSkillGridSelectionExecutionResult
                .PreconditionFailed());

        await InvokeAsync(
            fixture.Handler,
            CreateSelectionPacket(operationId: null));

        Check.True(
            fixture.Executor.Count == 0 &&
            fixture.Store.SelectionCount == 0 &&
            fixture.Transport.Events.Count == 0,
            "secure tokenless SID102 cannot execute or respond");
    }

    private static async Task CheckNonzeroTailFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            ZodiacSkillGridSelectionExecutionResult
                .PreconditionFailed());

        await InvokeAsync(
            fixture.Handler,
            CreateSelectionPacket(OperationId, tail: 1));

        Check.True(
            fixture.Executor.Count == 0 &&
            fixture.Store.SelectionCount == 0 &&
            fixture.Transport.Events.Count == 0,
            "nonzero SID102 tail cannot execute or respond");
    }

    private static void AssertProjection(
        HandlerFixture fixture,
        int selectedKind,
        string description)
    {
        Check.Equal(
            selectedKind,
            fixture.Character.ZodiacSkillGridSkillIds[GridIndex],
            $"{description} handler projection");
        Check.Equal(
            selectedKind,
            fixture.RegistryMirror.ZodiacSkillGridSkillIds[GridIndex],
            $"{description} registry projection");
    }

    private static void AssertResponse(
        HandlerFixture fixture,
        SecureLegacyCommandDisposition disposition,
        int expectedSelectionAcknowledgements,
        string description)
    {
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            expectedSelectionAcknowledgements,
            packets.Count(packet =>
                Opcode(packet) == Opcodes.Zodiac &&
                packet.Length == 24 &&
                ZodiacSid(packet) == 102),
            $"{description} SID102 count");
        Check.Equal(
            1,
            packets.Count(packet =>
                Opcode(packet) == Opcodes.Zodiac &&
                packet.Length == 328 &&
                ZodiacSid(packet) == 1),
            $"{description} full-sync count");
        Check.Equal(
            expectedSelectionAcknowledgements + 1,
            packets.Count,
            $"{description} stock packet count");

        if (expectedSelectionAcknowledgements == 1)
        {
            Check.True(
                ZodiacSid(packets[0]) == 102 &&
                BinaryPrimitives.ReadInt32LittleEndian(
                    packets[0].AsSpan(12, 4)) == GridIndex &&
                BinaryPrimitives.ReadInt32LittleEndian(
                    packets[0].AsSpan(16, 4)) == SelectedKind,
                $"{description} native acknowledgement fields");
        }
        Check.Equal(
            1,
            ZodiacSid(packets[^1]),
            $"{description} full sync is last stock packet");

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == disposition &&
            result.CommandFamily ==
                (ushort)CommandFamily.ZodiacSkillGridSelection &&
            result.ResultCode == 1 &&
            result.AuthoritativeRevision == 1 &&
            result.OperationId == OperationId,
            $"{description} family-21 terminal result");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} terminal result is last");
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, 2));

    private static ushort ZodiacSid(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(10, 2));
}
