using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsCharacterSelectionLifecyclePhase =>
        !_registered &&
        !_worldPresenceAnnounced &&
        !_clientReadyReceived &&
        !_playerDetailSent &&
        !_enterUiReadyReceived &&
        !_postEnterBootstrapSent &&
        !IsMapTransitionPending;

    private async Task RejectMissingLifecycleIdentityAsync(
        CommandFamily family,
        string operation,
        CancellationToken cancellationToken)
    {
        CommandMetrics.RecordUnsupportedLegacyIdentity(family);
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.InvalidIntent);
        Console.Error.WriteLine(
            $"[character] rejected secure {operation} without " +
            "operation identity");
        if (_characterSnapshotLoaded)
        {
            await SendCharacterPreviewAsync(cancellationToken);
        }
    }

    private async Task RejectMixedLifecycleProfileAsync(
        CommandFamily family,
        string operation,
        CancellationToken cancellationToken)
    {
        CommandMetrics.RecordUnsupportedLegacyIdentity(family);
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.InvalidIntent);
        Console.Error.WriteLine(
            $"[character] rejected raw {operation} while durable " +
            "PostgreSQL lifecycle commands are active");
        if (_characterSnapshotLoaded)
        {
            await SendCharacterPreviewAsync(cancellationToken);
        }
    }

    private async Task RejectOutsideSelectionLifecycleAsync(
        CommandFamily family,
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (family is not (
                CommandFamily.CharacterCreate or
                CommandFamily.CharacterDelete))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }

        var operationId = packet.ClientOperationId;
        var hasSecureIdentity =
            _session.IsSecure &&
            operationId is { } identity &&
            identity != Guid.Empty;
        var identityStrength = hasSecureIdentity
            ? CommandIdentityStrength.ClientOperationId
            : CommandIdentityStrength.UnsupportedLegacyRetry;
        if (!hasSecureIdentity)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(family);
        }
        CommandMetrics.Record(
            family,
            identityStrength,
            CommandOutcome.PreconditionFailed);
        Console.Error.WriteLine(
            "[character] ignored lifecycle command outside " +
            $"selection phase family={(ushort)family}");

        if (hasSecureIdentity)
        {
            await _session.SendLegacyCommandResultAsync(
                new SecureLegacyCommandResult(
                    SecureLegacyCommandDisposition.Rejected,
                    (ushort)family,
                    (uint)CharacterLifecycleReceiptStatus
                        .InvalidLifecycleState,
                    checked((ulong)Math.Max(
                        0,
                        _character?.LifecycleVersion ?? 0)),
                    operationId!.Value),
                cancellationToken);
        }
    }
}
