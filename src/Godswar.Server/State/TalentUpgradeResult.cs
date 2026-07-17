namespace Godswar.Server.State;

internal sealed class TalentUpgradeResult
{
    public GameCharacter Character { get; init; } = new();

    public int TalentId { get; init; }

    public int NewRank { get; init; }

    public int Cost { get; init; }

    public int RemainingTalentPoints { get; init; }

    public int DisplayValue { get; init; }
}
