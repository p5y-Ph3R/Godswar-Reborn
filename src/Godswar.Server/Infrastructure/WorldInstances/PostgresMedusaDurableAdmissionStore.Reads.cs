using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresMedusaDurableAdmissionStore
{
    private static async Task<MedusaAdmissionSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MedusaAdmissionId admissionId,
        bool lockAdmission,
        CancellationToken cancellationToken)
    {
        var sql = lockAdmission
            ? ReadSnapshotSql + " FOR UPDATE OF admission;"
            : ReadSnapshotSql + ";";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = ReadAdmissionRow(reader);
        var members = new List<PartyAdmissionMember>(row.MemberCount);
        do
        {
            var ordinal = reader.GetInt16(37);
            if (ordinal != members.Count)
            {
                throw new InvalidDataException(
                    "A Medusa admission roster has non-canonical ordering.");
            }
            members.Add(new PartyAdmissionMember(
                reader.GetInt32(38),
                reader.GetInt32(39),
                new PlayerOwnershipFence(
                    reader.GetGuid(44),
                    reader.GetInt64(45)),
                new RealmId(reader.GetInt32(40)),
                reader.GetInt32(41),
                new WorldInstanceId(reader.GetGuid(42)),
                new MapId(reader.GetInt16(43))));
        }
        while (await reader.ReadAsync(cancellationToken));

        if (members.Count != row.MemberCount)
        {
            throw new InvalidDataException(
                "A Medusa admission member count disagrees with its roster.");
        }

        var party = new PartyAdmissionLease(
            row.LeaseId,
            row.PartyId,
            row.PartyRevision,
            row.LeaderAccountId,
            row.LeaderCharacterId,
            members,
            row.LeaseIssuedAtUtc,
            row.LeaseExpiresAtUtc);
        return new MedusaAdmissionSnapshot(
            row.AdmissionId,
            row.WorldInstanceId,
            row.RealmDay,
            row.Difficulty,
            row.ContentMapId,
            row.Source,
            party,
            row.EncounterContentFingerprint,
            row.RosterHash,
            row.RequestHash,
            row.State,
            row.Revision,
            row.BarrierEvidence,
            row.ReservedAtUtc,
            row.RuntimeReadyAtUtc,
            row.RosterTransferCommittedAtUtc,
            row.ConsumedAtUtc,
            row.TerminalAtUtc,
            row.ReleasedAtUtc,
            row.CleanupEvidence,
            row.CleanupCompletedAtUtc);
    }

    private static AdmissionRow ReadAdmissionRow(NpgsqlDataReader reader) =>
        new(
            new MedusaAdmissionId(reader.GetGuid(0)),
            new WorldInstanceId(reader.GetGuid(1)),
            new MedusaRealmDay(
                new RealmId(reader.GetInt32(2)),
                reader.GetFieldValue<DateOnly>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)),
            checked((MedusaEncounterDifficulty)reader.GetInt16(7)),
            new MapId(reader.GetInt16(8)),
            new MedusaAdmissionSource(
                new WorldInstanceId(reader.GetGuid(9)),
                new MapId(reader.GetInt16(10)),
                checked((uint)reader.GetInt64(11))),
            reader.GetGuid(12),
            reader.GetGuid(13),
            reader.GetInt64(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            AsUtc(reader.GetDateTime(17)),
            AsUtc(reader.GetDateTime(18)),
            reader.GetInt16(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            checked((MedusaAdmissionState)reader.GetInt16(23)),
            reader.GetInt64(24),
            ReadBarrierEvidence(reader, 25, 26),
            AsUtc(reader.GetDateTime(27)),
            ReadNullableUtc(reader, 28),
            ReadNullableUtc(reader, 29),
            ReadNullableUtc(reader, 30),
            ReadNullableUtc(reader, 31),
            ReadNullableUtc(reader, 32),
            ReadCleanupEvidence(reader, 0, 33, 34, 35),
            ReadNullableUtc(reader, 36));

    private static MedusaRosterTransferBarrierEvidence? ReadBarrierEvidence(
        NpgsqlDataReader reader,
        int stageOrdinal,
        int hashOrdinal)
    {
        var stageIsNull = reader.IsDBNull(stageOrdinal);
        var hashIsNull = reader.IsDBNull(hashOrdinal);
        if (stageIsNull != hashIsNull)
        {
            throw new InvalidDataException(
                "A Medusa admission has partial transfer-barrier evidence.");
        }
        return stageIsNull
            ? null
            : new MedusaRosterTransferBarrierEvidence(
                reader.GetGuid(stageOrdinal),
                reader.GetString(hashOrdinal));
    }

    private static MedusaAdmissionCleanupEvidence? ReadCleanupEvidence(
        NpgsqlDataReader reader,
        int admissionOrdinal,
        int kindOrdinal,
        int rosterOrdinal,
        int runtimeOrdinal)
    {
        var kindIsNull = reader.IsDBNull(kindOrdinal);
        var rosterIsNull = reader.IsDBNull(rosterOrdinal);
        var runtimeIsNull = reader.IsDBNull(runtimeOrdinal);
        if (kindIsNull != rosterIsNull || kindIsNull != runtimeIsNull)
        {
            throw new InvalidDataException(
                "A Medusa admission has partial cleanup evidence.");
        }
        return kindIsNull
            ? null
            : new MedusaAdmissionCleanupEvidence(
                new MedusaAdmissionId(reader.GetGuid(admissionOrdinal)),
                checked((MedusaAdmissionCleanupKind)
                    reader.GetInt16(kindOrdinal)),
                reader.GetGuid(rosterOrdinal),
                reader.GetGuid(runtimeOrdinal));
    }

    private static DateTimeOffset? ReadNullableUtc(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : AsUtc(reader.GetDateTime(ordinal));

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record AdmissionRow(
        MedusaAdmissionId AdmissionId,
        WorldInstanceId WorldInstanceId,
        MedusaRealmDay RealmDay,
        MedusaEncounterDifficulty Difficulty,
        MapId ContentMapId,
        MedusaAdmissionSource Source,
        Guid LeaseId,
        Guid PartyId,
        long PartyRevision,
        int LeaderAccountId,
        int LeaderCharacterId,
        DateTimeOffset LeaseIssuedAtUtc,
        DateTimeOffset LeaseExpiresAtUtc,
        int MemberCount,
        string RosterHash,
        string RequestHash,
        string EncounterContentFingerprint,
        MedusaAdmissionState State,
        long Revision,
        MedusaRosterTransferBarrierEvidence? BarrierEvidence,
        DateTimeOffset ReservedAtUtc,
        DateTimeOffset? RuntimeReadyAtUtc,
        DateTimeOffset? RosterTransferCommittedAtUtc,
        DateTimeOffset? ConsumedAtUtc,
        DateTimeOffset? TerminalAtUtc,
        DateTimeOffset? ReleasedAtUtc,
        MedusaAdmissionCleanupEvidence? CleanupEvidence,
        DateTimeOffset? CleanupCompletedAtUtc);

    private const string ReadSnapshotSql =
        """
        SELECT
            admission.admission_id,
            admission.world_instance_id,
            admission.realm_id,
            admission.realm_day,
            admission.calendar_time_zone_id,
            admission.time_zone_rules_fingerprint,
            admission.calendar_revision,
            admission.difficulty,
            admission.content_map_id,
            admission.source_world_instance_id,
            admission.source_map_id,
            admission.source_npc_id,
            admission.lease_id,
            admission.party_id,
            admission.party_revision,
            admission.leader_account_id,
            admission.leader_character_id,
            admission.lease_issued_at,
            admission.lease_expires_at,
            admission.member_count,
            admission.roster_hash,
            admission.request_hash,
            admission.encounter_content_fingerprint,
            admission.state,
            admission.revision,
            admission.roster_transfer_stage_id,
            admission.roster_transfer_preparation_hash,
            admission.reserved_at,
            admission.runtime_ready_at,
            admission.roster_transfer_committed_at,
            admission.consumed_at,
            admission.terminal_at,
            admission.released_at,
            admission.cleanup_kind,
            admission.cleanup_roster_operation_id,
            admission.cleanup_runtime_operation_id,
            admission.cleanup_completed_at,
            member.ordinal,
            member.account_id,
            member.character_id,
            member.realm_id,
            member.player_level,
            member.source_world_instance_id,
            member.source_map_id,
            member.ownership_owner_id,
            member.ownership_generation
        FROM medusa_admission_foundation.admissions admission
        JOIN medusa_admission_foundation.members member
          ON member.admission_id = admission.admission_id
        WHERE admission.admission_id = @admissionId
        ORDER BY member.ordinal
        """;
}
