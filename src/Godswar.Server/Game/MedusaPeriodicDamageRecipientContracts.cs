using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal enum MedusaPeriodicDamageRecipientVariant : byte
{
    Observer = 1,
    Self = 2
}

internal readonly record struct MedusaPeriodicDamageRecipientIdentity(
    GameSessionContext Context,
    ClientSession Session,
    WorldInstanceId WorldInstanceId,
    long WorldRevision,
    long WorldMembershipEpoch,
    PlayerOwnershipFence Ownership,
    int CharacterId,
    uint ObjectId,
    long LifeRevision,
    MedusaPeriodicDamageRecipientVariant Variant)
{
    public bool IsValid =>
        Context is not null &&
        Session is not null &&
        ReferenceEquals(Context.Session, Session) &&
        !Session.IsDisconnected &&
        WorldInstanceId.IsValid &&
        Context.WorldInstanceId == WorldInstanceId &&
        WorldRevision >= 0 &&
        Context.WorldRevision == WorldRevision &&
        WorldMembershipEpoch > 0 &&
        Context.WorldMembershipEpoch == WorldMembershipEpoch &&
        Ownership.IsValid &&
        Context.Ownership == Ownership &&
        CharacterId > 0 &&
        Context.CharacterId == CharacterId &&
        ObjectId > 0 &&
        Context.ObjectId == ObjectId &&
        LifeRevision >= 0 &&
        Variant is MedusaPeriodicDamageRecipientVariant.Observer or
            MedusaPeriodicDamageRecipientVariant.Self;
}

/// <summary>
/// Preallocated per-recipient callback. The exact-send helper invokes this
/// while it still holds the registry authority gate, immediately after the
/// egress outcome transfers (or declines) queue ownership. The base marker is
/// durable before any fallible diagnostic/ledger callback.
/// </summary>
internal abstract class MedusaPeriodicDamageRecipientSettlementObserver
{
    private const int Unsettled = 0;
    private const int CaptureInProgress = 1;
    private const int SettledWithoutAdmission = 2;
    private const int SettledWithAdmission = 3;
    private const int ContradictorySettlement = 4;

    private int _state;

    private protected MedusaPeriodicDamageRecipientSettlementObserver()
    {
    }

    internal void MarkSettled(bool admissionOwned)
    {
        var desired = admissionOwned
            ? SettledWithAdmission
            : SettledWithoutAdmission;
        if (Interlocked.CompareExchange(
                ref _state,
                CaptureInProgress,
                Unsettled) == Unsettled)
        {
            _ = Interlocked.CompareExchange(
                ref _state,
                desired,
                CaptureInProgress);
            InvokeCoreNonThrowing(admissionOwned);
            return;
        }

        var observed = Volatile.Read(ref _state);
        if (observed == desired)
        {
            return;
        }

        Volatile.Write(ref _state, ContradictorySettlement);
        InvokeCoreNonThrowing(admissionOwned);
    }

    internal bool TryReadSettlement(
        out bool admissionOwned,
        out bool contradictory)
    {
        var state = Volatile.Read(ref _state);
        admissionOwned = state == SettledWithAdmission;
        contradictory = state is CaptureInProgress or
            ContradictorySettlement;
        return state != Unsettled;
    }

    private void InvokeCoreNonThrowing(bool admissionOwned)
    {
        try
        {
            MarkSettledCore(admissionOwned);
        }
        catch
        {
            // Queue ownership is already final. The retained base marker is
            // synchronized by the ledger before any publication retry.
        }
    }

    private protected abstract void MarkSettledCore(bool admissionOwned);
}
