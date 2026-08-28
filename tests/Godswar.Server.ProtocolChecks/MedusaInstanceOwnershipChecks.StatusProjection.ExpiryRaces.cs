using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo MedusaExpirySetupHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaExpirySetup");

    private static async Task
        CheckMedusaExpiryFaultFailsClosedAfterGateAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        _ = await fixture.Hit.AttackAsync(
            fixture.Hit.CreateAttack(
                fixture.Hit.FindEvent(
                    8_550_000,
                    static resolution => resolution.Hit &&
                        resolution.Damage > 0)));

        var gateWasFree = false;
        AfterMedusaStatusCaptureHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() => throw new InvalidOperationException(
                "simulated expiry projection fault")));
        ExactStatusDisconnectHook.SetValue(
            fixture.Hit.Registry,
            (Action<ClientSession>)(session =>
            {
                if (ReferenceEquals(session, fixture.Hit.Socket.Session))
                {
                    gateWasFree = IsMedusaStatusGateFree(
                        fixture.Hit.Registry,
                        session);
                }
            }));

        try
        {
            var disconnected = SpinWait.SpinUntil(
                () => fixture.Hit.Socket.Session.IsDisconnected,
                TimeSpan.FromSeconds(4));
            Check.True(
                disconnected && gateWasFree,
                "an expiry capture fault disconnects only after the player status gate is released");
        }
        finally
        {
            AfterMedusaStatusCaptureHook.SetValue(
                fixture.Hit.Registry,
                null);
            ExactStatusDisconnectHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task CheckMedusaExpirySetupFaultFailsClosedAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var setupInvoked = false;
        var gateWasFree = false;
        MedusaExpirySetupHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() =>
            {
                setupInvoked = true;
                throw new InvalidOperationException(
                    "simulated expiry setup fault");
            }));
        ExactStatusDisconnectHook.SetValue(
            fixture.Hit.Registry,
            (Action<ClientSession>)(session =>
            {
                if (ReferenceEquals(session, fixture.Hit.Socket.Session))
                {
                    gateWasFree = IsMedusaStatusGateFree(
                        fixture.Hit.Registry,
                        session);
                }
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_557_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                setupInvoked &&
                fixture.Hit.Socket.Session.IsDisconnected &&
                gateWasFree &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) !=
                        Opcodes.SkillCastInterrupt),
                "an expiry setup fault after initial status admission exact-fails-closed after the status gate and releases the prepared cast");
        }
        finally
        {
            MedusaExpirySetupHook.SetValue(
                fixture.Hit.Registry,
                null);
            ExactStatusDisconnectHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }
#endif
}
