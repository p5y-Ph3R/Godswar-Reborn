using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<LifecycleTransition>
        ExecuteCreateTransitionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<CharacterCreateCommand> envelope,
            LockedAccount account,
            CancellationToken cancellationToken)
    {
        var active = await ReadActiveCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            cancellationToken);
        if (active is not null)
        {
            return Rejected(
                CharacterLifecycleReceiptStatus.SlotOccupied,
                active.Value,
                account.LifecycleVersion);
        }

        var command = envelope.Command;
        var nextVersion = checked(account.LifecycleVersion + 1);
        var characterId = await InsertCharacterBaseAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            nextVersion,
            command,
            cancellationToken);
        if (!characterId.HasValue)
        {
            return new LifecycleTransition(
                CharacterLifecycleReceiptStatus.NameUnavailable,
                0,
                account.LifecycleVersion,
                command.Name,
                null,
                null);
        }

        await SeedStarterItemsAsync(
            connection,
            transaction,
            characterId.Value,
            command.Profession,
            cancellationToken);
        await SeedCreationEconomyBaselineAsync(
            connection,
            transaction,
            characterId.Value,
            envelope.Subject.AccountId,
            cancellationToken);
        await SeedStarterSkillsAsync(
            connection,
            transaction,
            characterId.Value,
            command.Profession,
            cancellationToken);
        await AdvanceAccountVersionAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            account.LifecycleVersion,
            nextVersion,
            cancellationToken);

        return new LifecycleTransition(
            CharacterLifecycleReceiptStatus.Created,
            characterId.Value,
            nextVersion,
            command.Name,
            null,
            null);
    }

    private async Task<int?> InsertCharacterBaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        long lifecycleVersion,
        CharacterCreateCommand intent,
        CancellationToken cancellationToken)
    {
        var currentMap = intent.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name,
                gender,
                "GM",
                camp,
                profession,
                fighter_job_lv,
                scholar_job_lv,
                fighter_job_exp,
                scholar_job_exp,
                "curHP",
                "curMP",
                status,
                belief,
                zodiac_type,
                prestige,
                earl_rank,
                consortia,
                consortia_job,
                consortia_contribute,
                store_num,
                bag_num,
                hair_style,
                face_shap,
                "Map",
                "Pos_X",
                "Pos_Z",
                "Money",
                "Stone",
                "SkillPoint",
                "SkillExp",
                holy_suit_points,
                "MaxHP",
                "MaxMP",
                "Register_time",
                "LastLogin_time",
                mutetime,
                character_slot,
                lifecycle_state,
                lifecycle_version
            )
            VALUES (
                @accountId,
                @realmId,
                @name,
                @gender,
                0,
                @camp,
                @profession,
                1,
                0,
                0,
                0,
                1500,
                177,
                0,
                @faith,
                @zodiacType,
                0,
                0,
                0,
                0,
                0,
                10,
                1,
                @hair,
                @face,
                @currentMap,
                @positionX,
                @positionZ,
                10000,
                10,
                10,
                0,
                0,
                1500,
                177,
                transaction_timestamp(),
                transaction_timestamp(),
                0,
                @characterSlot,
                'active',
                @lifecycleVersion
            )
            ON CONFLICT (name) DO NOTHING
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue(
            "realmId",
            RealmId.Tempest.Value);
        command.Parameters.AddWithValue("name", intent.Name);
        command.Parameters.AddWithValue(
            "gender",
            intent.Gender == 0 ? "female" : "male");
        command.Parameters.AddWithValue(
            "camp",
            checked((short)(intent.Camp == GameDefaults.SpartaCamp
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp)));
        command.Parameters.AddWithValue(
            "profession",
            checked((short)intent.Profession));
        command.Parameters.AddWithValue(
            "faith",
            checked((short)intent.Faith));
        command.Parameters.AddWithValue(
            "zodiacType",
            checked((short)intent.ZodiacType));
        command.Parameters.AddWithValue(
            "hair",
            checked((short)intent.Hair));
        command.Parameters.AddWithValue(
            "face",
            checked((short)intent.Face));
        command.Parameters.AddWithValue(
            "currentMap",
            checked((short)currentMap));
        command.Parameters.AddWithValue(
            "positionX",
            GameDefaults.StartingPositionX);
        command.Parameters.AddWithValue(
            "positionZ",
            GameDefaults.StartingPositionZ);
        command.Parameters.AddWithValue(
            "characterSlot",
            intent.CharacterSlot);
        command.Parameters.AddWithValue(
            "lifecycleVersion",
            lifecycleVersion);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull
            ? null
            : Convert.ToInt32(scalar);
    }

    private async Task SeedStarterSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        byte profession,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_skills (
                user_id,
                skill_id,
                skill_level,
                source
            )
            SELECT
                @characterId,
                template.skill_id,
                template.skill_level,
                'starter'
            FROM public.skill_templates template
            WHERE @profession = ANY(template.class_ids)
              AND template.previous_skill_id IS NULL
              AND COALESCE(template.min_level, 1) <= 1
              AND template.skill_level = 1
            ON CONFLICT (user_id, skill_id) DO NOTHING;

            INSERT INTO public.character_skills (
                user_id,
                skill_id,
                skill_level,
                source
            )
            SELECT @characterId, template.skill_id, 1, 'mount-compatibility'
            FROM public.skill_templates template
            WHERE template.skill_id = 4904
              AND @profession = ANY(template.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "profession",
            checked((short)profession));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
