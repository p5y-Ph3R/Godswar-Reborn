using Godswar.Server.Application.Items;

namespace Godswar.Server.Game;

internal static partial class DeveloperItemCommand
{
    private const int MinimumRubyLevel = 1;
    private const int MaximumRubyLevel = 3;

    private const string RubyUsage =
        "Usage: /ruby <level 1-3> [quantity] [op=<UUID>].";

    private static bool TryParseRuby(
        string[] tokens,
        IDeveloperItemGrantCatalog? items,
        out DeveloperItemRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        if (tokens.Length is < 2 or > 4)
        {
            error = RubyUsage;
            return true;
        }

        if (!int.TryParse(tokens[1], out var level) ||
            level is < MinimumRubyLevel or > MaximumRubyLevel)
        {
            error =
                $"Ruby level must be from {MinimumRubyLevel} to " +
                $"{MaximumRubyLevel}.";
            return true;
        }

        if (items is null)
        {
            error = "Ruby commands require the published developer-item catalog.";
            return true;
        }

        if (!items.TryResolveDeveloper($"ruby{level}", out var ruby))
        {
            error = $"Ruby level {level} is unavailable in the published item catalog.";
            return true;
        }

        var quantity = 1;
        Guid? clientOperationId = null;
        var argumentCount = tokens.Length - 2;
        var operationTokenRecognized = false;
        if (argumentCount > 0 &&
            TryParseOperationId(
                tokens[^1],
                out clientOperationId,
                out operationTokenRecognized))
        {
            argumentCount--;
        }
        else if (argumentCount > 0 && operationTokenRecognized)
        {
            error = OperationIdError;
            return true;
        }

        if (argumentCount > 1)
        {
            error = RubyUsage;
            return true;
        }

        if (argumentCount == 1 &&
            (!int.TryParse(tokens[2], out quantity) ||
             quantity is < 1 or > MaximumQuantity))
        {
            error = $"Quantity must be from 1 to {MaximumQuantity}.";
            return true;
        }

        request = new DeveloperItemRequest(
            DeveloperItemOperation.Add,
            ruby,
            quantity,
            ClientOperationId: clientOperationId);
        return true;
    }
}
