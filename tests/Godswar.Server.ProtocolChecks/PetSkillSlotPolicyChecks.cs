using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSkillSlotPolicyChecks
{
    public static Task RunAsync()
    {
        CheckLimitsAndHatchState();
        CheckInvariants();
        CheckItemTransitions();
        return Task.CompletedTask;
    }

    private static void CheckLimitsAndHatchState()
    {
        Check.Equal(
            (short)12,
            PetSkillSlotPolicy.MaximumLearnableSkillCells,
            "pets may learn across all twelve native skill cells");
        Check.Equal(
            (short)6,
            PetSkillSlotPolicy.MaximumAutoCastSkillSlots,
            "pet auto-casting remains limited to six assignments");
        Check.Equal(
            12,
            PetManagerPlanner.MaximumPetSkillCount,
            "Pet Manager validation uses the learnable-cell limit");
        Check.Equal(
            6,
            PetManagerPlanner.MaximumPetAutoCastSkillCount,
            "Pet Manager exposes the separate auto-cast limit");
        Check.Equal(
            (short)12,
            PetContentTestCatalog.Instance.Settings.MaximumSkillCount,
            "published gameplay content carries the twelve-cell limit");

        foreach (var definition in PetAptitudeCatalog.All)
        {
            var state = PetSkillSlotPolicy.CreateHatchState(
                definition.Aptitude);
            var expectedCells =
                definition.Value >= (short)PetAptitude.Smart
                    ? (short)2
                    : (short)1;
            Check.Equal(
                (short)1,
                state.LearnedSkillCount,
                $"{definition.DisplayName} hatches with its starter skill");
            Check.Equal(
                expectedCells,
                state.OpenSkillCellCount,
                $"{definition.DisplayName} hatch open cells");
            Check.Equal(
                expectedCells,
                state.AvailableSkillCellCount,
                $"{definition.DisplayName} hatch available cells");
            Check.True(
                PetSkillSlotPolicy.IsValid(state),
                $"{definition.DisplayName} hatch state is valid");
        }

        Check.True(
            PetSkillSlotPolicy.CreateHatchState(PetAptitude.Zealous) ==
                new PetSkillSlotState(1, 1, 1) &&
            PetSkillSlotPolicy.CreateHatchState(PetAptitude.Smart) ==
                new PetSkillSlotState(1, 2, 2) &&
            PetSkillSlotPolicy.CreateHatchState(PetAptitude.Transcendent) ==
                new PetSkillSlotState(1, 2, 2),
            "Smart is the inclusive two-cell hatch boundary");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetSkillSlotPolicy.CreateHatchState((PetAptitude)17),
            "unknown aptitude cannot create a hatch skill state");
    }

    private static void CheckInvariants()
    {
        Check.True(
            PetSkillSlotPolicy.IsValid(new PetSkillSlotState(0, 1, 1)) &&
            PetSkillSlotPolicy.IsValid(new PetSkillSlotState(12, 12, 12)),
            "skill-cell invariant accepts empty learned and maximum states");
        Check.True(
            !PetSkillSlotPolicy.IsValid(new PetSkillSlotState(-1, 0, 0)) &&
            !PetSkillSlotPolicy.IsValid(new PetSkillSlotState(0, 0, 0)) &&
            !PetSkillSlotPolicy.IsValid(new PetSkillSlotState(2, 1, 2)) &&
            !PetSkillSlotPolicy.IsValid(new PetSkillSlotState(1, 3, 2)) &&
            !PetSkillSlotPolicy.IsValid(new PetSkillSlotState(1, 1, 13)),
            "skill-cell invariant rejects negative and misordered counts");
        Check.True(
            !PetSkillSlotPolicy.CanLearnSkill(
                new PetSkillSlotState(1, 1, 1)) &&
            PetSkillSlotPolicy.CanLearnSkill(
                new PetSkillSlotState(1, 2, 2)),
            "learning requires an unoccupied open cell");
    }

    private static void CheckItemTransitions()
    {
        var initial = PetSkillSlotPolicy.CreateHatchState(PetAptitude.Weak);
        Check.True(
            !PetSkillSlotPolicy.TryApplyItem(
                initial,
                PetItemCatalog.GoldenAppleJuice,
                out var unchanged,
                out var rejection) &&
            unchanged == initial &&
            rejection == PetSkillSlotTransitionRejection.NoSealedSkillCell,
            "Apple cannot open a cell before Spring makes one available");

        var state = initial;
        for (short cells = 2;
             cells <= PetSkillSlotPolicy.MaximumLearnableSkillCells;
             cells++)
        {
            Check.True(
                PetSkillSlotPolicy.TryApplyItem(
                    state,
                    PetItemCatalog.PetEnhanceSpring,
                    out state,
                    out rejection) &&
                rejection == PetSkillSlotTransitionRejection.None &&
                state.AvailableSkillCellCount == cells &&
                state.OpenSkillCellCount == cells - 1,
                $"Spring makes skill cell {cells} available but sealed");
            Check.True(
                PetSkillSlotPolicy.TryApplyItem(
                    state,
                    PetItemCatalog.GoldenAppleJuice,
                    out state,
                    out rejection) &&
                rejection == PetSkillSlotTransitionRejection.None &&
                state.OpenSkillCellCount == cells &&
                state.AvailableSkillCellCount == cells,
                $"Apple opens available skill cell {cells}");
        }

        Check.True(
            state == new PetSkillSlotState(1, 12, 12) &&
            PetSkillSlotPolicy.IsValid(state),
            "Spring and Apple transitions reach the bounded twelve-cell state");
        Check.True(
            !PetSkillSlotPolicy.TryApplyEnhanceSpring(
                state,
                out unchanged,
                out rejection) &&
            unchanged == state &&
            rejection ==
                PetSkillSlotTransitionRejection.MaximumSkillCellsReached,
            "Spring cannot exceed twelve available cells");
        Check.True(
            !PetSkillSlotPolicy.TryApplyItem(
                state,
                99999,
                out unchanged,
                out rejection) &&
            unchanged == state &&
            rejection == PetSkillSlotTransitionRejection.UnsupportedItem,
            "unrelated items cannot mutate skill cells");

        var invalid = new PetSkillSlotState(2, 1, 1);
        Check.True(
            !PetSkillSlotPolicy.TryApplyEnhanceSpring(
                invalid,
                out unchanged,
                out rejection) &&
            unchanged == invalid &&
            rejection == PetSkillSlotTransitionRejection.InvalidState,
            "transitions fail closed on an invalid current state");
    }
}
