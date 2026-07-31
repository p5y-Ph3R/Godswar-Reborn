using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private const long DecomposeInventoryRevision = 73;
    private static readonly MethodInfo
        HandleDurableDecomposeMethod =
        FindHandlerMethod("HandleDurableGearMentorDecomposeAsync");

    private static async Task
        CheckUnavailableDecomposeExecutorLeavesOperationPendingAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "Provider-unavailable Decompose character did not hydrate.");
        var transport = new ReplayCaptureTransport();
        await using var session = new ClientSession(transport);
        var handler = new GameClientHandler(
            session,
            new ReplayGameStore(),
            new GameSessionRegistry(),
            new ReplaySnapshotReader(snapshot),
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                snapshot.AccountId,
                "decompose-provider-check"));
        SetField(handler, "_character", hydrated.Character);

        var invocation = HandleDurableDecomposeMethod.Invoke(
            handler,
            [
                (uint)GearMentorDecomposeGearCommandEnvelope
                    .SpartaGearMentorNpcId,
                ReplayOperationId,
                null,
                hydrated.Character.KitBag,
                "none",
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Durable Decompose handler did not return a task.");
        await invocation;

        Check.Equal(
            0,
            transport.ReadClearLegacyPackets().Count,
            "Decompose provider outage emits no stock response");
        Check.Equal(
            0,
            transport.CommandResults.Count,
            "Decompose provider outage emits no terminal 0x0102");
        Check.Equal(
            0,
            transport.Events.Count,
            "Decompose provider outage leaves the UUID pending");
    }

    private static async Task
        CheckDecomposeReplayWinsBeforeRouteRejectionAsync()
    {
        await using var fixture = CreateDecomposeFixture(
            GearMentorDecomposeGearExecutionResult.Duplicate(
                CreateSuccessfulDecomposeReceipt()));

        await InvokePacketAsync(
            fixture.Handler,
            CreateFunctionActionPacket(
                UnroutedNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                ReplayOperationId));

        Check.Equal(
            1,
            fixture.Executor.ReplayCount,
            "unrouted family-9 retry checks the Decompose inbox once");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "unrouted family-9 retry never executes a new mutation");
        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            "durable Decompose replay reloads one authoritative snapshot");
        Check.Equal(
            fixture.PersistedKitBag,
            fixture.LiveCharacter.KitBag,
            "durable Decompose replay installs the authoritative bag");

        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Count >= 2,
            "Decompose replay sends stock result then bag refresh");
        AssertNpcResult(
            packets[0],
            UnroutedNpcId,
            GearMentorDecomposeGearNativeResults.SucceededSubId,
            "durable Decompose replay stock response");
        Check.True(
            packets.Skip(1).Any(
                packet => ReadOpcode(packet) == 0x2731),
            "Decompose replay refreshes the bag after its stock response");

        var secureResult =
            fixture.Transport.CommandResults.Single();
        AssertSecureResult(
            secureResult,
            SecureLegacyCommandDisposition.Replayed,
            CommandFamily.GearMentorDecomposeGear,
            GearMentorDecomposeGearNativeResults.SucceededSubId,
            DecomposeInventoryRevision,
            ReplayOperationId,
            "durable Decompose replay");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            "Decompose Replayed 0x0102 follows every bag packet");
        Check.Equal(
            packets.Count,
            fixture.Transport.Events.Count(
                static value => value == "legacy"),
            "all Decompose stock and bag packets precede 0x0102");
    }

    private static DecomposeReplayFixture CreateDecomposeFixture(
        GearMentorDecomposeGearExecutionResult replay)
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "Decompose replay fixture character did not hydrate.");
        var liveCharacter = hydrated.Character;
        var persistedKitBag = liveCharacter.KitBag;
        liveCharacter.KitBag = GameDefaults.EmptyKitBag;

        var transport = new ReplayCaptureTransport();
        var session = new ClientSession(transport);
        var snapshotReader = new ReplaySnapshotReader(snapshot);
        var executor = new DecomposeReplayExecutor(
            snapshot.AccountId,
            liveCharacter.Id,
            ReplayOperationId,
            replay);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            snapshot.AccountId,
            liveCharacter);
        var handler = new GameClientHandler(
            session,
            new ReplayGameStore(),
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            gearMentorDecomposeGearCommands: executor);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                snapshot.AccountId,
                "durable-decompose-replay-check"));
        SetField(handler, "_character", liveCharacter);

        return new DecomposeReplayFixture(
            session,
            transport,
            handler,
            executor,
            snapshotReader,
            liveCharacter,
            persistedKitBag);
    }

    private static GearMentorDecomposeGearExecutionReceipt
        CreateSuccessfulDecomposeReceipt() =>
        new(
            characterId: 19,
            GearMentorDecomposeGearResultStatus.Succeeded,
            GearMentorDecomposeGearNativeResults.SucceededSubId,
            selections:
            [
                new GearMentorDecomposeReceiptSelection(
                    SelectedKitBagSlot: 0,
                    SourceItemId: 10_001)
            ],
            dustOutcomes:
            [
                new GearMentorDecomposeDustOutcome(
                    SelectedKitBagSlot: 0,
                    DustItemId: 9_900,
                    Quantity: 7,
                    Bound: 1)
            ],
            inventoryRevision: DecomposeInventoryRevision,
            auditReference: "audit:decompose:replay-check",
            outboxEventId:
                Guid.Parse("d8678061-7d77-48a7-9198-ef67b142ac84"));

    private sealed record DecomposeReplayFixture(
        ClientSession Session,
        ReplayCaptureTransport Transport,
        GameClientHandler Handler,
        DecomposeReplayExecutor Executor,
        ReplaySnapshotReader SnapshotReader,
        GameCharacter LiveCharacter,
        string PersistedKitBag) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            Session.DisposeAsync();
    }

    private sealed class DecomposeReplayExecutor(
        int expectedAccountId,
        int expectedCharacterId,
        Guid expectedOperationId,
        GearMentorDecomposeGearExecutionResult replay) :
        IGearMentorDecomposeGearCommandExecutor
    {
        public int ExecuteCount { get; private set; }

        public int ReplayCount { get; private set; }

        public Task<GearMentorDecomposeGearExecutionResult> ExecuteAsync(
            CommandEnvelope<GearMentorDecomposeGearCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            throw new InvalidOperationException(
                "Pre-route Decompose replay cannot execute a mutation.");
        }

        public Task<GearMentorDecomposeGearExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(
                expectedAccountId,
                subject.AccountId,
                "Decompose replay subject account");
            Check.Equal(
                expectedCharacterId,
                subject.CharacterId,
                "Decompose replay subject character");
            Check.Equal(
                expectedOperationId,
                clientOperationId,
                "Decompose replay operation identity");
            return Task.FromResult(replay);
        }
    }
}
