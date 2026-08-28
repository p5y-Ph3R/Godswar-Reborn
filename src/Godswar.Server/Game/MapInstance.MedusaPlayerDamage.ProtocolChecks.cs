namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
#if DEBUG
    private int _protocolCheckMedusaPreparedDefeatFault;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void ApplyProtocolCheckPreparedDefeatFault(
        MedusaInstanceOwnerBoundAggregate owner,
        MedusaInstanceOwnerBoundAggregate.PreparedPlayerDefeat prepared)
    {
#if DEBUG
        var fault = Interlocked.Exchange(
            ref _protocolCheckMedusaPreparedDefeatFault,
            0);
        MedusaInstanceOwnerBoundAggregate
            .InvalidatePreparedDefeatForProtocolCheck(prepared, fault);
#endif
    }
}
