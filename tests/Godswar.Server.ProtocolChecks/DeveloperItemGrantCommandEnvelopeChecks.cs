using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperItemGrantCommandEnvelopeChecks
{
    private static readonly Guid OperationId =
        Guid.Parse("426ecdf1-cce0-41e7-8a28-3c2de958cd29");

    public static void Run()
    {
        CheckOptionalOperationToken();
        CheckClearBagOperationToken();
        CheckStrictOperationTokenRejection();
        CheckCommandBounds();
        CheckCanonicalIdentity();
        CheckEnvelopeValidation();
        CheckBoundedExecutionResults();
    }

    private static void CheckOptionalOperationToken()
    {
        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/item add crystal1 17 op={OperationId:D}",
                out var explicitQuantity,
                out _) &&
            explicitQuantity is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4230,
                Quantity: 17,
                ClientOperationId: not null
            } &&
            explicitQuantity.ClientOperationId == OperationId,
            "developer grant accepts a final operation ID after quantity");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/gmitem add crystal1 OP={OperationId:D}",
                out var defaultQuantity,
                out _) &&
            defaultQuantity is
            {
                Quantity: 1,
                ClientOperationId: not null
            } &&
            defaultQuantity.ClientOperationId == OperationId,
            "developer grant accepts operation ID with default quantity");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/item add crystal1 2",
                out var legacy,
                out _) &&
            legacy is { Quantity: 2, ClientOperationId: null },
            "legacy developer grant remains compatible without a token");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/gmitem add crystal 2 9 op={OperationId:D}",
                out var splitAlias,
                out _) &&
            splitAlias is
            {
                Material.ItemId: 4231,
                Quantity: 9,
                ClientOperationId: not null
            },
            "split material aliases retain final operation-token parsing");
    }

    private static void CheckClearBagOperationToken()
    {
        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/item clearbag confirm",
                out var legacy,
                out _) &&
            legacy is
            {
                Operation: DeveloperItemOperation.ClearBag,
                ClientOperationId: null
            },
            "legacy clear-bag remains compatible without an operation ID");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/item clearbag confirm op={OperationId:D}",
                out var identified,
                out _) &&
            identified is
            {
                Operation: DeveloperItemOperation.ClearBag,
                Material: null,
                Quantity: 0,
                ClientOperationId: not null
            } &&
            identified.ClientOperationId == OperationId,
            "clear-bag accepts a final D-format operation ID");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/item clearbag",
                out var incomplete,
                out var usageError) &&
            incomplete is null &&
            usageError.Contains("[op=<UUID>]", StringComparison.Ordinal),
            "clear-bag usage advertises the optional operation token");
    }

    private static void CheckStrictOperationTokenRejection()
    {
        CheckRejected(
            "/item add crystal1 op=",
            "empty operation ID");
        CheckRejected(
            "/item add crystal1 op=00000000-0000-0000-0000-000000000000",
            "empty UUID value");
        CheckRejected(
            $"/item add crystal1 op={{{OperationId:D}}}",
            "non-canonical UUID form");
        CheckRejected(
            $"/item add crystal1 op={OperationId:D} 2",
            "operation token before quantity");
        CheckRejected(
            $"/item add crystal1 2 op={OperationId:D} trailing",
            "arguments after operation token");
        CheckRejected(
            "/item clearbag confirm op=",
            "empty clear-bag operation ID");
        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/item clearbag confirm op=",
                out var emptyClearBagOperation,
                out var emptyClearBagOperationError) &&
            emptyClearBagOperation is null &&
            emptyClearBagOperationError.Contains("D-format UUID", StringComparison.Ordinal),
            "clear-bag reports bounded operation-ID guidance");
        CheckRejected(
            "/item clearbag confirm op=00000000-0000-0000-0000-000000000000",
            "empty clear-bag UUID value");
        CheckRejected(
            $"/item clearbag confirm op={{{OperationId:D}}}",
            "non-canonical clear-bag UUID form");
        CheckRejected(
            $"/item clearbag confirm op={OperationId:D} trailing",
            "arguments after clear-bag operation token");
    }

    private static void CheckCommandBounds()
    {
        Check.True(
            DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                4230,
                DeveloperItemGrantCommandEnvelope.MaximumQuantity,
                OperationId,
                out _),
            "grant command accepts inclusive bounds");
        Check.True(
            !DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                0,
                1,
                OperationId,
                out _),
            "grant command rejects item zero");
        Check.True(
            !DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                4230,
                DeveloperItemGrantCommandEnvelope.MaximumQuantity + 1,
                OperationId,
                out _),
            "grant command rejects excessive quantity");
        Check.True(
            !DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                4230,
                1,
                Guid.Empty,
                out _),
            "grant command requires a non-empty client operation ID");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.DeveloperItemGrant),
            "developer grant identity is its client operation ID");
        Check.Equal(
            "developer_item_grant",
            CommandMetrics.FamilyCode(CommandFamily.DeveloperItemGrant),
            "developer grant metric family is bounded");
    }

    private static void CheckCanonicalIdentity()
    {
        var subject = new CommandSubject(347, 7);
        var original = CreateEnvelope(subject, 4230, 17, OperationId);
        var retry = DeveloperItemGrantCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow.AddMinutes(1),
            original.Command);
        Check.Equal(
            original.OperationId,
            retry.OperationId,
            "operation identity survives connection replacement");
        Check.Equal(
            original.RequestHash,
            retry.RequestHash,
            "canonical request survives connection replacement");

        var changedQuantity = CreateEnvelope(
            subject,
            4230,
            18,
            OperationId);
        Check.Equal(
            original.OperationId,
            changedQuantity.OperationId,
            "same UUID retains operation scope");
        Check.True(
            original.RequestHash != changedQuantity.RequestHash,
            "quantity participates in the canonical request hash");

        var changedItem = CreateEnvelope(
            subject,
            4231,
            17,
            OperationId);
        Check.Equal(
            original.OperationId,
            changedItem.OperationId,
            "item does not replace UUID operation scope");
        Check.True(
            original.RequestHash != changedItem.RequestHash,
            "item ID participates in the canonical request hash");

        var nextOperation = CreateEnvelope(
            subject,
            4230,
            17,
            Guid.Parse("7e1143a8-79c2-45ef-ac71-b09e94972c31"));
        Check.True(
            original.OperationId != nextOperation.OperationId,
            "new UUID creates a new operation");
        Check.Equal(
            original.RequestHash,
            nextOperation.RequestHash,
            "operation UUID is excluded from canonical request content");
    }

    private static void CheckEnvelopeValidation()
    {
        var envelope = CreateEnvelope(
            new CommandSubject(347, 7),
            4230,
            17,
            OperationId);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)DeveloperItemGrantCommandEnvelope.Validate(envelope),
            "well-formed grant envelope validates");

        var changedRequest = envelope with
        {
            Command = envelope.Command with { Quantity = 18 }
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.RequestHashConflict,
            (int)DeveloperItemGrantCommandEnvelope.Validate(changedRequest),
            "tampered grant quantity conflicts with request hash");

        var changedOperation = envelope with
        {
            Command = envelope.Command with
            {
                ClientOperationId =
                    Guid.Parse("b6522192-8315-41aa-a1ea-c9efe7729019")
            }
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.OperationIdentityConflict,
            (int)DeveloperItemGrantCommandEnvelope.Validate(changedOperation),
            "tampered operation UUID conflicts with operation identity");

        var invalid = envelope with
        {
            Command = envelope.Command with { ClientOperationId = Guid.Empty }
        };
        Check.Equal(
            (int)CommandEnvelopeValidation.InvalidCommand,
            (int)DeveloperItemGrantCommandEnvelope.Validate(invalid),
            "empty operation UUID is rejected before digest comparison");
    }

    private static void CheckBoundedExecutionResults()
    {
        var receipt = new DeveloperItemGrantExecutionReceipt(
            characterId: 7,
            itemId: 4230,
            grantedQuantity: 17,
            inventoryRevision: 3,
            auditReference: "command_audit:42",
            outboxEventId: Guid.NewGuid());
        Check.True(
            DeveloperItemGrantExecutionResult.Committed(receipt).IsSuccess,
            "committed grant carries a receipt");
        Check.True(
            DeveloperItemGrantExecutionResult.Duplicate(receipt).IsSuccess,
            "exact duplicate carries the canonical receipt");
        Check.True(
            !DeveloperItemGrantExecutionResult.RequestHashConflict().IsSuccess,
            "request-hash conflict cannot report success");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new DeveloperItemGrantExecutionReceipt(
                7,
                4230,
                17,
                0,
                "command_audit:42",
                Guid.NewGuid()),
            "receipt requires a positive inventory revision");
        Check.Throws<ArgumentException>(
            () => new DeveloperItemGrantExecutionResult(
                DeveloperItemGrantExecutionDisposition.Committed),
            "successful disposition cannot omit its receipt");
    }

    private static CommandEnvelope<DeveloperItemGrantCommand> CreateEnvelope(
        CommandSubject subject,
        uint itemId,
        int quantity,
        Guid operationId)
    {
        Check.True(
            DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                itemId,
                quantity,
                operationId,
                out var command),
            "test grant command is valid");
        return DeveloperItemGrantCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
    }

    private static void CheckRejected(string text, string description)
    {
        Check.True(
            TestDeveloperItemCommand.TryParse(
                text,
                out var request,
                out var error) &&
            request is null &&
            !string.IsNullOrWhiteSpace(error),
            $"developer item command rejects {description}");
    }
}
