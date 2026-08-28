using System.Reflection;
using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly MethodInfo
        LifeAuthorityTryBeginPendingSkillCastMethod =
            typeof(GameClientHandler).GetMethod(
                "TryBeginPendingSkillCastAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GameClientHandler.TryBeginPendingSkillCastAsync was not found.");

    private static readonly FieldInfo MonsterAttackEventFloorField =
        typeof(GameSessionRegistry).GetField(
            "_nextMonsterAttackEventId",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameSessionRegistry monster attack event floor was not found.");

    private static async Task CheckExactLifeAuthorityIntegrationAsync()
    {
        await CheckWorldPumpOmitsMissingLifeAsync();
        await CheckHandlerRejectsMissingLifeAsync();
        await CheckLifeAdvanceDoesNotEstablishAuthorityAsync();
    }

    private static async Task CheckWorldPumpOmitsMissingLifeAsync()
    {
        await using var control =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var effectEventId = control.FindEvent(
            1,
            static resolution => resolution.Hit && resolution.Damage > 0);
        MonsterAttackEventFloorField.SetValue(
            control.Registry,
            effectEventId - 1);
#if DEBUG
        var controlAttacks = 0;
        control.Registry.ProtocolCheckMonsterWorldTickObserved =
            (_, tick) => controlAttacks += tick.Updates.Count(update =>
                update.Kind == MonsterRuntimeUpdateKind.Attacked &&
                update.TargetCharacterId == control.Character.Id);
#endif
        var controlHealth = control.Character.CurrentHp;
        await control.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Check.True(
#if DEBUG
            controlAttacks >= 1 &&
#endif
            control.Character.CurrentHp < controlHealth &&
            control.Character.VitalsRevision > 0 &&
            control.Mechanics().ActiveEffects.Any(effect =>
                effect.Definition.Kind ==
                    MedusaEncounterEffectKind.Stun),
            "the established-life positive control emits and commits a " +
            "real first-group world-pump attack");

        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var beforeHealth = fixture.Character.CurrentHp;
        var beforeVitalsRevision = fixture.Character.VitalsRevision;
        var beforeMechanics = fixture.MechanicsSnapshot();
#if DEBUG
        var missingLifeAttacks = 0;
        fixture.Registry.ProtocolCheckMonsterWorldTickObserved =
            (_, tick) => missingLifeAttacks += tick.Updates.Count(update =>
                update.Kind == MonsterRuntimeUpdateKind.Attacked &&
                update.TargetCharacterId == fixture.Character.Id);
#endif

        Check.True(
            RemoveLifeAuthority(fixture),
            "world-pump race removes the established life authority");
        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var afterMechanics = fixture.MechanicsSnapshot();

        Check.True(
            !fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out _) &&
#if DEBUG
            missingLifeAttacks == 0 &&
#endif
            fixture.Character.CurrentHp == beforeHealth &&
            fixture.Character.VitalsRevision == beforeVitalsRevision &&
            afterMechanics.LastObservedAt >=
                beforeMechanics.LastObservedAt &&
            fixture.Mechanics().ActiveEffects.IsEmpty,
            "the real ECS world pump omits a missing-life target without " +
            "recreating revision zero, applying HP, or installing an effect");
    }

    private static async Task CheckHandlerRejectsMissingLifeAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        var publications = 0;

        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);
        while (fixture.Socket.Available > 0)
        {
            _ = await fixture.Socket.ReadPacketAsync();
        }
        var sourceHealth = fixture.Source.CurrentHealth;

        Check.True(
            RemoveLifeAuthority(fixture),
            "handler race removes the established life authority");
        var started = await InvokeLifeAuthorityPendingCastAsync(
            handler,
            _ =>
            {
                publications++;
                return Task.CompletedTask;
            });
        await InvokeMedusaPacketAsync(
            handler,
            MedusaSkillPacket(fixture.Character, fixture.Source));
        var blocked = await fixture.Socket.ReadPacketAsync();
        var currentSource = RequiredMonster(
            fixture.Map,
            fixture.Source.ObjectId);

        Check.True(
            !started &&
            publications == 0 &&
            !MedusaHasPendingCast(handler) &&
            currentSource.CurrentHealth == sourceHealth &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                blocked.AsSpan(2)) == Opcodes.SkillCastInterrupt &&
            fixture.Socket.Available == 0 &&
            !fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out _),
            "the real bound skill ingress and shared cast coordinator reject " +
            "missing life before publication or mutation and never " +
            "synthesize authority");
    }

    private static Task<bool> InvokeLifeAuthorityPendingCastAsync(
        GameClientHandler handler,
        Func<CancellationToken, Task> publish) =>
        LifeAuthorityTryBeginPendingSkillCastMethod.Invoke(
            handler,
            [
                530u,
                TimeSpan.Zero,
                "missing-life-check",
                publish,
                new Func<CancellationToken, Task>(
                    _ => Task.CompletedTask),
                CancellationToken.None,
                null
            ]) as Task<bool>
        ?? throw new InvalidOperationException(
            "TryBeginPendingSkillCastAsync returned no task.");

    private static async Task
        CheckLifeAdvanceDoesNotEstablishAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var beforeHealth = fixture.Character.CurrentHp;
        var beforeVitalsRevision = fixture.Character.VitalsRevision;
        var beforeWorldRevision = fixture.Context.WorldRevision;
        Check.True(
            RemoveLifeAuthority(fixture),
            "life-advance race removes established authority");

        var advanced = fixture.Registry.AdvancePlayerLifeRevision(
            fixture.Socket.Session);

        Check.True(
            advanced == -1 &&
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session) == -1 &&
            !fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out _) &&
            fixture.Character.CurrentHp == beforeHealth &&
            fixture.Character.VitalsRevision == beforeVitalsRevision &&
            fixture.Context.WorldRevision == beforeWorldRevision &&
            fixture.Mechanics().ActiveEffects.IsEmpty,
            "advancing a missing life is a side-effect-free rejection and " +
            "never recreates revision zero or one");
    }

    private static bool RemoveLifeAuthority(
        MonsterPlayerHitFixture fixture)
    {
        var established = fixture.Registry.TryGetPlayerLifeRevision(
            fixture.Socket.Session,
            out _);
        fixture.Registry.RemovePlayerStatusState(
            fixture.Socket.Session);
        return established &&
            !fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out _);
    }
}
