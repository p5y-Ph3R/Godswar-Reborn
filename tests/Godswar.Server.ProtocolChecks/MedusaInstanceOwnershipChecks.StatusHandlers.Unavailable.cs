using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task
        CheckUnavailableStatusHandlerAuthorityAsync()
    {
        await CheckPartialBoundStatusHandlerAuthorityAsync();
        await CheckStaleOwnershipStatusHandlerAuthorityAsync();
        await CheckStaleLifeStatusHandlerAuthorityAsync();
        await CheckLegacyBoundStatusHandlerAuthorityAsync();
    }

    private static async Task
        CheckPartialBoundStatusHandlerAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var attachment = typeof(MapInstance).GetField(
            "_medusaMonsterAttachment",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MapInstance._medusaMonsterAttachment was not found.");
        attachment.SetValue(fixture.Map, null);
        await AssertUnavailableStatusHandlerAsync(
            fixture,
            MedusaCharacterEffectAuthorityOutcome
                .BoundAuthorityUnavailable,
            "partially bound Medusa authority");
    }

    private static async Task
        CheckStaleOwnershipStatusHandlerAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var replacement = new PlayerOwnershipFence(
            Guid.NewGuid(),
            checked(fixture.Ownership.Generation + 1));
        Check.True(
            fixture.Registry.TryBindAccountSessionOwnership(
                fixture.Character.AccountId,
                fixture.Socket.Session,
                replacement),
            "status fixture advances registry ownership without replacing map membership");
        await AssertUnavailableStatusHandlerAsync(
            fixture,
            MedusaCharacterEffectAuthorityOutcome
                .CurrentMembershipRequired,
            "stale Medusa ownership");
    }

    private static async Task
        CheckStaleLifeStatusHandlerAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        fixture.Registry.RemovePlayerStatusState(
            fixture.Socket.Session);
        await AssertUnavailableStatusHandlerAsync(
            fixture,
            MedusaCharacterEffectAuthorityOutcome
                .CurrentMembershipRequired,
            "missing exact Medusa life");
    }

    private static async Task
        CheckLegacyBoundStatusHandlerAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "E1-Elite",
                PlayerRuntimeMode.Legacy);
        await AssertUnavailableStatusHandlerAsync(
            fixture,
            MedusaCharacterEffectAuthorityOutcome
                .BoundAuthorityUnavailable,
            "legacy-player bound Medusa authority");
    }

    private static async Task AssertUnavailableStatusHandlerAsync(
        MonsterPlayerHitFixture fixture,
        MedusaCharacterEffectAuthorityOutcome expectedOutcome,
        string description)
    {
        InstallMedusaHandlerEquipment(fixture.Character);
        var store = new MedusaHandlerStore(fixture.Character);
        var talents = new CountingTalentUpgradeExecutor();
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            store,
            talents);
        var authority = fixture.Registry
            .ResolveMedusaCharacterEffectAuthority(
                fixture.Socket.Session,
                DateTimeOffset.UtcNow);
        Check.True(
            authority.Outcome == expectedOutcome,
            $"{description} exposes its bounded failure outcome");

        try
        {
            var beforeX = fixture.Character.PositionX;
            var beforeZ = fixture.Character.PositionZ;
            var beforePositionRevision =
                fixture.Character.PositionRevision;
            var beforeMonster = RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId);

            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkBegin));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaWalkPacket(beforeX + 0.25f, beforeZ + 0.25f));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkEnd));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaBasicAttackPacket(
                    fixture.Character,
                    fixture.Source.ObjectId));
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
                afterMonster.CurrentHealth ==
                    beforeMonster.CurrentHealth &&
                afterMonster.HealthRevision ==
                    beforeMonster.HealthRevision &&
                MedusaBasicCooldown(handler) ==
                    DateTimeOffset.MinValue &&
                store.SkillReads == 0 &&
                !MedusaHasPendingCast(handler) &&
                talents.Executions == 0 &&
                store.EquipmentActivations == 0,
                $"{description} fails closed before movement, common basic, skill, or item mutation");
        }
        finally
        {
            await StopMedusaPendingCastsAsync(handler);
        }
    }

    private static async Task CheckMailboxFailureTranslationAsync()
    {
#if DEBUG
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var faultField = typeof(GameSessionRegistry).GetField(
            "_protocolCheckMedusaStatusAuthorityFailure",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Medusa status mailbox fault seam was not found.");
        var failures = new Exception[]
        {
            new SingleOwnerMailboxAdmissionException(
                SingleOwnerMailboxAdmissionStatus.Overloaded),
            new SingleOwnerMailboxStoppedException(),
            new SingleOwnerMailboxWorkerException(
                new InvalidOperationException(
                    "simulated status owner failure"))
        };

        foreach (var failure in failures)
        {
            faultField.SetValue(fixture.Registry, failure);
            Check.True(
                !fixture.Registry.IsMedusaActionAllowed(
                    fixture.Socket.Session,
                    MedusaEncounterControlRestriction.Movement,
                    DateTimeOffset.UtcNow,
                    out var authority) &&
                authority.Outcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .BoundAuthorityUnavailable,
                $"{failure.GetType().Name} translates to a graceful fail-closed authority result");
        }
#else
        await Task.CompletedTask;
#endif
    }
}
