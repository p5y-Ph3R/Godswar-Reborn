using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerOwnershipSessionRaceChecks
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        var registry = new GameSessionRegistry(store: null);
        var staleTransport = new ScriptedLegacyByteTransport();
        var currentTransport = new ScriptedLegacyByteTransport();
        var observerTransport = new ScriptedLegacyByteTransport();
        await using var staleSession = CreateSession(staleTransport);
        await using var currentSession = CreateSession(currentTransport);
        await using var observerSession = CreateSession(observerTransport);

        const int accountId = 7;
        const int characterId = 13;
        const int observerAccountId = 8;
        var staleOwnership =
            new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var currentOwnership =
            new PlayerOwnershipFence(Guid.NewGuid(), 2);
        var observerOwnership =
            new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var staleCharacter = CreateCharacter(
            accountId,
            characterId,
            "stale",
            staleOwnership);
        var currentCharacter = CreateCharacter(
            accountId,
            characterId,
            "current",
            currentOwnership);
        var observerCharacter = CreateCharacter(
            observerAccountId,
            characterId + 1,
            "observer",
            observerOwnership);

        Register(
            registry,
            accountId,
            staleSession,
            staleCharacter,
            staleOwnership);
        Register(
            registry,
            observerAccountId,
            observerSession,
            observerCharacter,
            observerOwnership);
        var staleHandler = CreateHandler(
            registry,
            staleSession,
            accountId,
            staleCharacter);

        Check.True(
            ReferenceEquals(
                staleSession,
                registry.ReplaceAccountSession(
                    accountId,
                    currentSession)),
            "replacement identifies the stale account session");
        Check.True(
            registry.TryBindAccountSessionOwnership(
                accountId,
                currentSession,
                currentOwnership),
            "replacement binds its exact ownership fence");
        Check.True(
            registry.Remove(staleSession, staleOwnership),
            "replacement removes the stale world registration");
        registry.JoinMap(
            currentSession,
            accountId,
            currentCharacter,
            WorldObjectIds.ForPlayer(characterId));

        var observerBytes = observerTransport.WrittenBytes.Length;
        var currentBytes = currentTransport.WrittenBytes.Length;
        await InvokeLeaveBroadcastAsync(staleHandler);
        Check.Equal(
            observerBytes,
            observerTransport.WrittenBytes.Length,
            "replaced handler emits no stale leave to peers");
        Check.Equal(
            currentBytes,
            currentTransport.WrittenBytes.Length,
            "replaced handler cannot remove the current client model");

        var currentHandler = CreateHandler(
            registry,
            currentSession,
            accountId,
            currentCharacter);
        await InvokeLeaveBroadcastAsync(currentHandler);
        Check.True(
            observerTransport.WrittenBytes.Length > observerBytes,
            "current exact owner still emits its legitimate leave");
    }

    private static ClientSession CreateSession(
        ScriptedLegacyByteTransport transport) =>
        new(
            transport,
            endpointRole: NetworkEndpointRole.Game);

    private static GameCharacter CreateCharacter(
        int accountId,
        int characterId,
        string name,
        PlayerOwnershipFence ownership) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            CheckpointOwnerId = ownership.OwnerId,
            CheckpointOwnerGeneration = ownership.Generation
        };

    private static void Register(
        GameSessionRegistry registry,
        int accountId,
        ClientSession session,
        GameCharacter character,
        PlayerOwnershipFence ownership)
    {
        registry.ReplaceAccountSession(accountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                accountId,
                session,
                ownership),
            "session fixture binds its exact ownership fence");
        registry.JoinMap(
            session,
            accountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
    }

    private static GameClientHandler CreateHandler(
        GameSessionRegistry registry,
        ClientSession session,
        int accountId,
        GameCharacter character)
    {
        var handler = new GameClientHandler(
            session,
            new EmptyStore(),
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        RequiredField("_account").SetValue(
            handler,
            new GameAccount
            {
                Id = accountId,
                Username = character.Name
            });
        RequiredField("_character").SetValue(handler, character);
        return handler;
    }

    private static async Task InvokeLeaveBroadcastAsync(
        GameClientHandler handler)
    {
        var method = typeof(GameClientHandler).GetMethod(
            "BroadcastPlayerLeaveAsync",
            PrivateInstance) ??
            throw new InvalidOperationException(
                "GameClientHandler.BroadcastPlayerLeaveAsync is missing.");
        try
        {
            await ((Task?)method.Invoke(
                handler,
                [CancellationToken.None]) ??
                throw new InvalidOperationException(
                    "Leave broadcast returned no task."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(GameClientHandler).GetField(
            name,
            PrivateInstance) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private sealed class EmptyStore : GameStoreTestStub
    {
    }
}
