using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static void AssertCommitAndDuplicate(
        IReadOnlyList<PetDurableExecutionResult> results,
        PetDurableReceiptStatus status,
        string phase)
    {
        Check.Equal(
            1,
            results.Count(result => result.Disposition ==
                PetDurableExecutionDisposition.Committed),
            $"{phase} commits once");
        Check.Equal(
            1,
            results.Count(result => result.Disposition ==
                PetDurableExecutionDisposition.Duplicate),
            $"{phase} replays once");
        Check.True(
            results.All(result => result.Receipt?.Status == status) &&
            results[0].Receipt == results[1].Receipt,
            $"{phase} returns one canonical receipt");
    }
}
