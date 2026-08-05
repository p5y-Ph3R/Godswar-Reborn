using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private static void CheckRawLocalUpgradeIdentity()
    {
        var operationId =
            Guid.Parse("45aef2a2-2924-4d92-98d1-90dd6078d8d8");
        var connectionId =
            Guid.Parse("1893305e-f4e7-409e-bf51-42cb70aad112");
        var identity = HolyStoneOperationIdentity.RawLocalServer(
            operationId,
            connectionId);

        foreach (var rejected in new[]
                 {
                     HolyStoneCommandOperation.Drill,
                     HolyStoneCommandOperation.AdvancedDrill
                 })
        {
            Check.True(
                !TryCreateRawIdentityCommand(
                    identity,
                    rejected,
                    out _),
                $"raw server identity is rejected for {rejected}");
            Check.Throws<ArgumentException>(
                () => HolyStoneCommandEnvelope.CreateOperationId(
                    new CommandSubject(7, 19),
                    rejected,
                    identity),
                $"raw server operation scope is rejected for {rejected}");
        }

        foreach (var durableRaw in new[]
                 {
                     HolyStoneCommandOperation.Mount,
                     HolyStoneCommandOperation.Remove
                 })
        {
            Check.True(
                TryCreateRawIdentityCommand(
                    identity,
                    durableRaw,
                    out _),
                $"raw server identity is accepted for durable {durableRaw}");
            Check.True(
                HolyStoneCommandEnvelope.CreateOperationId(
                    new CommandSubject(7, 19),
                    durableRaw,
                    identity).Length > 0,
                $"raw server operation scope is created for {durableRaw}");
        }

        Check.True(
            TryCreateRawIdentityCommand(
                identity,
                HolyStoneCommandOperation.Upgrade,
                out var command),
            "raw server identity is accepted for Upgrade");

        var subject = new CommandSubject(7, 19);
        var correlation = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var envelope = HolyStoneCommandEnvelope.CreateRawLocal(
            subject,
            correlation,
            DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            command);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolyStoneCommandEnvelope.Validate(envelope),
            "connection-scoped raw Upgrade envelope validates");
        Check.Equal(
            (int)CommandFamily.HolyStoneUpgrade,
            (int)envelope.Family,
            "raw Upgrade uses its durable command family");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)envelope.IdentityStrength,
            "raw Upgrade records server-generated identity strength");
        Check.Equal(
            (int)CommandTransportKind.LegacyTcp,
            (int)envelope.Connection.Transport,
            "raw Upgrade records legacy TCP provenance");
        Check.Equal(
            connectionId,
            envelope.Command.Identity.RawLocalConnectionId,
            "raw Upgrade identity is scoped to the envelope connection");

        var otherConnectionId =
            Guid.Parse("3e2b5f23-b0c9-48a5-96e6-1b081b12e490");
        var otherIdentity = HolyStoneOperationIdentity.RawLocalServer(
            operationId,
            otherConnectionId);
        Check.True(
            TryCreateRawIdentityCommand(
                otherIdentity,
                HolyStoneCommandOperation.Upgrade,
                out var otherCommand),
            "second connection can create its own raw Upgrade command");
        var otherEnvelope = HolyStoneCommandEnvelope.CreateRawLocal(
            subject,
            new CommandConnectionCorrelation(
                otherConnectionId,
                CommandTransportKind.LegacyTcp),
            envelope.ReceivedAt,
            otherCommand);
        Check.True(
            !string.Equals(
                envelope.OperationId,
                otherEnvelope.OperationId,
                StringComparison.Ordinal),
            "the same raw UUID on another connection derives a different durable operation identity");

        Check.Throws<ArgumentException>(
            () => HolyStoneCommandEnvelope.CreateRawLocal(
                subject,
                new CommandConnectionCorrelation(
                    otherConnectionId,
                    CommandTransportKind.LegacyTcp),
                envelope.ReceivedAt,
                command),
            "raw Upgrade cannot cross legacy connection boundaries");
        Check.Throws<ArgumentException>(
            () => HolyStoneCommandEnvelope.Create(
                subject,
                correlation with
                {
                    Transport = CommandTransportKind.SecureCommand
                },
                envelope.ReceivedAt,
                command),
            "raw server identity cannot enter the secure-client envelope path");

        foreach (var rejected in new[]
        {
            HolyStoneCommandOperation.Drill,
            HolyStoneCommandOperation.AdvancedDrill
        })
        {
            var forgedCommand = command with
            {
                Operation = rejected,
                SocketIndex = rejected ==
                    HolyStoneCommandOperation.Remove
                    ? 0
                    : HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                StoneKitBagSlot = rejected is
                    HolyStoneCommandOperation.Mount or
                    HolyStoneCommandOperation.AdvancedDrill
                    ? 0
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                ExpectedStoneCompactItemState = "[]"
            };
            var forgedEnvelope = envelope with
            {
                Family = HolyStoneCommandEnvelope.Family(rejected),
                Command = forgedCommand
            };
            Check.Equal(
                (int)CommandEnvelopeValidation.InvalidCommand,
                (int)HolyStoneCommandEnvelope.Validate(forgedEnvelope),
                $"validation rejects a crafted raw server {rejected} envelope");
        }
    }

    private static bool TryCreateRawIdentityCommand(
        HolyStoneOperationIdentity identity,
        HolyStoneCommandOperation operation,
        out HolyStoneCommand command)
    {
        var socketIndex = operation == HolyStoneCommandOperation.Remove
            ? 0
            : HolyStoneCommandEnvelope.ServerSelectedSocketIndex;
        var hasMaterial = operation is
            HolyStoneCommandOperation.Mount or
            HolyStoneCommandOperation.AdvancedDrill or
            HolyStoneCommandOperation.Upgrade;
        return HolyStoneCommandEnvelope.TryCreateCommand(
            identity,
            operation,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneTargetLocation.KitBag,
            targetSlot: 16,
            expectedTargetCompactItemState: "[]",
            socketIndex,
            stoneKitBagSlot: hasMaterial
                ? 0
                : HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            expectedStoneCompactItemState: "[]",
            catalystKitBagSlot:
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            expectedCatalystCompactItemState: "[]",
            out command);
    }
}
