using Godswar.Server.State;

namespace Godswar.Server.Game;

internal enum HolyStoneCombinationSelectionStatus
{
    Staged,
    Removed,
    AlreadyStaged,
    NotStaged,
    SlotEmpty,
    SlotsFull
}

internal readonly record struct HolyStoneCombinationSelectionResult(
    HolyStoneCombinationSelectionStatus Status,
    int KitBagSlot,
    CompactItemEntry Item,
    int SelectionCount);

/// <summary>
/// Holds the stock Holy Stone Combination page's four ordered ItemBtn
/// selections. The order is part of the command: major stone first, followed
/// by three consumed stones. A completed native clear burst is a one-shot
/// authorization snapshot for the initial A1 confirmation.
/// </summary>
internal sealed class HolyStoneCombinationSelectionContext
{
    public const int RequiredSelectionCount = 4;

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly GearEnhancerSelectionSnapshot?[] _selections =
        new GearEnhancerSelectionSnapshot?[RequiredSelectionCount];
    private GearEnhancerSelectionSnapshot[]? _clearCandidate;
    private GearEnhancerSelectionSnapshot[]? _pendingClearedCommit;
    private DateTimeOffset _clearCandidateExpiresAt;
    private DateTimeOffset _pendingClearedCommitExpiresAt;
    private int _clearStep;
    private bool _clearInvalidated;
    private bool _allowsPostResultCommit;

    public HolyStoneCombinationSelectionContext(
        int accountId,
        int characterId,
        uint npcId,
        int dialogIndex,
        DateTimeOffset expiresAt,
        Func<DateTimeOffset>? utcNow = null)
    {
        AccountId = accountId;
        CharacterId = characterId;
        NpcId = npcId;
        DialogIndex = dialogIndex;
        ExpiresAt = expiresAt;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public int AccountId { get; }
    public int CharacterId { get; }
    public uint NpcId { get; }
    public int DialogIndex { get; }
    public DateTimeOffset ExpiresAt { get; }

    public bool IsActiveFor(
        int accountId,
        int characterId,
        DateTimeOffset now) =>
        now < ExpiresAt &&
        accountId == AccountId &&
        characterId == CharacterId;

    public HolyStoneCombinationSelectionResult Apply(
        GearEnhancerItemSelectionPacket selection,
        string kitBag)
    {
        var now = _utcNow();
        Prune(now);
        var slot = ResolveSelectionSlot(selection);
        var item = KitBagSlots.GetItem(kitBag, slot);

        _pendingClearedCommit = null;
        _pendingClearedCommitExpiresAt = default;

        if (!selection.Selected)
        {
            TrackClear(slot, now);
            for (var index = 0; index < _selections.Length; index++)
            {
                if (_selections[index]?.KitBagSlot != slot)
                {
                    continue;
                }

                _selections[index] = null;
                return Result(
                    HolyStoneCombinationSelectionStatus.Removed,
                    slot,
                    item);
            }

            return Result(
                HolyStoneCombinationSelectionStatus.NotStaged,
                slot,
                item);
        }

        if (item.IsEmpty)
        {
            return Result(
                HolyStoneCombinationSelectionStatus.SlotEmpty,
                slot,
                item);
        }
        if (_selections.Any(candidate =>
                candidate?.KitBagSlot == slot))
        {
            return Result(
                HolyStoneCombinationSelectionStatus.AlreadyStaged,
                slot,
                item);
        }

        var emptyIndex = Array.FindIndex(
            _selections,
            static candidate => !candidate.HasValue);
        if (emptyIndex < 0)
        {
            return Result(
                HolyStoneCombinationSelectionStatus.SlotsFull,
                slot,
                item);
        }

        BeginEdit();
        _selections[emptyIndex] =
            new GearEnhancerSelectionSnapshot(slot, item);
        return Result(
            HolyStoneCombinationSelectionStatus.Staged,
            slot,
            item);
    }

    public bool TryConsumeInitialCommit(
        string kitBag,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections) =>
        TryConsumeCommit(
            kitBag,
            allowLivePostResult: false,
            out selections);

    public bool TryConsumePostResultCommit(
        string kitBag,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections) =>
        TryConsumeCommit(
            kitBag,
            allowLivePostResult: true,
            out selections);

    public void AllowPostResultCommit() =>
        _allowsPostResultCommit = true;

    private bool TryConsumeCommit(
        string kitBag,
        bool allowLivePostResult,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections)
    {
        selections = [];
        Prune(_utcNow());
        GearEnhancerSelectionSnapshot[]? candidate =
            _pendingClearedCommit;
        if (candidate is null &&
            allowLivePostResult &&
            _allowsPostResultCommit &&
            _clearCandidate is null &&
            !_clearInvalidated)
        {
            candidate = CurrentSelections();
        }

        _allowsPostResultCommit = false;
        _pendingClearedCommit = null;
        _pendingClearedCommitExpiresAt = default;
        if (candidate is null ||
            candidate.Length != RequiredSelectionCount ||
            candidate.Select(static value => value.KitBagSlot)
                .Distinct()
                .Count() != RequiredSelectionCount ||
            candidate.Any(value =>
                !KitBagSlots.GetItem(kitBag, value.KitBagSlot)
                    .Equals(value.ExpectedItem)))
        {
            return false;
        }

        selections = candidate;
        return true;
    }

    private int ResolveSelectionSlot(
        GearEnhancerItemSelectionPacket selection)
    {
        var declared = selection.KitBagSlot;
        if (selection.Selected ||
            _selections.Any(candidate =>
                candidate?.KitBagSlot == declared))
        {
            return declared;
        }

        var pageAliases = _selections
            .Where(static candidate => candidate.HasValue)
            .Select(static candidate => candidate!.Value.KitBagSlot)
            .Where(slot =>
                slot % GearEnhancerItemSelectionPacket.SlotsPerPage ==
                selection.PageSlot)
            .Take(2)
            .ToArray();
        return pageAliases.Length == 1 ? pageAliases[0] : declared;
    }

    private void TrackClear(int slot, DateTimeOffset now)
    {
        if (_clearInvalidated)
        {
            return;
        }
        if (_clearCandidate is null)
        {
            var current = CurrentSelections();
            if (current.Length != RequiredSelectionCount ||
                current[0].KitBagSlot != slot)
            {
                InvalidateClear();
                return;
            }
            if (!TryAddLifetime(now, out _clearCandidateExpiresAt))
            {
                InvalidateClear();
                return;
            }

            _clearCandidate = current;
            _clearStep = 1;
        }
        else
        {
            if (_clearStep >= _clearCandidate.Length ||
                _clearCandidate[_clearStep].KitBagSlot != slot)
            {
                InvalidateClear();
                return;
            }
            _clearStep++;
        }

        if (_clearStep != RequiredSelectionCount)
        {
            return;
        }
        if (!TryAddLifetime(now, out _pendingClearedCommitExpiresAt))
        {
            InvalidateClear();
            return;
        }

        _pendingClearedCommit = _clearCandidate;
        ResetClearCandidate();
    }

    private void BeginEdit()
    {
        _clearInvalidated = false;
        ResetClearCandidate();
        _pendingClearedCommit = null;
        _pendingClearedCommitExpiresAt = default;
    }

    private void Prune(DateTimeOffset now)
    {
        if (_clearCandidate is not null &&
            now >= _clearCandidateExpiresAt)
        {
            InvalidateClear();
        }
        if (_pendingClearedCommit is not null &&
            now >= _pendingClearedCommitExpiresAt)
        {
            _pendingClearedCommit = null;
            _pendingClearedCommitExpiresAt = default;
        }
    }

    private void InvalidateClear()
    {
        ResetClearCandidate();
        _clearInvalidated = true;
    }

    private void ResetClearCandidate()
    {
        _clearCandidate = null;
        _clearStep = 0;
        _clearCandidateExpiresAt = default;
    }

    private GearEnhancerSelectionSnapshot[] CurrentSelections() =>
        _selections
            .Where(static candidate => candidate.HasValue)
            .Select(static candidate => candidate!.Value)
            .ToArray();

    private static bool TryAddLifetime(
        DateTimeOffset now,
        out DateTimeOffset expiresAt)
    {
        try
        {
            expiresAt = now +
                GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAt = default;
            return false;
        }
    }

    private HolyStoneCombinationSelectionResult Result(
        HolyStoneCombinationSelectionStatus status,
        int slot,
        CompactItemEntry item) =>
        new(
            status,
            slot,
            item,
            _selections.Count(static candidate => candidate.HasValue));
}
