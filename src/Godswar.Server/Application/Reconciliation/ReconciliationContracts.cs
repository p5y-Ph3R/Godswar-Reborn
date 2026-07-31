using System.Text.Json.Serialization;

namespace Godswar.Server.Application.Reconciliation;

internal enum ReconciliationCategory : byte
{
    WalletBaselineMissing = 1,
    WalletCharacterMissing = 2,
    WalletIdentityMismatch = 3,
    WalletRevisionSequenceGap = 4,
    WalletRevisionMismatch = 5,
    WalletBalanceMismatch = 6,
    InventoryBaselineMissing = 7,
    InventoryCharacterMissing = 8,
    InventoryIdentityMismatch = 9,
    InventoryBaselineSnapshotMismatch = 10,
    InventoryRevisionSequenceGap = 11,
    InventoryRevisionMismatch = 12,
    InventoryItemsMismatch = 13,
    DuplicateInventorySlot = 14,
    OrphanItemTemplate = 15,
    ProgressionRewardRevisionGap = 16,
    ProgressionRewardEvidenceGap = 17,
    PetPresenceConflict = 18,
    PetStreamEvidenceGap = 19,
    OutboxPoisoned = 20,
    OutboxExpiredLease = 21,
    OutboxSequenceGap = 22,
    SchemaMigrationManifestMismatch = 23,
    NpcContentPublicationMismatch = 24,
    NpcContentCountMismatch = 25,
    RetainedCharacterWithoutPurgeEvidence = 26,
    OutboxLeaseMismatch = 27,
    UnknownOutboxConsumer = 28,
    OutboxPolicyMismatch = 29,
    OutboxConsumerPositionMismatch = 30,
    WalletLedgerChainMismatch = 31,
    InventoryLedgerChainMismatch = 32
}

[
    JsonConverter(typeof(JsonStringEnumConverter))
]
internal enum ReconciliationMode : byte
{
    ReportOnly = 1
}

internal enum ReconciliationRunStatus : byte
{
    Completed = 1,
    Truncated = 2,
    TimedOut = 3
}

internal static class ReconciliationCategoryNames
{
    public static string ToProtocolValue(
        this ReconciliationCategory category) =>
        category switch
        {
            ReconciliationCategory.WalletBaselineMissing =>
                "wallet_baseline_missing",
            ReconciliationCategory.WalletCharacterMissing =>
                "wallet_character_missing",
            ReconciliationCategory.WalletIdentityMismatch =>
                "wallet_identity_mismatch",
            ReconciliationCategory.WalletRevisionSequenceGap =>
                "wallet_revision_sequence_gap",
            ReconciliationCategory.WalletRevisionMismatch =>
                "wallet_revision_mismatch",
            ReconciliationCategory.WalletBalanceMismatch =>
                "wallet_balance_mismatch",
            ReconciliationCategory.InventoryBaselineMissing =>
                "inventory_baseline_missing",
            ReconciliationCategory.InventoryCharacterMissing =>
                "inventory_character_missing",
            ReconciliationCategory.InventoryIdentityMismatch =>
                "inventory_identity_mismatch",
            ReconciliationCategory.InventoryBaselineSnapshotMismatch =>
                "inventory_baseline_snapshot_mismatch",
            ReconciliationCategory.InventoryRevisionSequenceGap =>
                "inventory_revision_sequence_gap",
            ReconciliationCategory.InventoryRevisionMismatch =>
                "inventory_revision_mismatch",
            ReconciliationCategory.InventoryItemsMismatch =>
                "inventory_items_mismatch",
            ReconciliationCategory.DuplicateInventorySlot =>
                "duplicate_inventory_slot",
            ReconciliationCategory.OrphanItemTemplate =>
                "orphan_item_template",
            ReconciliationCategory.ProgressionRewardRevisionGap =>
                "progression_reward_revision_gap",
            ReconciliationCategory.ProgressionRewardEvidenceGap =>
                "progression_reward_evidence_gap",
            ReconciliationCategory.PetPresenceConflict =>
                "pet_presence_conflict",
            ReconciliationCategory.PetStreamEvidenceGap =>
                "pet_stream_evidence_gap",
            ReconciliationCategory.OutboxPoisoned =>
                "outbox_poisoned",
            ReconciliationCategory.OutboxExpiredLease =>
                "outbox_expired_lease",
            ReconciliationCategory.OutboxSequenceGap =>
                "outbox_sequence_gap",
            ReconciliationCategory.SchemaMigrationManifestMismatch =>
                "schema_migration_manifest_mismatch",
            ReconciliationCategory.NpcContentPublicationMismatch =>
                "npc_content_publication_mismatch",
            ReconciliationCategory.NpcContentCountMismatch =>
                "npc_content_count_mismatch",
            ReconciliationCategory.RetainedCharacterWithoutPurgeEvidence =>
                "retained_character_without_purge_evidence",
            ReconciliationCategory.OutboxLeaseMismatch =>
                "outbox_lease_mismatch",
            ReconciliationCategory.UnknownOutboxConsumer =>
                "unknown_outbox_consumer",
            ReconciliationCategory.OutboxPolicyMismatch =>
                "outbox_policy_mismatch",
            ReconciliationCategory.OutboxConsumerPositionMismatch =>
                "outbox_consumer_position_mismatch",
            ReconciliationCategory.WalletLedgerChainMismatch =>
                "wallet_ledger_chain_mismatch",
            ReconciliationCategory.InventoryLedgerChainMismatch =>
                "inventory_ledger_chain_mismatch",
            _ => throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                null)
        };
}

internal sealed class ReconciliationOptions
{
    public bool Enabled { get; set; }

    public ReconciliationMode Mode { get; set; } =
        ReconciliationMode.ReportOnly;

    public int BatchSize { get; set; } = 100;

    public int MaximumCharactersPerRun { get; set; } = 5_000;

    public int MaximumOutboxEventsPerRun { get; set; } = 5_000;

    public int PollIntervalMilliseconds { get; set; } = 300_000;

    public int CommandTimeoutMilliseconds { get; set; } = 5_000;

    public int RunTimeoutMilliseconds { get; set; } = 30_000;

    public TimeSpan PollInterval =>
        TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan CommandTimeout =>
        TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds);

    public TimeSpan RunTimeout =>
        TimeSpan.FromMilliseconds(RunTimeoutMilliseconds);

    public void Validate()
    {
        if (Mode != ReconciliationMode.ReportOnly)
        {
            throw new InvalidOperationException(
                "The reconciliation worker only supports ReportOnly mode.");
        }

        if (BatchSize is < 1 or > 500)
        {
            throw new InvalidOperationException(
                "Reconciliation batch size must be between 1 and 500.");
        }

        ValidateBound(
            MaximumCharactersPerRun,
            1,
            1_000_000,
            nameof(MaximumCharactersPerRun));
        ValidateBound(
            MaximumOutboxEventsPerRun,
            1,
            1_000_000,
            nameof(MaximumOutboxEventsPerRun));
        ValidateBound(
            PollIntervalMilliseconds,
            10_000,
            86_400_000,
            nameof(PollIntervalMilliseconds));
        ValidateBound(
            CommandTimeoutMilliseconds,
            100,
            60_000,
            nameof(CommandTimeoutMilliseconds));
        ValidateBound(
            RunTimeoutMilliseconds,
            CommandTimeoutMilliseconds,
            600_000,
            nameof(RunTimeoutMilliseconds));
    }

    private static void ValidateBound(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be between {minimum} and {maximum}.");
        }
    }
}

internal readonly record struct ReconciliationCategoryCount(
    ReconciliationCategory Category,
    long Count);

internal sealed record ReconciliationPage(
    long NextKey,
    int RowsScanned,
    bool ReachedEnd,
    IReadOnlyList<ReconciliationCategoryCount> Findings);

internal readonly record struct ReconciliationOutboxPositionCursor(
    string ConsumerKey,
    string AggregateType,
    string AggregateKey)
{
    public static ReconciliationOutboxPositionCursor Start => new(
        string.Empty,
        string.Empty,
        string.Empty);
}

internal sealed record ReconciliationOutboxPositionPage(
    ReconciliationOutboxPositionCursor NextCursor,
    int RowsScanned,
    bool ReachedEnd,
    IReadOnlyList<ReconciliationCategoryCount> Findings);

internal interface IReconciliationSnapshot : IAsyncDisposable
{
    Task<ReconciliationPage> ReadCharacterPageAsync(
        long afterCharacterKey,
        int limit,
        CancellationToken cancellationToken);

    Task<ReconciliationPage> ReadOutboxPageAsync(
        long afterOutboxKey,
        int limit,
        CancellationToken cancellationToken);

    Task<ReconciliationOutboxPositionPage>
        ReadOutboxPositionPageAsync(
            ReconciliationOutboxPositionCursor after,
            int limit,
            CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconciliationCategoryCount>>
        ReadManifestAndContentAsync(
            CancellationToken cancellationToken);
}

internal interface IReconciliationReader
{
    Task<IReconciliationSnapshot> OpenSnapshotAsync(
        TimeSpan commandTimeout,
        CancellationToken cancellationToken);
}

internal sealed record ReconciliationReport(
    int SchemaVersion,
    ReconciliationMode Mode,
    ReconciliationRunStatus Status,
    DateTimeOffset StartedAtUtc,
    long DurationMilliseconds,
    int CharacterRowsScanned,
    int OutboxRowsScanned,
    bool Truncated,
    IReadOnlyList<ReconciliationCategoryCount> Findings);

internal interface IReconciliationRepairer
{
    Task<ExpiredOutboxLeaseRepairResult>
        RecoverExpiredOutboxLeasesAsync(
            int maximumRepairs,
            CancellationToken cancellationToken = default);
}

internal readonly record struct ExpiredOutboxLeaseRepairResult(
    int RecoveredCount,
    bool LimitReached);
