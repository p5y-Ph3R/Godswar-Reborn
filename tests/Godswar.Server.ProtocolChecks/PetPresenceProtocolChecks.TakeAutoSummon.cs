using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetPresenceProtocolChecks
{
    private static async Task CheckTakeAutoSummonsDifferentPetAsync(
        bool previousWasSummoned)
    {
        const uint targetPetId = 2;
        var operationId = Guid.NewGuid();
        var previous = CreateAutoSummonPet(
            PetId,
            isCarried: true,
            isSummoned: previousWasSummoned,
            revision: 1);
        var targetBefore = CreateAutoSummonPet(
            targetPetId,
            isCarried: false,
            isSummoned: false,
            revision: 1);
        var previousAfter = previous with
        {
            IsCarried = false,
            IsSummoned = false,
            Revision = 2
        };
        var targetAfter = targetBefore with
        {
            IsCarried = true,
            IsSummoned = true,
            Revision = 2
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
                PetDurableExecutionResult.Committed(
                    CreateTakeReceipt(
                        envelope,
                        targetPetId,
                        targetAfter.Revision))
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [previousAfter, targetAfter],
            executor);
        SetInitialPetProjection(
            fixture,
            character,
            [previous, targetBefore]);

        await fixture.InvokeAsync(
            CreateActionPacket(
                Opcodes.PetTakeRequest,
                targetPetId,
                operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            4,
            packets.Count,
            "Take switches from a " +
            $"{(previousWasSummoned ? "summoned" : "recalled")} pet " +
            "with two native results and one stat snapshot pair");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    targetPetId,
                    PetOperationResultCode.TakeSucceeded)),
            "different-pet Take selects the new pet first");
        Check.True(
            packets[1].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    targetPetId,
                    PetOperationResultCode.CallOutSucceeded)),
            "different-pet Take summons the newly selected pet second");
        Check.True(
            ReadOpcode(packets[2]) == 10_167 &&
            ReadOpcode(packets[3]) == 10_166,
            "different-pet Take refreshes carried passives after native Take/CallOut");
        Check.True(
            packets.All(packet =>
                !packet.SequenceEqual(
                    PacketBuilder.PetOperationResult(
                        PetId,
                        PetOperationResultCode.RecallSucceeded))),
            "different-pet Take relies on native Take to dispose the old model");
        Check.Equal(
            1,
            executor.TransitionCount,
            "different-pet Take executes one durable transition");
    }

    private static async Task CheckTakeSamePetDoesNotAutoSummonAsync()
    {
        var operationId = Guid.NewGuid();
        var selected = CreateAutoSummonPet(
            PetId,
            isCarried: true,
            isSummoned: true,
            revision: 2);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
                PetDurableExecutionResult.Committed(
                    CreateTakeReceipt(
                        envelope,
                        PetId,
                        selected.Revision))
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [selected],
            executor);
        SetInitialPetProjection(
            fixture,
            character,
            [selected]);

        await fixture.InvokeAsync(
            CreateActionPacket(
                Opcodes.PetTakeRequest,
                PetId,
                operationId));

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            "Take of the already selected pet emits one native result");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    PetId,
                    PetOperationResultCode.TakeSucceeded)),
            "Take of the already selected pet does not call it out again");
    }

    private static async Task
        CheckTakeDuplicateReplayDoesNotAutoSummonTwiceAsync()
    {
        const uint targetPetId = 2;
        var operationId = Guid.NewGuid();
        var previous = CreateAutoSummonPet(
            PetId,
            isCarried: true,
            isSummoned: true,
            revision: 1);
        var targetBefore = CreateAutoSummonPet(
            targetPetId,
            isCarried: false,
            isSummoned: false,
            revision: 1);
        var targetAfter = targetBefore with
        {
            IsCarried = true,
            IsSummoned = true,
            Revision = 2
        };
        var previousAfter = previous with
        {
            IsCarried = false,
            IsSummoned = false,
            Revision = 2
        };
        var executionCount = 0;
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Transition = envelope =>
            {
                var receipt = CreateTakeReceipt(
                    envelope,
                    targetPetId,
                    targetAfter.Revision);
                return ++executionCount == 1
                    ? PetDurableExecutionResult.Committed(receipt)
                    : PetDurableExecutionResult.Duplicate(receipt);
            }
        };
        var character = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            character,
            character,
            [previousAfter, targetAfter],
            executor);
        SetInitialPetProjection(
            fixture,
            character,
            [previous, targetBefore]);
        var packet = CreateActionPacket(
            Opcodes.PetTakeRequest,
            targetPetId,
            operationId);

        await fixture.InvokeAsync(packet);
        await fixture.InvokeAsync(packet);

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.Equal(
            5,
            packets.Count,
            "duplicate Take replay does not emit a second CallOut or stale stat pair");
        Check.True(
            packets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    targetPetId,
                    PetOperationResultCode.TakeSucceeded)) &&
            packets[1].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    targetPetId,
                    PetOperationResultCode.CallOutSucceeded)) &&
            ReadOpcode(packets[2]) == 10_167 &&
            ReadOpcode(packets[3]) == 10_166 &&
            packets[4].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    targetPetId,
                    PetOperationResultCode.TakeSucceeded)),
            "duplicate Take replay preserves Take/CallOut/status/status/Take ordering");
        Check.Equal(
            2,
            executor.TransitionCount,
            "duplicate Take reaches the idempotent executor twice");
        Check.True(
            fixture.Transport.CommandResults is
            [
                {
                    Disposition: SecureLegacyCommandDisposition.Applied,
                    OperationId: var appliedOperation
                },
                {
                    Disposition: SecureLegacyCommandDisposition.Replayed,
                    OperationId: var replayedOperation
                }
            ] &&
            appliedOperation == operationId &&
            replayedOperation == operationId,
            "duplicate Take reports applied then replayed for one operation ID");
    }

    private static PetDurableReceipt CreateTakeReceipt(
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        uint petId,
        long revision) =>
        new(
            CommandFamily.PetPresenceTransition,
            PetDurableReceiptStatus.PresenceChanged,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            petId,
            PetLevel: 1,
            PetExperience: 0,
            PetRevision: revision,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 1,
            AggregateRevision: revision,
            AuditReference: "pet-take-auto-summon-check",
            OutboxEventId: Guid.NewGuid());

    private static void SetInitialPetProjection(
        PetDurableHandlerFixture fixture,
        GameCharacter character,
        IReadOnlyList<PetBootstrapSnapshot> pets) =>
        PetDurableHandlerFixture.SetField(
            fixture.Handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                character,
                [],
                [],
                new CharacterPetShedSnapshot(
                    checked((short)Math.Max(
                        PetShedCapacityPolicy.DefaultOpenedCellCount,
                        pets.Count)),
                    Revision: 0),
                pets,
                []));

    private static PetBootstrapSnapshot CreateAutoSummonPet(
        uint petId,
        bool isCarried,
        bool isSummoned,
        long revision) =>
        CreatePet(isCarried, isSummoned, revision) with
        {
            PetId = petId,
            Name = petId == PetId ? "Rock Elf" : "Flower Pixie"
        };
}
