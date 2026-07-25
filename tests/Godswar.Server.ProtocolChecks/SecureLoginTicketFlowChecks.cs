using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureLoginTicketFlowChecks
{
    public static async Task RunAsync()
    {
        await CheckSecureCredentialCleanupOnEarlyExitAsync();
        await CheckGrantBeforeRedirectAndRedeemAsync();
        await CheckGameUsernameMismatchFailsBeforeLookupAsync();
    }

    private static async Task CheckSecureCredentialCleanupOnEarlyExitAsync()
    {
        var clientInstanceId = Enumerable.Repeat(
                (byte)0x31,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .ToArray();
        var originHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        try
        {
            var context = new SecureConnectionContext(
                SecureEndpointRole.Login,
                SecureProtocolConstants.ProtocolMajor,
                SecureProtocolConstants.ProtocolMinor,
                clientInstanceId,
                originHash);

            var shortPacket = CreateLoginBuffer(length: 50);
            shortPacket.AsSpan(36).Fill(0xA5);
            await InvokeEarlyLoginAsync(context, shortPacket);
            Check.True(
                shortPacket.AsSpan(36).IndexOfAnyExcept((byte)0) < 0,
                "short secure login clears every available credential byte");

            var blankUsername = CreateLoginBuffer(length: 68);
            blankUsername.AsSpan(36, 32).Fill(0xB6);
            await InvokeEarlyLoginAsync(context, blankUsername);
            Check.True(
                blankUsername.AsSpan(36, 32)
                    .IndexOfAnyExcept((byte)0) < 0,
                "blank-username secure login clears its complete credential field");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clientInstanceId);
            CryptographicOperations.ZeroMemory(originHash);
        }
    }

    private static async Task InvokeEarlyLoginAsync(
        SecureConnectionContext context,
        byte[] buffer)
    {
        await using var transport = new ScriptedSecureControlTransport(
            context,
            []);
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new LoginClientHandler(
            session,
            new PrincipalLookupStore(),
            new ServerOptions());
        var method = typeof(LoginClientHandler).GetMethod(
            "HandleLoginAsync",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Secure login test hook was not found.");
        var invocation = method.Invoke(
            handler,
            [new GamePacket(buffer), CancellationToken.None]);
        await (invocation as Task ??
            throw new InvalidOperationException(
                "Secure login test hook did not return a task."));
    }

    private static byte[] CreateLoginBuffer(int length)
    {
        var buffer = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer,
            checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(2),
            Opcodes.Login);
        return buffer;
    }

    private static async Task CheckGrantBeforeRedirectAndRedeemAsync()
    {
        const string rawUsername = "test2";
        const string password = "password";
        var username = PacketText.DecodeLoginName(rawUsername);
        var accountStore = new AuthenticationStore(
            new GameAccount
            {
                Id = 7,
                Username = username
            },
            password);
        var authenticationOptions = new AuthenticationOptions
        {
            Iterations = 100_000,
            MinimumStoredIterations = 100_000,
            MaximumStoredIterations = 100_000,
            MaximumConcurrentKdfs = 1
        };
        await using var scheduler = new ImmediateKdfScheduler();
        await using var authentication =
            new AccountAuthenticationService(
                accountStore,
                authenticationOptions,
                scheduler: scheduler);
        using var ticketStore = new InMemoryGameTicketStore();
        var target = CreateTarget();
        var clientInstanceId = Enumerable.Range(
                1,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .Select(static value => checked((byte)value))
            .ToArray();
        var originHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var loginContext = new SecureConnectionContext(
            SecureEndpointRole.Login,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            clientInstanceId,
            originHash);
        var clearInput = CreateLoginPacket(rawUsername, password)
            .Concat(CreatePacket(Opcodes.SelectServer))
            .Concat(CreatePacket(Opcodes.LoginReturnInfo))
            .ToArray();
        EncryptInPlace(clearInput);
        var transport = new ScriptedSecureControlTransport(
            loginContext,
            clearInput);
        var pendingAtGrantBoundary = false;
        var activeAtRedirectBoundary = false;
        transport.AfterGameGrantWrite = () =>
            pendingAtGrantBoundary = !GetOnlyTicketCommitted(ticketStore);
        transport.BeforeLegacyWrite = () =>
        {
            if (transport.Events.LastOrDefault() == "grant")
            {
                activeAtRedirectBoundary =
                    GetOnlyTicketCommitted(ticketStore);
            }
        };
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login);
        var handler = new LoginClientHandler(
            session,
            accountStore,
            new ServerOptions(),
            authentication,
            ticketStore,
            target);

        await handler.RunAsync(CancellationToken.None);

        Check.True(
            transport.IsAuthenticated,
            "secure login marks authentication before issuing a grant");
        Check.True(
            transport.Events.SequenceEqual(
                ["legacy", "legacy", "grant", "legacy"]),
            "GameGrant is physically ordered after login replies and before redirect");
        Check.True(
            pendingAtGrantBoundary,
            "ticket remains non-redeemable until the physical grant write returns");
        Check.True(
            activeAtRedirectBoundary,
            "ticket is redeemable before the redirect write begins");
        var clearWrites = transport.LegacyWrites;
        EncryptInPlace(clearWrites);
        var expectedWrites = PacketBuilder.ServerList()
            .Concat(PacketBuilder.SendServer())
            .Concat(PacketBuilder.GameServerRedirect(
                target.RouteHost,
                target.RoutePort))
            .ToArray();
        Check.True(
            clearWrites.SequenceEqual(expectedWrites),
            "secure redirect exactly matches the authenticated grant route");
        Check.True(
            accountStore.MarkedOnline,
            "successful secure authentication marks the account online");
        Check.True(
            PasswordVerifierRecord.IsVersionedCandidate(
                accountStore.Verifier),
            "successful plaintext authentication migrates the credential");

        using var grant = transport.TakeGrant();
        Check.Equal(
            target.RouteHost,
            grant.RouteHost,
            "issued grant carries the configured logical route");
        var grantId = new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket = new byte[SecureProtocolConstants.TicketBytes];
        try
        {
            Check.True(
                grant.TryCopySecrets(grantId, ticket),
                "captured grant retains its short-lived presentation secret");
            using var bind = new SecureGameBind(grantId, ticket);
            var gameContext = new SecureConnectionContext(
                SecureEndpointRole.Game,
                SecureProtocolConstants.ProtocolMajor,
                SecureProtocolConstants.ProtocolMinor,
                clientInstanceId,
                originHash);
            var consumed = ticketStore.Consume(
                bind,
                gameContext,
                target);
            Check.True(
                consumed.IsAccepted,
                "redirect completion preserves the activated grant");
            Check.Equal(
                accountStore.Account.Id,
                consumed.Principal!.AccountId,
                "redeemed principal comes from authenticated login state");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
            CryptographicOperations.ZeroMemory(clientInstanceId);
            CryptographicOperations.ZeroMemory(originHash);
        }
    }

    private static async Task CheckGameUsernameMismatchFailsBeforeLookupAsync()
    {
        var accountStore = new PrincipalLookupStore();
        var instanceId = Enumerable.Repeat(
                (byte)0x44,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .ToArray();
        var buildHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            instanceId,
            buildHash);
        var principal = new SecureBoundGamePrincipal(
            7,
            "test2",
            SecureGamePermissions.EnterWorld,
            Guid.NewGuid());
        var login = CreateGameLoginPacket("wrong-user");
        EncryptInPlace(login);
        var transport = new ScriptedSecureControlTransport(
            context,
            login,
            principal);
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            accountStore,
            new GameSessionRegistry(accountStore));

        await handler.RunAsync(CancellationToken.None);

        Check.Equal(
            1,
            transport.DisconnectCount,
            "ticket username mismatch disconnects the secure game channel");
        Check.Equal(
            0,
            accountStore.FindByIdCalls,
            "compatibility username mismatch cannot reach account lookup or duplicate replacement");
        CryptographicOperations.ZeroMemory(instanceId);
        CryptographicOperations.ZeroMemory(buildHash);
    }

    private static SecureGameTarget CreateTarget()
    {
        return new SecureGameTarget(
            "game.reborn.test",
            "game.reborn.test",
            "reborn-game",
            routePort: 7000,
            tlsPort: 7443,
            serverId: 100);
    }

    private static bool GetOnlyTicketCommitted(
        InMemoryGameTicketStore store)
    {
        var ticketsField = typeof(InMemoryGameTicketStore).GetField(
            "_tickets",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var tickets = ticketsField?.GetValue(store) as
            System.Collections.IEnumerable ??
            throw new InvalidOperationException(
                "Ticket registry test inspection failed.");
        var entries = tickets.Cast<object>().ToArray();
        Check.Equal(
            1,
            entries.Length,
            "activation fixture has exactly one ticket");
        var record = entries[0].GetType().GetProperty("Value")?
            .GetValue(entries[0]) ??
            throw new InvalidOperationException(
                "Ticket record test inspection failed.");
        return (bool)(record.GetType().GetProperty(
                "Committed",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)?
            .GetValue(record) ??
            throw new InvalidOperationException(
                "Ticket activation state test inspection failed."));
    }

    private static byte[] CreateLoginPacket(
        string rawUsername,
        string password)
    {
        var packet = new byte[68];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Login);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), rawUsername);
        PacketText.WriteFixedAscii(packet.AsSpan(36, 32), password);
        return packet;
    }

    private static byte[] CreateGameLoginPacket(string username)
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.LoginGameServer);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        return packet;
    }

    private static byte[] CreatePacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        return packet;
    }

    private static void EncryptInPlace(byte[] bytes)
    {
        new PacketCipher().Transform(bytes);
    }

    private sealed class AuthenticationStore(
        GameAccount account,
        string verifier) :
        GameStoreTestStub
    {
        public GameAccount Account { get; } = account;

        public string Verifier { get; private set; } = verifier;

        public bool MarkedOnline { get; private set; }

        public override Task<StoredAccountCredential?>
            FindAccountCredentialAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<StoredAccountCredential?>(
                string.Equals(
                    username,
                    Account.Username,
                    StringComparison.Ordinal)
                    ? new StoredAccountCredential(Account, Verifier)
                    : null);
        }

        public override Task<bool> TryReplaceAccountCredentialAsync(
            int accountId,
            string expectedVerifier,
            string versionedVerifier,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accountId != Account.Id ||
                !string.Equals(
                    Verifier,
                    expectedVerifier,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Verifier = versionedVerifier;
            return Task.FromResult(true);
        }

        public override Task MarkAccountOnlineAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkedOnline = accountId == Account.Id;
            return Task.CompletedTask;
        }
    }

    private sealed class PrincipalLookupStore : GameStoreTestStub
    {
        public int FindByIdCalls { get; private set; }

        public override Task<GameAccount?> FindAccountByIdAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            FindByIdCalls++;
            return Task.FromResult<GameAccount?>(null);
        }
    }

    private sealed class ImmediateKdfScheduler : IPasswordKdfScheduler
    {
        public ValueTask<byte[]> DeriveAsync(
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> salt,
            int iterations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = new byte[
                password.Length +
                salt.Length +
                sizeof(int)];
            try
            {
                password.Span.CopyTo(input);
                salt.Span.CopyTo(input.AsSpan(password.Length));
                BinaryPrimitives.WriteInt32BigEndian(
                    input.AsSpan(password.Length + salt.Length),
                    iterations);
                return ValueTask.FromResult(
                    SHA256.HashData(input));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
