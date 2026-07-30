using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterCheckpointIntegrationChecks
{
    private static async Task AssertReorderedWritesAsync(
        PostgresCharacterCheckpointStore store,
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture,
        CharacterCheckpointOwnership ownership)
    {
        var position4 = new CharacterPositionCheckpoint(
            fixture.AccountId,
            fixture.CharacterId,
            ownership.Owner,
            CurrentMap: 9,
            PositionX: 104f,
            PositionZ: -104f,
            Revision: 4);
        var position5 = position4 with
        {
            PositionX = 105f,
            PositionZ = -105f,
            Revision = 5
        };
        var positionWrites = await Task.WhenAll(
            store.WritePositionAsync(position5),
            store.WritePositionAsync(position4));
        Check.True(
            positionWrites.Any(static result =>
                result.Status ==
                    CharacterCheckpointWriteStatus.Applied &&
                result.StoredRevision == 5),
            "concurrent position writes apply newest revision");
        Check.True(
            positionWrites[0].Satisfies(5) &&
            positionWrites[1].Satisfies(4),
            "concurrent position writes are applied or superseded");

        var vitals4 = new CharacterVitalsCheckpoint(
            fixture.AccountId,
            fixture.CharacterId,
            ownership.Owner,
            CurrentHp: 404,
            CurrentMp: 44,
            Revision: 4);
        var vitals5 = vitals4 with
        {
            CurrentHp = 505,
            CurrentMp = 55,
            Revision = 5
        };
        var vitalsWrites = await Task.WhenAll(
            store.WriteVitalsAsync(vitals5),
            store.WriteVitalsAsync(vitals4));
        Check.True(
            vitalsWrites.Any(static result =>
                result.Status ==
                    CharacterCheckpointWriteStatus.Applied &&
                result.StoredRevision == 5),
            "concurrent vitals writes apply newest revision");
        Check.True(
            vitalsWrites[0].Satisfies(5) &&
            vitalsWrites[1].Satisfies(4),
            "concurrent vitals writes are applied or superseded");

        var state = await ReadStateAsync(dataSource, fixture);
        Check.Equal(9, (int)state.MapId, "newest concurrent map wins");
        Check.Equal(105f, state.PositionX, "newest concurrent X wins");
        Check.Equal(-105f, state.PositionZ, "newest concurrent Z wins");
        Check.Equal(
            5L,
            state.PositionRevision,
            "newest position revision is durable");
        Check.Equal(505, state.CurrentHp, "newest concurrent HP wins");
        Check.Equal(55, state.CurrentMp, "newest concurrent MP wins");
        Check.Equal(
            5L,
            state.VitalsRevision,
            "newest vitals revision is durable");
    }
}
