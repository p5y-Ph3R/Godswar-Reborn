using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo AfterMedusaTransactionHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterMedusaTransaction");
    private static readonly FieldInfo AfterMedusaStatusCaptureHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterMedusaStatusCapture");
    private static readonly FieldInfo AfterMedusaStatusSelfAdmissionHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterMedusaStatusSelfAdmission");
    private static readonly FieldInfo BeforeMedusaInterruptHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaInterruptSubmit");
    private static readonly FieldInfo BeforeNotificationClaimHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforePreparedMedusaNotificationClaim");
    private static readonly FieldInfo PreparedReservationHook =
        RequiredPrivateField(
            typeof(GameClientHandler),
            "_protocolCheckAfterPreparedInterruptionReservation");
    private static readonly FieldInfo PreparedStartErrorHook =
        RequiredPrivateField(
            typeof(GameClientHandler),
            "_protocolCheckPreparedInterruptionStartError");
    private static readonly FieldInfo ExactStatusDisconnectHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeExactStatusDisconnect");
    private static readonly FieldInfo StatusProjectionBaselineHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterStatusProjectionBaselineCapture");
    private static readonly FieldInfo MedusaNativePrefixHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeMedusaNativePrefixPacket");
    private static readonly FieldInfo MedusaFinalizeEffectFaultField =
        RequiredPrivateField(
            typeof(MapInstance),
            "_protocolCheckMedusaFinalizeEffectFault");
    private static readonly FieldInfo PlayerStatusStatesField =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_playerStatusStates");
    private static readonly MethodInfo ExactCompletionObserverMethod =
        typeof(GameSessionRegistry).GetMethod(
            "ObserveExactAdmissionCompletionAfterStatusGate",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "Exact status completion observer was not found.");
    private static readonly FieldInfo MedusaAttachmentField =
        RequiredPrivateField(
            typeof(MapInstance),
            "_medusaMonsterAttachment");
#endif

    private static async Task CheckMedusaStatusPublicationRacesAsync()
    {
#if DEBUG
        await CheckMedusaUnavailableAfterCaptureAsync();
        await CheckMedusaValueEqualContextRefreshAfterCommitAsync();
        await CheckMedusaTransferAfterCommitAsync();
        await CheckMedusaClaimFaultReleasesReservationAsync();
        await CheckMedusaStartPublicationFaultFailsClosedAsync();
        await CheckMedusaDelayedExpiryInterruptOrderAsync();
        await CheckMedusaExpiryFaultFailsClosedAfterGateAsync();
        await CheckMedusaExpirySetupFaultFailsClosedAsync();
        await CheckExactBatchAdmissionIsAtomicAsync();
        await CheckLiveMedusaFullEgressFailsClosedAsync();
        await CheckSlowObserverDoesNotDelayMedusaInterruptionAsync();
        await CheckHeldViewerTransitionDoesNotDelayCastStartAsync();
        await CheckMedusaWorldSpawnBaselineRevisionRaceAsync();
        await CheckMedusaWorldSpawnProjectionExhaustionAsync();
        await CheckMedusaWorldSpawnViewerEpochSurvivesAsync();
        await CheckBoundMedusaViewerProjectionExhaustionAsync();
        await CheckMedusaNativePrefixFaultsFailClosedAsync();
        await CheckMedusaNativePrefixMembershipTransitionsAsync();
        await CheckPostClaimInvariantPrefixFaultFailsClosedAsync();
        await CheckClaimedDisconnectSurvivesThrowingCallbackAsync();
        await CheckOrdinaryExactAttackAdmissionFailureDisconnectsAsync();
        await CheckMedusaLocalAggregateFenceRecomposesAsync();
        await CheckDiagnosticExactFailureDoesNotOwnDisconnectAsync();
        await CheckRealPumpFailureTerminalizesDuringStatusGateAsync();
        await CheckAdmittedTerminalFailsClosedAfterStatusGateAsync();
        await CheckMedusaRefreshBeforeInterruptAsync();
#else
        await Task.CompletedTask;
#endif
    }

#if DEBUG
    private static async Task CheckMedusaUnavailableAfterCaptureAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var invoked = 0;
        AfterMedusaStatusCaptureHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() =>
            {
                if (Interlocked.Increment(ref invoked) == 1)
                {
                    MedusaAttachmentField.SetValue(
                        fixture.Hit.Map,
                        null);
                }
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_100_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            await Task.Delay(25);
            Check.True(
                fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler),
                "authority loss after the committed status capture disconnects the target and releases the prepared generation");
        }
        finally
        {
            AfterMedusaStatusCaptureHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task CheckMedusaTransferAfterCommitAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AfterMedusaTransactionHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Medusa transfer race was not released.");
                }
            }));

        try
        {
            var attack = Task.Run(async () =>
                await fixture.Hit.AttackAsync(
                    fixture.Hit.CreateAttack(
                        fixture.Hit.FindEvent(
                            8_200_000,
                            static resolution => resolution.Hit &&
                                resolution.Damage > 0))));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Hit.ReconnectAndReacquireAsync();
            release.Set();
            await attack.WaitAsync(TimeSpan.FromSeconds(5));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                !fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) !=
                        Opcodes.SkillCastInterrupt),
                "same-session transfer after commit suppresses every stale 10171 without disconnecting the current session and releases the old generation");
        }
        finally
        {
            release.Set();
            AfterMedusaTransactionHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task
        CheckMedusaClaimFaultReleasesReservationAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        PreparedReservationHook.SetValue(
            fixture.Handler,
            (Action)(() => throw new InvalidOperationException(
                "simulated prepared claim fault")));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_300_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) !=
                        Opcodes.SkillCastInterrupt),
                "a fault after prepared reservation disconnects the stale-cast client without leaking the generation or forging an interruption notification");
        }
        finally
        {
            PreparedReservationHook.SetValue(fixture.Handler, null);
        }
    }

    private static async Task CheckMedusaDelayedExpiryInterruptOrderAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        BeforeMedusaInterruptHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() => Thread.Sleep(TimeSpan.FromMilliseconds(2200))));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_400_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            var interruptIndex = packets.FindIndex(packet =>
                MedusaPacketOpcode(packet) ==
                    Opcodes.SkillCastInterrupt);
            var priorStatuses = packets
                .Take(interruptIndex)
                .Where(packet =>
                    MedusaPacketOpcode(packet) == MedusaStatusOpcode)
                .ToArray();
            Check.True(
                interruptIndex > 0 &&
                priorStatuses.Length >= 2 &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    priorStatuses[^1].AsSpan(8)) == 0 &&
                !fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler),
                "a control expiring before notification publishes an exact current clear before 10171 and releases the generation");
        }
        finally
        {
            BeforeMedusaInterruptHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task
        CheckMedusaStartPublicationFaultFailsClosedAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var gateWasFree = false;
        PreparedStartErrorHook.SetValue(
            fixture.Handler,
            (Func<Exception?>)(() => new OperationCanceledException(
                "simulated unpublished cast start")));
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
                        8_350_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                fixture.Hit.Socket.Session.IsDisconnected &&
                gateWasFree &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) !=
                        Opcodes.SkillCastInterrupt),
                "a failed cast-start publication never admits 10171 and fail-closes after releasing the status gate and prepared reservation");
        }
        finally
        {
            PreparedStartErrorHook.SetValue(fixture.Handler, null);
            ExactStatusDisconnectHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task CheckMedusaRefreshBeforeInterruptAsync()
    {
        await CheckMedusaRefreshBeforeInterruptOrderAsync(
            refreshClaimsNotificationFirst: false,
            firstSeed: 8_600_000,
            secondSeed: 8_700_000);
        await CheckMedusaRefreshBeforeInterruptOrderAsync(
            refreshClaimsNotificationFirst: true,
            firstSeed: 8_800_000,
            secondSeed: 8_900_000);
    }

    private static async Task
        CheckMedusaRefreshBeforeInterruptOrderAsync(
            bool refreshClaimsNotificationFirst,
            ulong firstSeed,
            ulong secondSeed)
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var secondEvent = fixture.Hit.FindEvent(
            secondSeed,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        var invoked = 0;
        Task? secondAttack = null;
        var orchestrationHook = refreshClaimsNotificationFirst
            ? BeforeNotificationClaimHook
            : BeforeMedusaInterruptHook;
        orchestrationHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() =>
            {
                if (Interlocked.Increment(ref invoked) != 1)
                {
                    return;
                }

                orchestrationHook.SetValue(
                    fixture.Hit.Registry,
                    null);
                if (refreshClaimsNotificationFirst)
                {
                    secondAttack = fixture.Hit.AttackAsync(
                        fixture.Hit.CreateAttack(secondEvent));
                    secondAttack.GetAwaiter().GetResult();
                }
                else
                {
                    secondAttack = Task.Run(async () =>
                    {
                        _ = await fixture.Hit.AttackAsync(
                            fixture.Hit.CreateAttack(secondEvent));
                    });
                }
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        firstSeed,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            await (secondAttack ??
                throw new InvalidOperationException(
                    "The refresh attack was not launched."));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            var interrupts = packets.Count(packet =>
                MedusaPacketOpcode(packet) ==
                    Opcodes.SkillCastInterrupt);
            var interruptIndex = packets.FindIndex(packet =>
                MedusaPacketOpcode(packet) ==
                    Opcodes.SkillCastInterrupt);
            var currentBeforeInterrupt = packets
                .Take(interruptIndex)
                .Last(packet =>
                    MedusaPacketOpcode(packet) == MedusaStatusOpcode);
            Check.True(
                interrupts == 1 &&
                MedusaFirstStatusId(currentBeforeInterrupt) == 330 &&
                !fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler),
                refreshClaimsNotificationFirst
                    ? "the refresh capability owns one 10171 while the original capability observes the shared successful terminal result"
                    : "the first prepared capability owns one 10171 and the delegated refresh shares its successful terminal result");
        }
        finally
        {
            orchestrationHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static FieldInfo RequiredPrivateField(
        Type owner,
        string name) =>
        owner.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"{owner.Name}.{name} was not found.");

    private static async Task<List<byte[]>>
        ReadAvailableMedusaPacketsAsync(
            RuntimePolicySessionSocket socket)
    {
        await Task.Delay(50);
        var packets = new List<byte[]>();
        while (socket.Available > 0)
        {
            packets.Add(await socket.ReadPacketAsync());
        }
        return packets;
    }

    private sealed class MedusaPendingCastFixture : IAsyncDisposable
    {
        private MedusaPendingCastFixture(
            MonsterPlayerHitFixture hit,
            GameClientHandler handler)
        {
            Hit = hit;
            Handler = handler;
        }

        internal MonsterPlayerHitFixture Hit { get; }
        internal GameClientHandler Handler { get; }

        internal static async Task<MedusaPendingCastFixture> CreateAsync(
            string spawnId)
        {
            var hit = await MonsterPlayerHitFixture.CreateAsync(spawnId);
            var handler = CreateMedusaHandler(
                hit.Socket.Session,
                hit.Registry,
                hit.Character,
                new MedusaHandlerStore(hit.Character));
            await PrepareMedusaMonsterVisibilityAsync(
                hit.Registry,
                hit.Socket.Session,
                hit.Character);
            RegisterMedusaCastInterruption(handler);
            await DrainMedusaPacketsAsync(hit.Socket);
            await InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(hit.Character, hit.Source));
            var castStart = await hit.Socket.ReadPacketAsync();
            Check.True(
                MedusaPacketOpcode(castStart) == Opcodes.SkillCast,
                "race fixture starts one real pending cast");
            return new(hit, handler);
        }

        public async ValueTask DisposeAsync()
        {
            UnregisterMedusaCastInterruption(Handler);
            await StopMedusaPendingCastsAsync(Handler);
            await Hit.DisposeAsync();
        }
    }
#endif
}
