using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Durable title identity. Semantic keys are ownership authority; client title
/// numbers are presentation metadata and are never ownership identity.
/// </summary>
internal readonly record struct MedusaTitleSemanticKey
{
    public MedusaTitleSemanticKey(string value)
    {
        if (!MedusaTitleAwardPolicy.IsKnownSemanticKey(value))
        {
            throw new ArgumentException(
                "The Medusa title semantic key is not authored.",
                nameof(value));
        }

        Value = value;
    }

    public string? Value { get; }

    public bool IsValid => MedusaTitleAwardPolicy.IsKnownSemanticKey(Value);

    public override string ToString() => Value ?? string.Empty;
}

internal readonly record struct MedusaTitleDefinition(
    MedusaEncounterTitle EncounterTitle,
    MedusaTitleSemanticKey SemanticKey,
    string DisplayName,
    uint ClientTitleId,
    MedusaTitleAttributeDefinition Attributes);

/// <summary>
/// Permanent server-authoritative combat attributes granted by one Medusa
/// title. Values use basis points; ownership resolution applies only the
/// strongest owned Medusa definition, never the sum of multiple titles.
/// </summary>
internal readonly record struct MedusaTitleAttributeDefinition(
    int PhysicalAttackBasisPoints,
    int MagicAttackBasisPoints,
    int PhysicalDefenseBasisPoints,
    int MagicDefenseBasisPoints)
{
    public int StrengthBasisPoints => Math.Max(
        Math.Max(PhysicalAttackBasisPoints, MagicAttackBasisPoints),
        Math.Max(PhysicalDefenseBasisPoints, MagicDefenseBasisPoints));

    public bool IsValid =>
        PhysicalAttackBasisPoints is > 0 and <= 10_000 &&
        MagicAttackBasisPoints is > 0 and <= 10_000 &&
        PhysicalDefenseBasisPoints is > 0 and <= 10_000 &&
        MagicDefenseBasisPoints is > 0 and <= 10_000;
}

internal readonly record struct MedusaTitleSettlementMember(
    int AccountId,
    int CharacterId)
{
    public bool IsValid => AccountId > 0 && CharacterId > 0;
}

internal enum MedusaTitleSettlementStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    NotFound = 3,
    RequestConflict = 4,
    TerminalConflict = 5,
    AdmissionEvidenceConflict = 6
}

/// <summary>
/// Immutable completion evidence. It deliberately contains no requested title
/// and no selected-title field: settlement recomputes the best award, while
/// title selection remains a separate, currently unavailable authority.
/// </summary>
internal sealed class MedusaTitleSettlementRequest
{
    public MedusaTitleSettlementRequest(
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        MedusaEncounterDifficulty difficulty,
        string encounterContentFingerprint,
        string rosterHash,
        string admissionRequestHash,
        IReadOnlyCollection<MedusaTitleSettlementMember> frozenMembers,
        DateTimeOffset completedAtUtc,
        TimeSpan elapsed,
        int finalScore)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(worldInstanceId));
        }
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var difficultyDefinition))
        {
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        }
        ArgumentNullException.ThrowIfNull(encounterContentFingerprint);
        ArgumentNullException.ThrowIfNull(rosterHash);
        ArgumentNullException.ThrowIfNull(admissionRequestHash);
        MedusaDurableAdmissionPolicy.ValidateHash(
            encounterContentFingerprint,
            nameof(encounterContentFingerprint));
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        MedusaDurableAdmissionPolicy.ValidateHash(
            admissionRequestHash,
            nameof(admissionRequestHash));
        ArgumentNullException.ThrowIfNull(frozenMembers);

        var members = frozenMembers
            .OrderBy(static member => member.CharacterId)
            .ThenBy(static member => member.AccountId)
            .ToArray();
        if (members.Length is < MedusaIslandPolicy.MinimumPartySize or
                > MedusaIslandPolicy.MaximumPartySize ||
            members.Any(static member => !member.IsValid) ||
            members.Select(static member => member.AccountId).Distinct().Count() !=
                members.Length ||
            members.Select(static member => member.CharacterId).Distinct().Count() !=
                members.Length)
        {
            throw new ArgumentException(
                "Completion requires the exact unique frozen Medusa roster.",
                nameof(frozenMembers));
        }
        if (elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit ||
            elapsed.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsed),
                "Elapsed time must be canonical microsecond precision within the run limit.");
        }
        var maximumScore =
            MedusaIslandEncounterPolicy.TotalVictoryScore(
                difficultyDefinition);
        if (!MedusaIslandPolicy.HasVictoryScore(finalScore) ||
            finalScore > maximumScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalScore),
                "A Medusa title completion score is outside the authored victory range.");
        }

        AdmissionId = admissionId;
        OperationId = MedusaTitleAwardOperationIds.Completion(admissionId);
        WorldInstanceId = worldInstanceId;
        Difficulty = difficulty;
        ContentMapId = difficultyDefinition.ContentMapId;
        EncounterContentFingerprint = encounterContentFingerprint;
        RosterHash = rosterHash;
        AdmissionRequestHash = admissionRequestHash;
        FrozenMembers = Array.AsReadOnly(members);
        FrozenCharacterIds = Array.AsReadOnly(
            members.Select(static member => member.CharacterId).ToArray());
        CompletedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            completedAtUtc,
            nameof(completedAtUtc));
        Elapsed = elapsed;
        FinalScore = finalScore;
        RequestHash = MedusaTitleAwardPolicy.ComputeRequestHash(this);
    }

    public MedusaAdmissionId AdmissionId { get; }

    public Guid OperationId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public MapId ContentMapId { get; }

    public string EncounterContentFingerprint { get; }

    public string RosterHash { get; }

    public string AdmissionRequestHash { get; }

    public IReadOnlyList<MedusaTitleSettlementMember> FrozenMembers { get; }

    public IReadOnlyList<int> FrozenCharacterIds { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public TimeSpan Elapsed { get; }

    public int FinalScore { get; }

    public string RequestHash { get; }
}

internal sealed class MedusaTitleSettlementSnapshot
{
    public MedusaTitleSettlementSnapshot(
        MedusaAdmissionId admissionId,
        Guid operationId,
        WorldInstanceId worldInstanceId,
        MedusaEncounterDifficulty difficulty,
        MapId contentMapId,
        string encounterContentFingerprint,
        string rosterHash,
        string admissionRequestHash,
        DateTimeOffset completedAtUtc,
        TimeSpan elapsed,
        int finalScore,
        string requestHash,
        MedusaTitleSemanticKey? awardedTitle,
        IReadOnlyCollection<MedusaTitleSettlementMember> frozenMembers)
    {
        var reconstructed = new MedusaTitleSettlementRequest(
            admissionId,
            worldInstanceId,
            difficulty,
            encounterContentFingerprint,
            rosterHash,
            admissionRequestHash,
            frozenMembers,
            completedAtUtc,
            elapsed,
            finalScore);
        if (operationId != reconstructed.OperationId ||
            !string.Equals(
                requestHash,
                reconstructed.RequestHash,
                StringComparison.Ordinal) ||
            contentMapId != reconstructed.ContentMapId)
        {
            throw new InvalidDataException(
                "A Medusa title settlement snapshot has mismatched evidence.");
        }
        var hasTitle = MedusaTitleAwardPolicy.TryResolveBestAward(
            difficulty,
            finalScore,
            elapsed,
            out var expectedTitle);
        if (hasTitle != (awardedTitle is not null) ||
            (awardedTitle is { } actual &&
                (!actual.IsValid || actual != expectedTitle.SemanticKey)))
        {
            throw new InvalidDataException(
                "A Medusa title settlement snapshot disagrees with award policy.");
        }

        AdmissionId = admissionId;
        OperationId = operationId;
        WorldInstanceId = worldInstanceId;
        Difficulty = difficulty;
        ContentMapId = contentMapId;
        EncounterContentFingerprint = encounterContentFingerprint;
        RosterHash = rosterHash;
        AdmissionRequestHash = admissionRequestHash;
        CompletedAtUtc = reconstructed.CompletedAtUtc;
        Elapsed = elapsed;
        FinalScore = finalScore;
        RequestHash = requestHash;
        AwardedTitle = awardedTitle;
        FrozenMembers = reconstructed.FrozenMembers;
    }

    public MedusaAdmissionId AdmissionId { get; }
    public Guid OperationId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MapId ContentMapId { get; }
    public string EncounterContentFingerprint { get; }
    public string RosterHash { get; }
    public string AdmissionRequestHash { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public TimeSpan Elapsed { get; }
    public int FinalScore { get; }
    public string RequestHash { get; }
    public MedusaTitleSemanticKey? AwardedTitle { get; }
    public IReadOnlyList<MedusaTitleSettlementMember> FrozenMembers { get; }
}

internal sealed class MedusaTitleSettlementReceipt
{
    public MedusaTitleSettlementReceipt(
        MedusaTitleSettlementStatus status,
        MedusaAdmissionId admissionId,
        MedusaTitleSettlementSnapshot? snapshot)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        var requiresSnapshot = status is
            MedusaTitleSettlementStatus.Applied or
            MedusaTitleSettlementStatus.Duplicate or
            MedusaTitleSettlementStatus.RequestConflict;
        if (requiresSnapshot != (snapshot is not null) ||
            (snapshot is not null && snapshot.AdmissionId != admissionId))
        {
            throw new ArgumentException(
                "The Medusa title settlement receipt has a mismatched snapshot.",
                nameof(snapshot));
        }

        Status = status;
        AdmissionId = admissionId;
        Snapshot = snapshot;
    }

    public MedusaTitleSettlementStatus Status { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public MedusaTitleSettlementSnapshot? Snapshot { get; }
    public bool IsSuccess => Status is MedusaTitleSettlementStatus.Applied or
        MedusaTitleSettlementStatus.Duplicate;
}

/// <summary>
/// Immutable acquisition provenance. Ownership does not imply that this title
/// is selected, equipped, or safe to project to a stock client.
/// </summary>
internal sealed class MedusaTitleOwnershipSnapshot
{
    public MedusaTitleOwnershipSnapshot(
        int characterId,
        MedusaTitleSemanticKey semanticKey,
        MedusaAdmissionId sourceAdmissionId,
        Guid sourceCompletionOperationId,
        DateTimeOffset acquiredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        if (!semanticKey.IsValid || !sourceAdmissionId.IsValid ||
            sourceCompletionOperationId !=
                MedusaTitleAwardOperationIds.Completion(sourceAdmissionId))
        {
            throw new InvalidDataException(
                "Medusa title ownership has invalid acquisition provenance.");
        }

        CharacterId = characterId;
        SemanticKey = semanticKey;
        SourceAdmissionId = sourceAdmissionId;
        SourceCompletionOperationId = sourceCompletionOperationId;
        AcquiredAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            acquiredAtUtc,
            nameof(acquiredAtUtc));
    }

    public int CharacterId { get; }
    public MedusaTitleSemanticKey SemanticKey { get; }
    public MedusaAdmissionId SourceAdmissionId { get; }
    public Guid SourceCompletionOperationId { get; }
    public DateTimeOffset AcquiredAtUtc { get; }
}

internal interface IMedusaTitleAwardStore
{
    /// <summary>
    /// Atomic durable boundary for a future completion command. A process-local
    /// run marker is not crash-lossless authority; live wiring must wait for a
    /// durable final-defeat/completion journal carrying this exact request.
    /// </summary>
    Task<MedusaTitleSettlementReceipt> SettleCompletionAsync(
        MedusaTitleSettlementRequest request,
        CancellationToken cancellationToken = default);

    Task<MedusaTitleSettlementSnapshot?> FindSettlementAsync(
        MedusaAdmissionId admissionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MedusaTitleOwnershipSnapshot>> FindOwnershipAsync(
        int characterId,
        CancellationToken cancellationToken = default);
}
