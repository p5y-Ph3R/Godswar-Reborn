using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.State;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<LockedAccount?> LockAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken)
    {
        await using (var ensureMembership = CreateCommand(
            """
            INSERT INTO public.account_realm (
                account_id,
                realm_id
            )
            SELECT account_row.id, realm.id
            FROM public.accounts account_row
            CROSS JOIN public.server realm
            WHERE account_row.id = @accountId
              AND realm.id = @realmId
              AND realm.enabled
            ON CONFLICT (account_id, realm_id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            ensureMembership.Parameters.AddWithValue("accountId", accountId);
            ensureMembership.Parameters.AddWithValue("realmId", realmId.Value);
            await ensureMembership.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = CreateCommand(
            """
            SELECT membership.character_lifecycle_version
            FROM public.account_realm membership
            JOIN public.server realm
              ON realm.id = membership.realm_id
             AND realm.enabled
            WHERE membership.account_id = @accountId
              AND membership.realm_id = @realmId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
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
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
            active.Value.CharacterId,
            active.Value.LifecycleVersion,
            nextVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
            target.Value.CharacterId,
            target.Value.LifecycleVersion,
            nextVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
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
            envelope.Command.RealmId,
            target.Value.CharacterId,
            target.Value.LifecycleVersion,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.RealmId,
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
