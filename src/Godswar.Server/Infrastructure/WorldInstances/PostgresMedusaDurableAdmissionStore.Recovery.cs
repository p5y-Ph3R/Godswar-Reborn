using System.Collections.Immutable;
using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresMedusaDurableAdmissionStore
{
    public async Task<MedusaAdmissionRecoveryPage> ScanRecoverableAsync(
        RealmId realmId,
        MedusaAdmissionRecoveryCursor? after,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (after is { IsValid: false })
        {
            throw new ArgumentOutOfRangeException(nameof(after));
        }
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        var candidates = new List<RecoveryCandidate>(maximumCount);
        await using (var command = new NpgsqlCommand(
            ScanRecoverableSql,
            connection))
        {
            command.Parameters.AddWithValue("realmId", realmId.Value);
            command.Parameters.AddWithValue("hasCursor", after is not null);
            command.Parameters.Add(
                "lastChangedAt",
                NpgsqlDbType.TimestampTz).Value =
                (after?.LastChangedAtUtc ?? DateTimeOffset.UnixEpoch).UtcDateTime;
            command.Parameters.AddWithValue(
                "afterAdmissionId",
                after?.AdmissionId.Value ?? Guid.Empty);
            command.Parameters.AddWithValue("maximumCount", maximumCount);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new RecoveryCandidate(
                    new MedusaAdmissionId(reader.GetGuid(0)),
                    new DateTimeOffset(
                        reader.GetFieldValue<DateTime>(1),
                        TimeSpan.Zero)));
            }
        }

        var snapshots = ImmutableArray.CreateBuilder<MedusaAdmissionSnapshot>(
            candidates.Count);
        foreach (var candidate in candidates)
        {
            var snapshot = await ReadSnapshotAsync(
                connection,
                transaction: null,
                candidate.AdmissionId,
                lockAdmission: false,
                cancellationToken);
            if (snapshot is not null && IsRecoverable(snapshot.State))
            {
                snapshots.Add(snapshot);
            }
        }
        MedusaAdmissionRecoveryCursor? next = candidates.Count == 0
            ? null
            : new MedusaAdmissionRecoveryCursor(
                candidates[^1].LastChangedAtUtc,
                candidates[^1].AdmissionId);
        return new MedusaAdmissionRecoveryPage(snapshots.ToImmutable(), next);
    }

    public async Task<MedusaAdmissionSnapshot?> FindActiveByMemberAsync(
        RealmId realmId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        MedusaAdmissionId? admissionId;
        await using (var command = new NpgsqlCommand(
            FindActiveByMemberSql,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("realmId", realmId.Value);
            command.Parameters.AddWithValue("characterId", characterId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            admissionId = value is Guid id
                ? new MedusaAdmissionId(id)
                : null;
        }
        if (admissionId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var snapshot = await ReadSnapshotAsync(
                connection,
                transaction,
                admissionId.Value,
                lockAdmission: false,
                cancellationToken);
        if (snapshot is null || snapshot.State is not (
                MedusaAdmissionState.Reserved or
                MedusaAdmissionState.RuntimeReady or
                MedusaAdmissionState.RosterTransferCommitted or
                MedusaAdmissionState.ConsumedRunning or
                MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut or
                MedusaAdmissionState.Released))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static bool IsRecoverable(MedusaAdmissionState state) =>
        state is
            MedusaAdmissionState.Reserved or
            MedusaAdmissionState.RuntimeReady or
            MedusaAdmissionState.RosterTransferCommitted or
            MedusaAdmissionState.ConsumedRunning or
            MedusaAdmissionState.Completed or
            MedusaAdmissionState.Abandoned or
            MedusaAdmissionState.TimedOut or
            MedusaAdmissionState.Released;

    private sealed record RecoveryCandidate(
        MedusaAdmissionId AdmissionId,
        DateTimeOffset LastChangedAtUtc);

    private const string ScanRecoverableSql =
        """
        SELECT admission_id,
               COALESCE(
                   released_at,
                   terminal_at,
                   consumed_at,
                   roster_transfer_committed_at,
                   runtime_ready_at,
                   reserved_at) AS last_changed_at
        FROM medusa_admission_foundation.admissions
        WHERE realm_id = @realmId
          AND state BETWEEN 1 AND 8
          AND (
              NOT @hasCursor OR
              COALESCE(
                  released_at,
                  terminal_at,
                  consumed_at,
                  roster_transfer_committed_at,
                  runtime_ready_at,
                  reserved_at) > @lastChangedAt OR
              (COALESCE(
                  released_at,
                  terminal_at,
                  consumed_at,
                  roster_transfer_committed_at,
                  runtime_ready_at,
                  reserved_at) = @lastChangedAt AND
               admission_id > @afterAdmissionId))
        ORDER BY last_changed_at, admission_id
        LIMIT @maximumCount;
        """;

    private const string FindActiveByMemberSql =
        """
        SELECT claim.admission_id
        FROM medusa_admission_foundation.active_member_claims AS claim
        INNER JOIN medusa_admission_foundation.admissions AS admission
            ON admission.admission_id = claim.admission_id
        WHERE claim.realm_id = @realmId
          AND claim.character_id = @characterId
          AND admission.state BETWEEN 1 AND 8
        FOR UPDATE OF admission;
        """;
}
