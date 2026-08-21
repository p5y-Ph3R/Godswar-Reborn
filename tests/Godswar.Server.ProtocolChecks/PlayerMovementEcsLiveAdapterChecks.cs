using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerMovementEcsLiveAdapterChecks
{
    public static async Task RunAsync()
    {
        await CheckLiveEcsWalkOrderingAsync();
        await CheckLegacyRollbackAsync();
        await CheckHostileStatusMovementControlsAsync();
        await CheckDeadLegacyTcpMovementParityAsync();
    }

    private static async Task CheckLiveEcsWalkOrderingAsync()
    {
        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var store = new BlockingPositionStore();
        var character = CreateCharacter(
            CharacterId,
            AccountId,
            "MovementEcsHero");
        var viewer = CreateCharacter(
            ViewerCharacterId,
            ViewerAccountId,
            "MovementEcsViewer");
        var monster = CreateMonster();
        var registry = CreateRegistry(PlayerRuntimeMode.Ecs);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [monster],
            TestTime);
        registry.JoinMap(
            actorSocket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        registry.JoinMap(
            viewerSocket.Session,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));

        var handler = CreateHandler(
            actorSocket.Session,
            store,
            registry,
            character);
        var acceptedPacket = CreateWalkPacket(
            opaqueMovementState: 0xDEAD_BEEFu,
            targetX: 5f,
            targetZ: 6f);
        var handling = InvokePacketAsync(
            handler,
            acceptedPacket);

        await store.FirstSaveStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Check.Equal(
            5f,
            character.PositionX,
            "accepted ECS walk mutates character before persistence");
        Check.Equal(
            6f,
            character.PositionZ,
            "accepted ECS walk mutates character Z before persistence");
        Check.True(
            registry.TryGetMapSessionByCharacterId(
                character.CurrentMap,
                character.Id,
                excludeSession: null,
                out var movedContext) &&
            movedContext.Character.PositionX == 5f &&
            movedContext.Character.PositionZ == 6f,
            "accepted ECS walk updates registry before persistence");
        Check.True(
            registry.IsMonsterVisibleTo(
                actorSocket.Session,
                monster.ObjectId),
            "accepted ECS walk commits monster AOI before persistence");
        Check.Equal(
            0,
            viewerSocket.Available,
            "walk broadcast waits for throttled persistence");
        Check.Equal(
            1,
            store.SaveAttempts,
            "accepted ECS walk reaches persistence once");
        Check.Equal(
            5f,
            store.SavedX,
            "persistence observes accepted ECS X");
        Check.Equal(
            6f,
            store.SavedZ,
            "persistence observes accepted ECS Z");

        store.ReleaseFirstSave();
        await handling;
        var firstBroadcast =
            await viewerSocket.ReadPacketAsync(
                acceptedPacket.Buffer.Length);
        AssertWalkBroadcast(
            firstBroadcast,
            acceptedPacket,
            WorldObjectIds.ForPlayer(character.Id),
            expectedX: 5f,
            expectedZ: 6f,
            "accepted ECS walk");
        var firstDecision =
            handler.GetPlayerMovementEcsDiagnostics()
            ?? throw new InvalidOperationException(
                "Accepted live ECS walk had no diagnostics.");
        Check.True(
            firstDecision.Accepted &&
            firstDecision.IntentSequence == 1 &&
            firstDecision.ProjectionRevision == 1,
            "accepted live ECS walk owns the first projection");

        var invalidPacket = CreateWalkPacket(
            opaqueMovementState: 0x1234_5678u,
            targetX: float.NaN,
            targetZ: 9f);
        await InvokePacketAsync(handler, invalidPacket);
        Check.Equal(
            5f,
            character.PositionX,
            "invalid ECS coordinate cannot mutate character");
        Check.Equal(
            1,
            store.SaveAttempts,
            "invalid ECS coordinate cannot persist");
        Check.Equal(
            0,
            viewerSocket.Available,
            "invalid ECS coordinate cannot broadcast");
        var invalidDecision =
            handler.GetPlayerMovementEcsDiagnostics()
            ?? throw new InvalidOperationException(
                "Rejected live ECS walk had no diagnostics.");
        Check.True(
            !invalidDecision.Accepted &&
            invalidDecision.RejectionReason ==
                PlayerMovementRejectionReason.InvalidCoordinates &&
            invalidDecision.ProjectionRevision == 1,
            "invalid live ECS coordinate is atomically rejected");

        character.AccountId = AccountId + 1;
        var identityPacket = CreateWalkPacket(
            opaqueMovementState: 0xFFFF_0001u,
            targetX: 7f,
            targetZ: 8f);
        await InvokePacketAsync(handler, identityPacket);
        character.AccountId = AccountId;
        Check.Equal(
            5f,
            character.PositionX,
            "identity-rejected ECS walk cannot mutate character");
        Check.Equal(
            1,
            store.SaveAttempts,
            "identity-rejected ECS walk cannot persist");
        Check.Equal(
            0,
            viewerSocket.Available,
            "identity-rejected ECS walk cannot broadcast");
        Check.True(
            handler.GetPlayerMovementEcsDiagnostics() is
            {
                Accepted: false,
                RejectionReason:
                    PlayerMovementRejectionReason.IdentityMismatch,
                ProjectionRevision: 1
            },
            "live ECS validates character/account identity");

        // The state word remains opaque. Its low bits intentionally do not
        // match the local object ID, yet a valid coordinate projection is
        // accepted and the outbound builder performs its existing rewrite.
        var opaquePacket = CreateWalkPacket(
            opaqueMovementState: 0xA5A5_0001u,
            targetX: -12f,
            targetZ: 14f);
        await InvokePacketAsync(handler, opaquePacket);
        var opaqueBroadcast =
            await viewerSocket.ReadPacketAsync(
                opaquePacket.Buffer.Length);
        AssertWalkBroadcast(
            opaqueBroadcast,
            opaquePacket,
            WorldObjectIds.ForPlayer(character.Id),
            expectedX: -12f,
            expectedZ: 14f,
            "opaque-state ECS walk");
        Check.Equal(
            1,
            store.SaveAttempts,
            "second accepted walk remains persistence-throttled");
        Check.True(
            handler.GetPlayerMovementEcsDiagnostics() is
            {
                Accepted: true,
                IntentSequence: 4,
                ProjectionRevision: 2
            },
            "accepted movement revisions remain monotonic across rejections");

        ResetMovementAdapter(handler);
        var afterResetPacket = CreateWalkPacket(
            opaqueMovementState: 0x5A5A_0002u,
            targetX: 20f,
            targetZ: 21f);
        await InvokePacketAsync(handler, afterResetPacket);
        await viewerSocket.ReadPacketAsync(
            afterResetPacket.Buffer.Length);
        Check.True(
            handler.GetPlayerMovementEcsDiagnostics() is
            {
                Accepted: true,
                IntentSequence: 1,
                ProjectionRevision: 1,
                PreviousX: -12f,
                PreviousZ: 14f
            },
            "character lifecycle reset rehydrates current transform and sequence");

        registry.Remove(actorSocket.Session);
        registry.Remove(viewerSocket.Session);
    }

    private static async Task CheckLegacyRollbackAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var store = new RecordingPositionStore();
        var character = CreateCharacter(
            CharacterId + 10,
            AccountId + 10,
            "MovementLegacyHero");
        var registry = CreateRegistry(
            PlayerRuntimeMode.Legacy);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var handler = CreateHandler(
            socket.Session,
            store,
            registry,
            character,
            configureVisibility: false);

        var opaquePacket = CreateWalkPacket(
            opaqueMovementState: 0x0000_0001u,
            targetX: 30f,
            targetZ: -40f);
        await InvokePacketAsync(handler, opaquePacket);
        Check.Equal(
            30f,
            character.PositionX,
            "legacy rollback retains existing walk mutation");
        Check.Equal(
            -40f,
            character.PositionZ,
            "legacy rollback retains existing walk Z");
        Check.Equal(
            1,
            store.SaveAttempts,
            "legacy rollback retains existing persistence");
        Check.True(
            handler.GetPlayerMovementEcsDiagnostics() is null,
            "legacy rollback never enters movement ECS");

        var invalidPacket = CreateWalkPacket(
            opaqueMovementState: 0xFFFF_FFFFu,
            targetX: float.PositiveInfinity,
            targetZ: 2f);
        await InvokePacketAsync(handler, invalidPacket);
        Check.Equal(
            30f,
            character.PositionX,
            "legacy rollback retains existing invalid-coordinate rejection");
        Check.Equal(
            1,
            store.SaveAttempts,
            "legacy invalid coordinate cannot persist");

        registry.Remove(socket.Session);
    }
}
