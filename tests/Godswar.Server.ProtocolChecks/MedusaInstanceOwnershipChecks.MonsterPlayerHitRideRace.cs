using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task
        CheckCommittedRideCompletionAfterLethalAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        fixture.Character.Equipment = EquipmentSlots.SetSlot(
            fixture.Character.Equipment,
            fixture.Character.Profession,
            EquipmentSlots.Mount,
            "[14220,,,,,,1,1,0,1,0]");
        Check.True(
            TestItemContent.Content.Mounts
                .TryGetEquippedRideDefinition(
                    fixture.Character,
                    out var mount),
            "lethal Ride race resolves its equipped mount");

        fixture.SetHealth(1);
        var expectedLifeRevision = fixture.Registry
            .GetPlayerLifeRevision(fixture.Socket.Session);
        int manaBefore;
        lock (fixture.Character.VitalsSync)
        {
            manaBefore = fixture.Character.CurrentMp;
        }

        var completionEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MountRideActivationCommit? lateActivation = null;
        fixture.Registry.RegisterSkillCastInterruptionSink(
            fixture.Socket.Session,
            (reason, _, _) => CompleteClaimedRideAsync(reason));
        var baselinePublishedBytes = fixture.Socket.Available;

        async Task CompleteClaimedRideAsync(
            SkillCastInterruptionReason reason)
        {
            Check.True(
                reason == SkillCastInterruptionReason.Death,
                "lethal hit requests the committed cast lifecycle");
            completionEntered.TrySetResult();
            await releaseCompletion.Task;
            lateActivation = await fixture.Registry
                .TryActivateMountRideAndPublishAsync(
                    fixture.Socket.Session,
                    fixture.Character.Id,
                    expectedLifeRevision,
                    mount,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
        }

        try
        {
            var eventId = fixture.FindEvent(
                start: 5_000_000,
                static value => value.Hit && value.Damage > 0);
            var attackTask = Task.Run(() =>
                fixture.AttackAsync(
                    fixture.CreateAttack(eventId)));
            await completionEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(3));

            var lethalCommitted = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                int currentHealth;
                lock (fixture.Character.VitalsSync)
                {
                    currentHealth = fixture.Character.CurrentHp;
                }
                lethalCommitted = currentHealth == 0 &&
                    fixture.Registry.GetPlayerLifeRevision(
                        fixture.Socket.Session) ==
                    expectedLifeRevision + 1;
                if (lethalCommitted)
                {
                    break;
                }

                await Task.Delay(10);
            }

            Check.True(
                lethalCommitted &&
                !attackTask.IsCompleted &&
                fixture.Socket.Available == baselinePublishedBytes,
                "completion-owned cast lifecycle remains ordered before lethal packet publication");

            lock (fixture.Character.VitalsSync)
            {
                // Exercise the publication fence defensively against a
                // hypothetical same-life HP/vitals mutation. Production
                // revive advances life and passive recovery does not revive
                // HP zero; neither fact is assumed by the fence itself.
                fixture.Character.CurrentHp = 1;
                fixture.Character.MarkVitalsChanged();
            }
            await fixture.Registry.AdvancePlayerRecoveryOnceAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None);
            int recoveredHealth;
            lock (fixture.Character.VitalsSync)
            {
                recoveredHealth = fixture.Character.CurrentHp;
            }
            var recoveredPublicationBytes = fixture.Socket.Available;
            Check.True(
                recoveredHealth > 1 &&
                recoveredPublicationBytes > baselinePublishedBytes &&
                !attackTask.IsCompleted,
                "a defensive newer same-life vitals epoch can publish while a completion-owned cast lifecycle remains held");

            while (fixture.Socket.Available > 0)
            {
                _ = await fixture.Socket.ReadPacketAsync();
            }

            releaseCompletion.TrySetResult();
            var applied = await attackTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            await Task.Delay(25);
            var reconciledPackets = new List<byte[]>();
            while (fixture.Socket.Available > 0)
            {
                reconciledPackets.Add(
                    await fixture.Socket.ReadPacketAsync());
            }

            int manaAfter;
            lock (fixture.Character.VitalsSync)
            {
                manaAfter = fixture.Character.CurrentMp;
            }
            Check.True(
                applied.AfterHealth == recoveredHealth &&
                lateActivation is null &&
                manaAfter == manaBefore &&
                !fixture.Socket.Session.IsDisconnected &&
                reconciledPackets.Count == 1 &&
                MedusaPacketOpcode(reconciledPackets[0]) ==
                    MedusaStatusOpcode &&
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        reconciledPackets[0].AsSpan(8)) == 0 &&
                !fixture.Registry.IsRuntimeStatusActive(
                    fixture.Socket.Session,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow),
                "completion-claimed Ride cannot install, old lethal packets remain fenced, and the newer same-life player receives one current empty status reconciliation");
        }
        finally
        {
            releaseCompletion.TrySetResult();
            fixture.Registry.UnregisterSkillCastInterruptionSink(
                fixture.Socket.Session);
        }
    }
}
