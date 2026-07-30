using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridUpgradeDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommittedUpgradeAsync();
        await CheckDuplicateUsesCurrentProjectionAsync();
        await CheckTerminalRejectionCodesAsync();
        await CheckNonDurableTerminalOutcomesAsync();
        await CheckInvalidGridNeverExecutesAsync();
        await CheckMissingProviderLeavesMarkerPendingAsync();
        await CheckRawUuidCannotCreateDurableCommandAsync();
        await CheckUncertainFailuresLeaveMarkerPendingAsync();
        await CheckSecureTokenlessRequestFailsClosedAsync();
        await CheckRawTokenlessRequestUsesCompatibilityAsync();
    }

    private static async Task CheckCommittedUpgradeAsync()
    {
        var receipt = SuccessfulReceipt();
        await using var fixture = CreateFixture(
            ZodiacSkillGridUpgradeExecutionResult.Committed(receipt));
        var equipmentBefore = fixture.Character.Equipment;
        var bagBefore = fixture.Character.KitBag;
        var otherGridBefore =
            fixture.Character.ZodiacSkillGridLevels[4];

        await InvokeAsync(
            fixture.Handler,
            CreateUpgradePacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor!.Count,
            "durable Zodiac upgrade executes once");
        var envelope = fixture.Executor.LastEnvelope ??
            throw new InvalidOperationException(
                "Durable Zodiac envelope was not captured.");
        Check.Equal(
            OperationId,
            envelope.Command.ClientOperationId,
            "durable Zodiac envelope preserves operation UUID");
        Check.Equal(
            GridIndex,
            envelope.Command.GridIndex,
            "durable Zodiac envelope preserves grid intent");
        Check.Equal(
            new CommandSubject(AccountId, CharacterId),
            envelope.Subject,
            "durable Zodiac envelope uses authenticated owner");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ZodiacSkillGridUpgradeCommandEnvelope.Validate(
                envelope),
            "handler creates a valid Zodiac upgrade envelope");

        AssertProjection(
            fixture.Character,
            energy: 995,
            remainderX100: 50,
            talentPoints: 883,
            gridLevel: 2,
            selectedSkillId: 10_050,
            "committed handler mirror");
        AssertProjection(
            fixture.RegistryMirror,
            energy: 995,
            remainderX100: 50,
            talentPoints: 883,
            gridLevel: 2,
            selectedSkillId: 10_050,
            "committed registry mirror");
        Check.Equal(
            equipmentBefore,
            fixture.Character.Equipment,
            "Zodiac commit preserves equipment");
        Check.Equal(
            bagBefore,
            fixture.Character.KitBag,
            "Zodiac commit preserves bag");
        Check.Equal(
            otherGridBefore,
            fixture.Character.ZodiacSkillGridLevels[4],
            "Zodiac commit preserves other grids");

        AssertResponse(
            fixture,
            SecureLegacyCommandDisposition.Applied,
            resultCode: 1,
            authoritativeRevision: 2,
            expectedUpgradeAcknowledgements: 1,
            "committed Zodiac upgrade");
    }

    private static async Task
        CheckDuplicateUsesCurrentProjectionAsync()
    {
        var receipt = SuccessfulReceipt();
        var duplicate =
            ZodiacSkillGridUpgradeExecutionResult.Duplicate(
                receipt,
                currentEnergy: 900,
                currentEnergyRemainderX100: 25,
                currentTalentPoints: 800,
                currentLevel: 3,
                selectedSkillId: 10_051);
        await using var fixture = CreateFixture(duplicate);

        await InvokeAsync(
            fixture.Handler,
            CreateUpgradePacket(OperationId));

        AssertProjection(
            fixture.Character,
            energy: 900,
            remainderX100: 25,
            talentPoints: 800,
            gridLevel: 3,
            selectedSkillId: 10_051,
            "duplicate handler mirror");
        AssertProjection(
            fixture.RegistryMirror,
            energy: 900,
            remainderX100: 25,
            talentPoints: 800,
            gridLevel: 3,
            selectedSkillId: 10_051,
            "duplicate registry mirror");
        AssertResponse(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            resultCode: 1,
            authoritativeRevision: 2,
            expectedUpgradeAcknowledgements: 0,
            "duplicate Zodiac upgrade");
    }

    private static async Task CheckTerminalRejectionCodesAsync()
    {
        var cases = new[]
        {
            (
                ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid,
                ResultCode: 3u),
            (
                ZodiacSkillGridUpgradeReceiptStatus
                    .MaximumLevelReached,
                ResultCode: 4u),
            (
                ZodiacSkillGridUpgradeReceiptStatus
                    .ZodiacLevelTooLow,
                ResultCode: 5u),
            (
                ZodiacSkillGridUpgradeReceiptStatus
                    .InsufficientEnergy,
                ResultCode: 6u),
            (
                ZodiacSkillGridUpgradeReceiptStatus
                    .InsufficientTalentPoints,
                ResultCode: 7u)
        };

        foreach (var item in cases)
        {
            var receipt = RejectedReceipt(item.Item1);
            await using var fixture = CreateFixture(
                ZodiacSkillGridUpgradeExecutionResult
                    .TerminalRejected(receipt));

            await InvokeAsync(
                fixture.Handler,
                CreateUpgradePacket(OperationId));

            AssertResponse(
                fixture,
                SecureLegacyCommandDisposition.Rejected,
                item.ResultCode,
                authoritativeRevision: 0,
                expectedUpgradeAcknowledgements: 0,
                $"terminal Zodiac rejection {item.Item1}");
        }

        var replayedRejection = RejectedReceipt(
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid);
        await using var replayFixture = CreateFixture(
            ZodiacSkillGridUpgradeExecutionResult.Duplicate(
                replayedRejection,
                currentEnergy: 1_000,
                currentEnergyRemainderX100: 50,
                currentTalentPoints: 890,
                currentLevel: 0,
                selectedSkillId: -1));
        await InvokeAsync(
            replayFixture.Handler,
            CreateUpgradePacket(OperationId));
        AssertResponse(
            replayFixture,
            SecureLegacyCommandDisposition.Replayed,
            resultCode: 3,
            authoritativeRevision: 0,
            expectedUpgradeAcknowledgements: 0,
            "replayed terminal Zodiac rejection");
    }

    private static async Task CheckNonDurableTerminalOutcomesAsync()
    {
        var cases = new[]
        {
            (
                ZodiacSkillGridUpgradeExecutionResult
                    .RequestHashConflict(),
                SecureLegacyCommandDisposition.Conflict,
                ResultCode: 0u),
            (
                ZodiacSkillGridUpgradeExecutionResult.InvalidIntent(),
                SecureLegacyCommandDisposition.Rejected,
                ResultCode: 0u),
            (
                ZodiacSkillGridUpgradeExecutionResult
                    .PreconditionFailed(),
                SecureLegacyCommandDisposition.Rejected,
                ResultCode: 8u)
        };

        foreach (var item in cases)
        {
            await using var fixture = CreateFixture(item.Item1);
            await InvokeAsync(
                fixture.Handler,
                CreateUpgradePacket(OperationId));

            AssertResponse(
                fixture,
                item.Item2,
                item.ResultCode,
                authoritativeRevision: 0,
                expectedUpgradeAcknowledgements: 0,
                $"non-durable Zodiac outcome {item.Item1.Disposition}",
                expectedProjection: false);
        }
    }

    private static async Task CheckInvalidGridNeverExecutesAsync()
    {
        await using var fixture = CreateFixture(
            ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed());

        await InvokeAsync(
            fixture.Handler,
            CreateUpgradePacket(OperationId, gridIndex: 16));

        Check.Equal(
            0,
            fixture.Executor!.Count,
            "invalid Zodiac grid never reaches executor");
        AssertResponse(
            fixture,
            SecureLegacyCommandDisposition.Rejected,
            resultCode: 2,
            authoritativeRevision: 0,
            expectedUpgradeAcknowledgements: 0,
            "invalid Zodiac grid",
            expectedProjection: false);
    }

    private static async Task
        CheckMissingProviderLeavesMarkerPendingAsync()
    {
        await using var fixture = CreateFixture(
            execution: null,
            configureExecutor: false);

        await InvokeAsync(
            fixture.Handler,
            CreateUpgradePacket(OperationId));

        Check.Equal(
            0,
            fixture.Store.UpgradeCount,
            "UUID-bearing request never falls back to compatibility store");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "missing provider emits no stock or terminal response");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "missing provider leaves operation UUID pending");
    }

    private static void AssertResponse(
        HandlerFixture fixture,
        SecureLegacyCommandDisposition disposition,
        uint resultCode,
        ulong authoritativeRevision,
        int expectedUpgradeAcknowledgements,
        string description,
        bool expectedProjection = true)
    {
        var packets = fixture.Transport.ReadLegacyPackets();
        if (expectedProjection)
        {
            AssertLegacyPacketShape(
                packets,
                expectedUpgradeAcknowledgements,
                description);
        }
        else
        {
            Check.Equal(
                0,
                packets.Count,
                $"{description} fabricates no authoritative projection");
        }
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == disposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.ZodiacSkillGridUpgrade,
            result.CommandFamily,
            $"{description} command family");
        Check.Equal(
            resultCode,
            result.ResultCode,
            $"{description} result code");
        Check.Equal(
            authoritativeRevision,
            result.AuthoritativeRevision,
            $"{description} authoritative revision");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} secure result is last");
        Check.Equal(
            packets.Count,
            fixture.Transport.Events.Count(
                static item => item == "legacy"),
            $"{description} sends all projections before terminal result");
    }

    private static void AssertLegacyPacketShape(
        IReadOnlyList<byte[]> packets,
        int expectedUpgradeAcknowledgements,
        string description)
    {
        Check.Equal(
            expectedUpgradeAcknowledgements,
            packets.Count(packet =>
                Opcode(packet) == Opcodes.Zodiac &&
                packet.Length == 24 &&
                ZodiacSid(packet) == 101),
            $"{description} SID101 count");
        Check.Equal(
            1,
            packets.Count(packet => Opcode(packet) == 0x27B6),
            $"{description} PlayerStatus count");
        Check.Equal(
            1,
            packets.Count(packet =>
                Opcode(packet) == Opcodes.Zodiac &&
                packet.Length == 328 &&
                ZodiacSid(packet) == 1),
            $"{description} full-sync count");

        var expectedCount = expectedUpgradeAcknowledgements + 2;
        Check.Equal(
            expectedCount,
            packets.Count,
            $"{description} packet count");
        var offset = 0;
        if (expectedUpgradeAcknowledgements == 1)
        {
            Check.Equal(
                101,
                ZodiacSid(packets[0]),
                $"{description} SID101 is first");
            offset = 1;
        }
        Check.Equal(
            (ushort)0x27B6,
            Opcode(packets[offset]),
            $"{description} PlayerStatus precedes full sync");
        Check.Equal(
            1,
            ZodiacSid(packets[offset + 1]),
            $"{description} full sync is last stock packet");
    }

    private static void AssertProjection(
        GameCharacter character,
        int energy,
        int remainderX100,
        int talentPoints,
        int gridLevel,
        int selectedSkillId,
        string description)
    {
        Check.Equal(
            energy,
            character.ZodiacEnergy,
            $"{description} energy");
        Check.Equal(
            remainderX100,
            character.ZodiacEnergyRemainderX100,
            $"{description} energy remainder");
        Check.Equal(
            talentPoints,
            character.TalentPoints,
            $"{description} Talent Points");
        Check.Equal(
            gridLevel,
            character.ZodiacSkillGridLevels[GridIndex],
            $"{description} grid level");
        Check.Equal(
            selectedSkillId,
            character.ZodiacSkillGridSkillIds[GridIndex],
            $"{description} selected skill");
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

    private static ushort ZodiacSid(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)));
}
