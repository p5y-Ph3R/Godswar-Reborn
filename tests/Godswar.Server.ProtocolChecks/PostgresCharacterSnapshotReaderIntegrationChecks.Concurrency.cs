using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task AssertRepeatableReadConsistencyAsync(
        string connectionString,
        PostgresGameStore store,
        NpgsqlDataSource dataSource,
        ICollection<SnapshotFixture> fixtures,
        string token)
    {
        var fixture = await CreateRichFixtureAsync(
            store,
            dataSource,
            $"snap_rr_{token}",
            $"SnapRepeat{token}");
        fixtures.Add(fixture);
        var probe = new BlockingSnapshotProbe();
        await using var reader =
            new PostgresCharacterSnapshotReader(connectionString, probe);

        var oldRead = reader.ReadAsync(fixture.AccountId);
        await probe.WaitUntilReachedAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await CommitCoordinatedNewStateAsync(dataSource, fixture);
        }
        finally
        {
            probe.Release();
        }

        var oldSnapshot =
            (await oldRead).Character ??
            throw new InvalidOperationException(
                "Coordinated old snapshot returned no character.");
        AssertCoordinatedState(
            oldSnapshot,
            talentPoints: 111,
            talentRank: 3,
            boostBasisPoints: 1_000,
            petExperience: 100,
            petAddedSavvy: 10,
            "repeatable-read snapshot is entirely old");

        var newSnapshot =
            (await reader.ReadAsync(fixture.AccountId)).Character ??
            throw new InvalidOperationException(
                "Coordinated new snapshot returned no character.");
        AssertCoordinatedState(
            newSnapshot,
            talentPoints: 222,
            talentRank: 7,
            boostBasisPoints: 2_000,
            petExperience: 200,
            petAddedSavvy: 20,
            "subsequent snapshot is entirely new");
    }

    private static async Task CommitCoordinatedNewStateAsync(
        NpgsqlDataSource dataSource,
        SnapshotFixture fixture)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "SkillPoint" = 222
            WHERE id = @characterId
              AND account_id = @accountId
              AND "SkillPoint" = 111;

            UPDATE public.character_talents
            SET rank = 7,
                updated_at = now()
            WHERE user_id = @characterId
              AND talent_id = @talentId
              AND rank = 3;

            UPDATE public.character_experience_modifiers
            SET bonus_basis_points = 2000
            WHERE character_id = @characterId
              AND kind = 1001
              AND bonus_basis_points = 1000;

            UPDATE public.character_pets
            SET experience = 200,
                revision = revision + 1,
                updated_at = now()
            WHERE id = @petId
              AND user_id = @characterId
              AND experience = 100;

            UPDATE public.character_pet_stat_values
            SET added_savvy = 20,
                rarity_added_savvy = 20,
                revision = revision + 1
            WHERE pet_id = @petId
              AND stat_code = 1
              AND added_savvy = 10;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "talentId",
            fixture.TalentId ??
            throw new InvalidOperationException(
                "Concurrency fixture is missing its talent."));
        command.Parameters.AddWithValue(
            "petId",
            fixture.PetId ??
            throw new InvalidOperationException(
                "Concurrency fixture is missing its pet."));
        Check.Equal(
            5,
            await command.ExecuteNonQueryAsync(),
            "coordinated writer changes one row in each state family");
        await transaction.CommitAsync();
    }

    private static void AssertCoordinatedState(
        CharacterLoadSnapshot snapshot,
        int talentPoints,
        int talentRank,
        int boostBasisPoints,
        long petExperience,
        decimal petAddedSavvy,
        string description)
    {
        Check.Equal(
            talentPoints,
            snapshot.Progression.TalentPoints,
            $"{description}: core");
        var talent = snapshot.Talents.Single(
            candidate => candidate.Rank == talentRank);
        Check.Equal(
            talentRank,
            talent.Rank,
            $"{description}: talent");
        Check.Equal(
            boostBasisPoints,
            snapshot.PersonalBoosts.Single().BonusBasisPoints,
            $"{description}: personal boost");
        var pet = snapshot.Pets.Single();
        Check.Equal(
            petExperience,
            pet.Experience,
            $"{description}: pet");
        Check.Equal(
            petAddedSavvy,
            pet.StatValues.Single(value => value.StatCode == 1).AddedSavvy,
            $"{description}: pet stat child");
    }

    private sealed class BlockingSnapshotProbe :
        IPostgresCharacterSnapshotReadProbe
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilReachedAsync() => _reached.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask ReachedAsync(
            PostgresCharacterSnapshotReadStage stage,
            CancellationToken cancellationToken)
        {
            Check.Equal(
                (int)PostgresCharacterSnapshotReadStage.CoreLoaded,
                (int)stage,
                "snapshot consistency probe pauses after the core read");
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
