namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFencedAccountPresenceProjection() => new(
            "20260820_096_fenced_account_presence_projection",
            "Fence the legacy account presence projection by player lease",
            """
            ALTER TABLE public.accounts
                ADD COLUMN login_presence_token uuid;

            COMMENT ON COLUMN public.accounts.login_presence_token IS
                'Disposable player-lease token fencing the legacy login_status projection.';
            """);
}
