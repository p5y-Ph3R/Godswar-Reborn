using System.Buffers.Binary;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct GearEnhancerItemSelectionPacket(
    int BagPage,
    int PageSlot,
    bool Selected)
{
    public const int PayloadLength = 12;
    public const int PageCount = 4;
    public const int SlotsPerPage = 24;

    public int KitBagSlot => checked((BagPage * SlotsPerPage) + PageSlot);

    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        out GearEnhancerItemSelectionPacket selection)
    {
        selection = default;
        if (payload.Length != PayloadLength)
        {
            return false;
        }

        var page = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        var pageSlot = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        // The low byte is the stable selected/removed flag. The shipped client
        // leaves three bytes of scratch data after it, and those bytes vary
        // between otherwise identical item-selection packets.
        var selected = payload[8];
        if (page >= PageCount ||
            pageSlot >= SlotsPerPage ||
            selected > 1)
        {
            return false;
        }

        selection = new GearEnhancerItemSelectionPacket(
            checked((int)page),
            checked((int)pageSlot),
            selected == 1);
        return true;
    }
}

internal enum GearEnhancerSelectionRole
{
    Gear,
    Catalyst,
    AttributeStone
}

internal enum GearEnhancerSelectionStageStatus
{
    Staged,
    Removed,
    AlreadyStaged,
    NotStaged,
    SlotEmpty,
    SlotsFull
}

internal readonly record struct GearEnhancerSelectionStageResult(
    GearEnhancerSelectionStageStatus Status,
    GearEnhancerSelectionRole? Role,
    int KitBagSlot,
    CompactItemEntry Item);

internal sealed partial class GearEnhancerSelectionContext
{
    private readonly Func<DateTimeOffset> _utcNow;
    private GearEnhancerSelectionSnapshot? _gearSelection;
    private GearEnhancerSelectionSnapshot? _catalystSelection;
    private GearEnhancerSelectionSnapshot? _attributeStoneSelection;
    private GearEnhancerSelectionTriplet? _confirmClearCandidate;
    private GearEnhancerSelectionTriplet? _pendingNativeCommit;
    private DateTimeOffset _confirmClearCandidateExpiresAt;
    private DateTimeOffset _pendingNativeCommitExpiresAt;
    private int _confirmClearStep;
    private GearEnhancerSelectionSnapshot[]? _genericClearCandidate;
    private GearEnhancerSelectionSnapshot[]? _pendingGenericCommit;
    private DateTimeOffset _genericClearCandidateExpiresAt;
    private DateTimeOffset _pendingGenericCommitExpiresAt;
    private int _genericClearStep;
    private bool _genericClearCorrelationInvalidated;

    public GearEnhancerSelectionContext(
        int accountId,
        int characterId,
        uint npcId,
        int dialogIndex,
        GearEnhancementOperation? operation,
        DateTimeOffset expiresAt,
        Func<DateTimeOffset>? utcNow = null)
    {
        AccountId = accountId;
        CharacterId = characterId;
        NpcId = npcId;
        DialogIndex = dialogIndex;
        Operation = operation;
        ExpiresAt = expiresAt;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public int AccountId { get; }

    public int CharacterId { get; }

    public uint NpcId { get; }

    public int DialogIndex { get; }

    // NpcFunBreak opens operation 2/3/6 locally after the server returns its
    // initial menu; no intervening 10069 identifies the chosen operation.
    // Physical Gear Mentor contexts are consequently unbound until the final
    // action packet supplies the authoritative operation sub-ID. Origin
    // Enhancer/inline workflows can continue creating an operation-bound
    // context.
    public GearEnhancementOperation? Operation { get; }

    public DateTimeOffset ExpiresAt { get; }

    public int GearKitBagSlot => _gearSelection?.KitBagSlot ?? -1;

    public int CatalystKitBagSlot => _catalystSelection?.KitBagSlot ?? -1;

    public int AttributeStoneKitBagSlot => _attributeStoneSelection?.KitBagSlot ?? -1;

    public bool IsActiveForSelection(
        int accountId,
        int characterId,
        DateTimeOffset now)
    {
        return now < ExpiresAt &&
               accountId == AccountId &&
               characterId == CharacterId;
    }

    public bool IsActiveFor(
        int accountId,
        int characterId,
        uint npcId,
        int dialogIndex,
        GearEnhancementOperation operation,
        DateTimeOffset now)
    {
        return now < ExpiresAt &&
               accountId == AccountId &&
               characterId == CharacterId &&
               npcId == NpcId &&
               dialogIndex == DialogIndex &&
               (!Operation.HasValue || operation == Operation.Value);
    }

    public bool TryResolveNativeCommit(
        GearEnhancerSelectionShape inlineShape,
        out GearEnhancerSelectionTriplet selections)
    {
        selections = default;
        if (inlineShape is not (
                GearEnhancerSelectionShape.MenuSelection or
                GearEnhancerSelectionShape.MalformedCommit))
        {
            return false;
        }

        // Stock NpcFunBreak sends its real choices separately through opcode
        // 10193. Its final 10069 tail contains scratch values and may therefore
        // look either empty or malformed; neither shape may override the
        // authoritative, session-scoped selections accumulated here.
        PruneExpiredNativeCorrelation(_utcNow());
        var triplet = _pendingNativeCommit ?? CurrentCompleteTriplet();
        if (!triplet.HasValue)
        {
            return false;
        }

        selections = triplet.Value;
        return true;
    }

    public bool TryResolveNativeSlots(
        GearEnhancerSelectionShape inlineShape,
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections)
    {
        selections = [];
        if (inlineShape is not (
                GearEnhancerSelectionShape.MenuSelection or
                GearEnhancerSelectionShape.MalformedCommit) ||
            minimumCount < 1 ||
            maximumCount < minimumCount)
        {
            return false;
        }

        // One-slot and decomposition controls also clear their visual fields
        // immediately before the final 10069 action. Preserve that exact clear
        // burst just as the established three-role enhancer path does.
        PruneExpiredNativeCorrelation(_utcNow());
        if (_pendingGenericCommit is null &&
            (_genericClearCorrelationInvalidated || _genericClearCandidate is not null))
        {
            // Once any removal starts, only the exact completed clear burst
            // may authorize the final action. Falling back to the remaining
            // live selections would turn a partial or expired clear into a
            // shorter decomposition request.
            return false;
        }
        var snapshots = _pendingGenericCommit ?? CurrentSelections();
        if (snapshots.Length < minimumCount || snapshots.Length > maximumCount)
        {
            return false;
        }

        selections = snapshots;
        return true;
    }

    public bool TryResolveClearedNativeSlots(
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections)
    {
        selections = [];
        if (minimumCount < 1 || maximumCount < minimumCount)
        {
            return false;
        }

        // A non-canonical final action is accepted only after the stock
        // controls emit their complete, ordered clear burst. Live selections
        // are never enough to authorize a scratch-tailed packet.
        PruneExpiredNativeCorrelation(_utcNow());
        var snapshots = _pendingGenericCommit;
        if (snapshots is null ||
            snapshots.Length < minimumCount ||
            snapshots.Length > maximumCount)
        {
            return false;
        }

        selections = snapshots;
        return true;
    }

    public GearEnhancerSelectionStageResult Apply(
        GearEnhancerItemSelectionPacket selection,
        string kitBag,
        IItemMaterialCatalog? materials = null)
    {
        var now = _utcNow();
        PruneExpiredNativeCorrelation(now);
        var slot = ResolveNativeSelectionSlot(
            selection,
            kitBag,
            materials);
        var item = KitBagSlots.GetItem(kitBag, slot);
        // Any event after a completed native clear burst starts a new edit and
        // invalidates that one-shot confirmation snapshot. GameClientHandler
        // removes the entire context immediately after consuming a final
        // action, so a completed request cannot be replayed.
        if (_pendingNativeCommit.HasValue)
        {
            _pendingNativeCommit = null;
            _pendingNativeCommitExpiresAt = default;
        }
        if (_pendingGenericCommit is not null)
        {
            _pendingGenericCommit = null;
            _pendingGenericCommitExpiresAt = default;
        }

        if (!selection.Selected)
        {
            TrackGenericNativeConfirmClear(slot, now);
            TrackNativeConfirmClear(slot, now);

            if (_gearSelection?.KitBagSlot == slot)
            {
                _gearSelection = null;
                return Result(GearEnhancerSelectionStageStatus.Removed, GearEnhancerSelectionRole.Gear, slot, item);
            }

            if (_catalystSelection?.KitBagSlot == slot)
            {
                _catalystSelection = null;
                return Result(GearEnhancerSelectionStageStatus.Removed, GearEnhancerSelectionRole.Catalyst, slot, item);
            }

            if (_attributeStoneSelection?.KitBagSlot == slot)
            {
                _attributeStoneSelection = null;
                return Result(GearEnhancerSelectionStageStatus.Removed, GearEnhancerSelectionRole.AttributeStone, slot, item);
            }

            return Result(GearEnhancerSelectionStageStatus.NotStaged, null, slot, item);
        }

        if (item.IsEmpty)
        {
            return Result(GearEnhancerSelectionStageStatus.SlotEmpty, null, slot, item);
        }

        if (_gearSelection?.KitBagSlot == slot)
        {
            return Result(GearEnhancerSelectionStageStatus.AlreadyStaged, GearEnhancerSelectionRole.Gear, slot, item);
        }

        if (_catalystSelection?.KitBagSlot == slot)
        {
            return Result(GearEnhancerSelectionStageStatus.AlreadyStaged, GearEnhancerSelectionRole.Catalyst, slot, item);
        }

        if (_attributeStoneSelection?.KitBagSlot == slot)
        {
            return Result(GearEnhancerSelectionStageStatus.AlreadyStaged, GearEnhancerSelectionRole.AttributeStone, slot, item);
        }

        // Native opcode 10193 carries only a bag coordinate and a selected
        // flag; it does not carry the destination control. NpcFunBreak exposes
        // the controls in this fixed order: gear, operation catalyst, then
        // Attribute Stone. Preserve that order and let the authoritative
        // planner validate the exact item type assigned to each role.
        var captured = new GearEnhancerSelectionSnapshot(slot, item);
        if (!_gearSelection.HasValue)
        {
            BeginFreshSelectionEdit();
            _gearSelection = captured;
            return Result(GearEnhancerSelectionStageStatus.Staged, GearEnhancerSelectionRole.Gear, slot, item);
        }

        if (!_catalystSelection.HasValue)
        {
            BeginFreshSelectionEdit();
            _catalystSelection = captured;
            return Result(GearEnhancerSelectionStageStatus.Staged, GearEnhancerSelectionRole.Catalyst, slot, item);
        }

        if (!_attributeStoneSelection.HasValue)
        {
            BeginFreshSelectionEdit();
            _attributeStoneSelection = captured;
            return Result(GearEnhancerSelectionStageStatus.Staged, GearEnhancerSelectionRole.AttributeStone, slot, item);
        }

        return Result(GearEnhancerSelectionStageStatus.SlotsFull, null, slot, item);
    }

    private GearEnhancerSelectionTriplet? CurrentCompleteTriplet()
    {
        return _gearSelection.HasValue &&
               _catalystSelection.HasValue &&
               _attributeStoneSelection.HasValue
            ? new GearEnhancerSelectionTriplet(
                _gearSelection.Value,
                _catalystSelection.Value,
                _attributeStoneSelection.Value)
            : null;
    }

    private GearEnhancerSelectionSnapshot[] CurrentSelections()
    {
        var selections = new List<GearEnhancerSelectionSnapshot>(3);
        if (_gearSelection.HasValue)
        {
            selections.Add(_gearSelection.Value);
        }
        if (_catalystSelection.HasValue)
        {
            selections.Add(_catalystSelection.Value);
        }
        if (_attributeStoneSelection.HasValue)
        {
            selections.Add(_attributeStoneSelection.Value);
        }

        return selections.ToArray();
    }

    private void BeginFreshSelectionEdit()
    {
        _genericClearCorrelationInvalidated = false;
        ResetGenericNativeConfirmClear();
        ResetNativeConfirmClear();
    }

    private void TrackGenericNativeConfirmClear(int slot, DateTimeOffset now)
    {
        if (_genericClearCorrelationInvalidated)
        {
            return;
        }

        if (_genericClearStep == 0)
        {
            var current = CurrentSelections();
            if (current.Length == 0)
            {
                ResetGenericNativeConfirmClear();
                return;
            }
            if (current[0].KitBagSlot != slot)
            {
                InvalidateGenericNativeConfirmClear();
                return;
            }

            _genericClearCandidate = current;
            _genericClearStep = 1;
            _genericClearCandidateExpiresAt = now + GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
            if (current.Length == 1)
            {
                _pendingGenericCommit = current;
                _pendingGenericCommitExpiresAt = now + GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
                ResetGenericNativeConfirmClear();
            }
            return;
        }

        if (_genericClearCandidate is null ||
            _genericClearStep >= _genericClearCandidate.Length ||
            _genericClearCandidate[_genericClearStep].KitBagSlot != slot)
        {
            InvalidateGenericNativeConfirmClear();
            return;
        }

        _genericClearStep++;
        if (_genericClearStep == _genericClearCandidate.Length)
        {
            _pendingGenericCommit = _genericClearCandidate;
            _pendingGenericCommitExpiresAt = now + GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
            ResetGenericNativeConfirmClear();
        }
    }

    private void ResetGenericNativeConfirmClear()
    {
        _genericClearCandidate = null;
        _genericClearStep = 0;
        _genericClearCandidateExpiresAt = default;
    }

    private void InvalidateGenericNativeConfirmClear()
    {
        ResetGenericNativeConfirmClear();
        _genericClearCorrelationInvalidated = true;
    }

    private void TrackNativeConfirmClear(int slot, DateTimeOffset now)
    {
        // On Start, the stock NpcFunBreak control clears its three visual
        // fields (gear, catalyst, stone) before it sends final opcode 10069.
        // Preserve a commit snapshot only for that exact complete three-role
        // clear sequence. A normal one-item removal remains a removal and
        // cannot accidentally commit an old selection.
        switch (_confirmClearStep)
        {
            case 0 when _gearSelection?.KitBagSlot == slot && CurrentCompleteTriplet() is { } complete:
                _confirmClearCandidate = complete;
                _confirmClearStep = 1;
                _confirmClearCandidateExpiresAt = now + GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
                return;
            case 1 when _catalystSelection?.KitBagSlot == slot && _confirmClearCandidate.HasValue:
                _confirmClearStep = 2;
                return;
            case 2 when _attributeStoneSelection?.KitBagSlot == slot && _confirmClearCandidate.HasValue:
                _pendingNativeCommit = _confirmClearCandidate;
                _pendingNativeCommitExpiresAt = now + GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
                _confirmClearCandidate = null;
                _confirmClearStep = 0;
                _confirmClearCandidateExpiresAt = default;
                return;
            default:
                ResetNativeConfirmClear();
                return;
        }
    }

    private void ResetNativeConfirmClear()
    {
        _confirmClearCandidate = null;
        _confirmClearStep = 0;
        _confirmClearCandidateExpiresAt = default;
    }

    private void PruneExpiredNativeCorrelation(DateTimeOffset now)
    {
        if (_pendingNativeCommit.HasValue && now >= _pendingNativeCommitExpiresAt)
        {
            _pendingNativeCommit = null;
            _pendingNativeCommitExpiresAt = default;
        }
        if (_pendingGenericCommit is not null && now >= _pendingGenericCommitExpiresAt)
        {
            _pendingGenericCommit = null;
            _pendingGenericCommitExpiresAt = default;
        }
        if (_confirmClearCandidate.HasValue && now >= _confirmClearCandidateExpiresAt)
        {
            ResetNativeConfirmClear();
        }
        if (_genericClearCandidate is not null && now >= _genericClearCandidateExpiresAt)
        {
            InvalidateGenericNativeConfirmClear();
        }
    }

    private static GearEnhancerSelectionStageResult Result(
        GearEnhancerSelectionStageStatus status,
        GearEnhancerSelectionRole? role,
        int slot,
        CompactItemEntry item)
    {
        return new GearEnhancerSelectionStageResult(status, role, slot, item);
    }
}

internal readonly record struct GearEnhancerSelectionSnapshot(
    int KitBagSlot,
    CompactItemEntry ExpectedItem);

internal readonly record struct GearEnhancerSelectionTriplet(
    GearEnhancerSelectionSnapshot Gear,
    GearEnhancerSelectionSnapshot Catalyst,
    GearEnhancerSelectionSnapshot AttributeStone)
{
    public int GearKitBagSlot => Gear.KitBagSlot;

    public int CatalystKitBagSlot => Catalyst.KitBagSlot;

    public int AttributeStoneKitBagSlot => AttributeStone.KitBagSlot;
}
