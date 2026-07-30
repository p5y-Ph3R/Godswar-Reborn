using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<LockedAccount?> LockAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT character_lifecycle_version
            FROM public.accounts
            WHERE id = @accountId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null or DBNull)
        {
            return null;
        }

        var version = Convert.ToInt64(scalar);
        return version >= 0
            ? new LockedAccount(version)
            : throw new InvalidDataException(
                "The account lifecycle version is invalid.");
    }

    private async Task<LifecycleTransition>
        ExecuteDeleteTransitionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<CharacterDeleteCommand> envelope,
            LockedAccount account,
            CancellationToken cancellationToken)
    {
        var active = await ReadActiveCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            cancellationToken);
        if (active is null)
        {
            return new LifecycleTransition(
                CharacterLifecycleReceiptStatus.CharacterNotFound,
                0,
                account.LifecycleVersion,
                envelope.Command.Name,
                null,
                null);
        }
        if (!string.Equals(
                active.Value.Name,
                envelope.Command.Name,
                StringComparison.Ordinal))
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.NameMismatch,
                active.Value,
                account.LifecycleVersion);
        }
        if (envelope.Command.ExpectedActiveCharacterId is { } expectedId &&
            (expectedId != active.Value.CharacterId ||
             envelope.Command.ExpectedLifecycleVersion !=
                active.Value.LifecycleVersion))
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.StaleLifecycleVersion,
                active.Value,
                account.LifecycleVersion);
        }
        if (active.Value.HasActiveOwner)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.CharacterInUse,
                active.Value,
                account.LifecycleVersion);
        }

        var nextVersion = checked(account.LifecycleVersion + 1);
        var timestamps = await TombstoneCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            active.Value.CharacterId,
            active.Value.LifecycleVersion,
            nextVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            account.LifecycleVersion,
            nextVersion,
            cancellationToken);
        return new LifecycleTransition(
            CharacterLifecycleReceiptStatus.Deleted,
            active.Value.CharacterId,
            nextVersion,
            active.Value.Name,
            timestamps.RestoreUntil,
            timestamps.PurgeAfter);
    }

    private async Task<LifecycleTransition>
        ExecuteRestoreTransitionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<CharacterRestoreCommand> envelope,
            LockedAccount account,
            CancellationToken cancellationToken)
    {
        var target = await ReadCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.CharacterId,
            cancellationToken);
        if (target is null)
        {
            return new LifecycleTransition(
                CharacterLifecycleReceiptStatus.CharacterNotFound,
                envelope.Command.CharacterId,
                account.LifecycleVersion,
                string.Empty,
                null,
                null);
        }
        if (target.Value.LifecycleVersion !=
            envelope.Command.ExpectedLifecycleVersion)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.StaleLifecycleVersion,
                target.Value,
                account.LifecycleVersion);
        }
        if (!target.Value.IsDeleted)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.InvalidLifecycleState,
                target.Value,
                account.LifecycleVersion);
        }
        if (!target.Value.RestoreEligible)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.RestoreExpired,
                target.Value,
                account.LifecycleVersion);
        }

        var active = await ReadActiveCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            cancellationToken);
        if (active is not null)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus
                    .RestoreBlockedByActiveSlot,
                target.Value,
                account.LifecycleVersion);
        }

        var nextVersion = checked(account.LifecycleVersion + 1);
        await RestoreCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            target.Value.CharacterId,
            target.Value.LifecycleVersion,
            nextVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            account.LifecycleVersion,
            nextVersion,
            cancellationToken);
        return new LifecycleTransition(
            CharacterLifecycleReceiptStatus.Restored,
            target.Value.CharacterId,
            nextVersion,
            target.Value.Name,
            null,
            null);
    }

    private async Task<LifecycleTransition>
        ExecutePurgeTransitionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<CharacterPurgeCommand> envelope,
            LockedAccount account,
            CancellationToken cancellationToken)
    {
        var target = await ReadCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.CharacterId,
            cancellationToken);
        if (target is null)
        {
            return new LifecycleTransition(
                CharacterLifecycleReceiptStatus.CharacterNotFound,
                envelope.Command.CharacterId,
                account.LifecycleVersion,
                string.Empty,
                null,
                null);
        }
        if (target.Value.LifecycleVersion !=
            envelope.Command.ExpectedLifecycleVersion)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.StaleLifecycleVersion,
                target.Value,
                account.LifecycleVersion);
        }
        if (!target.Value.IsDeleted)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.InvalidLifecycleState,
                target.Value,
                account.LifecycleVersion);
        }
        if (!target.Value.PurgeEligible)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.PurgeNotEligible,
                target.Value,
                account.LifecycleVersion);
        }

        var nextVersion = checked(account.LifecycleVersion + 1);
        await PurgeCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            target.Value.CharacterId,
            target.Value.LifecycleVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            account.LifecycleVersion,
            nextVersion,
            cancellationToken);
        return new LifecycleTransition(
            CharacterLifecycleReceiptStatus.Purged,
            target.Value.CharacterId,
            nextVersion,
            target.Value.Name,
            target.Value.RestoreUntil,
            target.Value.PurgeAfter);
    }

    private static LifecycleTransition Rejected(
        CharacterLifecycleReceiptStatus status,
        StoredCharacter character,
        long accountVersion) =>
        new(
            status,
            character.CharacterId,
            accountVersion,
            character.Name,
            character.RestoreUntil,
            character.PurgeAfter);

    private readonly record struct StoredCharacter(
        int CharacterId,
        string Name,
        bool IsDeleted,
        long LifecycleVersion,
        DateTimeOffset? RestoreUntil,
        DateTimeOffset? PurgeAfter,
        bool RestoreEligible,
        bool PurgeEligible,
        bool HasActiveOwner);

    private readonly record struct TombstoneTimestamps(
        DateTimeOffset RestoreUntil,
        DateTimeOffset PurgeAfter);
}
