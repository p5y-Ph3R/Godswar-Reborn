using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridSelectionCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT profession
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null)
        {
            return null;
        }

        var profession = Convert.ToInt16(scalar);
        if (profession is < 0 or > 3)
        {
            throw new InvalidDataException(
                "The Zodiac selection profession is invalid.");
        }

        return new LockedCharacter(checked((byte)profession));
    }

    private async Task<StoredGrid[]> ReadRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken)
    {
        var row = Enumerable.Repeat(
            new StoredGrid(
                0,
                ZodiacSkillGridSelectionCatalog.ClearSelection),
            ZodiacSkillGridSelectionCatalog.GridsPerRow).ToArray();
        var rowStart =
            ZodiacSkillGridSelectionCatalog.RowStart(gridIndex);
        await using var command = CreateCommand(
            """
            SELECT grid_index, level, selected_skill_id
            FROM public.character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index >= @rowStart
              AND grid_index < @rowEnd
            ORDER BY grid_index;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "rowStart",
            checked((short)rowStart));
        command.Parameters.AddWithValue(
            "rowEnd",
            checked((short)(
                rowStart +
                ZodiacSkillGridSelectionCatalog.GridsPerRow)));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidate = reader.GetInt16(0);
            var level = reader.GetInt16(1);
            var selected = reader.GetInt32(2);
            if (candidate < rowStart ||
                candidate >=
                    rowStart +
                    ZodiacSkillGridSelectionCatalog.GridsPerRow ||
                level is < 0 or >
                    ZodiacSkillGridCatalog.MaximumGridLevel ||
                selected <
                    ZodiacSkillGridSelectionCatalog.ClearSelection)
            {
                throw new InvalidDataException(
                    "The stored Zodiac selection row is invalid.");
            }

            row[candidate - rowStart] = new(
                checked((byte)level),
                selected);
        }

        return row;
    }

    private async Task<bool> IsSkillLearnedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        byte profession,
        int selectedSkillKind,
        CancellationToken cancellationToken)
    {
        if (selectedSkillKind ==
            ZodiacSkillGridSelectionCatalog.ClearSelection)
        {
            return true;
        }

        if (!ZodiacSkillGridSelectionCatalog.IsAllowedForClass(
                profession,
                selectedSkillKind))
        {
            return false;
        }

        var first =
            ZodiacSkillGridSelectionCatalog.SkillFamilyFirstRuntimeId(
                selectedSkillKind);
        await using var command = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.character_skills AS cs
                JOIN public.gameplay_skill_combat_definitions AS st
                  ON st.skill_id = cs.skill_id
                WHERE cs.user_id = @characterId
                  AND cs.skill_id >= @firstSkillId
                  AND cs.skill_id <= @lastSkillId
                  AND st.revision = COALESCE(
                      @gameplayContentRevision,
                      (
                          SELECT publication.revision
                          FROM public.gameplay_content_publication publication
                          WHERE publication.family = 'gameplay'
                      )
                  )
                  AND @profession = ANY(st.class_ids)
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("firstSkillId", first);
        command.Parameters.AddWithValue("lastSkillId", first + 4);
        command.Parameters.AddWithValue(
            "profession",
            checked((short)profession));
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<long> ReadCurrentRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT COALESCE(MAX(aggregate_version), 0)
            FROM public.outbox_events
            WHERE consumer_key = @consumerKey
              AND aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "consumerKey",
            ZodiacSkillGridSelectionPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridSelectionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            ZodiacSkillGridSelectionPersistenceCodec.EventAggregateKey(
                characterId,
                gridIndex));
        var revision = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
        return revision >= 0
            ? revision
            : throw new InvalidDataException(
                "The Zodiac selection revision is invalid.");
    }

    private static ZodiacSkillGridSelectionResult DeriveSelection(
        byte profession,
        StoredGrid[] row,
        int gridIndex,
        int selectedSkillKind,
        bool learned)
    {
        var character = new GameCharacter
        {
            Profession = profession,
            ZodiacSkillGridLevels =
                ZodiacSkillGridCatalog.CreateEmptyLevels(),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridCatalog.CreateEmptySkillIds()
        };
        var rowStart =
            ZodiacSkillGridSelectionCatalog.RowStart(gridIndex);
        for (var offset = 0; offset < row.Length; offset++)
        {
            character.ZodiacSkillGridLevels[rowStart + offset] =
                row[offset].Level;
            character.ZodiacSkillGridSkillIds[rowStart + offset] =
                row[offset].SelectedSkillKind;
        }

        return ZodiacSkillGridSelection.Apply(
            character,
            gridIndex,
            selectedSkillKind,
            learned);
    }

    private async Task UpdateGridAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        ZodiacSkillGridSelectionResult selection,
        CancellationToken cancellationToken)
    {
        if (!selection.Committed ||
            selection.CurrentLevel == 0 ||
            selection.PreviousSkillKind == selection.SelectedSkillKind)
        {
            throw new InvalidDataException(
                "The Zodiac selection transition is invalid.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_zodiac_skill_grids
            SET selected_skill_id = @selectedSkillKind,
                updated_at = now()
            WHERE user_id = @characterId
              AND grid_index = @gridIndex
              AND level = @currentLevel
              AND selected_skill_id = @previousSkillKind;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)selection.GridIndex));
        command.Parameters.AddWithValue(
            "currentLevel",
            checked((short)selection.CurrentLevel));
        command.Parameters.AddWithValue(
            "previousSkillKind",
            selection.PreviousSkillKind);
        command.Parameters.AddWithValue(
            "selectedSkillKind",
            selection.SelectedSkillKind);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac selection did not mutate exactly once.");
        }
    }
}
