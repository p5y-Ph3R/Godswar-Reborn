using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GameHandlerCheckpointLifecycleChecks
{
    private static async Task
        CheckStaleCrossProcessPresenceReleaseAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var firstCharacter =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)!.Character!;
        var secondCharacter =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)!.Character!;
        var firstToken = Guid.NewGuid();
        var secondToken = Guid.NewGuid();
        var presence = new RecordingAccountPresenceWriter();

        await using var firstSession = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var first = CreateHandler(
            firstSession,
            new FixedSnapshotReader(snapshot),
            new RecordingCoordinator(
                firstCharacter.PositionRevision,
                firstCharacter.VitalsRevision),
            new RecordingLeaseIssuer(
                acquire: true,
                [],
                firstToken,
                releaseCurrent: false),
            presence);
        InstallIdentity(first, snapshot.AccountId, firstCharacter);
        Check.True(
            await InvokeAsync<bool>(
                EnsureOwnershipMethod,
                first,
                CancellationToken.None),
            "first process installs its fenced account presence");

        await using var secondSession = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var second = CreateHandler(
            secondSession,
            new FixedSnapshotReader(snapshot),
            new RecordingCoordinator(
                secondCharacter.PositionRevision,
                secondCharacter.VitalsRevision),
            new RecordingLeaseIssuer(
                acquire: true,
                [],
                secondToken,
                releaseCurrent: true),
            presence);
        InstallIdentity(second, snapshot.AccountId, secondCharacter);
        Check.True(
            await InvokeAsync<bool>(
                EnsureOwnershipMethod,
                second,
                CancellationToken.None),
            "replacement process installs its newer account presence");

        await InvokeAsync(FinalizeOwnershipMethod, first);
        Check.True(
            presence.IsOnline &&
            presence.PresenceToken == secondToken,
            "stale process exit cannot mark the replacement offline");

        await InvokeAsync(FinalizeOwnershipMethod, second);
        Check.True(
            !presence.IsOnline &&
            presence.PresenceToken is null,
            "the current replacement exit marks the account offline");
    }

    private sealed class NoopAccountPresenceWriter :
        IAccountPresenceWriter
    {
        public static NoopAccountPresenceWriter Instance { get; } = new();

        public Task MarkAccountOnlineAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkAccountOfflineAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkAccountPlayerOnlineAsync(
            int accountId,
            Guid presenceToken,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> TryMarkAccountPlayerOfflineAsync(
            int accountId,
            Guid presenceToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAccountPresenceWriter :
        IAccountPresenceWriter
    {
        private readonly object _gate = new();

        public bool IsOnline { get; private set; }

        public Guid? PresenceToken { get; private set; }

        public Task MarkAccountOnlineAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (PresenceToken is null)
                {
                    IsOnline = true;
                }
            }
            return Task.CompletedTask;
        }

        public Task MarkAccountOfflineAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (PresenceToken is null)
                {
                    IsOnline = false;
                }
            }
            return Task.CompletedTask;
        }

        public Task MarkAccountPlayerOnlineAsync(
            int accountId,
            Guid presenceToken,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                IsOnline = true;
                PresenceToken = presenceToken;
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryMarkAccountPlayerOfflineAsync(
            int accountId,
            Guid presenceToken,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (PresenceToken != presenceToken)
                {
                    return Task.FromResult(false);
                }
                IsOnline = false;
                PresenceToken = null;
                return Task.FromResult(true);
            }
        }
    }
}
