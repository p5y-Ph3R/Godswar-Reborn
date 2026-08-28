namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
#if DEBUG
    private int _protocolCheckMedusaFinalizeEffectFault;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckMedusaFinalizeEffectFault()
    {
#if DEBUG
        if (Interlocked.Exchange(
                ref _protocolCheckMedusaFinalizeEffectFault,
                0) != 0)
        {
            throw new InvalidOperationException(
                "Simulated post-claim Medusa effect finalization fault.");
        }
#endif
    }
}
