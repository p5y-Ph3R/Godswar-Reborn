using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class JsonSemanticGatewayDataSessionChecks
{
    public const string CheckName =
        "JSON focused semantic-gateway persistence session";

    public static async Task RunAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            "godswar-b20b-json-gateway-" + Guid.NewGuid().ToString("N"));
        const string username = "B20BJsonGateway";
        const string password = "local-session-pass";

        try
        {
            AccountIdentity account;
            GameCharacter character;
            await using (var seed = new JsonGameStore(dataPath))
            {
                await seed.EnsureSeedDataAsync();
                account = await ((ILegacyAccountLoginStore)seed)
                    .LoginOrCreateLegacyAccountAsync(username, password);
                character = await seed.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "JsonGatewayHero",
                        Camp = GameDefaults.SpartaCamp,
                        Profession = 0,
                        Level = 1
                    });
            }

            var options = new ServerOptions
            {
                Authentication = FastAuthenticationOptions()
            };
            var session = await JsonSemanticGatewayDataSession.OpenAsync(
                options,
                dataPath);
            try
            {
                var authenticated = await session.AuthenticateAsync(
                    username,
                    Encoding.ASCII.GetBytes(password));
                Check.True(
                    authenticated is not null,
                    "JSON gateway session accepts a valid credential");
                Check.Equal(
                    account.Id,
                    authenticated!.AccountId,
                    "JSON gateway session preserves account identity");
                Check.Equal(
                    username,
                    authenticated.Username,
                    "JSON gateway session preserves account username");

                var rejected = await session.AuthenticateAsync(
                    username,
                    "wrong-password"u8.ToArray());
                Check.True(
                    rejected is null,
                    "JSON gateway session rejects an invalid credential");

                var route = await session.FindCharacterRouteAsync(
                    account.Id);
                Check.True(
                    route is not null,
                    "JSON gateway session resolves an active character route");
                Check.Equal(
                    character.Id,
                    route!.CharacterId,
                    "JSON gateway session preserves character identity");
                Check.Equal(
                    MapId.FromLegacy(character.CurrentMap),
                    route.MapId,
                    "JSON gateway session preserves map identity");
                Check.True(
                    await session.FindCharacterRouteAsync(int.MaxValue) is null,
                    "JSON gateway session returns no route for an unknown account");
            }
            finally
            {
                await session.DisposeAsync();
            }

            await ExpectDisposedAsync(session);
            await session.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static AuthenticationOptions FastAuthenticationOptions() =>
        new()
        {
            Iterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MinimumStoredIterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MaximumStoredIterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MaximumConcurrentKdfs = 1,
            QueueCapacity = 2,
            QueueCredentialBytes = 128,
            QueueAdmissionTimeoutMilliseconds = 250,
            OperationTimeoutMilliseconds = 5_000,
            AllowRegistration = false,
            AllowPlaintextMigration = true
        };

    private static async Task ExpectDisposedAsync(
        ISemanticGatewayDataSession session)
    {
        var threw = false;
        try
        {
            _ = await session.FindCharacterRouteAsync(1);
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }

        Check.True(
            threw,
            "disposed JSON gateway session rejects further route reads");
    }
}
