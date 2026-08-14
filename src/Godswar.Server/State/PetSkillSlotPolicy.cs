namespace Godswar.Server.State;

internal readonly record struct PetSkillSlotState(
    short LearnedSkillCount,
    short OpenSkillCellCount,
    short AvailableSkillCellCount);

internal enum PetSkillSlotTransitionRejection
{
    None,
    InvalidState,
    UnsupportedItem,
    MaximumSkillCellsReached,
    NoSealedSkillCell
}

/// <summary>
/// Pure pet skill-cell policy. Learnable cells and auto-cast positions are
/// deliberately separate limits: all twelve cells may contain learned skills,
/// while only six skills may be assigned to automatic casting.
/// </summary>
internal static class PetSkillSlotPolicy
{
    public const short MaximumLearnableSkillCells = 12;
    public const short MaximumAutoCastSkillSlots = 6;
    public const short HatchStarterSkillCount = 1;

    public static PetSkillSlotState CreateHatchState(
        PetAptitude aptitude)
    {
        if (!PetAptitudeCatalog.TryGet(aptitude, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aptitude),
                aptitude,
                "Unknown pet aptitude.");
        }

        var initialCells =
            (short)aptitude >= (short)PetAptitude.Smart
                ? (short)2
                : (short)1;
        return new(
            HatchStarterSkillCount,
            initialCells,
            initialCells);
    }

    public static bool IsValid(PetSkillSlotState state) =>
        state.LearnedSkillCount >= 0 &&
        state.LearnedSkillCount <= state.OpenSkillCellCount &&
        state.OpenSkillCellCount >= 1 &&
        state.OpenSkillCellCount <= state.AvailableSkillCellCount &&
        state.AvailableSkillCellCount >= 1 &&
        state.AvailableSkillCellCount <= MaximumLearnableSkillCells;

    public static bool CanLearnSkill(PetSkillSlotState state) =>
        IsValid(state) &&
        state.LearnedSkillCount < state.OpenSkillCellCount;

    public static bool TryApplyItem(
        PetSkillSlotState current,
        uint itemId,
        out PetSkillSlotState result,
        out PetSkillSlotTransitionRejection rejection) =>
        itemId switch
        {
            PetItemCatalog.PetEnhanceSpring =>
                TryApplyEnhanceSpring(current, out result, out rejection),
            PetItemCatalog.GoldenAppleJuice =>
                TryApplyGoldenAppleJuice(current, out result, out rejection),
            _ => Reject(
                current,
                PetSkillSlotTransitionRejection.UnsupportedItem,
                out result,
                out rejection)
        };

    public static bool TryApplyEnhanceSpring(
        PetSkillSlotState current,
        out PetSkillSlotState result,
        out PetSkillSlotTransitionRejection rejection)
    {
        if (!IsValid(current))
        {
            return Reject(
                current,
                PetSkillSlotTransitionRejection.InvalidState,
                out result,
                out rejection);
        }

        if (current.AvailableSkillCellCount >=
            MaximumLearnableSkillCells)
        {
            return Reject(
                current,
                PetSkillSlotTransitionRejection.MaximumSkillCellsReached,
                out result,
                out rejection);
        }

        result = current with
        {
            AvailableSkillCellCount = checked(
                (short)(current.AvailableSkillCellCount + 1))
        };
        rejection = PetSkillSlotTransitionRejection.None;
        return true;
    }

    public static bool TryApplyGoldenAppleJuice(
        PetSkillSlotState current,
        out PetSkillSlotState result,
        out PetSkillSlotTransitionRejection rejection)
    {
        if (!IsValid(current))
        {
            return Reject(
                current,
                PetSkillSlotTransitionRejection.InvalidState,
                out result,
                out rejection);
        }

        if (current.OpenSkillCellCount >=
            current.AvailableSkillCellCount)
        {
            return Reject(
                current,
                PetSkillSlotTransitionRejection.NoSealedSkillCell,
                out result,
                out rejection);
        }

        result = current with
        {
            OpenSkillCellCount = checked(
                (short)(current.OpenSkillCellCount + 1))
        };
        rejection = PetSkillSlotTransitionRejection.None;
        return true;
    }

    private static bool Reject(
        PetSkillSlotState current,
        PetSkillSlotTransitionRejection reason,
        out PetSkillSlotState result,
        out PetSkillSlotTransitionRejection rejection)
    {
        result = current;
        rejection = reason;
        return false;
    }
}
