using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private static readonly MethodInfo RefillLoginPetEnergyMethod =
        typeof(GameClientHandler).GetMethod(
            "RefillCarriedPetEnergyForLoginAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "Login pet-energy refill method was not found.");

    private static async Task CheckLoginBootstrapOrderingAsync()
    {
        var character = CreateCharacter();
        character.CheckpointOwnerId = Guid.Parse(
            "aef5829f-5958-4ef7-88e5-8db67e4df445");
        character.CheckpointOwnerGeneration = 7;
        var pet = CreateGodlyKingLion() with
        {
            CurrentEnergy = 84,
            MaximumEnergy = 100,
            IsCarried = true
        };
        var store = new LoginBootstrapStore(character, [pet]);
        var lifecycle = new LoginEnergyLifecycleExecutor(pet);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = CreateLoginEnergyHandler(
            session,
            store,
            lifecycle,
            character,
            [pet]);

        await InvokePacketAsync(
            handler,
            CreateOpcodePacket(Opcodes.EnterGame));

        Check.True(
            lifecycle.RestoreCount == 1 &&
            lifecycle.LastEnergyPoints == int.MaxValue &&
            lifecycle.CurrentEnergy == pet.MaximumEnergy &&
            lifecycle.LastSubject is
                { AccountId: AccountId, CharacterId: CharacterId } &&
            lifecycle.LastOwnership is { } loginOwnership &&
            loginOwnership.OwnerId == character.CheckpointOwnerId &&
            loginOwnership.Generation ==
                character.CheckpointOwnerGeneration,
            "enter durably fills the carried pet before presentation");
        var projectedPet = ReadLoginPets(handler).Single();
        Check.True(
            projectedPet.CurrentEnergy == projectedPet.MaximumEnergy &&
            projectedPet.Revision == lifecycle.Revision,
            "the login snapshot adopts the committed energy and revision");
        Check.Equal(
            pet.Revision + 1,
            lifecycle.Revision,
            "partial login refill advances the durable pet revision once");

        var clearBytes = transport.WrittenBytes;
        new PacketCipher().Transform(clearBytes);
        var packets = SplitPackets(clearBytes);
        var petPacketIndex = packets.FindIndex(
            static packet => ReadUInt16(packet, 2) == OwnedPetListOpcode);
        Check.True(
            petPacketIndex >= 0,
            "enter flow sends the owned-pet list");
        var energyPacketIndex = packets.FindIndex(
            static packet =>
                ReadUInt16(packet, 2) == Opcodes.PetEnergy);
        Check.Equal(
            petPacketIndex + 1,
            energyPacketIndex,
            "the first pet-energy packet immediately follows selection");
        var uiBootstrapIndex = packets.FindIndex(
            static packet => ReadUInt16(packet, 2) == 10_329);
        Check.True(
            uiBootstrapIndex >= 0,
            "enter flow sends the captured UI bootstrap");

        var expectedBagPackets = PacketBuilder
            .KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .ToArray();
        Check.True(
            petPacketIndex >= expectedBagPackets.Length,
            "owned-pet list follows the complete bag bootstrap");
        var bagStart = petPacketIndex - expectedBagPackets.Length;
        Check.True(
            uiBootstrapIndex < bagStart,
            "deterministic server order preserves UI before bag bootstrap");
        for (var index = 0; index < expectedBagPackets.Length; index++)
        {
            Check.True(
                packets[bagStart + index].SequenceEqual(
                    expectedBagPackets[index]),
                $"bag packet {index} precedes owned-pet list unchanged");
        }

        Check.True(
            uiBootstrapIndex < petPacketIndex,
            "captured UI bootstrap precedes OwnedPetList");
        Check.True(
            packets[petPacketIndex].SequenceEqual(
                PacketBuilder.OwnedPetList(
                    PetContentTestCatalog.Instance,
                    [projectedPet],
                    openedCellCount: 2)),
            "enter flow selects the committed carried-pet snapshot");
        Check.True(
            packets[energyPacketIndex].SequenceEqual(
                PacketBuilder.PetEnergy(
                    pet.MaximumEnergy,
                    pet.MaximumEnergy)),
            "the first client energy value truthfully reports full authority");
        Check.Equal(
            (ushort)10_196,
            ReadUInt16(packets[petPacketIndex + 2], 2),
            "SkillList immediately follows carried-pet energy");
        Check.Equal(
            Opcodes.GameServerReady,
            ReadUInt16(packets[petPacketIndex + 3], 2),
            "EnterComplete immediately follows SkillList");
        Check.Equal(
            packets.Count - 4,
            petPacketIndex,
            "no packet is inserted after the terminal enter sequence");
        Check.Equal(
            0,
            store.OwnedPetReadCount,
            "initial enter consumes pets from the single character snapshot");

        await CheckLoginEnergyScopeAsync(pet);
        await CheckAlreadyFullLoginEnergyAsync(pet);
        await CheckStaleMergeEndsBeforeLoginRefillAsync(pet);
    }

    private static async Task CheckLoginEnergyScopeAsync(
        PetBootstrapSnapshot pet)
    {
        var ordinary = CreateCharacter();
        var noPetLifecycle = new LoginEnergyLifecycleExecutor(pet);
        await using (var noPet = CreateLoginEnergyFixture(
                         ordinary,
                         [],
                         noPetLifecycle,
                         CreateRegistry()))
        {
            await InvokeLoginEnergyRefillAsync(noPet.Handler);
            Check.Equal(
                0,
                noPetLifecycle.RestoreCount,
                "login without a carried pet performs no durable refill");
        }

        var multiOwnedCharacter = CreateCharacter();
        SetLoginOwnership(multiOwnedCharacter, generation: 10);
        var uncarriedPet = pet with
        {
            PetId = pet.PetId + 1,
            Name = "Uncarried companion",
            CurrentEnergy = 19,
            IsCarried = false,
            IsSummoned = false,
            Revision = 41
        };
        var multiOwnedLifecycle = new LoginEnergyLifecycleExecutor(pet);
        await using (var multiOwned = CreateLoginEnergyFixture(
                         multiOwnedCharacter,
                         [pet, uncarriedPet],
                         multiOwnedLifecycle,
                         CreateRegistry()))
        {
            await InvokeLoginEnergyRefillAsync(multiOwned.Handler);
            var projected = ReadLoginPets(multiOwned.Handler);
            Check.True(
                projected.Single(candidate =>
                    candidate.PetId == pet.PetId).CurrentEnergy ==
                    pet.MaximumEnergy &&
                projected.Single(candidate =>
                    candidate.PetId == uncarriedPet.PetId) == uncarriedPet,
                "login refills only the one carried pet among multiple owned pets");
        }

        var duplicateLifecycle = new LoginEnergyLifecycleExecutor(pet);
        await using (var duplicate = CreateLoginEnergyFixture(
                         ordinary,
                         [
                             pet,
                             pet with
                             {
                                 PetId = pet.PetId + 1,
                                 Name = "Second carried pet"
                             }
                         ],
                         duplicateLifecycle,
                         CreateRegistry()))
        {
            await ExpectLoginEnergyFailureAsync(duplicate.Handler);
            Check.Equal(
                0,
                duplicateLifecycle.RestoreCount,
                "invalid multi-carried state cannot mutate durable energy");
        }

        var dummy = TrainingDummyHostileStatusTestFixture.CreateDummy();
        var dummyPet = pet with
        {
            AccountId = dummy.AccountId,
            OwnerCharacterId = dummy.Id,
            CurrentEnergy = 37,
            ContributesToCharacter = true
        };
        var dummyLifecycle = new LoginEnergyLifecycleExecutor(dummyPet);
        await using var dummyFixture = CreateLoginEnergyFixture(
            dummy,
            [dummyPet],
            dummyLifecycle,
            TrainingDummyHostileStatusTestFixture.CreateRegistry());
        await InvokeLoginEnergyRefillAsync(dummyFixture.Handler);
        Check.True(
            dummyLifecycle.RestoreCount == 0 &&
            ReadLoginPets(dummyFixture.Handler).Single().CurrentEnergy == 37,
            "pinned merged training dummies bypass user-login refill");
    }

    private static GameClientHandler CreateLoginEnergyHandler(
        ClientSession session,
        LoginBootstrapStore store,
        LoginEnergyLifecycleExecutor lifecycle,
        GameCharacter character,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        GameSessionRegistry? registry = null,
        ICharacterSnapshotReader? snapshotReader = null)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry ?? CreateRegistry(),
            snapshotReader ??
                CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            petDurableCommands: lifecycle,
            petContent: PetContentTestCatalog.Instance);
        SetField(
            handler,
            "_account",
            new AccountIdentity(character.AccountId, character.Name));
        SetField(handler, "_character", character);
        SetField(
            handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                character,
                [],
                [],
                new CharacterPetShedSnapshot(2, 0),
                pets,
                []));
        SetField(handler, "_characterSnapshotLoaded", true);
        SetField(
            handler,
            "_characterSnapshotBootstrapPending",
            true);
        return handler;
    }

    private static LoginEnergyFixture CreateLoginEnergyFixture(
        GameCharacter character,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        LoginEnergyLifecycleExecutor lifecycle,
        GameSessionRegistry registry)
    {
        var store = new LoginBootstrapStore(character, pets);
        var transport = new ScriptedLegacyByteTransport();
        var session = new ClientSession(transport);
        return new LoginEnergyFixture(
            session,
            CreateLoginEnergyHandler(
                session,
                store,
                lifecycle,
                character,
                pets,
                registry));
    }

    private static GameSessionRegistry CreateRegistry() =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);

    private static async Task InvokeLoginEnergyRefillAsync(
        GameClientHandler handler)
    {
        var task = RefillLoginPetEnergyMethod.Invoke(
            handler,
            [CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Login pet-energy refill returned no task.");
        await task;
    }

    private static async Task ExpectLoginEnergyFailureAsync(
        GameClientHandler handler)
    {
        try
        {
            await InvokeLoginEnergyRefillAsync(handler);
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Multiple carried pets did not fail login energy refill.");
    }

    private static IReadOnlyList<PetBootstrapSnapshot> ReadLoginPets(
        GameClientHandler handler)
    {
        var field = typeof(GameClientHandler).GetField(
            "_characterLoadSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Character load snapshot field was not found.");
        return (field.GetValue(handler) as HydratedCharacterLoadSnapshot)?.Pets
            ?? throw new InvalidOperationException(
                "Character load snapshot was not installed.");
    }

    private sealed class LoginEnergyLifecycleExecutor(
        PetBootstrapSnapshot pet) :
        DelegatingPetDurableCommandExecutor,
        IPetOwnerMergeLifecycleStore
    {
        public int RestoreCount { get; private set; }
        public int LastEnergyPoints { get; private set; }
        public int CurrentEnergy { get; private set; } = pet.CurrentEnergy;
        public long Revision { get; private set; } = pet.Revision;
        public List<string> Operations { get; } = [];
        public CommandSubject? LastSubject { get; private set; }
        public PlayerOwnershipFence? LastOwnership { get; private set; }

        public Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            int energyPoints,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PetOwnerMergeLifecycleResult> RestoreEnergyAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            int energyPoints,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("restore");
            LastSubject = subject;
            LastOwnership = ownership;
            RestoreCount++;
            LastEnergyPoints = energyPoints;
            var changed = CurrentEnergy != pet.MaximumEnergy;
            CurrentEnergy = pet.MaximumEnergy;
            if (changed)
            {
                Revision++;
            }
            return Task.FromResult(new PetOwnerMergeLifecycleResult(
                changed
                    ? PetOwnerMergeLifecycleStatus.EnergyChanged
                    : PetOwnerMergeLifecycleStatus.EnergyAtMaximum,
                pet.PetId,
                CurrentEnergy,
                pet.MaximumEnergy,
                Revision,
                IsCarried: true,
                pet.IsSummoned));
        }

        public Task<PetOwnerMergeLifecycleResult> EndAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            PetOwnerMergeEndReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reason != PetOwnerMergeEndReason.StaleLoginRecovery)
            {
                throw new InvalidOperationException(
                    "The login fixture received an unexpected Merge end.");
            }

            Operations.Add("end");
            LastSubject = subject;
            LastOwnership = ownership;
            Revision++;
            return Task.FromResult(new PetOwnerMergeLifecycleResult(
                PetOwnerMergeLifecycleStatus.MergeEnded,
                pet.PetId,
                CurrentEnergy,
                pet.MaximumEnergy,
                Revision,
                IsCarried: true,
                pet.IsSummoned));
        }
    }

    private sealed record LoginEnergyFixture(
        ClientSession Session,
        GameClientHandler Handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }
}
