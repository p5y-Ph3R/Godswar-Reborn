using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpReplayWindowChecks
{
    public static void Run()
    {
        CheckWindowBoundaries();
        CheckReorderingAndAcknowledgements();
        CheckSequenceAndEpochExhaustion();
    }

    private static void CheckWindowBoundaries()
    {
        var window = new SecureUdpReplayWindow();
        Check.True(window.WouldAccept(100), "fresh sequence accepted");
        Check.True(window.TryAccept(100), "sequence 100 commits");
        Check.True(!window.WouldAccept(100), "duplicate rejected");
        Check.True(!window.TryAccept(100), "duplicate cannot commit");
        Check.True(window.TryAccept(99), "one-behind reordering");
        Check.True(window.TryAccept(1), "99-behind remains in window");
        Check.True(
            !window.TryAccept(1),
            "reordered duplicate rejects");
        Check.True(
            window.TryAccept(ulong.MaxValue),
            "large forward jump is accepted");
        Check.True(
            !window.TryAccept(100),
            "large forward jump expires the old window");
    }

    private static void CheckReorderingAndAcknowledgements()
    {
        var window = new SecureUdpReplayWindow();
        Check.True(window.TryAccept(0), "zero is first valid sequence");
        var ack = window.ToAcknowledgement(1);
        Check.Equal(0UL, ack.Sequence, "zero high-water ACK");
        Check.Equal(0UL, ack.PreviousMask, "zero ACK mask");

        Check.True(window.TryAccept(64), "64-sequence jump accepted");
        Check.True(window.TryAccept(63), "sequence 63 reordered");
        Check.True(window.TryAccept(1), "sequence 1 reordered");
        ack = window.ToAcknowledgement(1);
        Check.Equal(64UL, ack.Sequence, "ACK high-water sequence");
        Check.True(
            (ack.PreviousMask & 1) != 0,
            "ACK bit zero represents high-water minus one");
        Check.True(
            (ack.PreviousMask & (1UL << 63)) != 0,
            "ACK bit 63 represents high-water minus 64");

        Check.True(window.TryAccept(128), "window advances to 128");
        Check.True(
            !window.WouldAccept(0),
            "exactly 128 behind falls outside replay window");
        Check.True(
            window.WouldAccept(127),
            "unseen in-window packet remains acceptable");

        var nearMaximum = new SecureUdpReplayWindow();
        Check.True(
            nearMaximum.TryAccept(ulong.MaxValue - 1),
            "near-maximum sequence accepted");
        Check.True(
            nearMaximum.TryAccept(ulong.MaxValue),
            "maximum sequence accepted once");
        Check.True(
            !nearMaximum.TryAccept(0),
            "sequence never wraps after maximum");
    }

    private static void CheckSequenceAndEpochExhaustion()
    {
        var counter = new SecureUdpSequenceCounter(ulong.MaxValue);
        Check.True(
            counter.TryPeek(out var sequence) &&
            sequence == ulong.MaxValue,
            "maximum send sequence is available exactly once");
        counter.Commit();
        Check.True(
            counter.IsExhausted && !counter.TryPeek(out _),
            "send sequence does not wrap");
        Check.Throws<InvalidOperationException>(
            counter.Commit,
            "exhausted sequence cannot commit again");
        counter.Reset();
        Check.True(
            counter.TryPeek(out sequence) && sequence == 0,
            "new key epoch resets sequence to zero");

        Check.True(
            SecureUdpSequenceRules.TryGetNextKeyEpoch(
                1,
                out var next) &&
            next == 2,
            "key epoch increments without wrap");
        Check.True(
            !SecureUdpSequenceRules.TryGetNextKeyEpoch(
                uint.MaxValue,
                out next) &&
            next == 0,
            "maximum key epoch cannot wrap");
        Check.True(
            !SecureUdpSequenceRules.TryGetNextKeyEpoch(0, out _),
            "zero key epoch is never valid");
    }
}
