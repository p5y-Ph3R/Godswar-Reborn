using System.Text;

namespace Godswar.Server.Packets;

internal static class PacketText
{
    private static readonly int[] LetterShift =
    [
        0, 1, -1, -3, 2, 7, 1, 2, -2, 7, 2, 4, 1, 2, 7, 23,
        2, 1, 2, 7, 1, 2, 2, 4, 2, 7, 15, -2, 2, 1, 11
    ];

    private static readonly int[] DigitShift =
    [
        0, -1, -3, 3, -2, 3, -1, -2, 4, 3, -2, 2, -1, -2, 3, 3,
        -2, -1, -2, 3, -1, 0, 0, 2, -2, 3, -5, -6, -2, -1, 1
    ];

    public static string ReadFixedAscii(ReadOnlySpan<byte> buffer, int offset, int length)
    {
        if (offset >= buffer.Length)
        {
            return string.Empty;
        }

        length = Math.Min(length, buffer.Length - offset);
        var slice = buffer.Slice(offset, length);
        var end = slice.IndexOf((byte)0);
        if (end >= 0)
        {
            slice = slice[..end];
        }

        return Encoding.ASCII.GetString(slice).Trim();
    }

    public static string DecodeLoginName(string raw)
    {
        raw = raw.Trim('\0', ' ', '\t', '\r', '\n');
        if (raw.Length == 0)
        {
            return "player";
        }

        var lengthIndex = Math.Min(raw.Length, LetterShift.Length - 1);
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = DecodeChar(chars[i], LetterShift[lengthIndex], DigitShift[lengthIndex]);
        }

        return new string(chars);
    }

    public static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, destination.Length)), destination);
    }

    private static char DecodeChar(char value, int letterShift, int digitShift)
    {
        if (value is >= 'a' and <= 'z')
        {
            return ShiftWithin(value, 'a', 26, letterShift);
        }

        if (value is >= '0' and <= '9')
        {
            return ShiftWithin(value, '0', 10, digitShift);
        }

        return value;
    }

    private static char ShiftWithin(char value, char baseChar, int size, int shift)
    {
        var index = value - baseChar;
        var shifted = (index + shift) % size;
        if (shifted < 0)
        {
            shifted += size;
        }

        return (char)(baseChar + shifted);
    }
}
