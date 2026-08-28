using System.Collections.ObjectModel;
using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresMedusaTitleAwardStore
{
    public async Task<MedusaTitleSettlementSnapshot?> FindSettlementAsync(
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken = default)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await ReadSettlementRowAsync(
            connection,
            transaction: null,
            admissionId,
            cancellationToken);
        if (row is null)
        {
            return null;
        }
        var admission = await ReadAdmissionAsync(
            connection,
            transaction: null,
            admissionId,
            lockRow: false,
            cancellationToken) ?? throw new InvalidDataException(
                "A Medusa title settlement has no source admission.");
        RequireSettlementAdmissionCoherence(admission, row);
        var roster = await ReadRosterAsync(
            connection,
            transaction: null,
            admissionId,
            cancellationToken);
        return CreateSnapshot(row, roster);
    }

    public async Task<IReadOnlyList<MedusaTitleOwnershipSnapshot>>
        FindOwnershipAsync(
            int characterId,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT ownership.title_key,
                   ownership.source_admission_id,
                   ownership.source_completion_operation_id,
                   ownership.acquired_at,
                   settlement.completed_at,
                   admission.world_instance_id,
                   admission.difficulty,
                   admission.content_map_id,
                   admission.encounter_content_fingerprint,
                   admission.roster_hash,
                   admission.request_hash,
                   admission.state,
                   admission.revision,
                   admission.consumed_at,
                   admission.terminal_at,
                   settlement.world_instance_id,
                   settlement.difficulty,
                   settlement.content_map_id,
                   settlement.encounter_content_fingerprint,
                   settlement.roster_hash,
                   settlement.admission_request_hash,
                   settlement.elapsed_microseconds
            FROM medusa_admission_foundation.character_title_ownership
                AS ownership
            INNER JOIN medusa_admission_foundation.medusa_completion_settlements
                AS settlement
                ON settlement.admission_id = ownership.source_admission_id
               AND settlement.completion_operation_id =
                   ownership.source_completion_operation_id
               AND settlement.title_key = ownership.title_key
            INNER JOIN medusa_admission_foundation.admissions AS admission
                ON admission.admission_id = settlement.admission_id
            WHERE ownership.character_id = @characterId
            ORDER BY ownership.title_key COLLATE "C";
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        var titles = new List<MedusaTitleOwnershipSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var acquiredAt = ReadUtc(reader, 3);
            var completedAt = ReadUtc(reader, 4);
            if (acquiredAt != completedAt)
            {
                throw new InvalidDataException(
                    "Medusa title acquisition time disagrees with its settlement.");
            }
            var admission = new LockedAdmission(
                new WorldInstanceId(reader.GetGuid(5)),
                checked((MedusaEncounterDifficulty)reader.GetInt16(6)),
                new MapId(reader.GetInt16(7)),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                checked((MedusaAdmissionState)reader.GetInt16(11)),
                reader.GetInt64(12),
                reader.IsDBNull(13) ? null : ReadUtc(reader, 13),
                reader.IsDBNull(14) ? null : ReadUtc(reader, 14));
            RequireSettlementAdmissionCoherence(
                admission,
                new SettlementCoherenceEvidence(
                    new WorldInstanceId(reader.GetGuid(15)),
                    checked((MedusaEncounterDifficulty)reader.GetInt16(16)),
                    new MapId(reader.GetInt16(17)),
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20),
                    completedAt,
                    admission.ConsumedAtUtc,
                    reader.GetInt64(21)));
            titles.Add(new MedusaTitleOwnershipSnapshot(
                characterId,
                new MedusaTitleSemanticKey(reader.GetString(0)),
                new MedusaAdmissionId(reader.GetGuid(1)),
                reader.GetGuid(2),
                acquiredAt));
        }
        return new ReadOnlyCollection<MedusaTitleOwnershipSnapshot>(titles);
    }

    private static async Task<LockedAdmission?> ReadAdmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MedusaAdmissionId admissionId,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT world_instance_id, difficulty, content_map_id,
                   encounter_content_fingerprint, roster_hash,
                   request_hash, state, revision, consumed_at, terminal_at
            FROM medusa_admission_foundation.admissions
            WHERE admission_id = @admissionId
            {(lockRow ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new LockedAdmission(
            new WorldInstanceId(reader.GetGuid(0)),
            checked((MedusaEncounterDifficulty)reader.GetInt16(1)),
            new MapId(reader.GetInt16(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            checked((MedusaAdmissionState)reader.GetInt16(6)),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : ReadUtc(reader, 8),
            reader.IsDBNull(9) ? null : ReadUtc(reader, 9));
    }

    private static async Task<IReadOnlyList<MedusaTitleSettlementMember>>
        ReadRosterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT account_id, character_id
            FROM medusa_admission_foundation.members
            WHERE admission_id = @admissionId
            ORDER BY character_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        var roster = new List<MedusaTitleSettlementMember>(
            MedusaIslandPolicy.MaximumPartySize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roster.Add(new MedusaTitleSettlementMember(
                reader.GetInt32(0),
                reader.GetInt32(1)));
        }
        return new ReadOnlyCollection<MedusaTitleSettlementMember>(roster);
    }

    private static async Task<SettlementRow?> ReadSettlementRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT admission_id, completion_operation_id, world_instance_id,
                   difficulty, content_map_id,
                   encounter_content_fingerprint, roster_hash,
                   admission_request_hash, completed_at,
                   elapsed_microseconds, final_score, request_hash, title_key
            FROM medusa_admission_foundation.medusa_completion_settlements
            WHERE admission_id = @admissionId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("admissionId", admissionId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new SettlementRow(
            new MedusaAdmissionId(reader.GetGuid(0)),
            reader.GetGuid(1),
            new WorldInstanceId(reader.GetGuid(2)),
            checked((MedusaEncounterDifficulty)reader.GetInt16(3)),
            new MapId(reader.GetInt16(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ReadUtc(reader, 8),
            reader.GetInt64(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static MedusaTitleSettlementSnapshot CreateSnapshot(
        SettlementRow row,
        IReadOnlyList<MedusaTitleSettlementMember> roster)
    {
        if (row.OperationId !=
                MedusaTitleAwardOperationIds.Completion(row.AdmissionId) ||
            row.ElapsedMicroseconds < 0 ||
            row.ElapsedMicroseconds > long.MaxValue / TimeSpan.TicksPerMicrosecond)
        {
            throw new InvalidDataException(
                "A durable Medusa title settlement has invalid identity or time.");
        }
        var elapsed = TimeSpan.FromTicks(checked(
            row.ElapsedMicroseconds * TimeSpan.TicksPerMicrosecond));
        MedusaTitleSemanticKey? title = row.TitleKey is null
            ? null
            : new MedusaTitleSemanticKey(row.TitleKey);
        return new MedusaTitleSettlementSnapshot(
            row.AdmissionId,
            row.OperationId,
            row.WorldInstanceId,
            row.Difficulty,
            row.ContentMapId,
            row.EncounterContentFingerprint,
            row.RosterHash,
            row.AdmissionRequestHash,
            row.CompletedAtUtc,
            elapsed,
            row.FinalScore,
            row.RequestHash,
            title,
            roster);
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);
}
