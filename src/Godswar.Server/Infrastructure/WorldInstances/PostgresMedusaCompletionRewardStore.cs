using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed class PostgresMedusaCompletionRewardStore(
    NpgsqlDataSource dataSource) : IMedusaCompletionRewardStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public async Task<MedusaCompletionRewardReceipt> SettleAsync(
        MedusaCompletionRewardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var existing = await ReadExistingAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var characters = await LockCharactersAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        if (characters.Count != request.CharacterIds.Count ||
            characters.Any(character =>
                character.Honor > int.MaxValue - request.Award.HardPoints ||
                character.RewardRevision == long.MaxValue))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Failed(
                request,
                MedusaCompletionRewardStatus.CharacterUnavailable);
        }

        if (!await TryInsertSettlementAsync(
                connection,
                transaction,
                request,
                cancellationToken))
        {
            var raced = await ReadExistingAsync(
                connection,
                transaction,
                request,
                cancellationToken) ?? Failed(
                    request,
                    MedusaCompletionRewardStatus.RequestConflict);
            await transaction.CommitAsync(cancellationToken);
            return raced;
        }

        var members = new List<MedusaCompletionRewardMember>(
            characters.Count);
        var awardedTitleId = request.Award.AwardedTitleId;
        foreach (var character in characters)
        {
            var honorAfter = checked(
                character.Honor + request.Award.HardPoints);
            var revisionAfter = checked(character.RewardRevision + 1);
            await UpdateCharacterAsync(
                connection,
                transaction,
                request,
                character,
                honorAfter,
                revisionAfter,
                awardedTitleId,
                cancellationToken);
            if (request.Award.Title is { } title)
            {
                await GrantTitleAsync(
                    connection,
                    transaction,
                    request,
                    character.CharacterId,
                    title.Title,
                    awardedTitleId,
                    cancellationToken);
            }
            await InsertMemberAsync(
                connection,
                transaction,
                request.WorldInstanceId,
                character,
                honorAfter,
                revisionAfter,
                awardedTitleId,
                cancellationToken);
            members.Add(new(
                character.CharacterId,
                character.Camp,
                character.Honor,
                honorAfter,
                revisionAfter,
                awardedTitleId));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            MedusaCompletionRewardStatus.Applied,
            request.WorldInstanceId,
            request.Award,
            members.AsReadOnly());
    }

    private static async Task<MedusaCompletionRewardReceipt?>
        ReadExistingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MedusaCompletionRewardRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT realm_id, difficulty, completed_at_ticks, elapsed_ticks,
                   final_score, hard_points, title, character_ids
            FROM medusa_completion_rewards
            WHERE world_instance_id = @worldInstanceId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "worldInstanceId",
            request.WorldInstanceId.Value);

        short realmId;
        short difficulty;
        long completedAtTicks;
        long elapsedTicks;
        int finalScore;
        int hardPoints;
        short? title;
        int[] characterIds;
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            realmId = reader.GetInt16(0);
            difficulty = reader.GetInt16(1);
            completedAtTicks = reader.GetInt64(2);
            elapsedTicks = reader.GetInt64(3);
            finalScore = reader.GetInt32(4);
            hardPoints = reader.GetInt32(5);
            title = reader.IsDBNull(6) ? null : reader.GetInt16(6);
            characterIds = reader.GetFieldValue<int[]>(7);
        }

        var requestedTitle = request.Award.Title is { } award
            ? checked((short)award.Title)
            : (short?)null;
        var matches = realmId == request.RealmId.Value &&
            difficulty == (short)request.Difficulty &&
            completedAtTicks == request.CompletedAtUtc.UtcTicks &&
            elapsedTicks == request.Elapsed.Ticks &&
            finalScore == request.FinalScore &&
            hardPoints == request.Award.HardPoints &&
            title == requestedTitle &&
            characterIds.SequenceEqual(request.CharacterIds);
        if (!matches)
        {
            return Failed(
                request,
                MedusaCompletionRewardStatus.RequestConflict);
        }

        return new(
            MedusaCompletionRewardStatus.Duplicate,
            request.WorldInstanceId,
            request.Award,
            await ReadMembersAsync(
                connection,
                transaction,
                request.WorldInstanceId,
                cancellationToken));
    }

    private static async Task<IReadOnlyList<LockedCharacter>>
        LockCharactersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MedusaCompletionRewardRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, camp, medusa_honor_points, medusa_reward_revision
            FROM character_base
            WHERE server_id = @realmId
              AND id = ANY(@characterIds)
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            request.CharacterIds.ToArray();

        var characters = new List<LockedCharacter>(
            request.CharacterIds.Count);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var camp = reader.GetInt16(1);
            if (camp is < GameDefaults.SpartaCamp or
                > GameDefaults.AthensCamp)
            {
                return [];
            }
            characters.Add(new(
                reader.GetInt32(0),
                checked((byte)camp),
                reader.GetInt32(2),
                reader.GetInt64(3)));
        }
        return characters;
    }

    private static async Task<bool> TryInsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaCompletionRewardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_completion_rewards (
                world_instance_id, realm_id, difficulty,
                completed_at_ticks, elapsed_ticks, final_score,
                hard_points, title, character_ids)
            VALUES (
                @worldInstanceId, @realmId, @difficulty,
                @completedAtTicks, @elapsedTicks, @finalScore,
                @hardPoints, @title, @characterIds)
            ON CONFLICT (world_instance_id) DO NOTHING
            RETURNING 1;
            """,
            connection,
            transaction);
        AddSettlementParameters(command, request);
        return await command.ExecuteScalarAsync(cancellationToken) is int and 1;
    }

    private static void AddSettlementParameters(
        NpgsqlCommand command,
        MedusaCompletionRewardRequest request)
    {
        command.Parameters.AddWithValue(
            "worldInstanceId",
            request.WorldInstanceId.Value);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue(
            "difficulty",
            checked((short)request.Difficulty));
        command.Parameters.AddWithValue(
            "completedAtTicks",
            request.CompletedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("elapsedTicks", request.Elapsed.Ticks);
        command.Parameters.AddWithValue("finalScore", request.FinalScore);
        command.Parameters.AddWithValue(
            "hardPoints",
            request.Award.HardPoints);
        command.Parameters.Add(
            "title",
            NpgsqlDbType.Smallint).Value = request.Award.Title is { } title
            ? checked((short)title.Title)
            : DBNull.Value;
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            request.CharacterIds.ToArray();
    }

    private static async Task UpdateCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaCompletionRewardRequest request,
        LockedCharacter character,
        int honorAfter,
        long revisionAfter,
        uint awardedTitleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_base
            SET medusa_honor_points = @honorAfter,
                medusa_reward_revision = @revisionAfter,
                selected_title_id = CASE
                    WHEN @awardedTitleId = 0 THEN selected_title_id
                    ELSE @awardedTitleId
                END
            WHERE id = @characterId
              AND server_id = @realmId
              AND medusa_honor_points = @honorBefore
              AND medusa_reward_revision = @revisionBefore;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("honorAfter", honorAfter);
        command.Parameters.AddWithValue("revisionAfter", revisionAfter);
        command.Parameters.AddWithValue(
            "characterId",
            character.CharacterId);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue("honorBefore", character.Honor);
        command.Parameters.AddWithValue(
            "revisionBefore",
            character.RewardRevision);
        command.Parameters.AddWithValue(
            "awardedTitleId",
            checked((int)awardedTitleId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new DBConcurrencyException(
                "A locked Medusa reward character changed unexpectedly.");
        }
    }

    private static async Task InsertMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldInstanceId worldInstanceId,
        LockedCharacter character,
        int honorAfter,
        long revisionAfter,
        uint awardedTitleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO medusa_completion_reward_members (
                world_instance_id, character_id, camp, honor_before,
                honor_after, reward_revision, awarded_title_id)
            VALUES (
                @worldInstanceId, @characterId, @camp, @honorBefore,
                @honorAfter, @rewardRevision, @awardedTitleId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "worldInstanceId",
            worldInstanceId.Value);
        command.Parameters.AddWithValue(
            "characterId",
            character.CharacterId);
        command.Parameters.AddWithValue("camp", checked((short)character.Camp));
        command.Parameters.AddWithValue("honorBefore", character.Honor);
        command.Parameters.AddWithValue("honorAfter", honorAfter);
        command.Parameters.AddWithValue("rewardRevision", revisionAfter);
        command.Parameters.AddWithValue(
            "awardedTitleId",
            checked((int)awardedTitleId));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task GrantTitleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MedusaCompletionRewardRequest request,
        int characterId,
        MedusaEncounterTitle title,
        uint titleId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_title_ownership (
                character_id, title, title_id,
                source_world_instance_id, acquired_at)
            VALUES (
                @characterId, @title, @titleId,
                @worldInstanceId, @acquiredAt)
            ON CONFLICT (character_id, title) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("title", checked((short)title));
        command.Parameters.AddWithValue("titleId", checked((int)titleId));
        command.Parameters.AddWithValue(
            "worldInstanceId",
            request.WorldInstanceId.Value);
        command.Parameters.Add(
            "acquiredAt",
            NpgsqlDbType.TimestampTz).Value = request.CompletedAtUtc.UtcDateTime;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<MedusaCompletionRewardMember>>
        ReadMembersAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            WorldInstanceId worldInstanceId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT character_id, camp, honor_before, honor_after,
                   reward_revision, awarded_title_id
            FROM medusa_completion_reward_members
            WHERE world_instance_id = @worldInstanceId
            ORDER BY character_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "worldInstanceId",
            worldInstanceId.Value);
        var members = new List<MedusaCompletionRewardMember>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new(
                reader.GetInt32(0),
                checked((byte)reader.GetInt16(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt64(4),
                checked((uint)reader.GetInt32(5))));
        }
        return members.AsReadOnly();
    }

    private static MedusaCompletionRewardReceipt Failed(
        MedusaCompletionRewardRequest request,
        MedusaCompletionRewardStatus status) =>
        new(status, request.WorldInstanceId, request.Award, []);

    private readonly record struct LockedCharacter(
        int CharacterId,
        byte Camp,
        int Honor,
        long RewardRevision);
}
