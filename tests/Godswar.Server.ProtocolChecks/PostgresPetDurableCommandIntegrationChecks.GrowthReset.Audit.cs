using System.Text.Json;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPhoenixPreviewAuditAsync(
        NpgsqlDataSource dataSource,
        Guid requestId,
        PetGrowthPreviewSnapshot preview)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT after_state::text
            FROM public.pet_operation_audit
            WHERE request_id = @requestId
              AND reason_code = 'phoenix_growth_preview';
            """);
        command.Parameters.AddWithValue("requestId", requestId);
        var json = await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "The Phoenix preview audit is missing.");
        using var document = JsonDocument.Parse(json);
        var state = document.RootElement;
        var nature = ReadDecimalArray(state, "nature_growth_rates");
        var modifiers = ReadDecimalArray(state, "rebirth_modifiers");
        var effective = ReadDecimalArray(state, "effective_growth_rates");
        var expectedNature = preview.ToOrderedRates();
        var expectedModifiers = preview.ToOrderedRebirthModifiers();
        Check.True(
            state.GetProperty("rate_semantics").GetString() ==
                "nature_base_rebirth_modifier_v1" &&
            state.GetProperty("completed_rebirths").GetInt32() == 5 &&
            state.GetProperty("rebirth_modifier_min").GetDecimal() == .50m &&
            state.GetProperty("rebirth_modifier_max").GetDecimal() == 1m &&
            nature.SequenceEqual(expectedNature) &&
            modifiers.SequenceEqual(expectedModifiers) &&
            effective.SequenceEqual(expectedNature.Zip(
                expectedModifiers,
                static (rate, modifier) => rate + modifier)),
            "Phoenix audit records nature, count bounds, modifier draws, and effective rates");
    }

    private static decimal[] ReadDecimalArray(
        JsonElement state,
        string propertyName) =>
        state.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static value => value.GetDecimal())
            .ToArray();
}
