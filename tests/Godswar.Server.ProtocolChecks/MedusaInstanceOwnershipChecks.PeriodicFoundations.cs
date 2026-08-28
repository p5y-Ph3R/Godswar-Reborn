namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task
        CheckPeriodicFoundationEventAndEgressAsync()
    {
        CheckPeriodicFoundationIdentityValueSemantics();
        CheckPeriodicLedgerPreparationAndAcknowledgement();
        CheckPeriodicLedgerRefreshAndAbort();
        await CheckPeriodicLedgerTerminalAndRecipientsAsync();
        CheckPeriodicLedgerCapacityAndRetention();
        await CheckSharedMonsterAttackEventFloorAsync();
        await CheckPeriodicLiveWorldPumpAsync();
#if DEBUG
        await CheckExactEgressOwnershipTruthAsync();
        await CheckRawExactTerminalBoolTruthAsync();
        await CheckStatusGatedObservationFaultAsync();
        await CheckCastStartTerminalOwnershipAsync();
#endif
    }
}
