using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal enum HolyStoneDrillEligibilityFailure : byte
{
    None = 0,
    SocketPrerequisite = 1,
    ItemLevel = 2,
    SocketSpell = 3,
    FourthSocketEquipment = 4,
    MaximumSockets = 5
}

/// <summary>
/// Server-authoritative progression rules for basic and advanced equipment
/// drilling. Item level is the immutable template PlayLv/MinLevel value; the
/// compact item supplies its current sockets, ware, quality, and grade.
/// </summary>
internal static class HolyStoneDrillEligibilityPolicy
{
    public const uint SocketSpellThreeItemId = 4272;
    public const uint SocketSpellFourItemId = 4273;
    public const short MaximumSockets = 4;
    public const int FirstSocketMinimumItemLevel = 100;
    public const int SecondSocketMinimumItemLevel = 120;
    public const int AdvancedSocketMinimumItemLevel = 140;
    public const short OrichalcumWareType = 6;
    public const short ArcaneQuality = 15;
    public const short FourthSocketMinimumGrade = 20;

    public static HolyStoneDrillEligibilityFailure ValidateBasic(
        ItemTemplateDefinition template,
        CompactItemEntry equipment)
    {
        ArgumentNullException.ThrowIfNull(template);
        var minimumLevel = equipment.SocketCount switch
        {
            0 => FirstSocketMinimumItemLevel,
            1 => SecondSocketMinimumItemLevel,
            _ => 0
        };
        if (minimumLevel == 0)
        {
            return HolyStoneDrillEligibilityFailure.MaximumSockets;
        }

        return template.MinLevel is { } itemLevel &&
            itemLevel >= minimumLevel
                ? HolyStoneDrillEligibilityFailure.None
                : HolyStoneDrillEligibilityFailure.ItemLevel;
    }

    public static HolyStoneDrillEligibilityFailure ValidateAdvanced(
        ItemTemplateDefinition template,
        CompactItemEntry equipment,
        CompactItemEntry socketSpell)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (equipment.SocketCount >= MaximumSockets)
        {
            return HolyStoneDrillEligibilityFailure.MaximumSockets;
        }
        if (equipment.SocketCount < 2)
        {
            return HolyStoneDrillEligibilityFailure.SocketPrerequisite;
        }
        if (template.MinLevel is not { } itemLevel ||
            itemLevel < AdvancedSocketMinimumItemLevel)
        {
            return HolyStoneDrillEligibilityFailure.ItemLevel;
        }

        var expectedSpell = equipment.SocketCount == 2
            ? SocketSpellThreeItemId
            : SocketSpellFourItemId;
        if (socketSpell.Id != expectedSpell || socketSpell.Stack <= 0)
        {
            return HolyStoneDrillEligibilityFailure.SocketSpell;
        }

        var hasRequiredWare =
            equipment.HolySuitType > OrichalcumWareType ||
            equipment.HolySuitType == OrichalcumWareType &&
            equipment.HolySuitLevel >= 1;
        if (equipment.SocketCount == 3 &&
            (!hasRequiredWare ||
             equipment.Quality < ArcaneQuality ||
             equipment.Grade < FourthSocketMinimumGrade))
        {
            return HolyStoneDrillEligibilityFailure.FourthSocketEquipment;
        }

        return HolyStoneDrillEligibilityFailure.None;
    }
}
