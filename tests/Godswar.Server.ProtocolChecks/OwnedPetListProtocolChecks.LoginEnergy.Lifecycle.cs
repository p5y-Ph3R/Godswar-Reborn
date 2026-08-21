using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private static async Task CheckAlreadyFullLoginEnergyAsync(
        PetBootstrapSnapshot basis)
    {
        var character = CreateCharacter();
        SetLoginOwnership(character, generation: 8);
        var fullPet = basis with
        {
            CurrentEnergy = basis.MaximumEnergy,
            Revision = 71
        };
        var lifecycle = new LoginEnergyLifecycleExecutor(fullPet);
        await using var fixture = CreateLoginEnergyFixture(
            character,
            [fullPet],
            lifecycle,
            CreateRegistry());

        await InvokeLoginEnergyRefillAsync(fixture.Handler);

        var projected = ReadLoginPets(fixture.Handler).Single();
        Check.True(
            lifecycle.RestoreCount == 1 &&
            lifecycle.LastEnergyPoints == int.MaxValue &&
            lifecycle.Revision == fullPet.Revision &&
            projected.Revision == fullPet.Revision &&
            projected.CurrentEnergy == projected.MaximumEnergy,
            "already-full login validates authority without revision churn");
    }

    private static async Task CheckStaleMergeEndsBeforeLoginRefillAsync(
        PetBootstrapSnapshot basis)
    {
        var character = CreateCharacter();
        SetLoginOwnership(character, generation: 9);
        var mergedPet = basis with
        {
            CurrentEnergy = 42,
            IsSummoned = true,
            ContributesToCharacter = true,
            Revision = 80
        };
        var recoveredPet = mergedPet with
        {
            ContributesToCharacter = false,
            Revision = mergedPet.Revision + 1
        };
        var refreshedSnapshot = PetDurableHandlerFixture.CreateSnapshot(
            character,
            [recoveredPet]);
        var lifecycle = new LoginEnergyLifecycleExecutor(mergedPet);
        var store = new LoginBootstrapStore(character, [mergedPet]);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = CreateLoginEnergyHandler(
            session,
            store,
            lifecycle,
            character,
            [mergedPet],
            snapshotReader:
                new LoginEnergySnapshotReader(refreshedSnapshot));

        await InvokePacketAsync(
            handler,
            CreateOpcodePacket(Opcodes.EnterGame));

        Check.True(
            lifecycle.Operations.SequenceEqual(["end", "restore"]),
            "stale owner Merge ends durably before login energy refill");
        var projected = ReadLoginPets(handler).Single();
        Check.True(
            !projected.ContributesToCharacter &&
            projected.CurrentEnergy == projected.MaximumEnergy &&
            projected.Revision == mergedPet.Revision + 2,
            "stale-Merge recovery snapshot receives the later full refill");

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        var packets = SplitPackets(clearBytes);
        var firstEnergy = packets.FindIndex(
            static packet => ReadUInt16(packet, 2) == Opcodes.PetEnergy);
        Check.True(
            firstEnergy >= 0 &&
            packets[firstEnergy].SequenceEqual(
                PacketBuilder.PetEnergy(
                    recoveredPet.MaximumEnergy,
                    recoveredPet.MaximumEnergy)),
            "stale-Merge login first publishes the committed full energy");
    }

    private static void SetLoginOwnership(
        GameCharacter character,
        long generation)
    {
        character.CheckpointOwnerId = Guid.Parse(
            "aef5829f-5958-4ef7-88e5-8db67e4df445");
        character.CheckpointOwnerGeneration = generation;
    }

    private sealed class LoginEnergySnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "login energy refresh account");
            return Task.FromResult(snapshot);
        }
    }
}
