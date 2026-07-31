using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private static readonly MethodInfo
        HandleDurableMaterialConversionMethod =
        FindHandlerMethod(
            "HandleDurableGearMentorMaterialConversionAsync");
    private static readonly MethodInfo
        HandleDurableMakeStoneMethod =
        FindHandlerMethod("HandleDurableMakeAttributeStoneAsync");

    private static async Task
        CheckUnavailableExecutorLeavesOperationPendingAsync()
    {
        foreach (var operation in new[]
        {
            GearMentorOperation.TransformCrystal,
            GearMentorOperation.CombineGemPieces
        })
        {
            var snapshot =
                CharacterSnapshotContractChecks.CreateValidSnapshot();
            var hydrated =
                CharacterLoadSnapshotHydrator.Hydrate(snapshot)
                ?? throw new InvalidOperationException(
                    "Provider-unavailable character did not hydrate.");
            var transport = new ReplayCaptureTransport();
            await using var session =
                new ClientSession(transport);
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
                    "provider-unavailable-check"));
            SetField(
                handler,
                "_character",
                hydrated.Character);

            var invocation =
                HandleDurableMaterialConversionMethod.Invoke(
                    handler,
                    [
                        operation,
                        (uint)5067,
                        ReplayOperationId,
                        null,
                        hydrated.Character.KitBag,
                        "none",
                        CancellationToken.None
                    ]) as Task
                ?? throw new InvalidOperationException(
                    "Durable material handler did not return a task.");
            await invocation;

            Check.Equal(
                0,
                transport.Events.Count,
                $"{operation} provider outage emits no stock result");
            Check.Equal(
                0,
                transport.CommandResults.Count,
                $"{operation} provider outage leaves UUID pending");
        }
    }

    private static async Task
        CheckUnavailableMakeStoneExecutorLeavesOperationPendingAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "Provider-unavailable character did not hydrate.");
        var transport = new ReplayCaptureTransport();
        await using var session =
            new ClientSession(transport);
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
                "provider-unavailable-check"));
        SetField(
            handler,
            "_character",
            hydrated.Character);

        var invocation = HandleDurableMakeStoneMethod.Invoke(
            handler,
            [
                (uint)5067,
                ReplayOperationId,
                null,
                hydrated.Character.KitBag,
                "none",
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "Durable Make Stone handler did not return a task.");
        await invocation;

        Check.Equal(
            0,
            transport.Events.Count,
            "Make Stone provider outage emits no stock result");
        Check.Equal(
            0,
            transport.CommandResults.Count,
            "Make Stone provider outage leaves UUID pending");
    }
}
