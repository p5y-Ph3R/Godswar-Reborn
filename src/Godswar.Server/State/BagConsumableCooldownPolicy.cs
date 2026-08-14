using System.Collections.Frozen;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct BagConsumableCooldownRule(
    int Group,
    TimeSpan Duration);

/// <summary>
/// Stock bag-consumable cooldown metadata transcribed from the reviewed
/// ItemBaseAttribute.xml Skill links and Magic.ini CoolingTime values.
/// </summary>
internal static class BagConsumableCooldownPolicy
{
    // Source SHA-256:
    // ItemBaseAttribute.xml F6BFC99191134B79E0EEAF5A56C9AB14EF0C58E23C4574F03C3AD9C13BCE1366
    // Magic.ini 08AEC7E0453C24ECD3C8C697AFAFA85F6DD812E83C91EC2F145B2A7A73F7C0AF
    private static readonly FrozenSet<int> OneSecondGroups =
        new int[]
        {
            3000, 3001, 3002, 3003, 3004, 3005, 3025, 3026, 3027, 3028,
            3029, 3030, 3031, 3032, 3033, 3034, 3035, 3036, 3037, 3038,
            3039, 3050, 3100, 3101, 3102, 3103, 3104, 3105, 3106, 3107,
            3108, 3120, 3121, 3122, 3123, 3124, 3125, 3126, 3127, 3128,
            4720, 4721, 4730, 4731, 4732, 4733, 4734, 4735, 4740, 4741,
            4742, 4743, 4744, 4745, 4746, 4747, 4748, 4750, 4751, 4752,
            4753, 4754, 4755, 4756, 4757, 4758, 4759, 4760, 4761, 4762,
            4763, 4764, 4765, 4798, 4799, 4825, 4866, 4867, 4868, 4869,
            4870, 4871, 5106, 5107, 5108, 5109, 5110, 5111, 5112, 5113,
            5114, 5115, 5116, 5117, 5118, 5119, 5120, 5121, 5122, 5123,
            5124, 5125, 5126, 5127, 5128, 5129, 5130, 5134, 5135, 5136,
            5137, 5138, 5139, 5140, 5149, 5150, 5160, 5161, 5162, 5163,
            5164, 5165, 5200, 5201, 5202, 5203, 5204, 5205, 5206, 5207,
            5208, 5209, 5210, 5211, 5212, 5213, 5214, 5215, 5216, 5217,
            5218, 5219, 5220, 5221, 5300, 5301, 5302, 5303, 5400, 5401,
            5402, 5403, 5404, 5405, 5406, 5407, 5408, 5409, 5410, 5411,
            5412, 5500, 5501, 5502, 5503, 5504, 5505, 5506, 5510, 5511,
            5512, 5513, 5514, 5515, 5516, 5517, 5518, 5519, 5520, 5521,
            5522, 5523, 5524, 5525, 5526, 5527, 5528, 5529, 5530, 5531,
            5532, 5533, 5534, 5535, 5536, 5537, 5538, 5539, 5540, 5541,
            5542, 5543, 5544, 5545, 5546, 5547, 5548, 5549, 5550, 5551,
            5552, 5553, 5554, 5560, 5561, 5562, 5563, 5564, 5565, 5566,
            5567, 5568, 5569, 5600, 5601, 5602, 5603, 5604, 5605, 5606,
            5607, 5608, 5609, 5610, 5611, 5612, 5613, 5622, 5626, 5627,
            5628, 5629, 5630, 5631
        }.ToFrozenSet();

    private static readonly FrozenSet<int> TwoSecondGroups =
        new int[]
        {
            4501, 4502, 4511, 4521, 4531, 4600, 4601, 4602, 4603, 4610,
            4611, 4612, 4613, 4614, 4616, 4617, 4620, 4621, 4622, 4623,
            4624, 4625, 4626, 4627, 4628, 4629, 4630, 4631, 4632, 4633,
            4640, 4641, 4642, 4643, 4650, 4660, 4670, 4680, 4695, 4700,
            4710, 4800, 4801, 4802, 4803, 4804, 4805, 4806, 4807, 4808,
            4809, 4810, 4811, 4812, 4813, 4820, 4821, 4822, 4823, 4824,
            4826, 4850, 4851, 4852, 4853, 4854, 4855
        }.ToFrozenSet();

    public static bool TryResolve(
        IItemTemplateCatalog templates,
        uint itemId,
        out BagConsumableCooldownRule rule)
    {
        ArgumentNullException.ThrowIfNull(templates);
        rule = default;
        if (!templates.TryGet(itemId, out var template) ||
            !string.Equals(
                template.Kind,
                "consume item",
                StringComparison.Ordinal))
        {
            return false;
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        if (!TryReadInt32(root, "Use", out var use) || use != 1 ||
            !TryReadInt32(root, "Skill", out var group) || group <= 0)
        {
            return false;
        }

        var seconds = OneSecondGroups.Contains(group)
            ? 1
            : TwoSecondGroups.Contains(group)
                ? 2
                : 0;
        if (seconds == 0)
        {
            // Stock CoolingTime=0 or a missing Magic.ini timing means that
            // the stock client has no cooldown clock to enforce.
            return false;
        }

        rule = new(group, TimeSpan.FromSeconds(seconds));
        return true;
    }

    private static bool TryReadInt32(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }
}
