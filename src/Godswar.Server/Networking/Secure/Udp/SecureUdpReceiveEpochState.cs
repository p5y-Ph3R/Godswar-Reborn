using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpReceiveEpochState
{
    private SecureUdpReplayWindow _replayWindow;

    public SecureUdpReceiveEpochState(uint keyEpoch, byte[] key)
    {
        if (keyEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        }
        if (key is null ||
            key.Length != SecureUdpProtectedConstants.KeyBytes)
        {
            throw new ArgumentException(
                "A protected UDP receive key must be exactly 32 bytes.",
                nameof(key));
        }

        KeyEpoch = keyEpoch;
        Key = key;
    }

    public uint KeyEpoch { get; }

    public byte[] Key { get; }

    public bool WouldAccept(ulong sequence) =>
        _replayWindow.WouldAccept(sequence);

    public bool TryAccept(ulong sequence) =>
        _replayWindow.TryAccept(sequence);

    public SecureUdpAcknowledgement GetAcknowledgement() =>
        _replayWindow.ToAcknowledgement(KeyEpoch);

    public bool HasReceived => _replayWindow.IsInitialized;

    public ulong HighestSequence => _replayWindow.HighestSequence;

    public ulong ReplayBitsLow => _replayWindow.BitsLow;

    public ulong ReplayBitsHigh => _replayWindow.BitsHigh;

    public void Clear()
    {
        CryptographicOperations.ZeroMemory(Key);
        _replayWindow = default;
    }
}
