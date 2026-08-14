using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const uint LocalPlayerObjectId = 0x00001448;
    private readonly bool _requiresDurablePlayerCommands;

    private bool TryBindCurrentPlayerOwnership<TCommand>(
        CommandEnvelope<TCommand> envelope,
        out CommandEnvelope<TCommand> ownedEnvelope,
        out PlayerOwnershipFence ownership)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ownedEnvelope = envelope;
        if (!TryCaptureCurrentPlayerOwnership(out ownership))
        {
            RejectLostPlayerOwnership();
            return false;
        }

        ownedEnvelope = envelope with { Ownership = ownership };
        return true;
    }

    private bool TryCaptureCurrentPlayerOwnership(
        out PlayerOwnershipFence ownership)
    {
        ownership = default;
        if (_account is null ||
            _character is null ||
            (_playerCoordination?.IsEnabled == true &&
             _playerCoordinationLease?.IsCurrent != true) ||
            !TryGetCharacterOwnership(_character, out var candidate) ||
            !_registry.IsCurrentAccountSession(
                _account.Id,
                _session,
                candidate))
        {
            return false;
        }

        ownership = candidate;
        return true;
    }

    private bool AuthorizeAuthenticatedPacket()
    {
        if (!_accountSessionRegistered)
        {
            return true;
        }

        if (_account is null ||
            !_registry.IsCurrentAccountSession(
                _account.Id,
                _session))
        {
            RejectLostPlayerOwnership();
            return false;
        }

        if (_character is null ||
            !TryGetCharacterOwnership(
                _character,
                out var ownership))
        {
            return true;
        }

        if ((_playerCoordination?.IsEnabled == true &&
             _playerCoordinationLease?.IsCurrent != true) ||
            !_registry.IsCurrentAccountSession(
                _account.Id,
                _session,
                ownership) ||
            _registered &&
            !_registry.IsCurrentWorldOwnership(
                _session,
                _account.Id,
                _character.Id,
                ownership))
        {
            RejectLostPlayerOwnership();
            return false;
        }

        return true;
    }

    private bool RevalidateCurrentWorldEffectOwnership(
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!_accountSessionRegistered)
        {
            // Protocol fixtures and pre-cutover compatibility handlers do not
            // own a registered account session. Live authenticated gameplay
            // always does, and therefore always takes the exact fence below.
            return true;
        }

        if (_account is not null &&
            _character is not null &&
            (_playerCoordination?.IsEnabled != true ||
             _playerCoordinationLease?.IsCurrent == true) &&
            TryGetCharacterOwnership(
                _character,
                out var ownership) &&
            _registry.IsCurrentWorldOwnership(
                _session,
                _account.Id,
                _character.Id,
                ownership))
        {
            return true;
        }

        Console.Error.WriteLine(
            "[ownership] rejected stale world effect " +
            $"operation={operation}");
        RejectLostPlayerOwnership();
        return false;
    }

    private bool RevalidateCurrentPlayerOwnership(
        PlayerOwnershipFence ownership)
    {
        if (ownership.IsValid &&
            _account is not null &&
            _character is not null &&
            (_playerCoordination?.IsEnabled != true ||
             _playerCoordinationLease?.IsCurrent == true) &&
            _character.CheckpointOwnerId == ownership.OwnerId &&
            _character.CheckpointOwnerGeneration ==
                ownership.Generation &&
            _registry.IsCurrentAccountSession(
                _account.Id,
                _session,
                ownership))
        {
            return true;
        }

        RejectLostPlayerOwnership();
        return false;
    }

    private void RejectLostPlayerOwnership()
    {
        Console.Error.WriteLine(
            "[ownership] rejected stale player session");
        _session.Disconnect();
    }

    private bool AllowLegacyPlayerMutationFallback(
        string operation)
    {
        if (CanUseLegacyPlayerMutationFallback(
                _requiresDurablePlayerCommands,
                _session.IsSecure,
                _legacyAuthenticationAccess is not null))
        {
            return true;
        }

        Console.Error.WriteLine(
            "[ownership] rejected production legacy player mutation " +
            $"operation={operation}");
        _session.Disconnect();
        return false;
    }

    internal static bool CanUseLegacyPlayerMutationFallback(
        bool requiresDurablePlayerCommands,
        bool isSecureSession,
        bool hasLocalLegacyAuthenticationAccess) =>
        // The capability is issued only to the explicitly enabled
        // LocalDevelopment raw-TCP rollback profile. Secure and production
        // sessions never receive it and therefore cannot bypass the durable
        // command boundary.
        !requiresDurablePlayerCommands ||
        (!isSecureSession && hasLocalLegacyAuthenticationAccess);

    private static bool TryGetCharacterOwnership(
        GameCharacter character,
        out PlayerOwnershipFence ownership)
    {
        ownership = new PlayerOwnershipFence(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
        return ownership.IsValid;
    }
}
