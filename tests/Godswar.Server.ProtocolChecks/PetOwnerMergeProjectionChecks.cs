using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeProjectionChecks
{
    private const int CharacterId = 2;
    private const int AccountId = 13;
    private const int CharmSlot = 9;
    private const uint PetId = 1;

    public static async Task RunAsync()
    {
        await CheckMergeStartProjectionAsync();
        await CheckMergeEndProjectionAsync();
        await CheckEnergyRejectionProjectsCurrentGaugeAsync();
        await CheckHistoricalStartReplayUsesCurrentEndedStateAsync();
        await CheckHistoricalEndReplayUsesCurrentMergedStateAsync();
        await CheckHistoricalReceiptPreservesDifferentActivePetAsync();
        await CheckNearbyObserverReceivesWorldNamespaceAsync();
        await CheckNearbyObserverReceivesMergeEndAsync();
        await CheckRemergeGetsFreshEnergyTimerAsync();
        await CheckStaleLoginMergeFailsClosedAsync();
    }

    private static async Task CheckNearbyObserverReceivesWorldNamespaceAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 14) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        var actor = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            actor,
            actor,
            [pet],
            Executor(PetDurableReceiptStatus.OwnerMerged, pet));
        fixture.Registry.JoinMap(
            fixture.Session,
            AccountId,
            actor,
            WorldObjectIds.ForPlayer(actor.Id));
        var beforePresentation = fixture.Registry
            .GetMapSessions(actor.CurrentMap)
            .Single(context => context.Session == fixture.Session);

        var viewerTransport = new PetDurableCaptureTransport();
        await using var viewerSession = new ClientSession(
            viewerTransport);
        var viewer = new GameCharacter
        {
            Id = 3,
            AccountId = 14,
            Name = "MergeViewer",
            CurrentMap = actor.CurrentMap,
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = GameDefaults.EmptyKitBag
        };
        GameHandlerOwnershipTestFences.Bind(
            fixture.Registry,
            viewerSession,
            viewer.AccountId,
            viewer);
        fixture.Registry.JoinMap(
            viewerSession,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var activePresentation = fixture.Registry
            .GetMapSessions(actor.CurrentMap)
            .Single(context => context.Session == fixture.Session);
        Check.True(
            activePresentation.PetOwnerMergeActive &&
            activePresentation.WorldRevision ==
                beforePresentation.WorldRevision + 1,
            "Merge start atomically advances AOI revision with its presentation flag");
        var observerStart = viewerTransport.ReadLegacyPackets()
            .Single(packet => Opcode(packet) ==
                Opcodes.PetOwnerMergeStarted);
        Check.Equal(
            WorldObjectIds.ForPlayer(actor.Id),
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerStart.AsSpan(4)),
            "nearby Merge start uses the actor's world object namespace");
        var observerStatus = viewerTransport.ReadLegacyPackets()
            .Single(packet => Opcode(packet) == 0x27B6);
        Check.Equal(
            WorldObjectIds.ForPlayer(actor.Id),
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerStatus.AsSpan(4)),
            "nearby Merge start refreshes the actor's authoritative stats");
        fixture.Registry.SetPetOwnerMergePresentation(
            fixture.Session,
            active: false);
        var endedPresentation = fixture.Registry
            .GetMapSessions(actor.CurrentMap)
            .Single(context => context.Session == fixture.Session);
        Check.True(
            !endedPresentation.PetOwnerMergeActive &&
            endedPresentation.WorldRevision ==
                activePresentation.WorldRevision + 1,
            "Merge end atomically advances AOI revision with its presentation flag");
        fixture.Registry.Remove(viewerSession);
        fixture.Registry.Remove(fixture.Session);
    }

    private static async Task CheckNearbyObserverReceivesMergeEndAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 15) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = false
        };
        var actor = CreateCharacter();
        await using var fixture = PetDurableHandlerFixture.Create(
            actor,
            actor,
            [pet],
            Executor(PetDurableReceiptStatus.OwnerUnmerged, pet));
        fixture.Registry.JoinMap(
            fixture.Session,
            AccountId,
            actor,
            WorldObjectIds.ForPlayer(actor.Id));

        var viewerTransport = new PetDurableCaptureTransport();
        await using var viewerSession = new ClientSession(viewerTransport);
        var viewer = new GameCharacter
        {
            Id = 4,
            AccountId = 15,
            Name = "MergeEndViewer",
            CurrentMap = actor.CurrentMap,
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = GameDefaults.EmptyKitBag
        };
        GameHandlerOwnershipTestFences.Bind(
            fixture.Registry,
            viewerSession,
            viewer.AccountId,
            viewer);
        fixture.Registry.JoinMap(
            viewerSession,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var packets = viewerTransport.ReadLegacyPackets();
        var end = packets.Single(packet =>
            Opcode(packet) == Opcodes.PetOwnerMergeEnded);
        var status = packets.Single(packet => Opcode(packet) == 0x27B6);
        var actorObjectId = WorldObjectIds.ForPlayer(actor.Id);
        Check.Equal(
            actorObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(4)),
            "nearby Merge end uses the actor's world object namespace");
        Check.Equal(
            actorObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)),
            "nearby Merge end refreshes the actor's restored stats");

        fixture.Registry.Remove(viewerSession);
        fixture.Registry.Remove(fixture.Session);
    }

    private static async Task
        CheckHistoricalStartReplayUsesCurrentEndedStateAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 12) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = false
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(
                PetDurableReceiptStatus.OwnerMerged,
                pet,
                duplicate: true));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(Opcode)
            .ToArray();
        Check.True(
            opcodes.Contains(Opcodes.PetOwnerMergeEnded) &&
            !opcodes.Contains(Opcodes.PetOwnerMergeStarted) &&
            !opcodes.Contains((ushort)10237),
            "delayed OwnerMerged replay reconciles the current ended snapshot");
    }

    private static async Task
        CheckHistoricalEndReplayUsesCurrentMergedStateAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 13) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(
                PetDurableReceiptStatus.OwnerUnmerged,
                pet,
                duplicate: true));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var opcodes = fixture.Transport.ReadLegacyPackets()
            .Select(Opcode)
            .ToArray();
        Check.True(
            opcodes.Contains(Opcodes.PetOwnerMergeStarted) &&
            !opcodes.Contains(Opcodes.PetOwnerMergeEnded) &&
            !opcodes.Contains((ushort)10237),
            "delayed OwnerUnmerged replay preserves the current newer Merge");
    }

    private static async Task CheckRemergeGetsFreshEnergyTimerAsync()
    {
        var executor = new OwnerMergeLifecycleTestExecutor();
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [],
            executor,
            petOwnerMergeEnergyInterval: TimeSpan.FromMilliseconds(200));
        var start = typeof(GameClientHandler).GetMethod(
            "StartPetOwnerMergeEnergyDrain",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Owner-Merge timer start method was not found.");
        var cancel = typeof(GameClientHandler).GetMethod(
            "CancelPetOwnerMergeEnergyDrain",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Owner-Merge timer cancel method was not found.");

        start.Invoke(fixture.Handler, null);
        await Task.Delay(140);
        cancel.Invoke(fixture.Handler, null);
        start.Invoke(fixture.Handler, null);
        await Task.Delay(100);
        Check.Equal(
            0,
            executor.DrainCount,
            "cancelled Merge generation cannot drain a fresh Merge on its old schedule");
        await Task.Delay(130);
        Check.Equal(
            1,
            executor.DrainCount,
            "fresh Merge receives exactly one new-generation energy tick");
        cancel.Invoke(fixture.Handler, null);
    }

    private static async Task CheckStaleLoginMergeFailsClosedAsync()
    {
        var mergedPet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 16) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [mergedPet],
            new DelegatingPetDurableCommandExecutor());
        var recover = typeof(GameClientHandler).GetMethod(
            "RecoverStalePetOwnerMergeOnLoginAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Owner-Merge login recovery method was not found.");

        var task = recover.Invoke(
            fixture.Handler,
            new object[]
            {
                new[] { mergedPet },
                CancellationToken.None
            }) as Task<bool> ?? throw new InvalidOperationException(
                "Owner-Merge login recovery returned no task.");
        var failedClosed = false;
        try
        {
            await task;
        }
        catch (InvalidDataException)
        {
            failedClosed = true;
        }
        Check.True(
            failedClosed,
            "world entry fails closed when stale Merge has no durable lifecycle store");
    }

    private static async Task CheckMergeStartProjectionAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 10) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = true
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(PetDurableReceiptStatus.OwnerMerged, pet));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var packets = fixture.Transport.ReadLegacyPackets();
        var energyIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetEnergy);
        var startIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetOwnerMergeStarted);
        Check.True(
            energyIndex >= 0 && startIndex > energyIndex,
            "committed Merge sends self energy before the native unite effect");
        Check.True(
            packets.All(packet => Opcode(packet) != 10237),
            "committed Merge never rebuilds the active-pet list");
        Check.True(
            packets.Any(packet => Opcode(packet) == 0x27B6),
            "committed Merge refreshes authoritative character stats");
        var start = packets.Single(packet => Opcode(packet) ==
            Opcodes.PetOwnerMergeStarted);
        var energy = packets[energyIndex];
        Check.Equal(
            1_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(energy.AsSpan(4)),
            "full normalized Merge energy projects as native 1800");
        Check.Equal(
            0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(4)),
            "self Merge start uses the native local-player object ID");
    }

    private static async Task CheckMergeEndProjectionAsync()
    {
        var pet = PetPresenceProtocolChecks.CreatePet(
            isCarried: true,
            isSummoned: true,
            revision: 11) with
        {
            HasOwnerMergeTalent = true,
            TalentMask = 16,
            ContributesToCharacter = false
        };
        await using var fixture = PetDurableHandlerFixture.Create(
            CreateCharacter(),
            CreateCharacter(),
            [pet],
            Executor(PetDurableReceiptStatus.OwnerUnmerged, pet));

        await fixture.InvokeAsync(Request(Guid.NewGuid()));
        var packets = fixture.Transport.ReadLegacyPackets();
        var energyIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetEnergy);
        var endIndex = packets.FindIndex(packet => Opcode(packet) ==
            Opcodes.PetOwnerMergeEnded);
        var callOutIndex = packets.FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetOperationResult(
                PetId,
                PetOperationResultCode.CallOutSucceeded)));
        var presenceIndex = packets.FindIndex(packet =>
            packet.SequenceEqual(PacketBuilder.PetWorldPresence(
                PetId,
                0x1448)));
        Check.True(
            energyIndex >= 0 &&
            endIndex > energyIndex &&
            callOutIndex > endIndex &&
            presenceIndex > callOutIndex,
            "Merge end removes unite before restoring Call Out and companion presence");
        Check.Equal(
            1_800u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[energyIndex].AsSpan(4)),
            "manual Merge end retains and projects authoritative full energy");
        Check.True(
            packets.All(packet => Opcode(packet) != 10237),
            "Merge toggle-off never rebuilds the active-pet list");
        Check.True(
            packets.Any(packet => Opcode(packet) == 0x27B6),
            "Merge toggle-off refreshes normal character stats");
        Check.Equal(
            0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[endIndex].AsSpan(4)),
            "self Merge end uses the native local-player object ID");
    }

    private static DelegatingPetDurableCommandExecutor Executor(
        PetDurableReceiptStatus status,
        PetBootstrapSnapshot pet,
        bool duplicate = false) =>
        new()
        {
            ToggleOwnerMerge = envelope =>
            {
                var succeeded = status is
                    PetDurableReceiptStatus.OwnerMerged or
                    PetDurableReceiptStatus.OwnerUnmerged;
                var receipt = new PetDurableReceipt(
                    CommandFamily.PetOwnerMergeToggle,
                    status,
                    envelope.Subject.AccountId,
                    envelope.Subject.CharacterId,
                    KitBagSlot: -1,
                    EquipmentSlot: -1,
                    PetId: pet.PetId,
                    PetLevel: pet.Level,
                    PetExperience: pet.Experience,
                    PetRevision: pet.Revision,
                    IsCarried: pet.IsCarried,
                    IsSummoned: pet.IsSummoned,
                    PresenceOperation: 0,
                    AggregateRevision: 12,
                    AuditReference: "owner-merge-projection-check",
                    OutboxEventId: succeeded ? Guid.NewGuid() : null);
                return duplicate
                    ? PetDurableExecutionResult.Duplicate(receipt)
                    : succeeded
                        ? PetDurableExecutionResult.Committed(receipt)
                        : PetDurableExecutionResult.Rejected(receipt);
            }
        };

    private static GameCharacter CreateCharacter()
    {
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "test2",
            Profession = 1,
            KitBag = GameDefaults.EmptyKitBag,
            Equipment = GameDefaults.DefaultEquipment(1)
        };
    }

    private static GamePacket Request(Guid? operationId)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.PetOwnerMergeRequest);
        return new GamePacket(packet, operationId);
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
