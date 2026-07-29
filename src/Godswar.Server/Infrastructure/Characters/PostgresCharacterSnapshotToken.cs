using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Infrastructure.Characters;

internal static class PostgresCharacterSnapshotToken
{
    private const int Sha256Length = 32;
    private const string Prefix = "pg-snapshot-sha256:";

    public static string FromDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Sha256Length)
        {
            throw new InvalidDataException(
                "PostgreSQL returned an invalid snapshot digest.");
        }

        return Prefix + Convert.ToHexString(digest);
    }

    internal static string FromRawSnapshotForTest(string rawSnapshot)
    {
        ArgumentNullException.ThrowIfNull(rawSnapshot);
        return FromDigest(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawSnapshot)));
    }
}
