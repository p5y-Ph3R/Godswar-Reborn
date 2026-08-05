namespace Godswar.Server.Game;

internal sealed partial class GearEnhancerSelectionContext
{
    private bool _allowsPostResultRawUpgradeCommit;

    public void AllowPostResultRawUpgradeCommit()
    {
        _allowsPostResultRawUpgradeCommit = true;
    }

    public bool TryResolvePostResultRawUpgradeCommit(
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections)
    {
        selections = [];
        if (!_allowsPostResultRawUpgradeCommit ||
            minimumCount < 1 ||
            maximumCount < minimumCount)
        {
            return false;
        }

        // Sub-ID 3100 replaces the normal A1 confirmation control with A3.
        // The stock A3 handler sends action 401 before it clears its item
        // controls, unlike the initial page's clear-then-action ordering.
        // Permit that live ordering only on the explicitly tagged result-page
        // context and only while no clear sequence has begun.
        PruneExpiredNativeCorrelation(_utcNow());
        if (_pendingGenericCommit is not null ||
            _genericClearCandidate is not null ||
            _genericClearCorrelationInvalidated)
        {
            return false;
        }

        var snapshots = CurrentSelections();
        if (snapshots.Length < minimumCount ||
            snapshots.Length > maximumCount)
        {
            return false;
        }

        // Consume this narrow capability before the caller performs immutable
        // item revalidation. The handler also discards the complete context in
        // a finally block, so a raw action cannot reuse these selections.
        _allowsPostResultRawUpgradeCommit = false;
        selections = snapshots;
        return true;
    }
}
