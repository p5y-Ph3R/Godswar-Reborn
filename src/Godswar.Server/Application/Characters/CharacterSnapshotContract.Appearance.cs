namespace Godswar.Server.Application.Characters;

internal static partial class CharacterSnapshotContract
{
    private static void ValidateAppearance(
        CharacterAppearanceSnapshot appearance)
    {
        if (appearance is null)
        {
            throw Invalid("Character appearance is missing.");
        }

        RequireCount(
            appearance.OwnedTitleIds,
            CharacterSnapshotLimits.OwnedTitleCount,
            exact: false,
            "owned titles");
        if (appearance.OwnedTitleIds.Any(static titleId => titleId == 0) ||
            HasDuplicates(
                appearance.OwnedTitleIds,
                static titleId => titleId))
        {
            throw Invalid("Owned titles contain invalid or duplicate IDs.");
        }
    }
}
