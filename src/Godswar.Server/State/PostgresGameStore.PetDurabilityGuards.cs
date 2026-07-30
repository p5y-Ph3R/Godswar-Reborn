using System.Data.Common;

namespace Godswar.Server.State;

internal sealed class PetDurableStreamActiveException :
    InvalidOperationException
{
    public PetDurableStreamActiveException(int characterId)
        : base(
            $"Character {characterId} has durable pet command evidence; " +
            "raw PostgreSQL pet mutations are disabled.")
    {
    }
}

internal sealed partial class PostgresGameStore
{
    private static async Task EnsureRawPetMutationAllowedAsync(
        DbConnection connection,
        DbTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.pet_durable_stream_versions stream
                WHERE stream.character_id = @characterId
            )
            OR EXISTS (
                SELECT 1
                FROM public.command_inbox inbox
                WHERE inbox.aggregate_type =
                          'character_pet_value'
                  AND inbox.aggregate_key =
                          'character:' || @characterId::text
                  AND inbox.command_family IN (
                      'bag_item_activation',
                      'pet_level_upgrade',
                      'pet_presence_transition'
                  )
            );
            """;
        var characterIdParameter = command.CreateParameter();
        characterIdParameter.ParameterName = "characterId";
        characterIdParameter.Value = characterId;
        command.Parameters.Add(characterIdParameter);
        if (await command.ExecuteScalarAsync(cancellationToken) is true)
        {
            throw new PetDurableStreamActiveException(characterId);
        }
    }
}
