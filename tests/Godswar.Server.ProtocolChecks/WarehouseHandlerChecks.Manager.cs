using System.Buffers.Binary;
using System.Collections.Immutable;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WarehouseHandlerChecks
{
    private static async Task CheckManagerFreshAndDuplicateAsync()
    {
        var policy = CreateManagerPolicy(revision: 8);
        var historicalPolicy = CreateManagerPolicy(revision: 7);
        await CheckManagerFreshAsync(policy);
        await CheckManagerDuplicateAsync(policy, historicalPolicy);
    }

    private static async Task CheckManagerFreshAsync(
        WarehouseExpansionPolicySnapshot policy)
    {
        var beforeBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            KitBagSlot,
            StorageKey.ToCompactString());
        var beforeCharacter = CharacterSnapshot(
            beforeBag,
            BeforeInventoryRevision,
            "warehouse-manager-before");
        var afterCharacter = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            AfterInventoryRevision,
            "warehouse-manager-after");
        var executor = new WarehouseExpansionExecutor
        {
            ExecuteResult = WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.Committed,
                ExpansionReceipt(policy))
        };
        await using var fixture = await CreateManagerFixtureAsync(
            beforeCharacter,
            [afterCharacter],
            [WarehouseSnapshot(
                BeforeInventoryRevision,
                containsKey: false)],
            executor,
            policy);
        fixture.Character.MaxHp = 77_700;
        fixture.Character.CurrentHp = 70_007;

        await InvokeAsync(fixture.Handler, CreateManagerRequest());

        var packets = fixture.ReadPackets();
        Check.True(
            packets.Count(packet => ReadOpcode(packet) == 0x2744) == 1 &&
            ReadOpcode(packets[0]) == 0x2744 &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[0].AsSpan(12)) == ushort.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[0].AsSpan(14)) == ushort.MaxValue,
            "fresh expansion emits exactly one consumed-key delete ACK");
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            bagIndex > 0 &&
            resultIndex > bagIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[resultIndex].AsSpan(12)) == 201,
            "fresh expansion refreshes bag before manager success 201");
        Check.True(
            executor.ReplayCount == 1 &&
            executor.ExecuteCount == 1 &&
            executor.Envelope?.Command is { } command &&
            command.ExpectedCapacity == 40 &&
            command.TargetCapacity == 80 &&
            command.ActionSubId == 100 &&
            fixture.Warehouses.ReadCount == 1 &&
            fixture.Characters.ReadCount == 1 &&
            fixture.Character.KitBag == GameDefaults.EmptyKitBag &&
            fixture.Character.MaxHp == 77_700 &&
            fixture.Character.CurrentHp == 70_007,
            "fresh expansion is replay-first and projects only the bag");
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily ==
                (ushort)CommandFamily.WarehouseExpansion &&
            secure.ResultCode == 201 &&
            secure.Disposition == SecureLegacyCommandDisposition.Applied &&
            secure.AuthoritativeRevision == 1,
            "fresh expansion settles family 59 after native projection");
    }

    private static async Task CheckManagerDuplicateAsync(
        WarehouseExpansionPolicySnapshot currentPolicy,
        WarehouseExpansionPolicySnapshot historicalPolicy)
    {
        var afterCharacter = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            AfterInventoryRevision,
            "warehouse-manager-duplicate");
        var executor = new WarehouseExpansionExecutor
        {
            ReplayResult = WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.Duplicate,
                ExpansionReceipt(historicalPolicy))
        };
        await using var fixture = await CreateManagerFixtureAsync(
            afterCharacter,
            [afterCharacter],
            [],
            executor,
            currentPolicy);

        await InvokeAsync(fixture.Handler, CreateManagerRequest());

        var packets = fixture.ReadPackets();
        Check.True(
            executor.ReplayCount == 1 &&
            executor.ExecuteCount == 0 &&
            fixture.Warehouses.ReadCount == 0 &&
            !packets.Any(packet => ReadOpcode(packet) == 0x2744),
            "historical duplicate bypasses current state and suppresses unsafe delete ACK");
        var bagIndex = FindOpcode(packets, 0x2731);
        var resultIndex = FindOpcode(
            packets,
            Opcodes.NpcFunctionActionResponse);
        Check.True(
            bagIndex >= 0 &&
            resultIndex > bagIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[resultIndex].AsSpan(12)) == 201,
            "duplicate expansion refreshes bag before replaying success 201");
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily ==
                (ushort)CommandFamily.WarehouseExpansion &&
            secure.ResultCode == 201 &&
            secure.Disposition == SecureLegacyCommandDisposition.Replayed &&
            secure.AuthoritativeRevision == 1,
            "historical expansion settles from its sealed durable receipt");
    }

    private static async Task<WarehouseManagerFixture>
        CreateManagerFixtureAsync(
            CharacterAccountSnapshot initialCharacter,
            IEnumerable<CharacterAccountSnapshot> characterReads,
            IEnumerable<WarehouseSnapshot> warehouseReads,
            WarehouseExpansionExecutor executor,
            WarehouseExpansionPolicySnapshot policy)
    {
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            initialCharacter) ?? throw new InvalidOperationException(
                "Warehouse Manager fixture did not hydrate.");
        var character = hydrated.Character;
        var npc = CreateManagerNpc(character);
        var route = new NpcDialogueRouteDefinition(
            npc.NpcKey,
            npc.NpcKey,
            WarehouseNpcProtocol.ManagerDialogIndex,
            NpcDialogueBehavior.WarehouseManager,
            ImmutableArray.Create(
                WarehouseNpcProtocol.ManagerActionSubId));
        var worldContent = PinnedWorldContentReader.Create(
            "warehouse-manager-handler-v1",
            [npc.MapId],
            [npc],
            [],
            [],
            new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Warehouse Manager",
                    "Warehouse expansion test dialogue")
            ],
            npcDialogueRoutes: [route]);
        var transport = new WarehouseCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            initialCharacter.AccountId,
            character);
        var characters = new WarehouseCharacterSnapshotReader(
            characterReads);
        var warehouses = new WarehouseSnapshotReader(warehouseReads);
        var handler = new GameClientHandler(
            session,
            new WarehouseGameStore(),
            registry,
            characters,
            worldContent,
            warehouseSnapshots: warehouses,
            warehouseExpansionCommands: executor,
            warehouseExpansionPolicy: policy);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "warehouse-manager-check"));
        SetHandlerField(handler, "_character", character);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Warehouse Manager visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Warehouse Manager visibility calculates");
        visibility.Commit(delta);

        return new WarehouseManagerFixture(
            session,
            transport,
            handler,
            executor,
            characters,
            warehouses,
            registry,
            character);
    }

    private static NpcSpawnDefinition CreateManagerNpc(
        GameCharacter character) => new(
        character.CurrentMap,
        "Athens",
        "Athens_134",
        "Athens_134_Female1",
        WarehouseNpcProtocol.AthensManagerNpcId,
        character.PositionX,
        character.PositionZ,
        WarehouseNpcProtocol.AthensManagerNpcId,
        AppearanceType: 1,
        Facing: 1.7f,
        Detail10077: [],
        Detail10080: []);

    private static GamePacket CreateManagerRequest()
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            WarehouseNpcProtocol.AthensManagerNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            WarehouseNpcProtocol.ManagerDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            WarehouseNpcProtocol.ManagerDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(16),
            WarehouseNpcProtocol.ManagerActionSubId);
        for (var index = 0; index < 18; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + index * sizeof(int)),
                unchecked((int)(0xA1060000u + (uint)index)));
        }
        return new GamePacket(bytes, OperationId);
    }

    private static WarehouseExpansionPolicySnapshot CreateManagerPolicy(
        long revision)
    {
        WarehouseExpansionPolicyLevel[] levels =
        [
            new(40, 0, 4102),
            new(80, 1, 4102),
            new(120, 2, 4102),
            new(160, 3, 4102)
        ];
        return new(
            revision,
            WarehouseExpansionPolicySnapshot.ComputeSha256(levels),
            levels);
    }

    private static WarehouseExpansionExecutionReceipt ExpansionReceipt(
        WarehouseExpansionPolicySnapshot policy) => new(
        CharacterId,
        RealmId: 1,
        WarehouseNpcProtocol.ManagerActionSubId,
        WarehouseExpansionResultStatus.Expanded,
        PreviousCapacity: 40,
        CurrentCapacity: 80,
        KeyItemId: 4102,
        RequiredKeyCount: 1,
        ConsumedKeyCount: 1,
        policy.Revision,
        policy.Sha256,
        WarehouseRevision: 1,
        AfterInventoryRevision,
        [new WarehouseItemMutation(
            ItemInstanceId: 7001,
            ItemId: 4102,
            WarehouseInventoryLocation.KitBag,
            BeforeSlot: KitBagSlot,
            BeforeStack: 1,
            AfterLocation: null,
            AfterSlot: null,
            AfterStack: null)],
        AuditReference: "warehouse-manager-handler-expand",
        OutboxEventId);

    private sealed class WarehouseExpansionExecutor :
        IWarehouseExpansionCommandExecutor
    {
        public WarehouseExpansionExecutionResult ReplayResult { get; init; } =
            WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.ReplayNotFound);

        public WarehouseExpansionExecutionResult? ExecuteResult { get; init; }

        public int ReplayCount { get; private set; }

        public int ExecuteCount { get; private set; }

        public CommandEnvelope<WarehouseExpansionCommand>? Envelope
        {
            get;
            private set;
        }

        public Task<WarehouseExpansionExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            WarehouseExpansionReplayIntent intent,
            WarehouseOperationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.True(
                subject == new CommandSubject(AccountId, CharacterId) &&
                intent.RealmId == 1 &&
                intent.ActionSubId == 100 &&
                identity.OperationId == OperationId,
                "warehouse expansion replay identity is server-bound");
            return Task.FromResult(ReplayResult);
        }

        public Task<WarehouseExpansionExecutionResult> ExecuteAsync(
            CommandEnvelope<WarehouseExpansionCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            Envelope = envelope;
            return Task.FromResult(
                ExecuteResult ?? throw new InvalidOperationException(
                    "Warehouse expansion execution was not configured."));
        }
    }

    private sealed record WarehouseManagerFixture(
        ClientSession Session,
        WarehouseCaptureTransport Transport,
        GameClientHandler Handler,
        WarehouseExpansionExecutor Executor,
        WarehouseCharacterSnapshotReader Characters,
        WarehouseSnapshotReader Warehouses,
        GameSessionRegistry Registry,
        GameCharacter Character) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadPackets() =>
            Transport.ReadLegacyPackets();

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
