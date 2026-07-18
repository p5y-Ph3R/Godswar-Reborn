namespace Godswar.Server.Packets;

/// <summary>
/// A status entry rendered by the original client status bar.
/// Remaining time is encoded as the original protocol's unsigned 16-bit seconds.
/// </summary>
internal readonly record struct ClientStatusEffect(uint StatusId, ushort RemainingSeconds);
