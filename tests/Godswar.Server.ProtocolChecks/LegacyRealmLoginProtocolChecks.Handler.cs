using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Realms;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.ProtocolChecks;

internal static partial class LegacyRealmLoginProtocolChecks
{
    private static async Task CheckRawHandlerFlowAsync(
        RealmCatalogEntry tempest,
        RealmCatalogEntry dwargon,
        RealmCatalogSnapshot catalog)
    {
        var transport = new ScriptedLegacyByteTransport();
        var options = LocalOptions();
        var reader = new FixedRealmCatalogReader(catalog);
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: reader);
            await DispatchAsync(handler, LoginPacket());
            await DispatchAsync(
                handler,
                SelectionPacket(dwargon.LegacyWireId));
            await DispatchAsync(
                handler,
                OpcodePacket(Opcodes.LoginReturnInfo));
        }

        var expected = PacketBuilder.ServerList(catalog)
            .Concat(PacketBuilder.SendServer(dwargon))
            .Concat(PacketBuilder.GameServerRedirect(dwargon))
            .ToArray();
        Check.True(
            transport.WrittenBytes.SequenceEqual(Encrypt(expected)),
            "raw login advertises, selects, and redirects to Dwargon");
        Check.Equal(
            2,
            reader.ReadCount,
            "realm snapshot reads at advertisement and selection");
        Check.Equal(0, transport.DisconnectCount, "valid realm flow stays open");

        var invalidTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            invalidTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: new FixedRealmCatalogReader(
                    new RealmCatalogSnapshot([tempest, dwargon])));
            await DispatchAsync(handler, LoginPacket());
            await DispatchAsync(handler, SelectionPacket(3));
        }
        Check.Equal(
            1,
            invalidTransport.DisconnectCount,
            "selection outside the advertised snapshot disconnects");
        Check.True(
            invalidTransport.WrittenBytes.SequenceEqual(
                Encrypt(PacketBuilder.ServerList(catalog))),
            "invalid selection receives no SendServer or redirect");

        var skippedTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            skippedTransport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: new FixedRealmCatalogReader(catalog));
            await DispatchAsync(handler, LoginPacket());
            await DispatchAsync(
                handler,
                OpcodePacket(Opcodes.LoginReturnInfo));
        }
        Check.Equal(
            1,
            skippedTransport.DisconnectCount,
            "redirect before realm selection disconnects");

        await CheckSelectionRevalidationAsync(
            tempest,
            dwargon,
            catalog,
            options);
        await CheckEmptyCatalogAsync(options);
        await CheckSecureRealmMismatchAsync(
            tempest,
            dwargon,
            catalog,
            options);
        await CheckGameHandlerRealmAdmissionAsync(
            tempest,
            dwargon,
            catalog,
            options);
    }

    private static async Task CheckSelectionRevalidationAsync(
        RealmCatalogEntry tempest,
        RealmCatalogEntry dwargon,
        RealmCatalogSnapshot advertised,
        ServerOptions options)
    {
        var changedDwargon = Entry(
            dwargon.RealmId,
            dwargon.Name,
            dwargon.Identifier,
            "127.1.1.112",
            dwargon.Recommended,
            dwargon.DisplayOrder);
        var reader = new SequencedRealmCatalogReader(
            advertised,
            new RealmCatalogSnapshot([tempest, changedDwargon]));
        var transport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: reader);
            await DispatchAsync(handler, LoginPacket());
            await DispatchAsync(
                handler,
                SelectionPacket(dwargon.LegacyWireId));
        }

        Check.Equal(
            2,
            reader.ReadCount,
            "realm selection re-reads the enabled catalog");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "realm endpoint changes after advertisement fail closed");
        Check.True(
            transport.WrittenBytes.SequenceEqual(
                Encrypt(PacketBuilder.ServerList(advertised))),
            "changed realm selection receives no SendServer");
    }

    private static async Task CheckEmptyCatalogAsync(
        ServerOptions options)
    {
        var reader = new FixedRealmCatalogReader(
            new RealmCatalogSnapshot([]));
        var transport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                legacyAuthenticationAccess:
                    LegacyAuthenticationAccess.Create(
                        ServerRuntimeProfilePolicy.Validate(options)),
                realmCatalog: reader);
            await DispatchAsync(handler, LoginPacket());
        }

        Check.Equal(
            1,
            transport.DisconnectCount,
            "authenticated login with no enabled realms fails closed");
        Check.Equal(
            0,
            transport.WrittenBytes.Length,
            "empty production catalog sends no count-zero placeholder");
    }

    private static async Task CheckSecureRealmMismatchAsync(
        RealmCatalogEntry tempest,
        RealmCatalogEntry dwargon,
        RealmCatalogSnapshot catalog,
        ServerOptions options)
    {
        options.Game.WorldInstances.RealmId = tempest.RealmId.Value;
        var instanceId = Enumerable.Repeat(
                (byte)0x45,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .ToArray();
        var context = new SecureConnectionContext(
            SecureEndpointRole.Login,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            instanceId,
            instanceId,
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        var transport = new ScriptedSecureControlTransport(
            context,
            []);
        await using (var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Login))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginAccountStore(),
                options,
                realmCatalog: new FixedRealmCatalogReader(catalog));
            SetField(
                handler,
                "_authenticatedAccount",
                new AccountIdentity(7, "test2"));
            SetField(handler, "_advertisedRealms", catalog);
            await DispatchAsync(
                handler,
                SelectionPacket(dwargon.LegacyWireId));

            Check.Equal(
                1,
                transport.DisconnectCount,
                "secure selection outside the process realm fails closed");
            Check.Equal(
                0,
                transport.LegacyWrites.Length,
                "secure realm mismatch receives no SendServer");
        }
    }

    private static async Task DispatchAsync(
        LoginClientHandler handler,
        byte[] packetBytes)
    {
        var method = typeof(LoginClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "LoginClientHandler packet dispatcher was not found.");
        var invocation = method.Invoke(
            handler,
            [new GamePacket(packetBytes), CancellationToken.None]);
        await (Task)(invocation ??
            throw new InvalidOperationException(
                "LoginClientHandler packet dispatcher returned no task."));
    }

    private static void SetField(
        LoginClientHandler handler,
        string fieldName,
        object value)
    {
        var field = typeof(LoginClientHandler).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"LoginClientHandler field {fieldName} was not found.");
        field.SetValue(handler, value);
    }
}
