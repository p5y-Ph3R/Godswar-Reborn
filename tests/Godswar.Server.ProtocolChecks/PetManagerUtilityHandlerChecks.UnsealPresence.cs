using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetManagerUtilityHandlerChecks
{
    private static async Task
        CheckPackedUnsealRestoresOnlyFullHealthAsync()
    {
        await CheckPackedUnsealHealthProjectionAsync(
            currentHp: 9_500,
            expectedHp: 22_000,
            expectRestoration: true);
        await CheckPackedUnsealHealthProjectionAsync(
            currentHp: 9_000,
            expectedHp: 9_000,
            expectRestoration: false);
    }

    private static async Task CheckPackedUnsealHealthProjectionAsync(
        int currentHp,
        int expectedHp,
        bool expectRestoration)
    {
        const int previousMaximumHp = 9_500;
        const int updatedMaximumHp = 22_000;
        const int currentMp = 177;
        const long vitalsRevision = 42;
        const uint localPlayerObjectId = 0x00001448;

        var sealedPet = CreatePet(revision: 7) with
        {
            ActivityState = "sealed",
            IsCarried = false,
            IsSummoned = false
        };
        var unsealedPet = sealedPet with
        {
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            Revision = 8
        };
        var initial = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: sealedPet.PetId,
            bound: 1);
        initial.MaxHp = previousMaximumHp;
        initial.CurrentHp = currentHp;
        initial.CurrentMp = currentMp;
        initial.VitalsRevision = vitalsRevision;
        var updated = CharacterWithItem(0, -1);
        var persistedStats = CharacterSnapshotContractChecks
            .CreateValidSnapshot()
            .Character!
            .CalculatedStats with
        {
            MaxHp = updatedMaximumHp,
            CurrentHp = currentHp,
            CurrentMp = currentMp
        };
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(
                        envelope,
                        sealedPet,
                        PetManagerUtilityOperation.Unseal))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [unsealedPet],
            executor,
            persistedStats: persistedStats);

        await fixture.InvokeAsync(
            BreakItemPacket(MaterialSlot, Guid.NewGuid()));

        var packets = fixture.Transport.ReadLegacyPackets();
        var status = packets.Single(packet =>
            ReadOpcode(packet) == 10_166);
        Check.True(
            BinaryPrimitives.ReadInt32LittleEndian(
                status.AsSpan(104)) == expectedHp &&
            BinaryPrimitives.ReadInt32LittleEndian(
                status.AsSpan(144)) == updatedMaximumHp,
            expectRestoration
                ? "full Unseal projects full current HP at the restored maximum"
                : "injured Unseal preserves exact current HP while restoring maximum HP");

        var exactVitals = PacketBuilder.PlayerVitalsUpdate(
            localPlayerObjectId,
            expectedHp,
            currentMp);
        Check.Equal(
            expectRestoration ? 1 : 0,
            packets.Count(packet => packet.SequenceEqual(exactVitals)),
            "Unseal emits a narrow vitals update only when full health was restored");
        Check.True(
            expectRestoration
                ? fixture.SavedVitals is { } saved &&
                    saved.AccountId == initial.AccountId &&
                    saved.CharacterId == initial.Id &&
                    saved.CurrentHp == updatedMaximumHp &&
                    saved.CurrentMp == currentMp &&
                    saved.Revision == vitalsRevision + 1
                : fixture.SavedVitals is null,
            expectRestoration
                ? "full-health restoration is durably checkpointed before settlement"
                : "injured Unseal creates no vitals mutation or checkpoint");
    }

    private static async Task
        CheckPackedUnsealReplacesSummonedCompanionAsync()
    {
        var previous = CreatePet(revision: 7);
        var sealedPet = previous with
        {
            PetId = previous.PetId + 1,
            Name = "Sealed Replacement",
            ActivityState = "sealed",
            IsCarried = false,
            IsSummoned = false
        };
        var previousAfter = previous with
        {
            IsCarried = false,
            IsSummoned = false,
            Revision = 8
        };
        var unsealedPet = sealedPet with
        {
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            Revision = 8
        };
        var initial = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: sealedPet.PetId,
            bound: 1);
        var updated = CharacterWithItem(0, -1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Committed(
                    SuccessReceipt(
                        envelope,
                        sealedPet,
                        PetManagerUtilityOperation.Unseal))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [previousAfter, unsealedPet],
            executor);
        SetInitialPetProjection(fixture, initial, [previous]);

        await fixture.InvokeAsync(
            BreakItemPacket(MaterialSlot, Guid.NewGuid()));

        var packets = fixture.Transport.ReadLegacyPackets();
        var opcodes = packets.Select(ReadOpcode).ToArray();
        var recallIndex = packets.ToList().FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)previous.PetId),
                PetOperationResultCode.RecallSucceeded)));
        var listIndex = Array.IndexOf(opcodes, (ushort)10_237);
        var takeIndex = packets.ToList().FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)unsealedPet.PetId),
                PetOperationResultCode.TakeSucceeded)));
        var callOutIndex = packets.ToList().FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                checked((uint)unsealedPet.PetId),
                PetOperationResultCode.CallOutSucceeded)));
        var energyIndex = Array.IndexOf(opcodes, Opcodes.PetEnergy);
        Check.True(
            recallIndex >= 0 && recallIndex < listIndex &&
            listIndex < takeIndex && takeIndex < callOutIndex &&
            callOutIndex < energyIndex,
            "Unseal recalls the previous model before selecting and fully energizing the pet");
        Check.True(
            !opcodes.Contains((ushort)10_248),
            "replacement Unseal remains a live 10244 lifecycle");
    }

    private static async Task
        CheckPackedUnsealReplayDoesNotRepeatPresenceAsync()
    {
        var sealedPet = CreatePet(revision: 7) with
        {
            ActivityState = "sealed",
            IsCarried = false,
            IsSummoned = false
        };
        var unsealedPet = sealedPet with
        {
            ActivityState = "owned",
            IsCarried = true,
            IsSummoned = true,
            Revision = 8
        };
        var initial = CharacterWithItem(
            10109,
            MaterialSlot,
            linkedPetId: sealedPet.PetId,
            bound: 1);
        var updated = CharacterWithItem(0, -1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            PetManagerUtility = envelope =>
                PetDurableExecutionResult.Duplicate(
                    SuccessReceipt(
                        envelope,
                        sealedPet,
                        PetManagerUtilityOperation.Unseal))
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            initial,
            updated,
            [unsealedPet],
            executor);

        await fixture.InvokeAsync(
            BreakItemPacket(MaterialSlot, Guid.NewGuid()));

        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(ReadOpcode)
            .ToArray();
        Check.True(
            executor.PetManagerUtilityEnvelope?.Command.Operation ==
                PetManagerUtilityOperation.Unseal &&
            fixture.Transport.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Replayed &&
            !opcodes.Contains(Opcodes.PetOperationResult) &&
            !opcodes.Contains(Opcodes.PetEnergy) &&
            !opcodes.Contains((ushort)10_237) &&
            !opcodes.Contains((ushort)10_248) &&
            !opcodes.Contains((ushort)10_167) &&
            !opcodes.Contains((ushort)10_166),
            "duplicate Unseal never replays historical presence mutation");
    }

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
}
