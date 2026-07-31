using Godswar.Server.Infrastructure.Accounts;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAccountStoreIntegrationChecks
{
    private static async Task AssertInvalidVerifierInputsAsync(
        NpgsqlDataSource dataSource,
        PostgresAccountStore store,
        string plaintextUsername,
        string malformedUsername)
    {
        await AssertVerifierCreateRejectedAsync(
            dataSource,
            store,
            plaintextUsername,
            "plaintext");
        await AssertVerifierCreateRejectedAsync(
            dataSource,
            store,
            malformedUsername,
            "gws$malformed");
    }

    private static async Task AssertVerifierCreateRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresAccountStore store,
        string username,
        string invalidVerifier)
    {
        var rejected = false;
        try
        {
            _ = await store.TryCreateAccountWithCredentialAsync(
                username,
                invalidVerifier);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Check.True(
            rejected,
            "focused account create rejects a non-versioned or malformed verifier");
        Check.Equal(
            0L,
            await ReadAccountCountAsync(dataSource, username),
            "rejected verifier creates no PostgreSQL account row");
    }

    private static async Task AssertConcurrentCredentialWritesAsync(
        NpgsqlDataSource dataSource,
        PostgresAccountStore store,
        string username)
    {
        var firstVerifier = CreateStructuralVerifier(100_000, 0x71);
        var secondVerifier = CreateStructuralVerifier(100_000, 0x72);
        var created = await Task.WhenAll(
            store.TryCreateAccountWithCredentialAsync(
                username,
                firstVerifier),
            store.TryCreateAccountWithCredentialAsync(
                username,
                secondVerifier));
        Check.Equal(
            1,
            created.Count(static account => account is not null),
            "concurrent same-username creates have exactly one winner");
        Check.Equal(
            1L,
            await ReadAccountCountAsync(dataSource, username),
            "concurrent same-username creates persist exactly one row");

        var stored = await store.FindAccountCredentialAsync(username) ??
            throw new InvalidOperationException(
                "Concurrent account fixture was not persisted.");
        var replacementA = CreateStructuralVerifier(100_000, 0x73);
        var replacementB = CreateStructuralVerifier(100_000, 0x74);
        var replacements = await Task.WhenAll(
            store.TryReplaceAccountCredentialAsync(
                stored.Account.Id,
                stored.Verifier,
                replacementA),
            store.TryReplaceAccountCredentialAsync(
                stored.Account.Id,
                stored.Verifier,
                replacementB));
        Check.Equal(
            1,
            replacements.Count(static replaced => replaced),
            "concurrent verifier compare-and-swap has exactly one winner");

        var final = await store.FindAccountCredentialAsync(username) ??
            throw new InvalidOperationException(
                "Concurrent credential fixture disappeared.");
        Check.True(
            final.Verifier == replacementA ||
            final.Verifier == replacementB,
            "concurrent verifier compare-and-swap persists one winning value");
    }
}
