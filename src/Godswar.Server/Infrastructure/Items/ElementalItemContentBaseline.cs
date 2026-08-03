using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

internal static class ElementalItemContentBaseline
{
    public static IReadOnlyList<ItemAttributeDefinition> Attributes { get; } =
        ElementalAttributeCatalog.All.Select(CreateAttribute).ToArray();

    private static ItemAttributeDefinition CreateAttribute(
        ElementalAttributeDefinition definition)
    {
        var distributionStart = checked((short)(
            391 + ((definition.AttributeId - 480) * 2)));
        var distribution = new[]
        {
            distributionStart,
            checked((short)(distributionStart + 1))
        };
        var values = Enumerable.Range(1, 25)
            .Select(grade => DecimalValue(definition, checked((short)grade)))
            .ToArray();
        var nameKey = $"{definition.Element}{definition.Family}Per";
        var stats = new Dictionary<string, string>
        {
            ["Type"] = ((short)(29 + (int)definition.Family)).ToString(
                CultureInfo.InvariantCulture),
            ["ID"] = definition.AttributeId.ToString(
                CultureInfo.InvariantCulture),
            ["Distribution"] = string.Join(',', distribution),
            ["Flag"] = "1"
        };
        for (var index = 0; index < values.Length; index++)
        {
            stats[$"L{index + 1}"] = values[index].ToString(
                "0.####",
                CultureInfo.InvariantCulture);
        }

        return new ItemAttributeDefinition(
            definition.AttributeId,
            nameKey,
            checked((short)(29 + (int)definition.Family)),
            distribution,
            Percent: true,
            MaxLevel: 25,
            LevelValues: '{' + string.Join(
                ',',
                values.Select(static value => value.ToString(
                    "0.####",
                    CultureInfo.InvariantCulture))) + '}',
            StatsJson: JsonSerializer.Serialize(stats));
    }

    private static decimal DecimalValue(
        ElementalAttributeDefinition definition,
        short grade) =>
        definition.ValueAtGrade(grade) / 10_000m;
}
