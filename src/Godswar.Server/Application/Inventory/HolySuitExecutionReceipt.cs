using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal sealed record HolySuitExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public HolySuitExecutionReceipt(
        int characterId,
        HolySuitCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolySuitCommandResultStatus status,
        int nativeResultSubId,
        long requestedExperience,
        int requestedPrisms,
        long characterExperienceBefore,
        long characterExperienceAfter,
        long dailyStoredExperienceBefore,
        long dailyStoredExperienceAfter,
        bool battlePassDailyLimitExempt,
        int prismsCreated,
        int prismsConsumed,
        IReadOnlyList<HolySuitReceiptMutation> mutations,
        long progressionRevision,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0 ||
            !Enum.IsDefined(operation) ||
            !HolySuitCommandEnvelope.IsEndpoint(npcId, dialogIndex) ||
            !Enum.IsDefined(status) ||
            !HolySuitNativeResults.IsReachable(operation, status) ||
            nativeResultSubId !=
                HolySuitNativeResults.GetResultSubId(operation, status) ||
            requestedExperience is
                < 0 or > HolySuitCommandEnvelope.MaximumExperienceToStore ||
            requestedPrisms is
                < 0 or > HolySuitCommandEnvelope.MaximumPrismsToCreate ||
            characterExperienceBefore is < 0 or > uint.MaxValue ||
            characterExperienceAfter is < 0 or > uint.MaxValue ||
            dailyStoredExperienceBefore < 0 ||
            dailyStoredExperienceAfter < 0 ||
            dailyStoredExperienceAfter < dailyStoredExperienceBefore ||
            prismsCreated < 0 ||
            prismsConsumed < 0 ||
            progressionRevision < 0 ||
            inventoryRevision < 0)
        {
            throw new ArgumentException(
                "The Holy Suit receipt contains invalid scalar evidence.");
        }

        ValidateRequestedIntent(
            operation,
            requestedExperience,
            requestedPrisms);
        Mutations = CopyAndValidateMutations(
            operation,
            status,
            prismsCreated,
            prismsConsumed,
            mutations);
        ValidateOutcomeEvidence(
            operation,
            status,
            requestedExperience,
            requestedPrisms,
            characterExperienceBefore,
            characterExperienceAfter,
            dailyStoredExperienceBefore,
            dailyStoredExperienceAfter,
            battlePassDailyLimitExempt,
            prismsCreated,
            prismsConsumed,
            progressionRevision,
            inventoryRevision,
            outboxEventId);

        CharacterId = characterId;
        Operation = operation;
        NpcId = npcId;
        DialogIndex = dialogIndex;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        RequestedExperience = requestedExperience;
        RequestedPrisms = requestedPrisms;
        CharacterExperienceBefore = characterExperienceBefore;
        CharacterExperienceAfter = characterExperienceAfter;
        DailyStoredExperienceBefore = dailyStoredExperienceBefore;
        DailyStoredExperienceAfter = dailyStoredExperienceAfter;
        BattlePassDailyLimitExempt = battlePassDailyLimitExempt;
        PrismsCreated = prismsCreated;
        PrismsConsumed = prismsConsumed;
        ProgressionRevision = progressionRevision;
        InventoryRevision = inventoryRevision;
        AuditReference = RequireAuditReference(auditReference);
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        HolySuitCommandEnvelope.Family(Operation);

    public int CharacterId { get; }
    public HolySuitCommandOperation Operation { get; }
    public int NpcId { get; }
    public int DialogIndex { get; }
    public HolySuitCommandResultStatus Status { get; }
    public int NativeResultSubId { get; }
    public long RequestedExperience { get; }
    public int RequestedPrisms { get; }
    public long CharacterExperienceBefore { get; }
    public long CharacterExperienceAfter { get; }
    public long DailyStoredExperienceBefore { get; }
    public long DailyStoredExperienceAfter { get; }
    public bool BattlePassDailyLimitExempt { get; }
    public int PrismsCreated { get; }
    public int PrismsConsumed { get; }
    public ImmutableArray<HolySuitReceiptMutation> Mutations { get; }
    public long ProgressionRevision { get; }
    public long InventoryRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public bool Committed => HolySuitNativeResults.IsCommitted(Status);
    public bool Succeeded => HolySuitNativeResults.IsSuccess(Status);

    private static void ValidateRequestedIntent(
        HolySuitCommandOperation operation,
        long requestedExperience,
        int requestedPrisms)
    {
        var valid = operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                requestedExperience >= 0 && requestedPrisms == 0,
            HolySuitCommandOperation.TransformExperience =>
                requestedExperience == 0 && requestedPrisms > 0,
            HolySuitCommandOperation.TransferExperience or
                HolySuitCommandOperation.ConsumeWare =>
                requestedExperience == 0 && requestedPrisms == 0,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The receipt request does not match its operation.");
        }
    }

    private static ImmutableArray<HolySuitReceiptMutation>
        CopyAndValidateMutations(
            HolySuitCommandOperation operation,
            HolySuitCommandResultStatus status,
            int prismsCreated,
            int prismsConsumed,
            IReadOnlyList<HolySuitReceiptMutation>? mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var copy = ImmutableArray.CreateRange(mutations);
        var committed = HolySuitNativeResults.IsCommitted(status);
        if (!committed)
        {
            if (!copy.IsEmpty)
            {
                throw new ArgumentException(
                    "A rejected Holy Suit receipt cannot contain mutations.",
                    nameof(mutations));
            }
            return copy;
        }

        if (copy.IsEmpty ||
            copy.Select(static mutation => mutation.KitBagSlot)
                .Distinct()
                .Count() != copy.Length ||
            copy.All(static mutation => string.Equals(
                mutation.BeforeCompactItemState,
                mutation.AfterCompactItemState,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A committed Holy Suit receipt needs distinct item changes.",
                nameof(mutations));
        }

        foreach (var mutation in copy)
        {
            if (!Enum.IsDefined(mutation.Role) ||
                mutation.KitBagSlot is
                    < HolySuitCommandEnvelope.MinimumKitBagSlot or
                    > HolySuitCommandEnvelope.MaximumKitBagSlot ||
                mutation.ItemId == 0 ||
                mutation.ItemInstanceId <= 0 ||
                !IsBoundedCompactState(
                    mutation.BeforeCompactItemState) ||
                !IsBoundedCompactState(
                    mutation.AfterCompactItemState) ||
                mutation.BeforeCompactItemState == "[]" &&
                    mutation.AfterCompactItemState == "[]")
            {
                throw new ArgumentException(
                    "The receipt contains invalid item evidence.",
                    nameof(mutations));
            }
        }

        var rolesValid = operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                copy.Length == 1 &&
                copy[0].Role == HolySuitReceiptItemRole.HolyBox,
            HolySuitCommandOperation.TransferExperience =>
                copy.Length == 2 &&
                copy[0].Role == HolySuitReceiptItemRole.Equipment &&
                copy[1].Role == HolySuitReceiptItemRole.HolyBox,
            HolySuitCommandOperation.ConsumeWare =>
                copy.Length >= 2 &&
                copy[0].Role == HolySuitReceiptItemRole.Equipment &&
                copy[1].Role == HolySuitReceiptItemRole.Ware &&
                copy.Skip(2).All(static mutation =>
                    mutation.Role ==
                        HolySuitReceiptItemRole.ExperiencePrism) &&
                (prismsConsumed > 0) == (copy.Length > 2),
            HolySuitCommandOperation.TransformExperience =>
                copy.All(static mutation =>
                    mutation.Role ==
                        HolySuitReceiptItemRole.ExperiencePrism) &&
                prismsCreated > 0,
            _ => false
        };
        if (!rolesValid)
        {
            throw new ArgumentException(
                "The mutation roles do not match the Holy Suit operation.",
                nameof(mutations));
        }

        return copy;
    }

    private static void ValidateOutcomeEvidence(
        HolySuitCommandOperation operation,
        HolySuitCommandResultStatus status,
        long requestedExperience,
        int requestedPrisms,
        long experienceBefore,
        long experienceAfter,
        long dailyBefore,
        long dailyAfter,
        bool dailyLimitExempt,
        int prismsCreated,
        int prismsConsumed,
        long progressionRevision,
        long inventoryRevision,
        Guid? outboxEventId)
    {
        var committed = HolySuitNativeResults.IsCommitted(status);
        if (committed !=
                (outboxEventId is { } eventId && eventId != Guid.Empty) ||
            committed && inventoryRevision <= 0)
        {
            throw new ArgumentException(
                "Only a committed Holy Suit result may publish an event.");
        }

        var actualExperienceSpent = checked(
            experienceBefore - experienceAfter);
        var expectedExperienceSpent = status switch
        {
            HolySuitCommandResultStatus.ExperienceStored =>
                actualExperienceSpent,
            HolySuitCommandResultStatus.ExperienceTransformed =>
                checked((long)requestedPrisms *
                    HolySuitCommandEnvelope.ExperiencePerPrism),
            _ => 0
        };
        if (actualExperienceSpent != expectedExperienceSpent ||
            (status == HolySuitCommandResultStatus.ExperienceStored &&
                (actualExperienceSpent <= 0 ||
                 requestedExperience > 0 &&
                    requestedExperience != actualExperienceSpent)) ||
            (expectedExperienceSpent > 0 && progressionRevision <= 0))
        {
            throw new ArgumentException(
                "The character EXP evidence contradicts the result.");
        }

        var expectedDailyIncrease =
            status == HolySuitCommandResultStatus.ExperienceStored
                ? actualExperienceSpent
                : 0;
        if (dailyAfter - dailyBefore != expectedDailyIncrease ||
            dailyLimitExempt &&
                operation != HolySuitCommandOperation.StoreExperience)
        {
            throw new ArgumentException(
                "The daily Holy Suit usage evidence is inconsistent.");
        }

        if (status ==
                HolySuitCommandResultStatus.ExperienceTransformed
                ? prismsCreated != requestedPrisms || prismsConsumed != 0
                : operation == HolySuitCommandOperation.ConsumeWare
                    ? prismsCreated != 0
                    : prismsCreated != 0 || prismsConsumed != 0)
        {
            throw new ArgumentException(
                "The prism evidence contradicts the operation result.");
        }

        if (!committed &&
            (prismsCreated != 0 || prismsConsumed != 0))
        {
            throw new ArgumentException(
                "A rejected result cannot create or consume prisms.");
        }
    }

    private static bool IsBoundedCompactState(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl) &&
        value[0] == '[' &&
        value[^1] == ']' &&
        Encoding.UTF8.GetByteCount(value) <=
            HolySuitCommandEnvelope.MaximumCompactItemStateUtf8Bytes;

    private static string RequireAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return value;
    }
}
