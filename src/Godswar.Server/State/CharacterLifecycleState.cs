namespace Godswar.Server.State;

internal enum CharacterLifecycleState : byte
{
    Active = 1,
    Deleted = 2
}

internal static class CharacterLifecyclePolicy
{
    public const short SingleCharacterSlot = 0;

    public static readonly TimeSpan DefaultRestoreWindow =
        TimeSpan.FromDays(30);

    public static readonly TimeSpan DefaultPurgeDelay =
        TimeSpan.FromDays(7);
}

internal sealed class CharacterLifecycleDurableStreamActiveException :
    InvalidOperationException
{
    public CharacterLifecycleDurableStreamActiveException()
        : base(
            "Broad character lifecycle mutation is forbidden after " +
            "the durable lifecycle stream has started.")
    {
    }
}
