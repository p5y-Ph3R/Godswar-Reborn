using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    internal static async Task RunControlledMovementReconciliationAsync()
    {
        await CheckBoundActiveStatusHandlerAsync();
        await CheckSecureRealtimeStatusGateAsync();
    }

    private static async Task CheckStatusHandlerIntegrationAsync()
    {
        await CheckBoundActiveStatusHandlerAsync();
        await CheckPendingSkillCompletionStatusGateAsync();
        await CheckSecureRealtimeStatusGateAsync();
        await CheckUnboundStatusHandlerCompatibilityAsync();
        await CheckUnavailableStatusHandlerAuthorityAsync();
        await CheckMailboxFailureTranslationAsync();
    }

    private static async Task CheckBoundActiveStatusHandlerAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "E1-Elite",
                102);
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var viewer = JoinMedusaHandlerMember(
            fixture,
            viewerSocket.Session,
            characterId: 102);
        var store = new MedusaHandlerStore(fixture.Character);
        var talents = new CountingTalentUpgradeExecutor();
        InstallMedusaHandlerEquipment(fixture.Character);
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            store,
            talents);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);

        try
        {
            var eventId = fixture.FindEvent(
                start: 5_200_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            var effect = fixture.Mechanics().ActiveEffects.Single();
            Check.True(
                effect.Definition.Kind ==
                    MedusaEncounterEffectKind.Stun,
                "real E1 hit installs the handler-control stun");

            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(viewerSocket);

            var beforeX = fixture.Character.PositionX;
            var beforeZ = fixture.Character.PositionZ;
            var beforePositionRevision =
                fixture.Character.PositionRevision;
            var beforeMonster = RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId);
            var selfBytes = fixture.Socket.Available;
            var viewerBytes = viewerSocket.Available;

            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkBegin));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaWalkPacket(beforeX + 0.25f, beforeZ + 0.25f));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkEnd));
            var movementRejectedWithoutEgress =
                fixture.Socket.Available == selfBytes &&
                viewerSocket.Available == viewerBytes;
            await InvokeMedusaPacketAsync(
                handler,
                MedusaBasicAttackPacket(
                    fixture.Character,
                    fixture.Source.ObjectId));
            var basicAttackRejectedWithoutEgress =
                fixture.Socket.Available == selfBytes &&
                viewerSocket.Available == viewerBytes;
            await InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(
                    fixture.Character,
                    fixture.Source));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaTalentPacket());
            await InvokeMedusaPacketAsync(
                handler,
                MedusaEquipmentPacket());

            var afterMonster = RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId);
            Check.True(
                fixture.Character.PositionX == beforeX &&
                fixture.Character.PositionZ == beforeZ &&
                fixture.Character.PositionRevision ==
                    beforePositionRevision &&
                store.PositionWrites == 0 &&
                movementRejectedWithoutEgress,
                "active bound stun rejects WalkBegin/Walk/WalkEnd without persistence or corrective packet jitter");
            Check.True(
                afterMonster.CurrentHealth ==
                    beforeMonster.CurrentHealth &&
                afterMonster.HealthRevision ==
                    beforeMonster.HealthRevision &&
                basicAttackRejectedWithoutEgress &&
                MedusaBasicCooldown(handler) ==
                    DateTimeOffset.MinValue,
                "active bound stun rejects common basic attack before the ECS backend without redundant stop egress");
            Check.True(
                store.SkillReads == 0 &&
                !MedusaHasPendingCast(handler),
                "active bound stun rejects an initial skill before learned-skill lookup or pending-cast creation");
            Check.True(
                talents.Executions == 0 &&
                store.EquipmentActivations == 0,
                "active bound stun rejects opcodes 10049 and 10051 before durable or compatibility mutation");
        }
        finally
        {
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(viewerSocket.Session);
            _ = viewer;
        }
    }

    private static async Task
        CheckPendingSkillCompletionStatusGateAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var store = new MedusaHandlerStore(fixture.Character);
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            store);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);

        try
        {
            var beforeMonster = RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId);
            var beforeMp = fixture.Character.CurrentMp;
            await InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(
                    fixture.Character,
                    fixture.Source));
            Check.True(
                store.SkillReads == 1 &&
                MedusaHasPendingCast(handler),
                "uncontrolled bound handler begins the real one-second Thunder cast");

            var eventId = fixture.FindEvent(
                start: 5_300_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            Check.True(
                MedusaHasPendingCast(handler),
                "a deliberately unregistered handler fixture retains its cast for completion-time gate coverage");
            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkBegin));
            Check.True(
                MedusaHasPendingCast(handler) &&
                store.PositionWrites == 0,
                "blocked WalkBegin cannot masquerade as movement-driven cast interruption");

            await Task.Delay(TimeSpan.FromMilliseconds(1_300));
            var afterMonster = RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId);
            Check.True(
                !MedusaHasPendingCast(handler) &&
                afterMonster.CurrentHealth ==
                    beforeMonster.CurrentHealth &&
                afterMonster.HealthRevision ==
                    beforeMonster.HealthRevision &&
                fixture.Character.CurrentMp == beforeMp &&
                store.VitalsWrites == 0,
                "active bound stun rejects pending skill completion before damage, mana, or vitals mutation");
        }
        finally
        {
            await StopMedusaPendingCastsAsync(handler);
        }
    }

    private static async Task CheckSecureRealtimeStatusGateAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "E1-Elite",
                102);
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session = new ClientSession(transport);
        var character = JoinMedusaHandlerMember(
            fixture,
            session,
            characterId: 102);
        var store = new MedusaHandlerStore(character);
        var handler = CreateMedusaHandler(
            session,
            fixture.Registry,
            character,
            store);

        try
        {
            var initialEffects =
                await InvokeMedusaRealtimeTickAsync(handler);
            Check.True(
                MedusaRealtimeEffect(
                    initialEffects,
                    "ViewerMovement") is null,
                "secure Medusa handler establishes a baseline without movement");
            var baseline = transport.Snapshots.Single();
            var member = fixture.Map.Snapshot().Single(context =>
                ReferenceEquals(context.Session, session));
            var eventId = fixture.FindEvent(
                start: 5_400_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            await fixture.Registry.ProcessMonsterAttackForSessionAsync(
                session,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Attacked,
                    fixture.Source,
                    TargetCharacterId: character.Id,
                    TargetX: character.PositionX,
                    TargetZ: character.PositionZ,
                    TargetObjectId: member.ObjectId,
                    TargetLifeRevision:
                        fixture.Registry.GetPlayerLifeRevision(session),
                    TargetOwnership: member.Ownership,
                    TargetWorldInstanceId: member.WorldInstanceId,
                    TargetWorldRevision: member.WorldRevision,
                    TargetWorldMembershipEpoch:
                        member.WorldMembershipEpoch,
                    AttackEventId: eventId),
                CancellationToken.None);
            var authority = fixture.Registry
                .ResolveMedusaCharacterEffectAuthority(
                    session,
                    DateTimeOffset.UtcNow);
            Check.True(
                authority.View?.ActiveEffects.Any(effect =>
                    effect.Definition.Kind ==
                        MedusaEncounterEffectKind.Stun) == true,
                "secure realtime member receives an exact-life E1 stun");

            var beforeX = character.PositionX;
            var beforeZ = character.PositionZ;
            var beforeRevision = character.PositionRevision;
            transport.EnqueueMovement(
                new SecureRealtimeMovementIngress(
                    new SecureRealtimeMovementInput(
                        SecureRealtimeMovementFlags.None,
                        TransportEpoch: 1,
                        InputId: 1,
                        ClientMonotonicMilliseconds: 50,
                        baseline.WorldGeneration,
                        LegacyState: 0xCAFE_0001,
                        X: beforeX + 0.25f,
                        Z: beforeZ + 0.25f,
                        Auxiliary: 1f,
                        MapId: character.CurrentMap),
                    SecureRealtimeTransportSource.Udp,
                    TimeSpan.FromMilliseconds(100),
                    SecureRealtimeMovementIngressKind.Input));
            var blocked = await InvokeMedusaRealtimeTickAsync(handler);
            await PublishMedusaRealtimeEffectsAsync(handler, blocked);

            Check.True(
                MedusaRealtimeEffect(
                    blocked,
                    "ViewerMovement") is null &&
                MedusaRealtimeEffect(
                    blocked,
                    "ReliableCorrection") is not null &&
                MedusaRealtimeEffectValue(
                    blocked,
                    "PositionSave") is null &&
                transport.Snapshots.Count == 2 &&
                character.PositionX == beforeX &&
                character.PositionZ == beforeZ &&
                character.PositionRevision == beforeRevision &&
                store.PositionWrites == 0,
                "active bound stun rejects authenticated realtime movement and returns an authoritative correction without mutation or viewer egress");
        }
        finally
        {
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(session);
        }
    }

    private static GameCharacter JoinMedusaHandlerMember(
        MonsterPlayerHitFixture fixture,
        ClientSession session,
        int characterId)
    {
        var character = CreateRegistryDamageCharacter(
            characterId,
            mapId: 200);
        character.PositionX = fixture.Source.X;
        character.PositionZ = fixture.Source.Z;
        character.CheckpointOwnerId = Guid.NewGuid();
        character.CheckpointOwnerGeneration = 1;
        var ownership = new PlayerOwnershipFence(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
        fixture.Registry.ReplaceAccountSession(
            character.AccountId,
            session);
        Check.True(
            fixture.Registry.TryBindAccountSessionOwnership(
                character.AccountId,
                session,
                ownership),
            "additional Medusa handler member binds exact ownership");
        fixture.Registry.JoinWorldInstance(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            fixture.Runtime.InstanceId,
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);
        return character;
    }
}
