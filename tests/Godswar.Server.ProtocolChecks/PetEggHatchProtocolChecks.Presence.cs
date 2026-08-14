using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchProtocolChecks
{
    private static async Task CheckSummonedCompanionReplacementAsync()
    {
        var initial = CharacterWithEgg(stack: 1);
        var updated = new GameCharacter
        {
            Id = initial.Id,
            AccountId = initial.AccountId,
            Name = initial.Name,
            Profession = initial.Profession,
            Equipment = initial.Equipment,
            KitBag = KitBagSlots.ClearSlot(initial.KitBag, EggSlot)
        };
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            50m,
            new Random(51));
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            3_500,
            new Random(3_501));
        var previous = CreatePet(savvy, growth) with
        {
            PetId = PetId - 1,
            Name = "Previous Companion",
            IsCarried = true,
            IsSummoned = true
        };
        var previousAfter = previous with
        {
            IsCarried = false,
            IsSummoned = false,
            Revision = previous.Revision + 1
        };
        var hatched = CreatePet(savvy, growth) with
        {
            IsCarried = true,
            IsSummoned = true
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope =>
                PetDurableExecutionResult.Committed(
                    new PetDurableReceipt(
                        CommandFamily.BagItemActivation,
                        PetDurableReceiptStatus.EggHatched,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        envelope.Command.KitBagSlot,
                        EquipmentSlot: -1,
                        PetId,
                        PetLevel: 1,
                        PetExperience: 0,
                        PetRevision: 0,
                        IsCarried: true,
                        IsSummoned: true,
                        PresenceOperation: 0,
                        AggregateRevision: 1,
                        AuditReference: "pet-hatch-presence-check",
                        OutboxEventId: Guid.NewGuid()))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [previousAfter, hatched],
            executor);
        PetDurableHandlerFixture.SetField(
            fixture.Handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                initial,
                [],
                [],
                new CharacterPetShedSnapshot(2, 0),
                [previous],
                []));

        await fixture.InvokeAsync(
            CreateEggUsePacket(EggSlot, Guid.NewGuid()));

        var packets = fixture.Transport.ReadLegacyPackets();
        var presencePackets = packets
            .Where(packet => ReadOpcode(packet) ==
                Opcodes.PetOperationResult)
            .ToArray();
        Check.Equal(
            3,
            presencePackets.Length,
            "hatch replaces a previously summoned companion in three steps");
        Check.True(
            presencePackets[0].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    checked((uint)previous.PetId),
                    PetOperationResultCode.RecallSucceeded)),
            "hatch removes the previous companion model first");
        Check.True(
            presencePackets[1].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    checked((uint)hatched.PetId),
                    PetOperationResultCode.TakeSucceeded)),
            "hatch selects the new pet");
        Check.True(
            presencePackets[2].SequenceEqual(
                PacketBuilder.PetOperationResult(
                    checked((uint)hatched.PetId),
                    PetOperationResultCode.CallOutSucceeded)),
            "hatch restores the visible companion with the new pet");
        var orderedPackets = packets.ToList();
        var previousRecallIndex = orderedPackets.FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)previous.PetId),
                PetOperationResultCode.RecallSucceeded)));
        var ownedPetListIndex = orderedPackets.FindIndex(packet =>
            ReadOpcode(packet) == 10_237);
        var hatchedTakeIndex = orderedPackets.FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)hatched.PetId),
                PetOperationResultCode.TakeSucceeded)));
        var hatchedCallOutIndex = orderedPackets.FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)hatched.PetId),
                PetOperationResultCode.CallOutSucceeded)));
        Check.True(
            previousRecallIndex >= 0 &&
            previousRecallIndex < ownedPetListIndex &&
            ownedPetListIndex < hatchedTakeIndex &&
            hatchedTakeIndex < hatchedCallOutIndex,
            "hatch recalls the old model before rebuilding native pet state");
        Check.True(
            packets.All(packet =>
                ReadOpcode(packet) != 10_248),
            "live hatch replacement does not misuse world-ready restore");
    }
}
