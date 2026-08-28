using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.WorldInstances;

internal sealed partial class MedusaAdmissionSagaCoordinator
{
    private MedusaRealmDay GetRealmDay(DateTimeOffset receivedAtUtc) =>
        new(
            _calendar.RealmId,
            _calendar.GetDay(receivedAtUtc),
            _calendar.TimeZoneId,
            _calendar.TimeZoneRulesFingerprint,
            _calendar.Revision);

    private DateTimeOffset UtcNow() =>
        MedusaDurableAdmissionPolicy.CanonicalUtc(
            _clock.GetUtcNow().ToUniversalTime(),
            "utcNow");

    private bool MatchesCommand(
        MedusaAdmissionSnapshot admission,
        MedusaAdmissionStartCommand command)
    {
        if (admission.AdmissionId != command.Operation.AdmissionId ||
            admission.WorldInstanceId != command.Operation.WorldInstanceId ||
            admission.Difficulty != command.Difficulty ||
            admission.Source != command.Source ||
            admission.EncounterContentFingerprint !=
                command.EncounterContentFingerprint ||
            admission.ReservedAtUtc != command.ReceivedAtUtc ||
            !MatchesPersistedRealmDay(
                admission.RealmDay,
                command.ReceivedAtUtc) ||
            admission.Party.LeaderAccountId != command.RequestingAccountId ||
            admission.Party.LeaderCharacterId !=
                command.RequestingCharacterId)
        {
            return false;
        }

        var leader = admission.Party.Members.SingleOrDefault(member =>
            member.AccountId == command.RequestingAccountId &&
            member.CharacterId == command.RequestingCharacterId);
        if (leader.AccountId == 0)
        {
            return false;
        }

        // Before the irreversible barrier the frozen session fence is part of
        // the authority. After it, reconnect may legitimately advance the
        // generation; the transfer gateway must reacquire fresh ownership for
        // the same frozen account+character identities.
        return admission.State is not (
                   MedusaAdmissionState.Reserved or
                   MedusaAdmissionState.RuntimeReady) ||
               leader.Ownership == command.RequestingOwnership;
    }

    private bool IsTrustedLease(
        PartyAdmissionLease lease,
        MedusaAdmissionStartCommand command)
    {
        if (!lease.IsValidAt(command.ReceivedAtUtc) ||
            lease.LeaderAccountId != command.RequestingAccountId ||
            lease.LeaderCharacterId != command.RequestingCharacterId ||
            lease.Members.Any(member =>
                member.RealmId != _calendar.RealmId ||
                member.SourceWorldInstanceId != command.Source.WorldInstanceId ||
                member.SourceMapId != command.Source.MapId))
        {
            return false;
        }
        var leader = lease.Members.SingleOrDefault(member =>
            member.AccountId == command.RequestingAccountId &&
            member.CharacterId == command.RequestingCharacterId);
        return leader.AccountId != 0 &&
               leader.Ownership == command.RequestingOwnership;
    }

    private static bool MatchesRuntime(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime)
    {
        if (!MatchesRuntimeIdentity(admission, runtime))
        {
            return false;
        }

        if (admission.RuntimeReadyAtUtc is { } readyAt &&
            runtime.PreparedAtUtc != readyAt)
        {
            return false;
        }
        if (admission.RuntimeReadyAtUtc is null &&
            runtime.PreparedAtUtc < admission.ReservedAtUtc)
        {
            return false;
        }

        return admission.State switch
        {
            MedusaAdmissionState.Reserved or
            MedusaAdmissionState.RuntimeReady or
            MedusaAdmissionState.RosterTransferCommitted =>
                runtime.State == MedusaPendingRuntimeState.PendingStart,
            MedusaAdmissionState.ConsumedRunning =>
                runtime.State == MedusaPendingRuntimeState.PendingStart ||
                (runtime.State == MedusaPendingRuntimeState.Running &&
                 runtime.StartedAtUtc == admission.ConsumedAtUtc),
            _ => false
        };
    }

    private static bool MatchesRuntimeIdentity(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime)
    {
        if (runtime.AdmissionId != admission.AdmissionId ||
            runtime.WorldInstanceId != admission.WorldInstanceId ||
            runtime.Difficulty != admission.Difficulty ||
            runtime.ContentMapId != admission.ContentMapId ||
            runtime.RosterHash != admission.RosterHash ||
            runtime.AdmissionRequestHash != admission.RequestHash ||
            runtime.EncounterContentFingerprint !=
                admission.EncounterContentFingerprint ||
            runtime.CreatedAtUtc != admission.ReservedAtUtc ||
            runtime.TransferToken != new MedusaPendingStartToken(
                MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                    admission.AdmissionId,
                    admission.RequestHash)))
        {
            return false;
        }
        return true;
    }

    private static bool MatchesPrepare(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime,
        MedusaRosterTransferPrepareResult? result)
    {
        if (result is null || !result.Succeeded ||
            result.AdmissionId != admission.AdmissionId ||
            result.WorldInstanceId != admission.WorldInstanceId ||
            result.RosterHash != admission.RosterHash ||
            result.PreparedAtUtc is null ||
            result.ExpiresAtUtc is null ||
            result.PreparedAtUtc < runtime.PreparedAtUtc ||
            result.OrderedCharacterIds.Length != admission.Party.Members.Length)
        {
            return false;
        }
        for (var index = 0; index < result.OrderedCharacterIds.Length; index++)
        {
            if (result.OrderedCharacterIds[index] !=
                admission.Party.Members[index].CharacterId)
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesCommit(
        MedusaAdmissionSnapshot admission,
        MedusaRosterTransferCommitResult? result)
    {
        if (result is null || !result.Succeeded ||
            result.AdmissionId != admission.AdmissionId ||
            result.WorldInstanceId != admission.WorldInstanceId ||
            result.RosterHash != admission.RosterHash ||
            result.CommittedAtUtc is null ||
            result.CommittedAtUtc < admission.RosterTransferCommittedAtUtc ||
            result.OrderedCharacterIds.Length != admission.Party.Members.Length)
        {
            return false;
        }
        try
        {
            if (MedusaDurableAdmissionPolicy.CanonicalUtc(
                    result.CommittedAtUtc.Value,
                    nameof(result.CommittedAtUtc)) != result.CommittedAtUtc)
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        for (var index = 0; index < result.OrderedCharacterIds.Length; index++)
        {
            if (result.OrderedCharacterIds[index] !=
                admission.Party.Members[index].CharacterId)
            {
                return false;
            }
        }
        return true;
    }

    private bool MatchesPersistedRealmDay(
        MedusaRealmDay realmDay,
        DateTimeOffset receivedAtUtc) =>
        // The snapshot request hash already binds this exact ReceivedAt to the
        // calendar day/TZ/revision that resolved it. Reinterpreting an old
        // instant through newer tzdata could strand an exact crash replay.
        receivedAtUtc != default &&
        realmDay.IsValid &&
        realmDay.RealmId == _calendar.RealmId;

    private static bool IsTerminal(MedusaAdmissionState state) =>
        state is
            MedusaAdmissionState.Completed or
            MedusaAdmissionState.Abandoned or
            MedusaAdmissionState.TimedOut or
            MedusaAdmissionState.CompletedCleaned or
            MedusaAdmissionState.AbandonedCleaned or
            MedusaAdmissionState.TimedOutCleaned;
}
