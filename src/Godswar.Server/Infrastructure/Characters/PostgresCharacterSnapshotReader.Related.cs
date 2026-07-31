using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    private static async Task<CharacterRelatedReadResult> ReadRelatedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            RelatedQuery,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("readAt", readAtUtc);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var stats = await ReadCalculatedStatsAsync(
            reader,
            cancellationToken);
        await RequireNextResultAsync(
            reader,
            "character skills",
            cancellationToken);
        var skills = await ReadSkillsAsync(reader, cancellationToken);
        await RequireNextResultAsync(
            reader,
            "character talents",
            cancellationToken);
        var talents = await ReadTalentsAsync(reader, cancellationToken);
        await RequireNextResultAsync(
            reader,
            "personal progression boosts",
            cancellationToken);
        var boosts = await ReadBoostsAsync(reader, cancellationToken);
        await RequireNextResultAsync(
            reader,
            "owned pets",
            cancellationToken);
        var pets = await ReadOwnedPetSnapshotsAsync(
            reader,
            accountId,
            cancellationToken);
        return new CharacterRelatedReadResult(
            stats,
            skills,
            talents,
            pets,
            boosts);
    }

    private static async Task RequireNextResultAsync(
        NpgsqlDataReader reader,
        string expected,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidDataException(
                $"Character snapshot did not return {expected}.");
        }
    }

    private sealed record CharacterRelatedReadResult(
        CharacterCalculatedStatsSnapshot CalculatedStats,
        ImmutableArray<CharacterSkillSnapshot> Skills,
        ImmutableArray<CharacterTalentSnapshot> Talents,
        ImmutableArray<CharacterPetSnapshot> Pets,
        ImmutableArray<CharacterProgressionBoostSnapshot> PersonalBoosts);

    private const string RelatedQuery =
        CalculatedStatsQuery +
        SkillsQuery +
        TalentsQuery +
        PersonalBoostsQuery +
        PetsQuery;
}
