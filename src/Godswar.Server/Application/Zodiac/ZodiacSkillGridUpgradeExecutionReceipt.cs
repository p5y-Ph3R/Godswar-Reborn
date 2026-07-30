using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal enum ZodiacSkillGridUpgradeReceiptStatus : byte
{
    Succeeded = 1,
    InactiveGrid = 2,
    MaximumLevelReached = 3,
    ZodiacLevelTooLow = 4,
    InsufficientEnergy = 5,
    InsufficientTalentPoints = 6
}

internal sealed record ZodiacSkillGridUpgradeExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public ZodiacSkillGridUpgradeExecutionReceipt(
        int characterId,
        ZodiacSkillGridUpgradeReceiptStatus status,
        int gridIndex,
        byte previousLevel,
        byte currentLevel,
        byte currentZodiacLevel,
        byte requiredZodiacLevel,
        int energyCost,
        int energyBefore,
        int energyRemainderBeforeX100,
        int energyAfter,
        int energyRemainderAfterX100,
        int talentPointCost,
        int talentPointsBefore,
        int talentPointsAfter,
        int selectedSkillId,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (gridIndex is
            < ZodiacSkillGridUpgradeCommandEnvelope.MinimumGridIndex or
            > ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }
        if (previousLevel >
                ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel ||
            currentLevel >
                ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(currentLevel));
        }
        if (currentZodiacLevel is < 1 or > 30 ||
            requiredZodiacLevel > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentZodiacLevel));
        }
        if (!HasValidResourceScalars(
                energyCost,
                energyBefore,
                energyRemainderBeforeX100,
                energyAfter,
                energyRemainderAfterX100,
                talentPointCost,
                talentPointsBefore,
                talentPointsAfter))
        {
            throw new ArgumentOutOfRangeException(nameof(energyCost));
        }
        if (selectedSkillId <
            ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSkillId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (auditReference.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }

        ValidateOutcome(
            status,
            previousLevel,
            currentLevel,
            currentZodiacLevel,
            requiredZodiacLevel,
            energyCost,
            energyBefore,
            energyRemainderBeforeX100,
            energyAfter,
            energyRemainderAfterX100,
            talentPointCost,
            talentPointsBefore,
            talentPointsAfter,
            outboxEventId);

        CharacterId = characterId;
        Status = status;
        GridIndex = gridIndex;
        PreviousLevel = previousLevel;
        CurrentLevel = currentLevel;
        CurrentZodiacLevel = currentZodiacLevel;
        RequiredZodiacLevel = requiredZodiacLevel;
        EnergyCost = energyCost;
        EnergyBefore = energyBefore;
        EnergyRemainderBeforeX100 = energyRemainderBeforeX100;
        EnergyAfter = energyAfter;
        EnergyRemainderAfterX100 = energyRemainderAfterX100;
        TalentPointCost = talentPointCost;
        TalentPointsBefore = talentPointsBefore;
        TalentPointsAfter = talentPointsAfter;
        SelectedSkillId = selectedSkillId;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family => CommandFamily.ZodiacSkillGridUpgrade;
    public int CharacterId { get; }
    public ZodiacSkillGridUpgradeReceiptStatus Status { get; }
    public int GridIndex { get; }
    public byte PreviousLevel { get; }
    public byte CurrentLevel { get; }
    public byte CurrentZodiacLevel { get; }
    public byte RequiredZodiacLevel { get; }
    public int EnergyCost { get; }
    public int EnergyBefore { get; }
    public int EnergyRemainderBeforeX100 { get; }
    public int EnergyAfter { get; }
    public int EnergyRemainderAfterX100 { get; }
    public int TalentPointCost { get; }
    public int TalentPointsBefore { get; }
    public int TalentPointsAfter { get; }
    public int SelectedSkillId { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public bool Succeeded =>
        Status == ZodiacSkillGridUpgradeReceiptStatus.Succeeded;
    public long? AggregateRevision =>
        Succeeded ? CurrentLevel : null;

    private static bool HasValidResourceScalars(
        int energyCost,
        int energyBefore,
        int energyRemainderBeforeX100,
        int energyAfter,
        int energyRemainderAfterX100,
        int talentPointCost,
        int talentPointsBefore,
        int talentPointsAfter) =>
        energyCost >= 0 &&
        energyBefore >= 0 &&
        energyRemainderBeforeX100 is >= 0 and <= 99 &&
        energyAfter >= 0 &&
        energyRemainderAfterX100 is >= 0 and <= 99 &&
        talentPointCost >= 0 &&
        talentPointsBefore >= 0 &&
        talentPointsAfter >= 0;

    private static void ValidateOutcome(
        ZodiacSkillGridUpgradeReceiptStatus status,
        byte previousLevel,
        byte currentLevel,
        byte currentZodiacLevel,
        byte requiredZodiacLevel,
        int energyCost,
        int energyBefore,
        int energyRemainderBeforeX100,
        int energyAfter,
        int energyRemainderAfterX100,
        int talentPointCost,
        int talentPointsBefore,
        int talentPointsAfter,
        Guid? outboxEventId)
    {
        var beforeEnergyX100 =
            ((long)energyBefore * 100L) +
            energyRemainderBeforeX100;
        var afterEnergyX100 =
            ((long)energyAfter * 100L) +
            energyRemainderAfterX100;
        var costEnergyX100 = (long)energyCost * 100L;
        var unchanged =
            previousLevel == currentLevel &&
            beforeEnergyX100 == afterEnergyX100 &&
            talentPointsBefore == talentPointsAfter;
        var validEventId =
            outboxEventId is { } eventId && eventId != Guid.Empty;

        if (status == ZodiacSkillGridUpgradeReceiptStatus.Succeeded)
        {
            if (previousLevel is
                    < ZodiacSkillGridUpgradeCommandEnvelope
                        .MinimumActiveLevel or
                    >= ZodiacSkillGridUpgradeCommandEnvelope
                        .MaximumGridLevel ||
                currentLevel != previousLevel + 1 ||
                requiredZodiacLevel is < 1 or > 30 ||
                currentZodiacLevel < requiredZodiacLevel ||
                energyCost <= 0 ||
                talentPointCost <= 0 ||
                beforeEnergyX100 < costEnergyX100 ||
                afterEnergyX100 !=
                    beforeEnergyX100 - costEnergyX100 ||
                talentPointsBefore < talentPointCost ||
                talentPointsAfter !=
                    talentPointsBefore - talentPointCost ||
                !validEventId)
            {
                throw new ArgumentException(
                    "Successful Zodiac upgrade evidence is inconsistent.");
            }
            return;
        }

        if (!unchanged || outboxEventId is not null)
        {
            throw new ArgumentException(
                "Rejected Zodiac upgrades cannot carry mutation evidence.");
        }

        var validActiveRequirement =
            previousLevel is
                >= ZodiacSkillGridUpgradeCommandEnvelope
                    .MinimumActiveLevel and
                < ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel &&
            requiredZodiacLevel is >= 1 and <= 30 &&
            energyCost > 0 &&
            talentPointCost > 0;
        var valid = status switch
        {
            ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid =>
                previousLevel == 0 &&
                requiredZodiacLevel == 0 &&
                energyCost == 0 &&
                talentPointCost == 0,
            ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached =>
                previousLevel ==
                    ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel &&
                requiredZodiacLevel == 0 &&
                energyCost == 0 &&
                talentPointCost == 0,
            ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow =>
                validActiveRequirement &&
                currentZodiacLevel < requiredZodiacLevel,
            ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy =>
                validActiveRequirement &&
                currentZodiacLevel >= requiredZodiacLevel &&
                beforeEnergyX100 < costEnergyX100,
            ZodiacSkillGridUpgradeReceiptStatus
                .InsufficientTalentPoints =>
                validActiveRequirement &&
                currentZodiacLevel >= requiredZodiacLevel &&
                beforeEnergyX100 >= costEnergyX100 &&
                talentPointsBefore < talentPointCost,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Rejected Zodiac upgrade evidence contradicts its status.");
        }
    }
}
