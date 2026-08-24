using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static class WarehouseContractChecks
{
    public const string CheckName =
        "Warehouse capacity, dialogue, and durable command contracts";

    public static Task RunAsync()
    {
        CheckCapacityAndPolicy();
        CheckExpansionEnvelope();
        CheckTransferEnvelopeAndBound();
        CheckNpcCapabilities();
        CheckAccessLeaseBounds();
        CheckHistoricalExpansionReplay();
        return Task.CompletedTask;
    }

    private static void CheckCapacityAndPolicy()
    {
        Check.Equal(40, WarehouseCapacityPolicy.DefaultCapacity,
            "a new character owns exactly one 40-cell warehouse box");
        Check.Equal(360, WarehouseCapacityPolicy.MaximumSupportedCapacity,
            "the audited client projection supports nine warehouse boxes");
        Check.True(
            Enumerable.Range(1, 9)
                .Select(static box => box * 40)
                .All(WarehouseCapacityPolicy.IsValidCapacity) &&
            !WarehouseCapacityPolicy.IsValidCapacity(41) &&
            !WarehouseCapacityPolicy.IsValidCapacity(400),
            "only complete projected box capacities are valid");
        Check.True(
            WarehouseCapacityPolicy.StateSubId(40, 160, 1) == 100101 &&
            WarehouseCapacityPolicy.StateSubId(120, 160, 3) == 100303 &&
            WarehouseCapacityPolicy.StateSubId(160, 160, 0) == 998 &&
            WarehouseCapacityPolicy.SuccessSubId(360) == 208 &&
            WarehouseCapacityPolicy.InsufficientKeysSubId(160, 3) ==
                900403,
            "manager messages cap Storage Box Keys at SB4 while retaining the nine-box wire bound");

        var policy = CreatePolicy(revision: 7);
        policy.Validate();
        Check.True(
            policy.Levels.Select(static level => level.KeyCost)
                .SequenceEqual([0, 1, 2, 3]) &&
            policy.Levels.All(static level => level.KeyItemId == 4102),
            "the current database baseline exposes key upgrades only through SB4");
        Check.True(
            GameClientHandler.ResolveWarehouseManagerStateSubId(
                120,
                policy) == 100303 &&
            GameClientHandler.ResolveWarehouseManagerStateSubId(
                160,
                policy) == 998 &&
            GameClientHandler.ResolveWarehouseManagerStateSubId(
                200,
                policy) == 998,
            "the manager offers the last key upgrade at SB3 and shows maximum for SB4 or Battle Pass capacities");

        var databaseOwnedLevels = policy.Levels.ToArray();
        databaseOwnedLevels[3] = databaseOwnedLevels[3] with
        {
            KeyCost = 17,
            KeyItemId = 4999
        };
        new WarehouseExpansionPolicySnapshot(
            8,
            WarehouseExpansionPolicySnapshot.ComputeSha256(
                databaseOwnedLevels),
            databaseOwnedLevels).Validate();

        var incompleteLevels = policy.Levels
            .Where(static level => level.Capacity != 120)
            .ToArray();
        var incompletePolicy = new WarehouseExpansionPolicySnapshot(
            9,
            WarehouseExpansionPolicySnapshot.ComputeSha256(
                incompleteLevels),
            incompleteLevels);
        Check.Throws<InvalidDataException>(
            incompletePolicy.Validate,
            "a database policy cannot omit an intermediate box");
    }

    private static void CheckExpansionEnvelope()
    {
        var identity = WarehouseOperationIdentity.SecureClient(Guid.NewGuid());
        var policy = CreatePolicy(9);
        Check.True(
            WarehouseExpansionCommandEnvelope.TryCreateCommand(
                identity,
                realmId: 1,
                npcId: checked((int)WarehouseNpcProtocol.AthensManagerNpcId),
                WarehouseNpcProtocol.ManagerDialogIndex,
                WarehouseNpcProtocol.ManagerActionSubId,
                currentCapacity: 40,
                policy,
                out var first) &&
            first.TargetCapacity == 80,
            "action 100 derives the next capacity from authoritative state");
        Check.True(
            WarehouseExpansionCommandEnvelope.TryCreateCommand(
                identity,
                realmId: 1,
                npcId: checked((int)WarehouseNpcProtocol.SpartaManagerNpcId),
                WarehouseNpcProtocol.ManagerDialogIndex,
                WarehouseNpcProtocol.ManagerActionSubId,
                currentCapacity: 160,
                policy,
                out var maximum) &&
            maximum.TargetCapacity == 160 &&
            !WarehouseExpansionCommandEnvelope.TryCreateCommand(
                identity,
                realmId: 1,
                npcId: checked((int)WarehouseNpcProtocol.AthensManagerNpcId),
                WarehouseNpcProtocol.ManagerDialogIndex,
                WarehouseNpcProtocol.ManagerActionSubId,
                currentCapacity: 200,
                policy,
                out _),
            "SB4 maximum attempts remain replayable while Battle Pass capacities cannot create key commands");

        var subject = new CommandSubject(11, 22);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var firstEnvelope = WarehouseExpansionCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            first);
        var secondEnvelope = WarehouseExpansionCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            first with
            {
                NpcId = checked((int)WarehouseNpcProtocol.SpartaManagerNpcId)
            });
        Check.Equal(
            firstEnvelope.RequestHash,
            secondEnvelope.RequestHash,
            "manager endpoint is normalized for retry after city transfer");
    }

    private static void CheckTransferEnvelopeAndBound()
    {
        var identity = WarehouseOperationIdentity.SecureClient(Guid.NewGuid());
        Check.True(
            WarehouseTransferCommandEnvelope.TryCreateCommand(
                identity,
                realmId: 1,
                WarehouseTransferOperation.Deposit,
                WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                kitBagSlot: 0,
                destinationWarehouseSlot: -1,
                money: 0,
                WarehouseStorageType.Normal,
                expectedWarehouseRevision: 0,
                expectedInventoryRevision: 3,
                "[4102,,,,,,1,1,0,3]",
                "[]",
                out _),
            "normal item deposit permits stock auto destination -1");
        Check.True(
            !WarehouseTransferCommandEnvelope.TryCreateCommand(
                identity,
                1,
                WarehouseTransferOperation.Deposit,
                0,
                0,
                -1,
                money: 1,
                WarehouseStorageType.Normal,
                0,
                3,
                "[4102,,,,,,1,1,0,3]",
                "[]",
                out _) &&
            !WarehouseTransferCommandEnvelope.TryCreateCommand(
                identity,
                1,
                WarehouseTransferOperation.Deposit,
                0,
                0,
                -1,
                money: 0,
                WarehouseStorageType.Award,
                0,
                3,
                "[4102,,,,,,1,1,0,3]",
                "[]",
                out _),
            "money and award-storage mutation paths fail closed");

        var mutations = Enumerable.Range(0, 101)
            .Select(index => new WarehouseItemMutation(
                index + 1,
                4102,
                index == 0
                    ? WarehouseInventoryLocation.KitBag
                    : WarehouseInventoryLocation.Warehouse,
                index == 0 ? 0 : index - 1,
                1,
                AfterLocation: null,
                AfterSlot: null,
                AfterStack: null))
            .ToArray();
        var fanoutReceipt = new WarehouseTransferExecutionReceipt(
            CharacterId: 22,
            WarehouseTransferOperation.Deposit,
            WarehouseSlot: -1,
            KitBagSlot: 0,
            DestinationWarehouseSlot: -1,
            ActualWarehouseSlot: 0,
            ActualKitBagSlot: 0,
            WarehouseTransferResultStatus.Stacked,
            MovedQuantity: 101,
            Capacity: 360,
            WarehouseRevision: 0,
            InventoryRevision: 4,
            Mutations: mutations,
            AuditReference: "warehouse:test:fanout",
            OutboxEventId: Guid.NewGuid());
        fanoutReceipt.Validate();
    }

    private static void CheckNpcCapabilities()
    {
        var routes = NpcDialogueBaselineV8.CreateRoutes();
        foreach (var (key, id) in new[]
        {
            ("Athens_134", WarehouseNpcProtocol.AthensManagerNpcId),
            ("Sparta_134", WarehouseNpcProtocol.SpartaManagerNpcId)
        })
        {
            var route = routes.Single(value =>
                string.Equals(value.NpcKey, key, StringComparison.Ordinal));
            Check.True(
                route.Behavior == NpcDialogueBehavior.WarehouseManager &&
                route.DialogIndex == WarehouseNpcProtocol.ManagerDialogIndex &&
                route.InitialMenuSubIds.SequenceEqual([100]) &&
                NpcDialogueBehaviorRegistry.IsAllowed(
                    CreateNpc(key, id),
                    route),
                $"{key} has only the reviewed Warehouse Manager capability");
        }

        Check.True(
            WarehouseNpcProtocol.IsWarehouseEndpoint("Athens_025", 5164) &&
            WarehouseNpcProtocol.IsWarehouseEndpoint("Sparta_023", 47750) &&
            !WarehouseNpcProtocol.IsWarehouseEndpoint("Sparta_023", 5022) &&
            WarehouseNpcProtocol.IsManagerEndpoint("Sparta_134", 5131) &&
            !WarehouseNpcProtocol.IsManagerEndpoint("Sparta_134", 5133),
            "runtime routes accept published interaction IDs, not source provenance IDs");
    }

    private static void CheckAccessLeaseBounds()
    {
        var now = DateTimeOffset.UtcNow;
        var context = new WarehouseAccessContext(
            1, 2, 3, 4, 5164, now + WarehouseAccessContext.Lifetime);
        Check.True(
            WarehouseAccessContext.Lifetime == TimeSpan.FromMinutes(15) &&
            context.Matches(1, 2, 3, 4, now) &&
            !context.Matches(1, 2, 3, 5, now) &&
            !context.Matches(1, 2, 3, 4, context.ExpiresAt),
            "warehouse access is owner/map-bound and expires fail closed");
    }

    private static void CheckHistoricalExpansionReplay()
    {
        var historicalPolicy = CreatePolicy(7);
        var currentPolicy = CreatePolicy(8);
        var receipt = new WarehouseExpansionExecutionReceipt(
            CharacterId: 22,
            RealmId: 1,
            ActionSubId: WarehouseNpcProtocol.ManagerActionSubId,
            WarehouseExpansionResultStatus.Expanded,
            PreviousCapacity: 40,
            CurrentCapacity: 80,
            KeyItemId: 4102,
            RequiredKeyCount: 1,
            ConsumedKeyCount: 1,
            PolicyRevision: historicalPolicy.Revision,
            PolicySha256: historicalPolicy.Sha256,
            WarehouseRevision: 1,
            InventoryRevision: 2,
            KeyMutations:
            [
                new WarehouseItemMutation(
                    31,
                    4102,
                    WarehouseInventoryLocation.KitBag,
                    4,
                    1,
                    null,
                    null,
                    null)
            ],
            AuditReference: "warehouse:test:historical-replay",
            OutboxEventId: Guid.NewGuid());
        receipt.Validate();

        GameClientHandler.ValidateWarehouseExpansionReceipt(
            22,
            1,
            receipt,
            WarehouseExpansionExecutionDisposition.Duplicate,
            currentPolicy);
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ValidateWarehouseExpansionReceipt(
                22,
                1,
                receipt,
                WarehouseExpansionExecutionDisposition.Committed,
                currentPolicy),
            "a fresh commit must match the currently pinned policy");
        Check.True(
            GameClientHandler
                .ShouldEmitWarehouseExpansionDeleteAcknowledgement(
                    WarehouseExpansionExecutionDisposition.Committed) &&
            !GameClientHandler
                .ShouldEmitWarehouseExpansionDeleteAcknowledgement(
                    WarehouseExpansionExecutionDisposition.Duplicate),
            "duplicate expansion suppresses the unsafe empty-slot delete ACK");
    }

    private static WarehouseExpansionPolicySnapshot CreatePolicy(
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

    private static NpcSpawnDefinition CreateNpc(string key, uint id) =>
        new(
            MapId: 1,
            SceneKey: key.Split('_')[0],
            NpcKey: key,
            TemplateKey: key,
            ObjectId: id,
            X: 1,
            Z: 1,
            InteractionId: id,
            AppearanceType: 1,
            Facing: 0,
            Detail10077: [1],
            Detail10080: [1]);
}
