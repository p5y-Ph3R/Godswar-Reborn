using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class HolySuitCommandContractChecks
{
    public const string CheckName =
        "Durable Holy Suit command identity contract";

    private static readonly Guid ClientOperationId =
        Guid.Parse("28cd59c6-04f1-4536-b30b-45092f93ad11");

    public static Task RunAsync()
    {
        CheckFamiliesAndPolicy();
        CheckSecureIdentityAndCanonicalEndpoint();
        CheckRawLocalServerIdentity();
        CheckOperationShapesAndBounds();
        return Task.CompletedTask;
    }

    private static void CheckFamiliesAndPolicy()
    {
        var expected = new[]
        {
            (
                HolySuitCommandOperation.StoreExperience,
                CommandFamily.HolySuitStoreExperience,
                30,
                "holy_suit_store_experience"),
            (
                HolySuitCommandOperation.TransferExperience,
                CommandFamily.HolySuitTransferExperience,
                31,
                "holy_suit_transfer_experience"),
            (
                HolySuitCommandOperation.ConsumeWare,
                CommandFamily.HolySuitConsumeWare,
                32,
                "holy_suit_consume_ware"),
            (
                HolySuitCommandOperation.TransformExperience,
                CommandFamily.HolySuitTransformExperience,
                33,
                "holy_suit_transform_experience")
        };

        foreach (var (operation, family, value, metricCode) in expected)
        {
            Check.Equal(
                value,
                (int)HolySuitCommandEnvelope.Family(operation),
                $"{operation} family value");
            Check.True(
                family == HolySuitCommandEnvelope.Family(operation),
                $"{operation} family identity");
            Check.Equal(
                (int)CommandIdentityStrength.ClientOperationId,
                (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                    family),
                $"{family} production identity policy");
            Check.Equal(
                metricCode,
                CommandMetrics.FamilyCode(family),
                $"{family} bounded metric code");
        }
    }

    private static void CheckSecureIdentityAndCanonicalEndpoint()
    {
        var subject = new CommandSubject(7, 13);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var identity =
            HolySuitOperationIdentity.SecureClient(ClientOperationId);
        var command = CreateStoreCommand(
            identity,
            HolySuitCommandEnvelope.SpartaNpcId,
            experience: 100_000);
        var envelope = HolySuitCommandEnvelope.CreateSecure(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolySuitCommandEnvelope.Validate(envelope),
            "secure Store EXP envelope validates");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)envelope.IdentityStrength,
            "secure command retains client UUID provenance");

        var athens = HolySuitCommandEnvelope.CreateSecure(
            subject,
            connection,
            envelope.ReceivedAt,
            command with
            {
                NpcId = HolySuitCommandEnvelope.AthensNpcId
            });
        Check.True(
            envelope.OperationId == athens.OperationId &&
            envelope.RequestHash == athens.RequestHash,
            "Athens and Sparta forgers share one canonical endpoint");

        var changedAmount = HolySuitCommandEnvelope.CreateSecure(
            subject,
            connection,
            envelope.ReceivedAt,
            command with { ExperienceToStore = 200_000 });
        Check.True(
            envelope.OperationId == changedAmount.OperationId &&
            envelope.RequestHash != changedAmount.RequestHash,
            "same UUID with a changed amount is a request conflict");
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)HolySuitCommandEnvelope.Validate(
                envelope with
                {
                    Command = command with
                    {
                        ExperienceToStore = 200_000
                    }
                }),
            "tampered requested amount fails hash validation");

        Check.True(
            HolySuitCommandEnvelope.CreateOperationId(
                subject,
                HolySuitCommandOperation.StoreExperience,
                identity) !=
            HolySuitCommandEnvelope.CreateOperationId(
                subject,
                HolySuitCommandOperation.TransferExperience,
                identity),
            "one UUID cannot alias two Holy Suit families");
        Check.Throws<ArgumentException>(
            () => HolySuitCommandEnvelope.CreateSecure(
                subject,
                connection with
                {
                    Transport = CommandTransportKind.LegacyTcp
                },
                envelope.ReceivedAt,
                command),
            "raw legacy transport cannot claim a client UUID");
    }

    private static void CheckRawLocalServerIdentity()
    {
        var subject = new CommandSubject(7, 13);
        var connectionId = Guid.Parse(
            "4a74e023-1e87-42b3-b7a2-7212df950309");
        var serverOperationId = Guid.Parse(
            "2662c073-07ac-443f-9793-137555bccf2a");
        var identity = HolySuitOperationIdentity.RawLocalServer(
            serverOperationId,
            connectionId);
        var command = CreateTransformCommand(identity, prismCount: 2);
        var connection = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var envelope = HolySuitCommandEnvelope.CreateRawLocal(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            command);

        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolySuitCommandEnvelope.Validate(envelope),
            "explicit raw-local server identity validates");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)envelope.IdentityStrength,
            "raw-local identity is explicitly server-owned");

        var otherConnection = Guid.NewGuid();
        var otherIdentity = HolySuitOperationIdentity.RawLocalServer(
            serverOperationId,
            otherConnection);
        Check.True(
            HolySuitCommandEnvelope.CreateOperationId(
                subject,
                command.Operation,
                identity) !=
            HolySuitCommandEnvelope.CreateOperationId(
                subject,
                command.Operation,
                otherIdentity),
            "raw server UUID is scoped to one local connection");

        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCorrelation,
            (int)HolySuitCommandEnvelope.Validate(
                envelope with
                {
                    Connection = connection with
                    {
                        ConnectionId = otherConnection
                    }
                }),
            "raw identity cannot move to another connection");
        Check.Throws<ArgumentException>(
            () => HolySuitCommandEnvelope.CreateRawLocal(
                subject,
                connection with
                {
                    Transport = CommandTransportKind.SecureTlsLegacy
                },
                envelope.ReceivedAt,
                command),
            "server-scoped raw identity cannot enter secure provenance");
        Check.Throws<ArgumentException>(
            () => HolySuitCommandEnvelope.CreateSecure(
                subject,
                connection with
                {
                    Transport = CommandTransportKind.SecureCommand
                },
                envelope.ReceivedAt,
                command),
            "secure factory rejects a raw-local server identity");
    }

    private static void CheckOperationShapesAndBounds()
    {
        var identity =
            HolySuitOperationIdentity.SecureClient(ClientOperationId);
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.StoreExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                primaryKitBagSlot: 0,
                expectedPrimaryCompactItemState: "[9024,,,,,,1,1]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                expectedSecondaryCompactItemState: "[]",
                experienceToStore: uint.MaxValue,
                prismsToCreate: 0,
                out _),
            "UInt32 maximum Store EXP reaches authoritative policy validation");
        Check.True(
            !HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.StoreExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                primaryKitBagSlot: 0,
                expectedPrimaryCompactItemState: "[9024,,,,,,1,1]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                expectedSecondaryCompactItemState: "[]",
                experienceToStore: (long)uint.MaxValue + 1,
                prismsToCreate: 0,
                out _),
            "Store EXP rejects values outside the legacy UInt32 encoding");
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.TransferExperience,
                HolySuitCommandEnvelope.AthensNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                primaryKitBagSlot: 0,
                expectedPrimaryCompactItemState: "[]",
                secondaryKitBagSlot: 95,
                expectedSecondaryCompactItemState: "[]",
                experienceToStore: 0,
                prismsToCreate: 0,
                out _),
            "missing gear and box snapshots reach durable validation");
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.ConsumeWare,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                primaryKitBagSlot: 1,
                expectedPrimaryCompactItemState: "[1100]",
                secondaryKitBagSlot: 2,
                expectedSecondaryCompactItemState: "[9010]",
                experienceToStore: 0,
                prismsToCreate: 0,
                out _),
            "gear and ware shape is accepted");
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                experienceToStore: 0,
                prismsToCreate:
                    HolySuitCommandEnvelope.MaximumPrismsToCreate,
                out _),
            "maximum bounded prism request is accepted");

        Check.True(
            !HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.TransferExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                4,
                "[]",
                4,
                "[]",
                0,
                0,
                out _),
            "one bag slot cannot be gear and Holy Box");
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.StoreExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                0,
                "[]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                0,
                0,
                out _),
            "zero EXP store request encodes authoritative Store Maximum");
        Check.True(
            !HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                0,
                HolySuitCommandEnvelope.MaximumPrismsToCreate + 1,
                out _),
            "oversized prism request is rejected");
        Check.True(
            !HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.StoreExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                0,
                $"[{new string('x', 512)}]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                1,
                0,
                out _),
            "oversized compact item state is rejected");
    }

    private static HolySuitCommand CreateStoreCommand(
        HolySuitOperationIdentity identity,
        int npcId,
        long experience)
    {
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.StoreExperience,
                npcId,
                HolySuitCommandEnvelope.DialogIndex,
                primaryKitBagSlot: 7,
                expectedPrimaryCompactItemState: "[9020,,,,,,1,1]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                expectedSecondaryCompactItemState: "[]",
                experience,
                prismsToCreate: 0,
                out var command),
            "Store EXP fixture command");
        return command;
    }

    private static HolySuitCommand CreateTransformCommand(
        HolySuitOperationIdentity identity,
        int prismCount)
    {
        Check.True(
            HolySuitCommandEnvelope.TryCreateCommand(
                identity,
                HolySuitCommandOperation.TransformExperience,
                HolySuitCommandEnvelope.SpartaNpcId,
                HolySuitCommandEnvelope.DialogIndex,
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                HolySuitCommandEnvelope.NoKitBagSlot,
                "[]",
                experienceToStore: 0,
                prismsToCreate: prismCount,
                out var command),
            "Transform EXP fixture command");
        return command;
    }
}
