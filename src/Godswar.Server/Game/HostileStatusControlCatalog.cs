namespace Godswar.Server.Game;

/// <summary>
/// Stock Status.ini kinds whose active effects include NonMoving. The status
/// IDs within each kind still select the client icon, timer, and presentation;
/// the authoritative movement gate follows the mutually-exclusive kind.
/// </summary>
internal static class HostileStatusControlCatalog
{
    internal const int FrozenKind = 10;
    internal const int StunnedKind = 11;
    internal const int CagedKind = 13;
}
