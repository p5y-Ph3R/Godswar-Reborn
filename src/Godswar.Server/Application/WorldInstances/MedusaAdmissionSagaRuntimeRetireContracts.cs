using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Store-derived terminal runtime-retirement authority. It is reconstructible
/// from durable admission state alone, so process loss before Start cannot
/// strand terminal cleanup. A gateway must create/replay a durable retirement
/// tombstone even when no process-local runtime remains.
/// </summary>
internal sealed class MedusaRuntimeRetirePermit
{
    private MedusaRuntimeRetirePermit(MedusaAdmissionSnapshot terminal)
    {
        OperationId = MedusaAdmissionSagaOperationIds.RuntimeRetire(
            terminal.AdmissionId);
        AdmissionId = terminal.AdmissionId;
        WorldInstanceId = terminal.WorldInstanceId;
        Difficulty = terminal.Difficulty;
        ContentMapId = terminal.ContentMapId;
        AdmissionRequestHash = terminal.RequestHash;
        RosterHash = terminal.RosterHash;
        EncounterContentFingerprint = terminal.EncounterContentFingerprint;
        TransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                terminal.AdmissionId,
                terminal.RequestHash));
        CreatedAtUtc = terminal.ReservedAtUtc;
        PreparedAtUtc = terminal.RuntimeReadyAtUtc!.Value;
        StartedAtUtc = terminal.ConsumedAtUtc!.Value;
        TerminalAtUtc = terminal.TerminalAtUtc!.Value;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MapId ContentMapId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public string EncounterContentFingerprint { get; }
    public MedusaPendingStartToken TransferToken { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset TerminalAtUtc { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot terminal,
        out MedusaRuntimeRetirePermit permit)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.State is not (
                MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut) ||
            terminal.RuntimeReadyAtUtc is null ||
            terminal.ConsumedAtUtc is null ||
            terminal.TerminalAtUtc is null ||
            terminal.CleanupEvidence is not null)
        {
            permit = null!;
            return false;
        }
        permit = new(terminal);
        return true;
    }

    internal bool Matches(MedusaPendingRuntimeResult? result) =>
        result is not null &&
        result.Status is
            MedusaPendingRuntimeStatus.Applied or
            MedusaPendingRuntimeStatus.ExactReplay &&
        result.Snapshot is { } runtime &&
        runtime.State == MedusaPendingRuntimeState.Retired &&
        runtime.AdmissionId == AdmissionId &&
        runtime.WorldInstanceId == WorldInstanceId &&
        runtime.Difficulty == Difficulty &&
        runtime.ContentMapId == ContentMapId &&
        runtime.AdmissionRequestHash == AdmissionRequestHash &&
        runtime.RosterHash == RosterHash &&
        runtime.EncounterContentFingerprint == EncounterContentFingerprint &&
        runtime.TransferToken == TransferToken &&
        runtime.CreatedAtUtc == CreatedAtUtc &&
        runtime.PreparedAtUtc == PreparedAtUtc &&
        runtime.StartedAtUtc == StartedAtUtc &&
        runtime.ReleasedAtUtc == TerminalAtUtc;
}
