using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private const int GoddessStoneItemId = 9050;

    private HolyStonePlan PlanImplementSpirit(
        HolyStoneCommandContext context,
        CompactItemEntry holyStone,
        CompactItemEntry spirit,
        CompactItemEntry goddessStone)
    {
        if (holyStone.Stack != 1 ||
            holyStone.Id is not (
                HolyStoneUpgradePolicy.HeatedHolyStoneItemId or
                HolyStoneUpgradePolicy.CooledHolyStoneItemId) ||
            holyStone.Grade is
                < HolySpiritEffectivenessPolicy.MinimumHolyStoneGrade or
                > HolySpiritEffectivenessPolicy.MaximumHolyStoneGrade)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.TargetNotHolyStone,
                holyStone,
                spirit);
        }
        if (spirit.Stack <= 0 ||
            !HolySpiritEffectivenessPolicy.TryGetDefinition(
                spirit.Id,
                out var definition))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneNotHolyStone,
                holyStone,
                spirit);
        }
        if (!HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(
                spirit.Id,
                holyStone.Id))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.IncompatibleTarget,
                holyStone,
                spirit);
        }

        var usesGoddessStone = !goddessStone.IsEmpty;
        if (usesGoddessStone &&
            (goddessStone.Id != GoddessStoneItemId ||
             goddessStone.Stack <= 0))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.CatalystMissing,
                holyStone,
                spirit);
        }

        var roll = HolySpiritEffectivenessPolicy.Roll(
            spirit.Id,
            holyStone.Grade,
            usesGoddessStone,
            _holySpiritRandomSource);
        if (roll.Definition.EffectId != definition.EffectId ||
            roll.Value is <= 0 or > short.MaxValue)
        {
            throw new InvalidDataException(
                "The Holy Spirit effectiveness roll is invalid.");
        }

        var implemented = holyStone with
        {
            SocketCount = 1,
            Socket1EffectId = definition.EffectId,
            Socket1Level = holyStone.Grade,
            Socket1Value = checked((short)roll.Value),
            Socket2EffectId = null,
            Socket2Level = null,
            Socket2Value = null,
            Socket3EffectId = null,
            Socket3Level = null,
            Socket3Value = null,
            Socket4EffectId = null,
            Socket4Level = null,
            Socket4Value = null,
            Socket5EffectId = null,
            Socket5Level = null,
            Socket6EffectId = null,
            Socket6Level = null
        };
        return new HolyStonePlan(
            HolyStoneCommandResultStatus.SpiritImplemented,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            implemented,
            ConsumeOne(spirit),
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0)
        {
            CatalystAfter = usesGoddessStone
                ? ConsumeOne(goddessStone)
                : CompactItemEntry.Empty
        };
    }

    private static HolyStonePlan PlanMount(
        HolyStoneCommandContext context,
        ItemTemplateDefinition targetTemplate,
        CompactItemEntry target,
        CompactItemEntry stone)
    {
        if (stone.Id is not (
                HolyStoneUpgradePolicy.HeatedHolyStoneItemId or
                HolyStoneUpgradePolicy.CooledHolyStoneItemId) ||
            stone.Stack != 1)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneNotHolyStone,
                target,
                stone);
        }
        if (!TryReadImplementedStone(
                stone,
                out var effectId,
                out var level,
                out var effectiveness))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneMissingSpirit,
                target,
                stone);
        }
        if (targetTemplate.MinLevel is null or < 100 ||
            !HolyStoneEquipmentEligibility.IsCompatibleWithHolyStone(
                targetTemplate.EquipmentSlot,
                stone.Id))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.IncompatibleTarget,
                target,
                stone);
        }
        if (HasSocketEffect(target, effectId))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.DuplicateSpirit,
                target,
                stone);
        }
        if (target.SocketCount <= 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketNotDrilled,
                target,
                stone);
        }

        var socketIndex = FindFirstEmptyOpenedSocket(target);
        if (socketIndex < 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketCapacityReached,
                target,
                stone);
        }

        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Mounted,
            socketIndex,
            SetSocket(
                target,
                socketIndex,
                effectId,
                level,
                effectiveness),
            CompactItemEntry.Empty,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0);
    }

    private static bool TryReadImplementedStone(
        CompactItemEntry stone,
        out short effectId,
        out short level,
        out short effectiveness)
    {
        effectId = 0;
        level = 0;
        effectiveness = 0;
        if (stone.SocketCount != 1 ||
            stone.Socket1EffectId is not { } storedEffect ||
            stone.Socket1Level is not { } storedLevel ||
            storedLevel != stone.Grade ||
            !TryResolveDefinition(
                stone.Id,
                storedEffect,
                out var definition))
        {
            return false;
        }

        var storedValue = stone.Socket1Value;
        if (!storedValue.HasValue &&
            HolySpiritLegacyEffectiveness.TryResolve(
                storedEffect,
                storedLevel,
                out var legacyValue))
        {
            storedValue = legacyValue;
        }
        if (storedValue is not (> 0) ||
            !IsPermittedStoredValue(
                definition,
                storedLevel,
                storedValue.Value))
        {
            return false;
        }

        effectId = storedEffect;
        level = storedLevel;
        effectiveness = storedValue.Value;
        return true;
    }

    private static bool TryResolveDefinition(
        uint holyStoneId,
        short effectId,
        out HolySpiritDefinition definition)
    {
        definition = HolySpiritEffectivenessPolicy.All
            .SingleOrDefault(candidate =>
                candidate.EffectId == effectId);
        return definition.ItemId != 0 &&
            HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(
                definition.ItemId,
                holyStoneId);
    }

    private static bool IsPermittedStoredValue(
        HolySpiritDefinition definition,
        short grade,
        short value)
    {
        if (HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                definition.ItemId,
                grade,
                out var minimum,
                out var maximum) &&
            value >= minimum &&
            value <= maximum)
        {
            return true;
        }

        return HolySpiritLegacyEffectiveness.TryResolve(
                   definition.EffectId,
                   grade,
                   out var legacyValue) &&
               value == legacyValue;
    }

    private static CompactItemEntry ConsumeOne(
        CompactItemEntry material) =>
        material.Stack == 1
            ? CompactItemEntry.Empty
            : material with
            {
                Stack = checked((short)(material.Stack - 1))
            };

    private static uint ResolveHolyStoneItemId(short effectId)
    {
        var definition = HolySpiritEffectivenessPolicy.All
            .SingleOrDefault(candidate => candidate.EffectId == effectId);
        if (definition.ItemId == 0)
        {
            throw new InvalidDataException(
                "The mounted Holy Spirit effect is unknown.");
        }

        if (!HolyStoneAffinityCatalog.TryGetItemId(
                definition.Affinity,
                out var holyStoneItemId))
        {
            throw new InvalidDataException(
                "The mounted Holy Spirit affinity is unknown.");
        }

        return holyStoneItemId;
    }

    private static CompactItemEntry AlignImplementedStoneLevel(
        CompactItemEntry stone) =>
        stone.SocketCount == 1 &&
        stone.Socket1EffectId.HasValue &&
        stone.Socket1Value.HasValue
            ? stone with { Socket1Level = stone.Grade }
            : stone;
}
