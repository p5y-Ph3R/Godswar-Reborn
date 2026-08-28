namespace Godswar.Server.Domain.World.Content;

internal enum InstanceCallerDifficulty
{
    Advanced = 204,
    Normal = 205,
    Mythic = 207
}

/// <summary>
/// Finite stock NpcFunRepetition surface for Medusa Island. The stock client
/// keeps the root choice in the request sub-id and appends the difficulty to
/// the fixed 18-value argument path.
/// </summary>
internal static class InstanceCallerProtocol
{
    public const uint AthensNpcId = 5199;
    public const uint SpartaNpcId = 5057;
    public const int DialogIndex = 9;
    public const int ActionPacketBytes = 92;
    public const int FunctionArgumentCount = 18;
    public const int InitialRequestSubId = -1;
    public const int MedusaRootSubId = 11;
    public const int DescriptionSubId = 206;
    public const int AdvancedDifficultySubId =
        (int)InstanceCallerDifficulty.Advanced;
    public const int NormalDifficultySubId =
        (int)InstanceCallerDifficulty.Normal;
    public const int MythicDifficultySubId =
        (int)InstanceCallerDifficulty.Mythic;
    public const int QueueUnavailableResultSubId = 1000;

    public static readonly TimeSpan PageContextLifetime =
        TimeSpan.FromMinutes(2);

    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(new[] { MedusaRootSubId });

    public static IReadOnlyList<int> MedusaPageSubIds { get; } =
        Array.AsReadOnly(new[]
        {
            DescriptionSubId,
            AdvancedDifficultySubId,
            NormalDifficultySubId,
            MythicDifficultySubId
        });

    public static bool IsEndpoint(string npcKey, uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_060", AthensNpcId) or
            ("Sparta_060", SpartaNpcId);

    public static bool TryGetMedusaPage(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out int[] responseSubIds)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        responseSubIds = [];
        if (dialogIndex != DialogIndex ||
            subId != MedusaRootSubId ||
            !HasExactPath(arguments))
        {
            return false;
        }

        responseSubIds = MedusaPageSubIds.ToArray();
        return true;
    }

    public static bool TryResolveDifficulty(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        out InstanceCallerDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        difficulty = default;
        if (dialogIndex != DialogIndex ||
            subId != MedusaRootSubId ||
            arguments.Count != FunctionArgumentCount)
        {
            return false;
        }

        difficulty = arguments[0] switch
        {
            AdvancedDifficultySubId => InstanceCallerDifficulty.Advanced,
            NormalDifficultySubId => InstanceCallerDifficulty.Normal,
            MythicDifficultySubId => InstanceCallerDifficulty.Mythic,
            _ => default
        };
        return difficulty != default &&
            HasExactPath(arguments, (int)difficulty);
    }

    private static bool HasExactPath(
        IReadOnlyList<int> arguments,
        params int[] path)
    {
        if (arguments.Count != FunctionArgumentCount ||
            path.Length > arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var expected = index < path.Length ? path[index] : -1;
            if (arguments[index] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
