using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private static readonly MethodInfo InstallBoundaryNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");

    private static void CheckSecureFunctionActionLengthBoundary()
    {
        var exact = CreateFunctionActionPacket(
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            GearEnhancerProtocol.TransformCrystalSubId,
            ReplayOperationId);
        Check.True(
            GearEnhancerProtocol.IsExactFunctionActionPacket(exact),
            "92-byte Gear Mentor function action is canonical");

        foreach (var packetBytes in new[] { 88, 96 })
        {
            var resized = ResizeFunctionActionPacket(
                exact,
                packetBytes,
                ReplayOperationId);
            Check.True(
                !GearEnhancerProtocol.IsExactFunctionActionPacket(
                    resized),
                $"{packetBytes}-byte Gear Mentor action is noncanonical");
        }

        var mismatchedLength = exact.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            mismatchedLength,
            GearEnhancerProtocol.FunctionActionPacketBytes - 1);
        Check.True(
            !GearEnhancerProtocol.IsExactFunctionActionPacket(
                new GamePacket(
                    mismatchedLength,
                    ReplayOperationId)),
            "declared Gear Mentor length must match the 92-byte buffer");
    }

    private static async Task
        CheckNonCanonicalSecureFunctionActionRejectedAsync()
    {
        await using var fixture = CreateFixture(
            GearMentorMaterialConversionExecutionResult
                .ReplayNotFound());
        var exact = CreateFunctionActionPacket(
            UnroutedNpcId,
            GearEnhancerProtocol.TransformCrystalSubId,
            ReplayOperationId);

        await InvokePacketAsync(
            fixture.Handler,
            ResizeFunctionActionPacket(
                exact,
                packetBytes: 96,
                operationId: ReplayOperationId));

        Check.Equal(
            1,
            fixture.Executor.TransformReplayCount,
            "noncanonical secure action checks durable replay once");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "noncanonical secure action never executes");
        AssertNpcResult(
            fixture.Transport.ReadClearLegacyPackets().Single(),
            UnroutedNpcId,
            GearMentorMaterialConversionNativeResults
                .TransformInvalidCrystalSubId,
            "noncanonical secure action");
        AssertSecureResult(
            fixture.Transport.CommandResults.Single(),
            SecureLegacyCommandDisposition.Rejected,
            CommandFamily.GearMentorTransformCrystal,
            GearMentorMaterialConversionNativeResults
                .TransformInvalidCrystalSubId,
            inventoryRevision: 0,
            ReplayOperationId,
            "noncanonical secure action");
    }

    private static async Task
        CheckNonCanonicalDurableReplayStillWinsAsync()
    {
        var receipt = CreateSuccessfulTransformReceipt();
        await using var fixture = CreateFixture(
            GearMentorMaterialConversionExecutionResult
                .Duplicate(receipt));
        var exact = CreateFunctionActionPacket(
            UnroutedNpcId,
            GearEnhancerProtocol.TransformCrystalSubId,
            ReplayOperationId);

        await InvokePacketAsync(
            fixture.Handler,
            ResizeFunctionActionPacket(
                exact,
                packetBytes: 88,
                operationId: ReplayOperationId));

        Check.Equal(
            1,
            fixture.Executor.TransformReplayCount,
            "noncanonical retry still resolves durable replay");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "noncanonical replay never executes");
        AssertSecureResult(
            fixture.Transport.CommandResults.Single(),
            SecureLegacyCommandDisposition.Replayed,
            CommandFamily.GearMentorTransformCrystal,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            ReplayOperationId,
            "noncanonical durable replay");
    }

    private static async Task
        CheckNonCanonicalRoutedGearMentorNeverExecutesAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "routed packet-boundary fixture did not hydrate");
        var character = hydrated.Character;
        character.KitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            (CompactItemEntry.Empty with
            {
                Id = 4_231,
                Quality = 1,
                Grade = 1,
                Stack = 1
            }).ToCompactString());
        var npc = new NpcSpawnDefinition(
            character.CurrentMap,
            "Sparta",
            "Sparta_070",
            "Sparta_070_Male1",
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            character.PositionX,
            character.PositionZ,
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            AppearanceType: 1,
            Facing: 0,
            Detail10077: [],
            Detail10080: []);
        var worldContent = PinnedWorldContentReader.Create(
            "gear-mentor-packet-boundary-v1",
            [npc.MapId],
            [npc],
            [],
            [],
            new DateTimeOffset(
                2026,
                7,
                30,
                0,
                0,
                0,
                TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Gear Mentor",
                    "Test dialogue")
            ],
            npcDialogueRoutes:
            [
                new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    GearEnhancerProtocol.DialogIndex,
                    NpcDialogueBehavior.GearMentor,
                    ImmutableArray.Create(
                        1,
                        2,
                        3,
                        4,
                        5,
                        6,
                        7,
                        8,
                        9))
            ]);
        var registry = new GameSessionRegistry();
        var transport = new ReplayCaptureTransport();
        var session = new ClientSession(transport);
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            snapshot.AccountId,
            character);
        var executor = new ReplayExecutor(
            snapshot.AccountId,
            character.Id,
            ReplayOperationId,
            GearMentorMaterialConversionExecutionResult
                .ReplayNotFound());
        var handler = new GameClientHandler(
            session,
            new ReplayGameStore(),
            registry,
            new ReplaySnapshotReader(snapshot),
            worldContent,
            gearMentorMaterialConversionCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = snapshot.AccountId,
                Username = "routed-packet-boundary-check"
            });
        SetField(handler, "_character", character);

        try
        {
            var catalog =
                await registry.PublishMapNpcDefinitionsAsync(
                    character.CurrentMap,
                    [npc],
                    originSession: null,
                    CancellationToken.None);
            InstallBoundaryNpcCatalogMethod.Invoke(
                handler,
                [catalog]);
            var tracker =
                GetBoundaryField<WorldSectorVisibilityTracker<
                    NpcSpawnDefinition>>(
                    handler,
                    "_npcVisibility")
                ?? throw new InvalidOperationException(
                    "routed Gear Mentor visibility was not installed");
            Check.True(
                tracker.TryCalculate(
                    character.PositionX,
                    character.PositionZ,
                    out var delta),
                "routed Gear Mentor visibility calculates");
            tracker.Commit(delta);

            var context = new GearEnhancerSelectionContext(
                snapshot.AccountId,
                character.Id,
                npc.InteractionId,
                GearEnhancerProtocol.DialogIndex,
                operation: null,
                DateTimeOffset.UtcNow.AddMinutes(1));
            context.Apply(
                new GearEnhancerItemSelectionPacket(
                    BagPage: 0,
                    PageSlot: 0,
                    Selected: true),
                character.KitBag);
            SetField(
                handler,
                "_gearEnhancerSelectionContext",
                context);

            var exact = CreateFunctionActionPacket(
                npc.InteractionId,
                GearEnhancerProtocol.TransformCrystalSubId,
                ReplayOperationId);
            await InvokePacketAsync(
                handler,
                ResizeFunctionActionPacket(
                    exact,
                    packetBytes: 96,
                    operationId: ReplayOperationId));

            Check.Equal(
                1,
                executor.TransformReplayCount,
                "routed noncanonical action checks replay once");
            Check.Equal(
                0,
                executor.ExecuteCount,
                "routed noncanonical action cannot execute");
            AssertSecureResult(
                transport.CommandResults.Single(),
                SecureLegacyCommandDisposition.Rejected,
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionNativeResults
                    .TransformInvalidCrystalSubId,
                inventoryRevision: 0,
                ReplayOperationId,
                "routed noncanonical action");
        }
        finally
        {
            registry.Remove(session);
            await session.DisposeAsync();
        }
    }

    private static T? GetBoundaryField<T>(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return (T?)field.GetValue(handler);
    }

    private static GamePacket ResizeFunctionActionPacket(
        GamePacket source,
        int packetBytes,
        Guid? operationId)
    {
        var resized = new byte[packetBytes];
        source.Buffer
            .AsSpan(0, Math.Min(source.Buffer.Length, resized.Length))
            .CopyTo(resized);
        BinaryPrimitives.WriteUInt16LittleEndian(
            resized,
            checked((ushort)resized.Length));
        return new GamePacket(resized, operationId);
    }
}
