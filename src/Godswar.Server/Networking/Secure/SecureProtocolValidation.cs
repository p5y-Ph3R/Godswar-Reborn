namespace Godswar.Server.Networking.Secure;

internal static class SecureProtocolValidation
{
    public static bool IsEndpointRole(SecureEndpointRole role)
    {
        return role is SecureEndpointRole.Login or SecureEndpointRole.Game;
    }

    public static bool IsServerStatus(SecureServerPrefaceStatus status)
    {
        return status is >= SecureServerPrefaceStatus.Ok and
            <= SecureServerPrefaceStatus.PolicyRejected;
    }

    public static bool IsFrameType(SecureFrameType type)
    {
        return type is SecureFrameType.Ping or
            SecureFrameType.Pong or
            SecureFrameType.Close or
            SecureFrameType.LegacyBytes or
            SecureFrameType.LegacyCommandOperation or
            SecureFrameType.GameGrant or
            SecureFrameType.GameBind or
            SecureFrameType.BindResult or
            SecureFrameType.UdpBindingGrant or
            SecureFrameType.RealtimeMovementInput;
    }

    public static bool IsFrameDirection(SecureFrameDirection direction)
    {
        return direction is SecureFrameDirection.ClientToServer or
            SecureFrameDirection.ServerToClient;
    }

    public static bool IsBindStatus(SecureBindStatus status)
    {
        return status is >= SecureBindStatus.Accepted and
            <= SecureBindStatus.PolicyRejected;
    }

    public static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsDnsName(string? value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumBytes)
        {
            return false;
        }

        Span<byte> bytes = value.Length <= 253
            ? stackalloc byte[value.Length]
            : new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > 0x7F)
            {
                return false;
            }
            bytes[index] = (byte)character;
        }

        return IsDnsName(bytes, maximumBytes);
    }

    public static bool IsDnsName(
        ReadOnlySpan<byte> value,
        int maximumBytes)
    {
        if (value.IsEmpty || value.Length > maximumBytes)
        {
            return false;
        }

        var labelLength = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == (byte)'.')
            {
                if (labelLength == 0 ||
                    value[index - 1] == (byte)'-')
                {
                    return false;
                }
                labelLength = 0;
                continue;
            }

            var isLowerLetter = character is >= (byte)'a' and <= (byte)'z';
            var isDigit = character is >= (byte)'0' and <= (byte)'9';
            if (!isLowerLetter && !isDigit && character != (byte)'-')
            {
                return false;
            }
            if (labelLength == 0 && character == (byte)'-')
            {
                return false;
            }

            labelLength++;
            if (labelLength > 63)
            {
                return false;
            }
        }

        return labelLength > 0 && value[^1] != (byte)'-';
    }

    public static bool IsAudience(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLetter =
                character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z';
            var isDigit = character is >= '0' and <= '9';
            if (!isLetter &&
                !isDigit &&
                character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAudience(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLetter =
                character is >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'a' and <= (byte)'z';
            var isDigit = character is >= (byte)'0' and <= (byte)'9';
            if (!isLetter &&
                !isDigit &&
                character is not ((byte)'.' or (byte)'_' or (byte)'-'))
            {
                return false;
            }
        }

        return true;
    }

    public static void WriteAscii(string value, Span<byte> destination)
    {
        for (var index = 0; index < value.Length; index++)
        {
            destination[index] = (byte)value[index];
        }
    }
}
