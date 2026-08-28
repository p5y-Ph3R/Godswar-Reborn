namespace Godswar.Server.Game;

internal static class MedusaMonsterPresentationPolicy
{
    // Removing the client object in the same receive/render window as lethal
    // damage suppresses its floating damage. Keep cleanup prompt but ordered
    // behind enough client frames to render the already-published hit.
    internal static readonly TimeSpan CorpseRemovalDelay =
        TimeSpan.FromMilliseconds(4_200);
}
