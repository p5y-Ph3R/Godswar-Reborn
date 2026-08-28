using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// PostgreSQL claim store for the unwired Medusa admission foundation. Schema
/// publication is intentionally external; constructing this type never creates
/// tables and never grants party, NPC, runtime, or transfer authority.
/// </summary>
internal sealed partial class PostgresMedusaDurableAdmissionStore :
    IMedusaDurableAdmissionStore,
    IMedusaDurableAdmissionRecoverySource
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresMedusaDurableAdmissionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<MedusaAdmissionReceipt> ReserveAsync(
        MedusaAdmissionReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        if (!await TryInsertAdmissionAsync(
                connection,
                transaction,
                request,
                cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return await ResolveReservationCollisionAsync(
                request,
                cancellationToken);
        }

        for (var ordinal = 0; ordinal < request.Party.Members.Length; ordinal++)
        {
            await InsertMemberAsync(
                connection,
                transaction,
                request,
                ordinal,
                cancellationToken);
        }

        // Acquire unique claim keys in canonical order. Party order remains
        // authored in members.ordinal, but inverse leader/roster order cannot
        // deadlock concurrent overlapping reservations.
        foreach (var member in request.Party.Members.OrderBy(
                     static member => member.CharacterId))
        {
            if (!await TryInsertAttemptClaimAsync(
                    connection,
                    transaction,
                    request,
                    member.CharacterId,
                    cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new MedusaAdmissionReceipt(
                    MedusaAdmissionReceiptStatus.MemberAttemptConflict,
                    request.AdmissionId,
                    null,
                    null,
                    null);
            }
            if (!await TryInsertActiveMemberClaimAsync(
                    connection,
                    transaction,
                    request,
                    member.CharacterId,
                    cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new MedusaAdmissionReceipt(
                    MedusaAdmissionReceiptStatus.MemberActiveAdmissionConflict,
                    request.AdmissionId,
                    null,
                    null,
                    null);
            }
        }

        var snapshot = CreateReservedSnapshot(request);
        await transaction.CommitAsync(cancellationToken);
        return Applied(snapshot);
    }

    public async Task<MedusaAdmissionSnapshot?> FindAsync(
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken = default)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSnapshotAsync(
            connection,
            transaction: null,
            admissionId,
            lockAdmission: false,
            cancellationToken);
    }

    private async Task<MedusaAdmissionReceipt>
        ResolveReservationCollisionAsync(
            MedusaAdmissionReservationRequest request,
            CancellationToken cancellationToken)
    {
        var existing = await FindAsync(
            request.AdmissionId,
            cancellationToken);
        if (existing is not null &&
            string.Equals(
                existing.RequestHash,
                request.RequestHash,
                StringComparison.Ordinal))
        {
            return Duplicate(
                existing,
                existing.State,
                existing.Revision);
        }

        return new MedusaAdmissionReceipt(
            MedusaAdmissionReceiptStatus.RequestConflict,
            request.AdmissionId,
            null,
            null,
            existing);
    }

    private static async Task<bool> TryInsertAdmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionReservationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertAdmissionSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue(
            "worldInstanceId",
            request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("realmId", request.RealmDay.RealmId.Value);
        command.Parameters.Add(
            "realmDay",
            NpgsqlDbType.Date).Value = request.RealmDay.Day;
        command.Parameters.AddWithValue(
            "calendarTimeZoneId",
            request.RealmDay.CalendarTimeZoneId!);
        command.Parameters.AddWithValue(
            "timeZoneRulesFingerprint",
            request.RealmDay.TimeZoneRulesFingerprint!);
        command.Parameters.AddWithValue(
            "calendarRevision",
            request.RealmDay.CalendarRevision);
        command.Parameters.AddWithValue("difficulty", (short)request.Difficulty);
        command.Parameters.AddWithValue("contentMapId", request.ContentMapId.Value);
        command.Parameters.AddWithValue(
            "sourceWorldInstanceId",
            request.Source.WorldInstanceId.Value);
        command.Parameters.AddWithValue("sourceMapId", request.Source.MapId.Value);
        command.Parameters.AddWithValue(
            "sourceNpcId",
            checked((long)request.Source.NpcId));
        command.Parameters.AddWithValue("leaseId", request.Party.LeaseId);
        command.Parameters.AddWithValue("partyId", request.Party.PartyId);
        command.Parameters.AddWithValue(
            "partyRevision",
            request.Party.PartyRevision);
        command.Parameters.AddWithValue(
            "leaderAccountId",
            request.Party.LeaderAccountId);
        command.Parameters.AddWithValue(
            "leaderCharacterId",
            request.Party.LeaderCharacterId);
        AddTimestamp(command, "leaseIssuedAt", request.Party.IssuedAtUtc);
        AddTimestamp(command, "leaseExpiresAt", request.Party.ExpiresAtUtc);
        command.Parameters.AddWithValue(
            "memberCount",
            checked((short)request.Party.Members.Length));
        command.Parameters.AddWithValue("rosterHash", request.RosterHash);
        command.Parameters.AddWithValue("requestHash", request.RequestHash);
        command.Parameters.AddWithValue(
            "encounterContentFingerprint",
            request.EncounterContentFingerprint);
        AddTimestamp(command, "reservedAt", request.RequestedAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionReservationRequest request,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var member = request.Party.Members[ordinal];
        await using var command = new NpgsqlCommand(
            InsertMemberSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        command.Parameters.AddWithValue("ordinal", checked((short)ordinal));
        command.Parameters.AddWithValue("accountId", member.AccountId);
        command.Parameters.AddWithValue("characterId", member.CharacterId);
        command.Parameters.AddWithValue("memberRealmId", member.RealmId.Value);
        command.Parameters.AddWithValue("playerLevel", member.Level);
        command.Parameters.AddWithValue(
            "memberSourceWorldInstanceId",
            member.SourceWorldInstanceId.Value);
        command.Parameters.AddWithValue(
            "memberSourceMapId",
            member.SourceMapId.Value);
        command.Parameters.AddWithValue(
            "ownerId",
            member.Ownership.OwnerId);
        command.Parameters.AddWithValue(
            "generation",
            member.Ownership.Generation);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "A Medusa admission member was not inserted exactly once.");
        }
    }

    private static async Task<bool> TryInsertAttemptClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionReservationRequest request,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertAttemptClaimSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", request.RealmDay.RealmId.Value);
        command.Parameters.Add(
            "realmDay",
            NpgsqlDbType.Date).Value = request.RealmDay.Day;
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        AddTimestamp(command, "reservedAt", request.RequestedAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> TryInsertActiveMemberClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaAdmissionReservationRequest request,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InsertActiveMemberClaimSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", request.RealmDay.RealmId.Value);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("admissionId", request.AdmissionId.Value);
        AddTimestamp(command, "reservedAt", request.RequestedAtUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static MedusaAdmissionSnapshot CreateReservedSnapshot(
        MedusaAdmissionReservationRequest request) =>
        new(
            request.AdmissionId,
            request.WorldInstanceId,
            request.RealmDay,
            request.Difficulty,
            request.ContentMapId,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.RequestHash,
            MedusaAdmissionState.Reserved,
            revision: 1,
            barrierEvidence: null,
            request.RequestedAtUtc,
            runtimeReadyAtUtc: null,
            rosterTransferCommittedAtUtc: null,
            consumedAtUtc: null,
            terminalAtUtc: null,
            releasedAtUtc: null);

    private static MedusaAdmissionReceipt Applied(
        MedusaAdmissionSnapshot snapshot) =>
        new(
            MedusaAdmissionReceiptStatus.Applied,
            snapshot.AdmissionId,
            snapshot.State,
            snapshot.Revision,
            snapshot);

    private static MedusaAdmissionReceipt Duplicate(
        MedusaAdmissionSnapshot snapshot,
        MedusaAdmissionState committedState,
        long committedRevision) =>
        new(
            MedusaAdmissionReceiptStatus.Duplicate,
            snapshot.AdmissionId,
            committedState,
            committedRevision,
            snapshot);

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value.UtcDateTime;

    private const string InsertAdmissionSql =
        """
        INSERT INTO medusa_admission_foundation.admissions (
            admission_id, world_instance_id, realm_id, realm_day,
            calendar_time_zone_id, time_zone_rules_fingerprint,
            calendar_revision, difficulty, content_map_id,
            source_world_instance_id, source_map_id, source_npc_id,
            lease_id, party_id, party_revision,
            leader_account_id, leader_character_id,
            lease_issued_at, lease_expires_at, member_count,
            roster_hash, request_hash, encounter_content_fingerprint,
            state, revision, reserved_at)
        VALUES (
            @admissionId, @worldInstanceId, @realmId, @realmDay,
            @calendarTimeZoneId, @timeZoneRulesFingerprint,
            @calendarRevision, @difficulty, @contentMapId,
            @sourceWorldInstanceId, @sourceMapId, @sourceNpcId,
            @leaseId, @partyId, @partyRevision,
            @leaderAccountId, @leaderCharacterId,
            @leaseIssuedAt, @leaseExpiresAt, @memberCount,
            @rosterHash, @requestHash, @encounterContentFingerprint,
            1, 1, @reservedAt)
        ON CONFLICT DO NOTHING;
        """;

    private const string InsertMemberSql =
        """
        INSERT INTO medusa_admission_foundation.members (
            admission_id, ordinal, account_id, character_id,
            realm_id, player_level,
            source_world_instance_id, source_map_id,
            ownership_owner_id, ownership_generation)
        VALUES (
            @admissionId, @ordinal, @accountId, @characterId,
            @memberRealmId, @playerLevel,
            @memberSourceWorldInstanceId, @memberSourceMapId,
            @ownerId, @generation);
        """;

    private const string InsertAttemptClaimSql =
        """
        INSERT INTO medusa_admission_foundation.attempt_claims (
            realm_id, realm_day, character_id, admission_id,
            claim_state, reserved_at)
        VALUES (
            @realmId, @realmDay, @characterId, @admissionId, 1, @reservedAt)
        ON CONFLICT DO NOTHING;
        """;

    private const string InsertActiveMemberClaimSql =
        """
        INSERT INTO medusa_admission_foundation.active_member_claims (
            realm_id, character_id, admission_id, reserved_at)
        VALUES (@realmId, @characterId, @admissionId, @reservedAt)
        ON CONFLICT DO NOTHING;
        """;
}
