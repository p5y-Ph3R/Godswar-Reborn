using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpCookieKeyRing : IDisposable
{
    internal const int SecretBytes = 32;
    internal const int HashBytes = 32;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _rotationPeriod;
    private readonly Func<byte[]> _secretFactory;
    private readonly Func<uint> _keyIdFactory;
    private byte[] _currentSecret;
    private byte[]? _previousSecret;
    private uint _currentKeyId;
    private uint _previousKeyId;
    private readonly long _rotationOriginTimestamp;
    private long _currentGeneration;
    private bool _disposed;

    public SecureUdpCookieKeyRing(
        TimeProvider timeProvider,
        TimeSpan rotationPeriod)
        : this(
            timeProvider,
            rotationPeriod,
            CreateSecret,
            CreateKeyId)
    {
    }

    internal SecureUdpCookieKeyRing(
        TimeProvider timeProvider,
        TimeSpan rotationPeriod,
        Func<byte[]> secretFactory,
        Func<uint> keyIdFactory)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(secretFactory);
        ArgumentNullException.ThrowIfNull(keyIdFactory);
        if (rotationPeriod < TimeSpan.FromSeconds(1) ||
            rotationPeriod > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationPeriod));
        }

        _timeProvider = timeProvider;
        _rotationPeriod = rotationPeriod;
        _secretFactory = secretFactory;
        _keyIdFactory = keyIdFactory;
        (_currentSecret, _currentKeyId) = CreateMaterial(0, 0);
        _rotationOriginTimestamp = timeProvider.GetTimestamp();
    }

    public uint GetCurrentKeyId()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            RotateIfDue();
            return _currentKeyId;
        }
    }

    public bool TryComputeHash(
        uint keyId,
        ReadOnlySpan<byte> input,
        Span<byte> destination)
    {
        if (destination.Length < HashBytes)
        {
            return false;
        }

        Span<byte> selectedSecret = stackalloc byte[SecretBytes];
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                RotateIfDue();
                var secret = keyId == _currentKeyId
                    ? _currentSecret
                    : keyId == _previousKeyId
                        ? _previousSecret
                        : null;
                if (secret is null)
                {
                    return false;
                }
                secret.CopyTo(selectedSecret);
            }

            _ = HMACSHA256.HashData(
                selectedSecret,
                input,
                destination[..HashBytes]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(selectedSecret);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_currentSecret);
            if (_previousSecret is not null)
            {
                CryptographicOperations.ZeroMemory(_previousSecret);
            }
            _currentKeyId = 0;
            _previousKeyId = 0;
            _disposed = true;
        }
    }

    private void RotateIfDue()
    {
        var now = _timeProvider.GetTimestamp();
        if (now < _rotationOriginTimestamp)
        {
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(
            _rotationOriginTimestamp,
            now);
        var completedGeneration =
            elapsed.Ticks / _rotationPeriod.Ticks;
        var generationsElapsed =
            completedGeneration - _currentGeneration;
        if (generationsElapsed <= 0)
        {
            return;
        }

        var (secret, keyId) =
            CreateMaterial(_currentKeyId, _previousKeyId);
        if (generationsElapsed >= 2)
        {
            CryptographicOperations.ZeroMemory(_currentSecret);
            if (_previousSecret is not null)
            {
                CryptographicOperations.ZeroMemory(_previousSecret);
            }
            _previousSecret = null;
            _previousKeyId = 0;
            _currentSecret = secret;
            _currentKeyId = keyId;
            _currentGeneration = completedGeneration;
            return;
        }

        if (_previousSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_previousSecret);
        }
        _previousSecret = _currentSecret;
        _previousKeyId = _currentKeyId;
        _currentSecret = secret;
        _currentKeyId = keyId;
        _currentGeneration = completedGeneration;
    }

    private (byte[] Secret, uint KeyId) CreateMaterial(
        uint disallowedCurrent,
        uint disallowedPrevious)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var secret = _secretFactory();
            if (secret is null ||
                secret.Length != SecretBytes ||
                SecureUdpBindingCodec.IsAllZero(secret))
            {
                if (secret is not null)
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
                throw new CryptographicException(
                    "UDP cookie secret factory returned invalid key material.");
            }

            var keyId = _keyIdFactory();
            if (keyId != 0 &&
                keyId != disallowedCurrent &&
                keyId != disallowedPrevious)
            {
                return (secret, keyId);
            }
            CryptographicOperations.ZeroMemory(secret);
        }

        throw new CryptographicException(
            "UDP cookie key ID generation repeatedly collided.");
    }

    private static byte[] CreateSecret()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var secret = RandomNumberGenerator.GetBytes(SecretBytes);
            if (!SecureUdpBindingCodec.IsAllZero(secret))
            {
                return secret;
            }
            CryptographicOperations.ZeroMemory(secret);
        }
        throw new CryptographicException(
            "CSPRNG returned repeated invalid UDP cookie secrets.");
    }

    private static uint CreateKeyId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        try
        {
            RandomNumberGenerator.Fill(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
