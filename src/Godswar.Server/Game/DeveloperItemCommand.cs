using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal enum DeveloperItemOperation
{
    Add,
    ClearBag,
    MountAdd,
    MountList
}

internal sealed record DeveloperItemRequest(
    DeveloperItemOperation Operation,
    DeveloperGrantMaterialDefinition? Material,
    int Quantity,
    DeveloperMountDefinition? Mount = null,
    DeveloperMountListRequest? MountList = null,
    Guid? ClientOperationId = null);

internal sealed record DeveloperMountListRequest(
    int? Page,
    DeveloperMountFamilyDefinition? Family);

internal static partial class DeveloperItemCommand
{
    // The stock client masks the word "gmitem" before sending map chat, so
    // /gmitem reaches the server as /******. Accept that exact wire spelling
    // as well as /item, while retaining /gmitem for protocol tooling.
    public const string Prefix = "/item";
    public const string LegacyPrefix = "/gmitem";
    public const string MaskedLegacyPrefix = "/******";
    public const string RubyPrefix = "/ruby";
    public const int MaximumQuantity =
        DeveloperItemGrantCommandEnvelope.MaximumQuantity;

    private static readonly string[] Prefixes =
        [Prefix, LegacyPrefix, MaskedLegacyPrefix, RubyPrefix];

    public static bool TryParse(
        string text,
        out DeveloperItemRequest? request,
        out string error,
        DeveloperMountCatalog? mounts = null,
        IDeveloperItemGrantCatalog? items = null)
    {
        request = null;
        error = string.Empty;
        var commandOffset = FindCommandOffset(text, out var matchedPrefix);
        if (commandOffset < 0)
        {
            return false;
        }

        var tokens = text[commandOffset..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2 ||
            !tokens[0].Equals(matchedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = Usage;
            return true;
        }

        if (matchedPrefix.Equals(
                RubyPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRuby(
                tokens,
                items,
                out request,
                out error);
        }

        if (tokens[1].Equals("mount", StringComparison.OrdinalIgnoreCase))
        {
            if (mounts is null)
            {
                error = "Mount commands require the published item catalog.";
                return true;
            }

            return TryParseMount(
                tokens,
                mounts,
                out request,
                out error);
        }

        if (tokens[1].Equals("clearbag", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length is < 3 or > 4 ||
                !tokens[2].Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                error = ClearBagUsage;
                return true;
            }

            Guid? clearBagOperationId = null;
            if (tokens.Length == 4 &&
                !TryParseOperationId(
                    tokens[3],
                    out clearBagOperationId,
                    out var clearBagOperationTokenRecognized))
            {
                error = clearBagOperationTokenRecognized
                    ? OperationIdError
                    : ClearBagUsage;
                return true;
            }

            request = new DeveloperItemRequest(
                DeveloperItemOperation.ClearBag,
                Material: null,
                Quantity: 0,
                ClientOperationId: clearBagOperationId);
            return true;
        }

        if (tokens.Length < 3 ||
            !tokens[1].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            error = Usage;
            return true;
        }

        DeveloperGrantMaterialDefinition material;
        if (items is null)
        {
            error = "Item commands require the published developer-item catalog.";
            return true;
        }
        var quantityOffset = 3;
        if (uint.TryParse(tokens[2], out var itemId))
        {
            if (!items.TryResolveDeveloper(itemId, out material))
            {
                error = $"Item ID {itemId} is not an allowlisted developer item.";
                return true;
            }
        }
        else if (items.TryResolveDeveloper(tokens[2], out material))
        {
        }
        else if (tokens.Length >= 4 &&
                 int.TryParse(tokens[3], out var level) &&
                 items.TryResolveDeveloper($"{tokens[2]}{level}", out material))
        {
            quantityOffset = 4;
        }
        else
        {
            error = $"Unknown or unavailable developer-item alias '{tokens[2]}'.";
            return true;
        }

        if (tokens.Length > quantityOffset + 2)
        {
            error = "Too many command arguments.";
            return true;
        }

        var quantity = 1;
        Guid? clientOperationId = null;
        var remainingTokens = tokens.Length - quantityOffset;
        if (remainingTokens > 0 &&
            !TryParseOperationId(
                tokens[^1],
                out clientOperationId,
                out var operationTokenRecognized))
        {
            if (operationTokenRecognized)
            {
                error = OperationIdError;
                return true;
            }
        }

        var hasOperationId = clientOperationId.HasValue;
        var quantityTokenCount = remainingTokens - (hasOperationId ? 1 : 0);
        if (quantityTokenCount > 1)
        {
            error = "Too many command arguments.";
            return true;
        }

        if (quantityTokenCount == 1 &&
            (!int.TryParse(tokens[quantityOffset], out quantity) ||
             quantity is < 1 or > MaximumQuantity))
        {
            error = $"Quantity must be from 1 to {MaximumQuantity}.";
            return true;
        }

        request = new DeveloperItemRequest(
            DeveloperItemOperation.Add,
            material,
            quantity,
            ClientOperationId: clientOperationId);
        return true;
    }

    private const string Usage =
        "Usage: /item add <item-id|material-alias> [quantity] [op=<UUID>], " +
        "/ruby <level 1-3> [quantity] [op=<UUID>], " +
        "/item mount list [page|family], " +
        "/item mount add <item-id> [op=<UUID>], " +
        "/item mount add <family> <tier|max|special> [op=<UUID>], " +
        "or /item clearbag confirm [op=<UUID>].";

    private const string ClearBagUsage =
        "Bag clearing is destructive. Use: /item clearbag confirm [op=<UUID>].";

    private const string MountUsage =
        "Usage: /item mount list [page|family], " +
        "/item mount add <item-id> [op=<UUID>], or " +
        "/item mount add <family> <tier|max|special> [op=<UUID>].";

    private const string MountAddUsage =
        "Usage: /item mount add <item-id> [op=<UUID>] or " +
        "/item mount add <family> <tier|max|special> [op=<UUID>].";

    private const string OperationIdError =
        "Operation ID must use op=<UUID> with a non-empty D-format UUID.";

    private static bool TryParseMount(
        string[] tokens,
        DeveloperMountCatalog mounts,
        out DeveloperItemRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        if (tokens.Length < 3)
        {
            error = MountUsage;
            return true;
        }

        if (tokens[2].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length > 4)
            {
                error = "Mount list accepts at most one page number or family alias.";
                return true;
            }

            if (tokens.Length == 3)
            {
                request = new DeveloperItemRequest(
                    DeveloperItemOperation.MountList,
                    Material: null,
                    Quantity: 0,
                    MountList: new DeveloperMountListRequest(Page: 1, Family: null));
                return true;
            }

            if (int.TryParse(tokens[3], out var page))
            {
                if (page is < 1 || page > mounts.PageCount)
                {
                    error = $"Mount-list page must be from 1 to {mounts.PageCount}.";
                    return true;
                }

                request = new DeveloperItemRequest(
                    DeveloperItemOperation.MountList,
                    Material: null,
                    Quantity: 0,
                    MountList: new DeveloperMountListRequest(page, Family: null));
                return true;
            }

            if (!mounts.TryGetFamily(tokens[3], out var family))
            {
                error = $"Unknown mount family alias '{tokens[3]}'.";
                return true;
            }

            request = new DeveloperItemRequest(
                DeveloperItemOperation.MountList,
                Material: null,
                Quantity: 0,
                MountList: new DeveloperMountListRequest(Page: null, family));
            return true;
        }

        if (!tokens[2].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            error = MountUsage;
            return true;
        }

        var commandTokenCount = tokens.Length;
        Guid? clientOperationId = null;
        if (!TryParseOperationId(
                tokens[^1],
                out clientOperationId,
                out var operationTokenRecognized) &&
            operationTokenRecognized)
        {
            error = OperationIdError;
            return true;
        }

        if (clientOperationId.HasValue)
        {
            commandTokenCount--;
        }

        if (commandTokenCount == 4 && uint.TryParse(tokens[3], out var itemId))
        {
            if (!mounts.TryResolveGrantable(itemId, out var numericMount))
            {
                error = itemId == DeveloperMountCatalog.OrphanedMountItemId
                    ? $"Mount item {itemId} is an orphaned client entry and cannot be generated."
                    : $"Item ID {itemId} is not an allowlisted client mount.";
                return true;
            }

            request = new DeveloperItemRequest(
                DeveloperItemOperation.MountAdd,
                Material: null,
                Quantity: 1,
                Mount: numericMount,
                ClientOperationId: clientOperationId);
            return true;
        }

        if (commandTokenCount != 5)
        {
            error = MountAddUsage;
            return true;
        }

        if (!mounts.TryGetFamily(tokens[3], out _))
        {
            error = $"Unknown mount family alias '{tokens[3]}'.";
            return true;
        }

        if (!mounts.TryResolveGrantable(tokens[3], tokens[4], out var aliasedMount))
        {
            error = $"Mount family '{tokens[3]}' has no grantable tier '{tokens[4]}'.";
            return true;
        }

        request = new DeveloperItemRequest(
            DeveloperItemOperation.MountAdd,
            Material: null,
            Quantity: 1,
            Mount: aliasedMount,
            ClientOperationId: clientOperationId);
        return true;
    }

    private static int FindCommandOffset(string text, out string matchedPrefix)
    {
        var bestOffset = -1;
        matchedPrefix = string.Empty;
        foreach (var prefix in Prefixes)
        {
            var offset = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            while (offset >= 0)
            {
                if ((offset == 0 || char.IsWhiteSpace(text[offset - 1]) || text[offset - 1] is ':' or '>') &&
                    (bestOffset < 0 || offset < bestOffset))
                {
                    bestOffset = offset;
                    matchedPrefix = prefix;
                    break;
                }

                offset = text.IndexOf(prefix, offset + prefix.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return bestOffset;
    }

    private static bool TryParseOperationId(
        string token,
        out Guid? operationId,
        out bool recognized)
    {
        const string prefix = "op=";
        operationId = null;
        recognized = token.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
        if (!recognized)
        {
            return false;
        }

        var value = token[prefix.Length..];
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty)
        {
            return false;
        }

        operationId = parsed;
        return true;
    }
}
