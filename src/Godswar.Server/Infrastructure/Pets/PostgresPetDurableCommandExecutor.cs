using System.Diagnostics;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor :
    IPetDurableCommandExecutor,
    IPetOwnerMergeLifecycleStore,
    IPetGrowthPreviewLifecycleStore,
    IPetBasicSavvyPreviewLifecycleStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly GameplayItemContent _itemContent;
    private readonly IPetContentCatalog _petContent;
    private readonly IPetOwnerMergeContentCatalog _ownerMergeContent;
    private readonly IPetLearnedSkillContentCatalog _learnedSkillContent;
    private readonly IPetHatchRankRollSource _petHatchRankRollSource;
    private readonly IPetCaptureRarityRollSource _petCaptureRarityRollSource;

    public PostgresPetDurableCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        GameplayItemContent itemContent,
        IPetContentCatalog petContent,
        IPetOwnerMergeContentCatalog ownerMergeContent,
        IPetLearnedSkillContentCatalog learnedSkillContent,
        IPetHatchRankRollSource? petHatchRankRollSource = null,
        IPetCaptureRarityRollSource? petCaptureRarityRollSource = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        _itemContent = itemContent ?? throw new ArgumentNullException(
            nameof(itemContent));
        _petContent = petContent ?? throw new ArgumentNullException(
            nameof(petContent));
        _ownerMergeContent = ownerMergeContent ??
            throw new ArgumentNullException(nameof(ownerMergeContent));
        _learnedSkillContent = learnedSkillContent ??
            throw new ArgumentNullException(nameof(learnedSkillContent));
        _petHatchRankRollSource = petHatchRankRollSource ??
            CryptographicPetHatchRankRollSource.Instance;
        _petCaptureRarityRollSource = petCaptureRarityRollSource ??
            CryptographicPetCaptureRarityRollSource.Instance;
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<BagItemActivationCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            BagItemActivationCommandEnvelope.Validate(envelope),
            ExecuteBagItemActivationAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetLevelUpgradeCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetLevelUpgradeCommandEnvelope.Validate(envelope),
            ExecutePetLevelUpgradeAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetPresenceTransitionCommandEnvelope.Validate(envelope),
            ExecutePetPresenceAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSkillUnlearnCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetSkillUnlearnCommandEnvelope.Validate(envelope),
            ExecutePetSkillUnlearnAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetGrowthResetCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetGrowthResetCommandEnvelope.Validate(envelope),
            ExecutePetGrowthResetAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetBasicSavvyResetCommandEnvelope.Validate(envelope),
            ExecutePetBasicSavvyResetAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetOwnerMergeToggleCommandEnvelope.Validate(envelope),
            ToggleOwnerMergeAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetToPetMergeCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetToPetMergeCommandEnvelope.Validate(envelope),
            ExecutePetToPetMergeAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetRebirthCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetRebirthCommandEnvelope.Validate(envelope),
            ExecutePetRebirthAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetAppearanceChangeCommandEnvelope.Validate(envelope),
            ExecutePetAppearanceChangeAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBindCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetBindCommandEnvelope.Validate(envelope),
            ExecutePetBindAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSoulContractCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetSoulContractCommandEnvelope.Validate(envelope),
            ExecutePetSoulContractAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetManagerUtilityCommandEnvelope.Validate(envelope),
            ExecutePetManagerUtilityAsync,
            cancellationToken);

    private async Task<PetDurableExecutionResult> ExecuteCoreAsync<T>(
        CommandEnvelope<T> envelope,
        CommandEnvelopeValidation validation,
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            CommandEnvelope<T>,
            LockedCharacter,
            CancellationToken,
            Task<PetTransition>> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return PetDurableExecutionResult.NonDurable(
                    PetDurableExecutionDisposition
                        .RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return PetDurableExecutionResult.NonDurable(
                    PetDurableExecutionDisposition.InvalidIntent);
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                transition,
                cancellationToken);
            if (result.Receipt is not null)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    envelope.Subject,
                    envelope.Ownership,
                    cancellationToken)).RequireCurrent();
            }
            outcome = result.Disposition.ToString().ToLowerInvariant();
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                PetDurablePersistenceCodec.FamilyCode(
                    envelope.Family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<PetDurableExecutionResult>
        ExecuteTransactionAsync<T>(
            CommandEnvelope<T> envelope,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CommandEnvelope<T>,
                LockedCharacter,
                CancellationToken,
                Task<PetTransition>> transition,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.CharacterNotFound);
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.CharacterNotFound);
        }

        var stored = await ReadInboxAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Family,
            operationId,
            cancellationToken);
        if (stored is not null)
        {
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                stored,
                requestHash,
                cancellationToken);
        }

        if (envelope.Family is
                CommandFamily.BagItemActivation or
                CommandFamily.PetSkillUnlearn or
                CommandFamily.PetGrowthReset or
                CommandFamily.PetBasicSavvyReset or
                CommandFamily.PetToPetMerge or
                CommandFamily.PetRebirth or
                CommandFamily.PetAppearanceChange or
                CommandFamily.PetSoulContract or
                CommandFamily.PetManagerUtility &&
            !await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.CharacterNotFound);
        }

        var mutation = await transition(
            connection,
            transaction,
            envelope,
            character.Value,
            cancellationToken);
        return await PersistTransitionAsync(
            connection,
            transaction,
            envelope,
            mutation,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<PetDurableExecutionResult> ReplayAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        StoredInbox stored,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
        {
            await RecordConflictAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.RequestHashConflict);
        }

        var receipt = ValidateStoredReceipt(stored, envelope);
        await RecordDuplicateAsync(
            connection,
            transaction,
            stored.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PetDurableExecutionResult.Duplicate(receipt);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static byte[] DecodeDigest(string digest)
    {
        var bytes = Convert.FromHexString(digest);
        return bytes.Length == CommandEnvelopeContract.DigestBytes
            ? bytes
            : throw new InvalidDataException(
                "The pet command digest has an invalid size.");
    }

    private readonly record struct LockedCharacter(
        int Profession,
        int Level,
        long InventoryRevision,
        short PetShedCapacity,
        long PetShedRevision);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record PetTransition(
        PetDurableReceiptStatus Status,
        int KitBagSlot = -1,
        int EquipmentSlot = -1,
        long PetId = 0,
        short PetLevel = 0,
        long PetExperience = 0,
        long PetRevision = 0,
        bool IsCarried = false,
        bool IsSummoned = false,
        byte PresenceOperation = 0,
        IReadOnlyList<InventoryMutation>? InventoryMutations = null,
        long DeputyPetId = 0,
        PetToPetMergeDelta? PetMergeDelta = null,
        PetGrowthPreviewSnapshot? GrowthPreview = null,
        PetBasicSavvyPreviewSnapshot? BasicSavvyPreview = null,
        PetHatchRankEvidence? HatchRank = null,
        PetAppearanceChangeEvidence? AppearanceChange = null,
        PetSoulContractEvidence? SoulContract = null,
        PetManagerUtilityEvidence? PetManagerUtility = null,
        PetRebirthGrowthEvidence? RebirthGrowth = null,
        PetSkillLearnEvidence? SkillLearn = null)
    {
        public bool Succeeded =>
            Status is PetDurableReceiptStatus.PetCaptured or
                PetDurableReceiptStatus.EggHatched or
                PetDurableReceiptStatus.EquipmentEquipped or
                PetDurableReceiptStatus.PetLevelUpgraded or
                PetDurableReceiptStatus.PresenceChanged or
                PetDurableReceiptStatus.PetShedExpanded or
                PetDurableReceiptStatus.PetSkillCellMadeAvailable or
                PetDurableReceiptStatus.PetSkillCellOpened or
                PetDurableReceiptStatus.PetSkillUnlearned or
                PetDurableReceiptStatus.PetGrowthReset or
                PetDurableReceiptStatus.PetGrowthPreviewed or
                PetDurableReceiptStatus.PetGrowthAccepted or
                PetDurableReceiptStatus.PetBasicSavvyPreviewed or
                PetDurableReceiptStatus.PetBasicSavvyAccepted or
                PetDurableReceiptStatus.PetExperienceAdded or
                PetDurableReceiptStatus.PetToPetMerged or
                PetDurableReceiptStatus.PetReborn or
                PetDurableReceiptStatus.PetSoulContractSigned or
                PetDurableReceiptStatus.PetGrowthChecked or
                PetDurableReceiptStatus.PetSealed or
                PetDurableReceiptStatus.PetUnsealed or
                PetDurableReceiptStatus.PetCallClaimed or
                PetDurableReceiptStatus.PetMergeClaimed or
                PetDurableReceiptStatus.PetGenderChanged or
                PetDurableReceiptStatus.PetAppearanceChanged or
                PetDurableReceiptStatus.PetBound or
                PetDurableReceiptStatus.PetSkillLearned or
                PetDurableReceiptStatus.OwnerMerged or
                PetDurableReceiptStatus.OwnerUnmerged;
    }

    private sealed record InventoryMutation(
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState,
        string ReasonCode,
        long InventoryRevision);
}
