namespace Godswar.Server.Packets;

/// <summary>
/// A status entry rendered by the original client status bar.
/// Remaining time is encoded as this client revision's unsigned 32-bit seconds.
/// </summary>
internal readonly record struct ClientStatusEffect(uint StatusId, uint RemainingSeconds);
