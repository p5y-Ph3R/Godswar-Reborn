using System.Collections.Immutable;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaPendingStartToken
{
    public MedusaPendingStartToken(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Pending-start tokens cannot be empty.",
                nameof(value));
        }
        Value = value;
    }

    public Guid Value { get; }

    public bool IsValid => Value != Guid.Empty;
}

internal enum MedusaPendingRuntimeState : byte
{
    PendingStart = 1,
    Running = 2,
    Released = 3,
    Retired = 4
}

internal sealed class MedusaPendingRuntimeSnapshot
{
    public MedusaPendingRuntimeSnapshot(
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        MedusaEncounterDifficulty difficulty,
        MapId contentMapId,
        string rosterHash,
        string admissionRequestHash,
        string encounterContentFingerprint,
        MedusaPendingStartToken transferToken,
        MedusaPendingRuntimeState state,
        DateTimeOffset createdAtUtc,
        DateTimeOffset preparedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? releasedAtUtc)
    {
        if (!admissionId.IsValid || !worldInstanceId.IsValid ||
            !transferToken.IsValid ||
            !MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var definition) ||
            definition.ContentMapId != contentMapId)
        {
            throw new ArgumentException(
                "A pending runtime snapshot requires complete exact identity.");
        }
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        MedusaDurableAdmissionPolicy.ValidateHash(
            admissionRequestHash,
            nameof(admissionRequestHash));
        MedusaDurableAdmissionPolicy.ValidateHash(
            encounterContentFingerprint,
            nameof(encounterContentFingerprint));
        var expectedTransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                admissionId,
                admissionRequestHash));
        if (transferToken != expectedTransferToken)
        {
            throw new ArgumentException(
                "Pending runtime tokens must be deterministic request-bound identities.",
                nameof(transferToken));
        }
        createdAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            createdAtUtc,
            nameof(createdAtUtc));
        preparedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            preparedAtUtc,
            nameof(preparedAtUtc));
        startedAtUtc = CanonicalNullable(startedAtUtc, nameof(startedAtUtc));
        releasedAtUtc = CanonicalNullable(releasedAtUtc, nameof(releasedAtUtc));
        if (preparedAtUtc < createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preparedAtUtc),
                "Runtime preparation cannot precede reservation.");
        }
        var shapeIsValid = state switch
        {
            MedusaPendingRuntimeState.PendingStart =>
                startedAtUtc is null && releasedAtUtc is null,
            MedusaPendingRuntimeState.Running =>
                startedAtUtc >= preparedAtUtc && releasedAtUtc is null,
            MedusaPendingRuntimeState.Released =>
                startedAtUtc is null && releasedAtUtc >= preparedAtUtc,
            MedusaPendingRuntimeState.Retired =>
                startedAtUtc is not null &&
                releasedAtUtc >= startedAtUtc &&
                releasedAtUtc >= preparedAtUtc,
            _ => false
        };
        if (!shapeIsValid)
        {
            throw new ArgumentException(
                "Runtime state and lifecycle timestamps are inconsistent.",
                nameof(state));
        }

        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        Difficulty = difficulty;
        ContentMapId = contentMapId;
        RosterHash = rosterHash;
        AdmissionRequestHash = admissionRequestHash;
        EncounterContentFingerprint = encounterContentFingerprint;
        TransferToken = transferToken;
        State = state;
        CreatedAtUtc = createdAtUtc;
        PreparedAtUtc = preparedAtUtc;
        StartedAtUtc = startedAtUtc;
        ReleasedAtUtc = releasedAtUtc;
    }

    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MapId ContentMapId { get; }
    public string RosterHash { get; }
    public string AdmissionRequestHash { get; }
    public string EncounterContentFingerprint { get; }
    public MedusaPendingStartToken TransferToken { get; }
    public MedusaPendingRuntimeState State { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset? ReleasedAtUtc { get; }

    private static DateTimeOffset? CanonicalNullable(
        DateTimeOffset? value,
        string parameterName) =>
        value is null
            ? null
            : MedusaDurableAdmissionPolicy.CanonicalUtc(
                value.Value,
                parameterName);
}

internal enum MedusaPendingRuntimeStatus : byte
{
    Applied = 1,
    ExactReplay = 2,
    RejectedNoPublication = 3,
    RejectedNoChange = 4,
    IdentityConflict = 5
}

internal sealed record MedusaPendingRuntimeResult(
    MedusaPendingRuntimeStatus Status,
    MedusaPendingRuntimeSnapshot? Snapshot)
{
    public bool Succeeded => Status is
        MedusaPendingRuntimeStatus.Applied or
        MedusaPendingRuntimeStatus.ExactReplay;
}

internal sealed class MedusaPendingStartRuntimeRequest
{
    public MedusaPendingStartRuntimeRequest(MedusaAdmissionSnapshot admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.State is not (
                MedusaAdmissionState.Reserved or
                MedusaAdmissionState.RuntimeReady or
                MedusaAdmissionState.RosterTransferCommitted or
                MedusaAdmissionState.ConsumedRunning))
        {
            throw new ArgumentException(
                "Released and terminal admissions cannot create or recover a runtime.",
                nameof(admission));
        }
        AdmissionId = admission.AdmissionId;
        WorldInstanceId = admission.WorldInstanceId;
        RealmDay = admission.RealmDay;
        Difficulty = admission.Difficulty;
        ContentMapId = admission.ContentMapId;
        RosterHash = admission.RosterHash;
        AdmissionRequestHash = admission.RequestHash;
        EncounterContentFingerprint = admission.EncounterContentFingerprint;
        CreatedAtUtc = admission.ReservedAtUtc;
        ExpectedPreparedAtUtc = admission.RuntimeReadyAtUtc;
        ExpectedTransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                admission.AdmissionId,
                admission.RequestHash));
        OrderedCharacterIds = admission.Party.Members
            .Select(static member => member.CharacterId)
            .ToImmutableArray();
    }

    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public MedusaRealmDay RealmDay { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MapId ContentMapId { get; }
    public string RosterHash { get; }
    public string AdmissionRequestHash { get; }
    public string EncounterContentFingerprint { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    /// <summary>
    /// Durable logical preparation time on replay. A gateway rebuilding a
    /// lost PendingStart process must preserve this value instead of sampling
    /// its rematerialization clock.
    /// </summary>
    public DateTimeOffset? ExpectedPreparedAtUtc { get; }
    /// <summary>
    /// Deterministic token that must survive process loss before or after the
    /// RuntimeReady receipt. A gateway may never mint a fresh replay token.
    /// </summary>
    public MedusaPendingStartToken ExpectedTransferToken { get; }
    public ImmutableArray<int> OrderedCharacterIds { get; }
}

/// <summary>
/// Store-derived authority to start one exact dormant runtime. StartedAt is
/// immutable durable ConsumedAtUtc; a retry cannot choose a fresh clock value.
/// </summary>
internal sealed class MedusaRuntimeStartPermit
{
    private MedusaRuntimeStartPermit(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime)
    {
        OperationId = MedusaAdmissionSagaOperationIds.RuntimeStart(
            admission.AdmissionId);
        AdmissionId = admission.AdmissionId;
        WorldInstanceId = admission.WorldInstanceId;
        RosterHash = admission.RosterHash;
        AdmissionRequestHash = admission.RequestHash;
        EncounterContentFingerprint = admission.EncounterContentFingerprint;
        TransferToken = runtime.TransferToken;
        StartedAtUtc = admission.ConsumedAtUtc!.Value;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string RosterHash { get; }
    public string AdmissionRequestHash { get; }
    public string EncounterContentFingerprint { get; }
    public MedusaPendingStartToken TransferToken { get; }
    public DateTimeOffset StartedAtUtc { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime,
        out MedusaRuntimeStartPermit permit)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(runtime);
        if (admission.State != MedusaAdmissionState.ConsumedRunning ||
            admission.ConsumedAtUtc is null ||
            !Matches(admission, runtime) ||
            runtime.State is not (
                MedusaPendingRuntimeState.PendingStart or
                MedusaPendingRuntimeState.Running) ||
            admission.RuntimeReadyAtUtc is null ||
            runtime.PreparedAtUtc != admission.RuntimeReadyAtUtc ||
            runtime.PreparedAtUtc > admission.ConsumedAtUtc.Value ||
            (runtime.State == MedusaPendingRuntimeState.Running &&
             runtime.StartedAtUtc != admission.ConsumedAtUtc))
        {
            permit = null!;
            return false;
        }
        permit = new(admission, runtime);
        return true;
    }

    private static bool Matches(
        MedusaAdmissionSnapshot admission,
        MedusaPendingRuntimeSnapshot runtime) =>
        runtime.AdmissionId == admission.AdmissionId &&
        runtime.WorldInstanceId == admission.WorldInstanceId &&
        runtime.Difficulty == admission.Difficulty &&
        runtime.ContentMapId == admission.ContentMapId &&
        runtime.RosterHash == admission.RosterHash &&
        runtime.AdmissionRequestHash == admission.RequestHash &&
        runtime.EncounterContentFingerprint ==
            admission.EncounterContentFingerprint &&
        runtime.CreatedAtUtc == admission.ReservedAtUtc;
}

/// <summary>
/// Store-derived authority to retire an exact empty runtime. It can only be
/// minted after the durable admission is Released, which is impossible once
/// the irreversible roster-transfer barrier exists.
/// </summary>
internal sealed class MedusaRuntimeReleasePermit
{
    private MedusaRuntimeReleasePermit(MedusaAdmissionSnapshot admission)
    {
        OperationId = MedusaAdmissionSagaOperationIds.RuntimeRelease(
            admission.AdmissionId);
        AdmissionId = admission.AdmissionId;
        WorldInstanceId = admission.WorldInstanceId;
        Difficulty = admission.Difficulty;
        ContentMapId = admission.ContentMapId;
        RosterHash = admission.RosterHash;
        AdmissionRequestHash = admission.RequestHash;
        EncounterContentFingerprint = admission.EncounterContentFingerprint;
        TransferToken = new MedusaPendingStartToken(
            MedusaAdmissionSagaOperationIds.RuntimeTransferToken(
                admission.AdmissionId,
                admission.RequestHash));
        CreatedAtUtc = admission.ReservedAtUtc;
        PreparedAtUtc = admission.RuntimeReadyAtUtc ??
            admission.ReleasedAtUtc!.Value;
        ReleasedAtUtc = admission.ReleasedAtUtc!.Value;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MapId ContentMapId { get; }
    public string RosterHash { get; }
    public string AdmissionRequestHash { get; }
    public string EncounterContentFingerprint { get; }
    public MedusaPendingStartToken TransferToken { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public DateTimeOffset ReleasedAtUtc { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot admission,
        out MedusaRuntimeReleasePermit permit)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.State != MedusaAdmissionState.Released ||
            admission.ReleasedAtUtc is null ||
            admission.BarrierEvidence is not null)
        {
            permit = null!;
            return false;
        }
        permit = new(admission);
        return true;
    }

    internal bool Matches(MedusaPendingRuntimeResult? result) =>
        result is not null &&
        result.Status is
            MedusaPendingRuntimeStatus.Applied or
            MedusaPendingRuntimeStatus.ExactReplay &&
        result.Snapshot is { } runtime &&
        runtime.State == MedusaPendingRuntimeState.Released &&
        runtime.AdmissionId == AdmissionId &&
        runtime.WorldInstanceId == WorldInstanceId &&
        runtime.Difficulty == Difficulty &&
        runtime.ContentMapId == ContentMapId &&
        runtime.RosterHash == RosterHash &&
        runtime.AdmissionRequestHash == AdmissionRequestHash &&
        runtime.EncounterContentFingerprint == EncounterContentFingerprint &&
        runtime.TransferToken == TransferToken &&
        runtime.CreatedAtUtc == CreatedAtUtc &&
        runtime.PreparedAtUtc == PreparedAtUtc &&
        runtime.ReleasedAtUtc == ReleasedAtUtc;
}

/// <summary>
/// Exact-ID dormant runtime capability. Ensure must not start the encounter
/// clock. Start and release require store-derived typed permits.
/// Its durable runtime ledger must tombstone both pre-barrier Release and
/// terminal Retire. After either succeeds, process loss and a stale Ensure or
/// Start capability can never publish/revive the runtime.
/// </summary>
internal interface IMedusaPendingStartRuntimeGateway
{
    Task<MedusaPendingRuntimeResult> EnsurePendingStartAsync(
        MedusaPendingStartRuntimeRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaPendingRuntimeResult> StartAsync(
        MedusaRuntimeStartPermit permit,
        CancellationToken cancellationToken = default);

    Task<MedusaPendingRuntimeResult> ReleaseEmptyAsync(
        MedusaRuntimeReleasePermit permit,
        CancellationToken cancellationToken = default);

    Task<MedusaPendingRuntimeResult> RetireTerminalAsync(
        MedusaRuntimeRetirePermit permit,
        CancellationToken cancellationToken = default);
}
