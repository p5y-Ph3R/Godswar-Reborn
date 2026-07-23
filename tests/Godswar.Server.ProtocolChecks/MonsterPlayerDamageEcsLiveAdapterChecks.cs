using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = CreateCharacter();
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start);

        CheckNonlethalDuplicateAndStale(
            registry,
            socket.Session,
            character,
            objectId);
        CheckLethalDecision(
            registry,
            socket.Session,
            character,
            objectId);
        CheckReviveLifecycleReset(
            registry,
            socket.Session,
            character,
            objectId);
        CheckRemoveLifecycleReset(
            registry,
            socket.Session,
            character,
            objectId);
        CheckCharacterReplacementReset(
            registry,
            socket.Session,
            objectId);
        CheckLegacyRollback(
            socket.Session,
            character,
            objectId);
        registry.Remove(socket.Session);

        await CheckRemovalDuringMitigationEvaluationAsync();
    }

    private static void CheckNonlethalDuplicateAndStale(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var request = Request(
            eventId: 100,
            character,
            objectId,
            lifeRevision: 0,
            vitalsRevision: 0,
            damage: 21);
        var applied = registry.ResolvePlayerVitalsDamageEcs(
            session,
            character,
            objectId,
            request);
        Check.True(
            applied.Applied && !applied.Killed,
            "live incoming damage applies a nonlethal hit");
        Check.Equal(
            79,
            character.CurrentHp,
            "live adapter writes ECS HP to GameCharacter");
        Check.Equal(
            1L,
            character.VitalsRevision,
            "live adapter mirrors one vitals revision");
        Check.Equal(
            50,
            character.CurrentMp,
            "live incoming damage preserves MP");

        var duplicate = registry.ResolvePlayerVitalsDamageEcs(
            session,
            character,
            objectId,
            request);
        Check.True(
            duplicate.RejectionReason ==
                MonsterPlayerDamageRejectionReason
                    .DuplicateAttackEvent,
            "live adapter deduplicates one attack identity");
        Check.Equal(
            79,
            character.CurrentHp,
            "duplicate attack cannot apply HP twice");
        Check.Equal(
            1L,
            character.VitalsRevision,
            "duplicate attack cannot advance vitals");

        var stale = registry.ResolvePlayerVitalsDamageEcs(
            session,
            character,
            objectId,
            Request(
                eventId: 99,
                character,
                objectId,
                lifeRevision: 0,
                vitalsRevision: 1,
                damage: 21));
        Check.True(
            stale.RejectionReason ==
                MonsterPlayerDamageRejectionReason
                    .StaleAttackEvent,
            "live adapter rejects stale attack identity");

        var staleVitals =
            registry.ResolvePlayerVitalsDamageEcs(
                session,
                character,
                objectId,
                Request(
                    eventId: 101,
                    character,
                    objectId,
                    lifeRevision: 0,
                    vitalsRevision: 0,
                    damage: 21));
        Check.True(
            staleVitals.RejectionReason ==
                MonsterPlayerDamageRejectionReason
                    .VitalsRevisionMismatch,
            "live adapter rejects stale expected vitals");
    }

    private static void CheckLethalDecision(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var lethal = registry.ResolvePlayerVitalsDamageEcs(
            session,
            character,
            objectId,
            Request(
                eventId: 102,
                character,
                objectId,
                lifeRevision: 0,
                vitalsRevision: 1,
                damage: 500));
        Check.True(
            lethal.Applied && lethal.Killed,
            "live incoming damage emits a lethal decision");
        Check.Equal(
            0,
            character.CurrentHp,
            "live lethal damage clamps GameCharacter HP");
        Check.Equal(
            2L,
            character.VitalsRevision,
            "live lethal damage advances vitals once");
        Check.Equal(
            1L,
            registry.GetPlayerLifeRevision(session),
            "live death advances registry life revision once");
        Check.True(
            registry.GetPlayerVitalsDamageEcsDiagnostics(
                session) is { Killed: true },
            "live lethal decision remains observable");
    }

    private static void CheckReviveLifecycleReset(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var revivedLife =
            registry.AdvancePlayerLifeRevision(
                session,
                Start.AddSeconds(10));
        Check.Equal(
            2L,
            revivedLife,
            "revive advances the life identity");
        Check.True(
            registry.GetPlayerVitalsDamageEcsDiagnostics(
                session) is null,
            "revive resets incoming-damage ECS diagnostics");
        Restore(character);

        var afterRevive =
            registry.ResolvePlayerVitalsDamageEcs(
                session,
                character,
                objectId,
                Request(
                    eventId: 1,
                    character,
                    objectId,
                    lifeRevision: revivedLife,
                    vitalsRevision:
                        character.VitalsRevision,
                    damage: 10));
        Check.True(
            afterRevive.Applied,
            "revived player owns a fresh attack-event ledger");
    }

    private static void CheckRemoveLifecycleReset(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        registry.Remove(session);
        Check.True(
            registry.GetPlayerVitalsDamageEcsDiagnostics(
                session) is null,
            "session removal discards incoming-damage ECS");
        Restore(character);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start.AddMinutes(1));

        var afterRejoin =
            registry.ResolvePlayerVitalsDamageEcs(
                session,
                character,
                objectId,
                Request(
                    eventId: 1,
                    character,
                    objectId,
                    lifeRevision: 0,
                    vitalsRevision:
                        character.VitalsRevision,
                    damage: 10));
        Check.True(
            afterRejoin.Applied,
            "rejoined player owns a fresh attack-event ledger");
    }

    private static void CheckCharacterReplacementReset(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        uint objectId)
    {
        var replacement = CreateCharacter();
        replacement.Id++;
        replacement.Name = "IncomingDamageReplacement";
        registry.JoinMap(
            session,
            replacement.AccountId,
            replacement,
            objectId,
            joinedAt: Start.AddMinutes(2));
        Check.True(
            registry.GetPlayerVitalsDamageEcsDiagnostics(
                session) is null,
            "character replacement discards prior damage ECS");

        var replacementHit =
            registry.ResolvePlayerVitalsDamageEcs(
                session,
                replacement,
                objectId,
                Request(
                    eventId: 1,
                    replacement,
                    objectId,
                    lifeRevision:
                        registry.GetPlayerLifeRevision(session),
                    vitalsRevision: 0,
                    damage: 10));
        Check.True(
            replacementHit.Applied,
            "replacement character starts a fresh damage ledger");
    }

    private static void CheckLegacyRollback(
        Networking.ClientSession session,
        GameCharacter character,
        uint objectId)
    {
        var legacy = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy);
        Check.Throws<InvalidOperationException>(
            () => legacy.ResolvePlayerVitalsDamageEcs(
                session,
                character,
                objectId,
                Request(
                    eventId: 1,
                    character,
                    objectId,
                    lifeRevision: 0,
                    vitalsRevision:
                        character.VitalsRevision,
                    damage: 10)),
            "Legacy rollback cannot enter incoming-damage ECS");
    }

    private static async Task
        CheckRemovalDuringMitigationEvaluationAsync()
    {
        const uint monsterObjectId = 9_101;
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = CreateCharacter();
        character.Id++;
        character.Name = "MitigationRemovalHero";
        var objectId = WorldObjectIds.ForPlayer(character.Id);
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [CreateMonster(
                monsterObjectId,
                character.PositionX,
                character.PositionZ)],
            Start);
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId,
            joinedAt: Start);

        Check.True(
            SkillStatusEffectCatalog.TryGet(
                90,
                out var mitigation),
            "mitigation race fixture resolves Holy Ward");
        Check.True(
            await registry.ApplyRuntimeStatusAndPublishAsync(
                socket.Session,
                mitigation,
                Start,
                "mitigation-removal-race",
                CancellationToken.None),
            "mitigation race fixture publishes its status");
        await socket.ReadPacketAsync(340);

        Check.True(
            registry.TryApplyMonsterDamage(
                character.CurrentMap,
                monsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: Start,
                out _),
            "mitigation race fixture establishes aggro");
        await registry.AdvanceMonsterWorldOnceAsync(
            Start,
            CancellationToken.None);

        var hookEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hookCount = 0;
        registry.RuntimeStatusSessionLookupHook = () =>
        {
            Interlocked.Increment(ref hookCount);
            hookEntered.TrySetResult();
            releaseHook.Task.GetAwaiter().GetResult();
        };

        var beforeHp = character.CurrentHp;
        var beforeVitalsRevision = character.VitalsRevision;
        try
        {
            var attackTask = Task.Run(() =>
                registry.AdvanceMonsterWorldOnceAsync(
                    Start + MonsterMapRuntime.TickInterval,
                    CancellationToken.None));
            await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
                registry.Remove(
                    socket.Session,
                    preservePlayerStatus: true));
            releaseHook.TrySetResult();
            await attackTask.WaitAsync(TimeSpan.FromSeconds(5));

            Check.Equal(
                1,
                hookCount,
                "mitigation lookup race reaches one deterministic barrier");
            Check.Equal(
                beforeHp,
                character.CurrentHp,
                "removed target receives no raced monster damage");
            Check.Equal(
                beforeVitalsRevision,
                character.VitalsRevision,
                "removed target advances no raced vitals revision");
            Check.Equal(
                0,
                registry.GetMapPopulation(character.CurrentMap),
                "raced removal clears map membership");
            Check.True(
                registry.GetPlayerVitalsDamageEcsDiagnostics(
                    socket.Session) is null,
                "raced removal clears incoming-damage ECS state");

            await registry.AdvanceMonsterWorldOnceAsync(
                Start + MonsterMapRuntime.TickInterval * 2,
                CancellationToken.None);
            Check.Equal(
                0,
                socket.Available,
                "stale target race emits no combat packets");
        }
        finally
        {
            releaseHook.TrySetResult();
            registry.RuntimeStatusSessionLookupHook = null;
            registry.RemovePlayerStatusState(socket.Session);
        }
    }

    private static PlayerMonsterDamageEcsRequest Request(
        ulong eventId,
        GameCharacter character,
        uint objectId,
        long lifeRevision,
        long vitalsRevision,
        uint damage) =>
        new(
            eventId,
            MonsterObjectId: 9_001,
            MonsterSpawnGeneration: 4,
            character.Id,
            objectId,
            lifeRevision,
            vitalsRevision,
            damage);

    private static void Restore(GameCharacter character)
    {
        lock (character.VitalsSync)
        {
            character.CurrentHp = character.MaxHp;
            character.MarkVitalsChanged();
        }
    }

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = 9_731,
            AccountId = 817,
            Name = "IncomingDamageHero",
            CreatedUtc = Start.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 0,
            PositionX = 100f,
            PositionZ = 100f,
            CurrentHp = 100,
            MaxHp = 100,
            CurrentMp = 50,
            MaxMp = 50,
            CalculatedStats = new CharacterStats()
        };

    private static CapturedMonsterSpawn CreateMonster(
        uint objectId,
        float x,
        float z)
    {
        const string templateKey = "MitigationRaceMonster";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 0,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            objectId,
            x,
            z,
            packet);
    }
}
