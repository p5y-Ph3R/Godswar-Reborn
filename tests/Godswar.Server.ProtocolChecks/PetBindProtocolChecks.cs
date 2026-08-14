using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

internal static class PetBindProtocolChecks
{
    public const string CheckName = "Stock Pet Manager bind protocol";

    public static Task RunAsync()
    {
        Check.True(
            PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                out var page) &&
            page.SequenceEqual(
                [17, PetManagerProtocol.PetBindActionSubId]),
            "Pet Manager choice 7 opens the stock bind page");

        var navigation = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        var nested = navigation.ToArray();
        nested[0] = PetManagerProtocol.PetBindActionSubId;
        Check.True(
            PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                nested) &&
            !PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                navigation),
            "only nested choice 7 -> 112 resolves as a bind mutation");

        var nonMinusOnePadding = nested.ToArray();
        nonMinusOnePadding[1] = 0;
        Check.True(
            !PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindActionSubId,
                navigation) &&
            !PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                nested) &&
            !PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                nonMinusOnePadding) &&
            !PetManagerProtocol.TryResolvePetBindMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PetBindMenuSubId,
                nested[..^1]),
            "bind rejects flattened, wrong-dialog, padded, and short shapes");

        Check.True(
            new[]
            {
                PetManagerProtocol.PetBindAlreadyBoundResultSubId,
                PetManagerProtocol.PetBindSucceededResultSubId,
                PetManagerProtocol.PetBindNoPetResultSubId
            }.SequenceEqual([1072, 1073, 1075]),
            "stock bind terminal result sub-IDs remain exact");

        return Task.CompletedTask;
    }
}
