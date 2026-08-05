using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private const int MaximumUsableSockets = 4;
    private const int HeatedHolyStoneItemId = 9030;

    private async Task<HolyStonePlan> CreatePlanAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            HolyStoneCommandContext context,
            LockedCharacter character,
            LockedCommandItems locked,
        CancellationToken cancellationToken)
    {
        var target = locked.Target;
        var stone = locked.Stone;
        var targetState =
            target?.Item.ToCompactString() ?? "[]";
        var stoneState =
            stone?.Item.ToCompactString() ?? "[]";
        var catalyst = locked.Catalyst;
        var catalystState =
            catalyst?.Item.ToCompactString() ?? "[]";
        var thirdMaterial = locked.ThirdMaterial;
        var thirdMaterialState =
            thirdMaterial?.Item.ToCompactString() ?? "[]";
        if (target is null)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.TargetMissing,
                CompactItemEntry.Empty,
                CompactItemEntry.Empty);
        }
        if (!string.Equals(
                context.Command.ExpectedTargetCompactItemState,
                targetState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StaleTarget,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }
        var consumesSourceMaterial =
            context.Command.Operation is
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.AdvancedDrill or
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit;
        if (consumesSourceMaterial &&
            stone is null)
        {
            return Rejected(
                context,
                context.Command.Operation switch
                {
                    HolyStoneCommandOperation.Combine =>
                        HolyStoneCommandResultStatus
                            .CombinationSelectionRequired,
                    HolyStoneCommandOperation.Upgrade =>
                        MissingUpgradeMaterialStatus(target.Item),
                    _ => HolyStoneCommandResultStatus.StoneMissing
                },
                target.Item,
                CompactItemEntry.Empty);
        }
        if (consumesSourceMaterial &&
            !string.Equals(
                context.Command.ExpectedStoneCompactItemState,
                stoneState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StaleStone,
                target.Item,
                stone!.Item);
        }
        if ((context.Command.Operation is
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit) &&
            context.Command.CatalystKitBagSlot >= 0 &&
            catalyst is null)
        {
            return Rejected(
                context,
                context.Command.Operation ==
                    HolyStoneCommandOperation.Combine
                    ? HolyStoneCommandResultStatus
                        .CombinationSelectionRequired
                    : HolyStoneCommandResultStatus.CatalystMissing,
                target.Item,
                stone!.Item);
        }
        if ((context.Command.Operation is
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit) &&
            !string.Equals(
                context.Command.ExpectedCatalystCompactItemState,
                catalystState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                context.Command.Operation ==
                    HolyStoneCommandOperation.Combine
                    ? HolyStoneCommandResultStatus
                        .CombinationSelectionRequired
                    : HolyStoneCommandResultStatus.StaleCatalyst,
                target.Item,
                stone!.Item);
        }
        if (context.Command.Operation ==
                HolyStoneCommandOperation.Combine &&
            thirdMaterial is null)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.CombinationSelectionRequired,
                target.Item,
                stone!.Item);
        }
        if (context.Command.Operation ==
                HolyStoneCommandOperation.Combine &&
            !string.Equals(
                context.Command.ExpectedThirdMaterialCompactItemState,
                thirdMaterialState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.CombinationSelectionRequired,
                target.Item,
                stone!.Item);
        }

        if (context.Command.Operation == HolyStoneCommandOperation.Upgrade)
        {
            return PlanUpgrade(
                context,
                target.Item,
                stone!.Item,
                catalyst?.Item ?? CompactItemEntry.Empty);
        }

        if (context.Command.Operation == HolyStoneCommandOperation.Combine)
        {
            return PlanCombination(
                context,
                target.Item,
                stone!.Item,
                catalyst!.Item,
                thirdMaterial!.Item);
        }

        if (context.Command.Operation ==
            HolyStoneCommandOperation.ImplementSpirit)
        {
            return PlanImplementSpirit(
                context,
                target.Item,
                stone!.Item,
                catalyst?.Item ?? CompactItemEntry.Empty);
        }

        if (target.Item.Stack != 1)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.WrongSelection,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }
        var targetIsEligible =
            context.Command.Operation is
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.Remove or
                HolyStoneCommandOperation.Drill or
                HolyStoneCommandOperation.AdvancedDrill
                ? HolyStoneEquipmentEligibility.IsNormalCharacterGear(
                    _itemContent.Templates,
                    target.Item.Id)
                : HolyStoneEquipmentEligibility.IsWeapon(
                    _itemContent.Templates,
                    target.Item.Id);
        if (!targetIsEligible)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.TargetNotEquipment,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }

        if (!_itemContent.Templates.TryGet(
                target.Item.Id,
                out var targetTemplate))
        {
            throw new InvalidDataException(
                "The eligible Holy Stone target has no item template.");
        }

        ValidateSocketState(target.Item);
        return context.Command.Operation switch
        {
            HolyStoneCommandOperation.Mount =>
                PlanMount(
                    context,
                    targetTemplate,
                    target.Item,
                    stone!.Item),
            HolyStoneCommandOperation.Remove =>
                PlanRemove(context, target.Item, locked.KitBag),
            HolyStoneCommandOperation.Drill =>
                PlanDrill(
                    context,
                    targetTemplate,
                    target.Item,
                    character.Gold),
            HolyStoneCommandOperation.AdvancedDrill =>
                PlanAdvancedDrill(
                    context,
                    targetTemplate,
                    target.Item,
                    stone!.Item),
            _ => throw new InvalidDataException(
                "The Holy Stone operation is unsupported.")
        };
    }

    private static HolyStonePlan PlanRemove(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        IReadOnlyDictionary<short, LockedItem> kitBag)
    {
        var socketIndex = context.Command.SocketIndex;
        if (socketIndex < 0 ||
            socketIndex >= target.SocketCount ||
            socketIndex >= MaximumUsableSockets)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.InvalidSocket,
                target,
                CompactItemEntry.Empty);
        }

        var (effectId, level, value) = GetSocket(target, socketIndex);
        if (!effectId.HasValue || !level.HasValue)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketEmpty,
                target,
                CompactItemEntry.Empty);
        }

        var destination = FindFirstEmptyKitBagSlot(kitBag);
        if (destination < 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.BagFull,
                target,
                CompactItemEntry.Empty);
        }

        var restoredValue = value;
        if (!restoredValue.HasValue &&
            HolySpiritLegacyEffectiveness.TryResolve(
                effectId.Value,
                level.Value,
                out var legacyValue))
        {
            restoredValue = legacyValue;
        }
        if (!restoredValue.HasValue)
        {
            throw new InvalidDataException(
                "The legacy Holy Spirit effectiveness cannot be resolved.");
        }

        var output = CompactItemEntry.Empty with
        {
            Id = ResolveHolyStoneItemId(effectId.Value),
            Quality = 1,
            Grade = level.Value,
            Bound = 1,
            Stack = 1,
            SocketCount = 1,
            Socket1EffectId = effectId,
            Socket1Level = level,
            Socket1Value = restoredValue
        };
        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Removed,
            socketIndex,
            SetSocket(target, socketIndex, null, null, null),
            CompactItemEntry.Empty,
            destination,
            output,
            effectId,
            level,
            0);
    }

    private static HolyStonePlan PlanDrill(
        HolyStoneCommandContext context,
        Godswar.Server.Application.Items.ItemTemplateDefinition template,
        CompactItemEntry target,
        int gold)
    {
        var eligibility =
            HolyStoneDrillEligibilityPolicy.ValidateBasic(
                template,
                target);
        if (eligibility ==
            HolyStoneDrillEligibilityFailure.MaximumSockets)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.MaximumSockets,
                target,
                CompactItemEntry.Empty);
        }
        if (eligibility != HolyStoneDrillEligibilityFailure.None)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.DrillPrerequisite,
                target,
                CompactItemEntry.Empty);
        }

        if (!HolyStoneDrillCostPolicy.TryGetGoldCost(
                target.SocketCount,
                out var goldCost))
        {
            throw new InvalidDataException(
                "The basic Drill socket count is invalid.");
        }
        if (gold < goldCost)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.InsufficientFunds,
                target,
                CompactItemEntry.Empty);
        }

        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Drilled,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            target with
            {
                SocketCount = checked((short)(target.SocketCount + 1))
            },
            CompactItemEntry.Empty,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            goldCost);
    }

    private static HolyStonePlan PlanAdvancedDrill(
        HolyStoneCommandContext context,
        Godswar.Server.Application.Items.ItemTemplateDefinition template,
        CompactItemEntry target,
        CompactItemEntry socketSpell)
    {
        var eligibility =
            HolyStoneDrillEligibilityPolicy.ValidateAdvanced(
                template,
                target,
                socketSpell);
        var rejection = eligibility switch
        {
            HolyStoneDrillEligibilityFailure.None =>
                (HolyStoneCommandResultStatus?)null,
            HolyStoneDrillEligibilityFailure.MaximumSockets =>
                HolyStoneCommandResultStatus.MaximumSockets,
            HolyStoneDrillEligibilityFailure.SocketSpell =>
                HolyStoneCommandResultStatus.StoneNotHolyStone,
            _ => HolyStoneCommandResultStatus.DrillPrerequisite
        };
        if (rejection.HasValue)
        {
            return Rejected(
                context,
                rejection.Value,
                target,
                socketSpell);
        }

        var socketIndex = target.SocketCount;
        var spellAfter = socketSpell.Stack == 1
            ? CompactItemEntry.Empty
            : socketSpell with
            {
                Stack = checked((short)(socketSpell.Stack - 1))
            };
        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Drilled,
            socketIndex,
            target with
            {
                SocketCount = checked((short)(socketIndex + 1))
            },
            spellAfter,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0);
    }

    private static HolyStonePlan Rejected(
        HolyStoneCommandContext context,
        HolyStoneCommandResultStatus status,
        CompactItemEntry target,
        CompactItemEntry stone) =>
        new(
            status,
            context.Command.SocketIndex,
            target,
            stone,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0);

    private static int FindFirstEmptyKitBagSlot(
        IReadOnlyDictionary<short, LockedItem> kitBag)
    {
        for (short slot = 0;
             slot <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
             slot++)
        {
            if (!kitBag.ContainsKey(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    private static int FindFirstEmptyOpenedSocket(
        CompactItemEntry item)
    {
        for (var index = 0; index < item.SocketCount; index++)
        {
            if (!GetSocket(item, index).EffectId.HasValue)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool HasSocketEffect(
        CompactItemEntry item,
        short effectId)
    {
        for (var index = 0; index < item.SocketCount; index++)
        {
            if (GetSocket(item, index).EffectId == effectId)
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateSocketState(CompactItemEntry item)
    {
        if (item.SocketCount is < 0 or > MaximumUsableSockets)
        {
            throw new InvalidDataException(
                "The target weapon socket count is outside the " +
                "supported client contract.");
        }

        for (var index = 0; index < 6; index++)
        {
            var (effectId, level, value) = GetSocket(item, index);
            if (effectId.HasValue != level.HasValue ||
                value.HasValue && !effectId.HasValue ||
                value is <= 0 ||
                effectId is <= 0 ||
                level is < 1 or > 10 ||
                index >= item.SocketCount &&
                (effectId.HasValue || level.HasValue || value.HasValue))
            {
                throw new InvalidDataException(
                    "The target weapon has corrupt Holy Stone state.");
            }
        }
    }

    private static (short? EffectId, short? Level, short? Value) GetSocket(
        CompactItemEntry item,
        int index) =>
        index switch
        {
            0 => (item.Socket1EffectId, item.Socket1Level,
                item.Socket1Value),
            1 => (item.Socket2EffectId, item.Socket2Level,
                item.Socket2Value),
            2 => (item.Socket3EffectId, item.Socket3Level,
                item.Socket3Value),
            3 => (item.Socket4EffectId, item.Socket4Level,
                item.Socket4Value),
            4 => (item.Socket5EffectId, item.Socket5Level, null),
            5 => (item.Socket6EffectId, item.Socket6Level, null),
            _ => (null, null, null)
        };

    private static CompactItemEntry SetSocket(
        CompactItemEntry item,
        int index,
        short? effectId,
        short? level,
        short? value) =>
        index switch
        {
            0 => item with
            {
                Socket1EffectId = effectId,
                Socket1Level = level,
                Socket1Value = value
            },
            1 => item with
            {
                Socket2EffectId = effectId,
                Socket2Level = level,
                Socket2Value = value
            },
            2 => item with
            {
                Socket3EffectId = effectId,
                Socket3Level = level,
                Socket3Value = value
            },
            3 => item with
            {
                Socket4EffectId = effectId,
                Socket4Level = level,
                Socket4Value = value
            },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

}
