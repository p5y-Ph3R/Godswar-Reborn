namespace Godswar.Server.State;

/// <summary>
/// The current original-client contract supports exactly one durable
/// character slot per account.
/// </summary>
internal sealed class CharacterSlotOccupiedException : InvalidOperationException
{
    public CharacterSlotOccupiedException()
        : base("The account already owns its single character slot.")
    {
    }
}
