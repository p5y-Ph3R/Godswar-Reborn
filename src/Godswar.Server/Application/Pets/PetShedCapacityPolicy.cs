namespace Godswar.Server.Application.Pets;

/// <summary>
/// Bounds the native client's character-owned pet shed. The eight physical
/// cells are not all unlocked by default; opcode 10237 carries the persisted
/// opened-cell count independently from the number of owned pets.
/// </summary>
internal static class PetShedCapacityPolicy
{
    public const short DefaultOpenedCellCount = 2;
    public const short MaximumOpenedCellCount = 8;

    public static bool IsValid(int openedCellCount) =>
        openedCellCount is >= DefaultOpenedCellCount and
            <= MaximumOpenedCellCount;
}
