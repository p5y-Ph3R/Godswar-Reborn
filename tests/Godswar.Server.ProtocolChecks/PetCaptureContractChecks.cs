using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetCaptureContractChecks
{
    public const string CheckName = "Authoritative pet-capture protocol";

    public static Task RunAsync()
    {
        CheckCapturedRequest();
        CheckMalformedRequests();
        CheckDurableIdentity();
        CheckReceipt();
        CheckAcquisitionProjection();
        return Task.CompletedTask;
    }

    private static void CheckCapturedRequest()
    {
        var packet = new GamePacket(Convert.FromHexString(
            "1C000C28899C000000000F00B01C34414C4AC74228A36940451BC842"));
        Check.True(
            PetCaptureRequest.TryRead(packet, out var request),
            "the stock-client net request is recognized");
        Check.Equal(
            40_073u,
            request.TargetObjectId,
            "the capture target is decoded");
        Check.Equal(
            15,
            request.KitBagSlot,
            "the capture net bag slot is decoded");
        Check.True(
            float.IsFinite(request.ReportedPlayerX) &&
            float.IsFinite(request.ReportedPlayerZ) &&
            float.IsFinite(request.ReportedTargetX) &&
            float.IsFinite(request.ReportedTargetZ),
            "the captured coordinates are finite");

        var secondPage = (byte[])packet.Buffer.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            secondPage.AsSpan(8, 2),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            secondPage.AsSpan(10, 2),
            3);
        Check.True(
            PetCaptureRequest.TryRead(
                new GamePacket(secondPage),
                out var secondPageRequest) &&
            secondPageRequest.KitBagSlot == 27,
            "capture pages map to the authoritative absolute bag slot");
    }

    private static void CheckMalformedRequests()
    {
        var valid = Convert.FromHexString(
            "1C000C28899C000000000F00B01C34414C4AC74228A36940451BC842");

        var badPage = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            badPage.AsSpan(8, 2),
            4);
        Reject(badPage, "capture rejects a bag page beyond the native bag");

        var badSlot = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            badSlot.AsSpan(10, 2),
            24);
        Reject(badSlot, "capture rejects a slot beyond the native page");

        var notFinite = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            notFinite.AsSpan(12, 4),
            0x7FC00000);
        Reject(notFinite, "capture rejects non-finite client coordinates");

        var wrongOpcode = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongOpcode.AsSpan(2, 2),
            Opcodes.BasicAttack);
        Reject(wrongOpcode, "capture rejects another opcode");

        var truncated = valid[..^1];
        Reject(truncated, "capture rejects a truncated request");
    }

    private static void CheckDurableIdentity()
    {
        var connectionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();
        var identity = PetCommandOperationIdentity.ServerSessionLifecycle(
            operationId,
            connectionId);
        var capture = new PetCaptureIntent(
            40_073,
            runtimeId,
            7,
            11,
            10_150,
            MedusaEncounterDifficulty.Enhanced);
        var command = new BagItemActivationCommand(
            identity,
            15,
            Capture: capture);
        var subject = new CommandSubject(3, 5);
        var correlation = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var envelope =
            BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        var retry =
            BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
                subject,
                correlation,
                envelope.ReceivedAt.AddSeconds(1),
                command);

        Check.True(
            BagItemActivationCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "a server-owned capture command validates");
        Check.True(
            envelope.OperationId == retry.OperationId &&
            envelope.RequestHash == retry.RequestHash,
            "an exact capture retry has stable durable identity");
        Check.Equal(
            4,
            PetDurableCommandContract.CanonicalBagActivation(15).Length,
            "ordinary bag activation keeps its historical canonical bytes");
        Check.True(
            !PetDurableCommandContract.CanonicalBagActivation(15)
                .SequenceEqual(
                    PetDurableCommandContract.CanonicalBagActivation(
                        15,
                        capture)),
            "capture intent is included in the durable request hash");

        var differentTarget = command with
        {
            Capture = capture with { TargetHealthRevision = 12 }
        };
        var changed =
            BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
                subject,
                correlation,
                envelope.ReceivedAt,
                differentTarget);
        Check.True(
            envelope.RequestHash != changed.RequestHash,
            "different target evidence cannot replay the first capture");

        var changedDifficulty =
            BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(
                subject,
                correlation,
                envelope.ReceivedAt,
                command with
                {
                    Capture = capture with
                    {
                        Difficulty = MedusaEncounterDifficulty.Mythic
                    }
                });
        Check.True(
            envelope.RequestHash != changedDifficulty.RequestHash,
            "Advanced and Mythic captures have distinct durable identity");

        var invalid = envelope with
        {
            Command = command with
            {
                Capture = capture with { TargetObjectId = 0 }
            }
        };
        Check.True(
            BagItemActivationCommandEnvelope.Validate(invalid) ==
                CommandEnvelopeValidation.InvalidCommand,
            "incomplete capture evidence is rejected");

        var normal = envelope with
        {
            Command = command with
            {
                Capture = capture with
                {
                    Difficulty = MedusaEncounterDifficulty.Normal
                }
            }
        };
        Check.True(
            BagItemActivationCommandEnvelope.Validate(normal) ==
                CommandEnvelopeValidation.InvalidCommand,
            "Normal Medusa has no capturable Rock Elf distribution");
    }

    private static void CheckReceipt()
    {
        var receipt = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PetCaptured,
            AccountId: 3,
            CharacterId: 5,
            KitBagSlot: 15,
            EquipmentSlot: -1,
            PetId: 0,
            PetLevel: 0,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 9,
            AuditReference: "pet-capture-contract",
            OutboxEventId: Guid.NewGuid());
        receipt.Validate();
        Check.True(
            receipt.Succeeded,
            "a committed pet capture is a successful durable receipt");
    }

    private static void CheckAcquisitionProjection()
    {
        var net = CompactItemEntry.Empty with
        {
            Id = 10_084,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };
        var egg = CompactItemEntry.Empty with
        {
            Id = 10_150,
            Quality = 7,
            Grade = 1,
            Stack = 1
        };
        var existingEgg = egg with { Quality = 3 };
        var before = SetItems((1, existingEgg), (4, net));
        var after = SetItems((1, existingEgg), (4, egg));

        Check.True(
            GameClientHandler.TryResolvePetCaptureAcquisition(
                before,
                after,
                out var eggSlot,
                out var acquired) &&
            eggSlot == 4 && acquired == egg,
            "capture resolves the one newly committed egg despite an " +
            "existing egg");
        Check.Equal(
            0,
            GameClientHandler.GetPetCaptureAcquisitionScratchSlot(
                after,
                eggSlot,
                out var deleteBefore),
            "capture uses the first empty native acquisition slot");
        Check.True(
            !deleteBefore,
            "an empty acquisition slot needs no pre-delete");

        var fullAfter = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < 96; slot++)
        {
            fullAfter = KitBagSlots.SetSlot(
                fullAfter,
                slot,
                (slot == eggSlot
                    ? egg
                    : net with { Id = checked((uint)(20_000 + slot)) })
                .ToCompactString());
        }
        Check.Equal(
            eggSlot,
            GameClientHandler.GetPetCaptureAcquisitionScratchSlot(
                fullAfter,
                eggSlot,
                out deleteBefore),
            "a full bag temporarily reuses the committed egg slot");
        Check.True(
            deleteBefore,
            "a full-bag acquisition clears the egg slot before native add");

        var packet = PacketBuilder.SystemAddItemWithAcquisitionLog(acquired);
        Check.True(
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)) ==
                0x27C9 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)) ==
                10_150 &&
            packet[32] == egg.Quality,
            "the native acquisition log preserves the egg quality color");
    }

    private static string SetItems(
        params (int Slot, CompactItemEntry Item)[] items)
    {
        var bag = GameDefaults.EmptyKitBag;
        foreach (var (slot, item) in items)
        {
            bag = KitBagSlots.SetSlot(
                bag,
                slot,
                item.ToCompactString());
        }
        return bag;
    }

    private static void Reject(byte[] bytes, string description) =>
        Check.True(
            !PetCaptureRequest.TryRead(
                new GamePacket(bytes),
                out _),
            description);
}
