using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerMovementEcsParityChecks
{
    private const int AccountId = 91;
    private const int CharacterId = 931;
    private const uint LocalObjectId = 0x1448;

    public static Task RunAsync()
    {
        CheckAcceptedProjectionAndLegacyCoordinateParity();
        CheckInvalidCoordinatesAreAtomic();
        CheckIdentityAndOptionalSourceValidation();
        CheckOutOfOrderIntentRejection();
        return Task.CompletedTask;
    }

    private static void
        CheckAcceptedProjectionAndLegacyCoordinateParity()
    {
        var character = CreateCharacter(
            mapId: 0,
            x: 10f,
            z: -10f);
        var adapter = new PlayerMovementEcsAdapter();

        var first = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 25f,
            targetZ: -35f);
        Check.True(
            first.Accepted &&
            first.RejectionReason ==
                PlayerMovementRejectionReason.None,
            "ordinary ECS movement is accepted");
        Check.Equal(
            1UL,
            first.IntentSequence,
            "first movement intent sequence");
        Check.Equal(
            1UL,
            first.ProjectionRevision,
            "first movement projection revision");
        Check.Equal(
            10f,
            first.PreviousX,
            "first movement previous X");
        Check.Equal(
            -10f,
            first.PreviousZ,
            "first movement previous Z");
        Check.Equal(
            25f,
            first.TargetX,
            "first movement target X");
        Check.Equal(
            -35f,
            first.CurrentZ,
            "first movement current Z");
        Check.True(
            WorldSectorVisibilityTracker<
                NpcSpawnDefinition>.TryGetCell(
                    first.CurrentX,
                    first.CurrentZ,
                    out _),
            "ordinary ECS target matches legacy coordinate acceptance");

        // Legacy accepts any representable cell regardless of elapsed time or
        // distance. ECS must retain that behavior until a real movement rule
        // is evidenced.
        const float farX = 1_000_000f;
        const float farZ = -1_000_000f;
        Check.True(
            WorldSectorVisibilityTracker<
                NpcSpawnDefinition>.TryGetCell(
                    farX,
                    farZ,
                    out _),
            "legacy accepts a large coordinate delta");
        var far = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            farX,
            farZ);
        Check.True(
            far.Accepted,
            "ECS does not invent a distance or speed rule");
        Check.Equal(
            2UL,
            far.ProjectionRevision,
            "large accepted delta advances projection revision");
        Check.Equal(
            first.CurrentX,
            far.PreviousX,
            "large delta starts from the prior ECS projection");
        Check.Equal(
            farX,
            far.CurrentX,
            "large delta becomes authoritative current X");

        var backwards = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: -250_000f,
            targetZ: 500_000f);
        Check.True(
            backwards.Accepted,
            "ECS accepts an unrestricted reverse delta like legacy");
        Check.Equal(
            3UL,
            backwards.ProjectionRevision,
            "reverse delta advances projection monotonically");
        Check.Equal(
            far.CurrentX,
            backwards.PreviousX,
            "reverse delta uses the last accepted current transform");
    }

    private static void CheckInvalidCoordinatesAreAtomic()
    {
        var character = CreateCharacter(
            mapId: 0,
            x: 2f,
            z: 3f);
        var adapter = new PlayerMovementEcsAdapter();
        var accepted = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 4f,
            targetZ: 5f);
        Check.True(
            accepted.Accepted,
            "coordinate fixture initial movement");

        var invalidTargets = new[]
        {
            (X: float.NaN, Z: 5f),
            (X: 4f, Z: float.PositiveInfinity),
            (X: float.MaxValue, Z: 5f)
        };
        foreach (var invalidTarget in invalidTargets)
        {
            Check.True(
                !WorldSectorVisibilityTracker<
                    NpcSpawnDefinition>.TryGetCell(
                        invalidTarget.X,
                        invalidTarget.Z,
                        out _),
                "invalid ECS coordinate matches legacy rejection");
            var rejected = adapter.Evaluate(
                character,
                AccountId,
                LocalObjectId,
                verifiedSourceObjectId: null,
                invalidTarget.X,
                invalidTarget.Z);
            Check.True(
                !rejected.Accepted &&
                rejected.RejectionReason ==
                    PlayerMovementRejectionReason
                        .InvalidCoordinates,
                "invalid coordinates are rejected by ECS");
            Check.Equal(
                accepted.ProjectionRevision,
                rejected.ProjectionRevision,
                "invalid coordinates do not advance projection");
            Check.Equal(
                accepted.CurrentX,
                rejected.CurrentX,
                "invalid coordinates retain authoritative X");
            Check.Equal(
                accepted.CurrentZ,
                rejected.CurrentZ,
                "invalid coordinates retain authoritative Z");
        }

        var resumed = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 6f,
            targetZ: 7f);
        Check.True(
            resumed.Accepted,
            "valid movement resumes after coordinate rejection");
        Check.Equal(
            accepted.ProjectionRevision + 1,
            resumed.ProjectionRevision,
            "only the resumed accepted movement advances projection");
    }

    private static void
        CheckIdentityAndOptionalSourceValidation()
    {
        var character = CreateCharacter(
            mapId: 0,
            x: 1f,
            z: 1f);
        var adapter = new PlayerMovementEcsAdapter();
        var initial = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 2f,
            targetZ: 2f);
        Check.True(
            initial.Accepted,
            "identity fixture initial movement");

        character.AccountId = AccountId + 1;
        var accountMismatch = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 3f,
            targetZ: 3f);
        AssertIdentityRejected(
            initial,
            accountMismatch,
            "character/account mismatch");
        character.AccountId = AccountId;

        character.Id = CharacterId + 1;
        var characterMismatch = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 3f,
            targetZ: 3f);
        AssertIdentityRejected(
            initial,
            characterMismatch,
            "character swap without lifecycle reset");
        character.Id = CharacterId;

        character.CurrentMap = 1;
        var mapMismatch = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 3f,
            targetZ: 3f);
        AssertIdentityRejected(
            initial,
            mapMismatch,
            "map swap without lifecycle reset");
        character.CurrentMap = 0;

        var verifiedSpoof = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: LocalObjectId + 1,
            targetX: 3f,
            targetZ: 3f);
        Check.True(
            !verifiedSpoof.Accepted &&
            verifiedSpoof.RejectionReason ==
                PlayerMovementRejectionReason
                    .SourceObjectMismatch,
            "an explicitly verified source mismatch is rejected");
        Check.Equal(
            initial.ProjectionRevision,
            verifiedSpoof.ProjectionRevision,
            "verified source mismatch cannot advance projection");

        adapter.Reset();
        character.CurrentMap = 1;
        character.PositionX = 50f;
        character.PositionZ = 60f;
        var afterReset = adapter.Evaluate(
            character,
            AccountId,
            LocalObjectId,
            verifiedSourceObjectId: null,
            targetX: 55f,
            targetZ: 65f);
        Check.True(
            afterReset.Accepted,
            "lifecycle reset accepts the new map identity");
        Check.Equal(
            1UL,
            afterReset.IntentSequence,
            "lifecycle reset restarts intent sequence");
        Check.Equal(
            1UL,
            afterReset.ProjectionRevision,
            "lifecycle reset restarts projection revision");
        Check.Equal(
            50f,
            afterReset.PreviousX,
            "lifecycle reset hydrates the new current transform");
    }

    private static void CheckOutOfOrderIntentRejection()
    {
        var world = new EcsWorld();
        world.RegisterComponent<
            PlayerMovementIdentityComponent>();
        world.RegisterComponent<
            PlayerMovementTransformComponent>();
        world.RegisterComponent<
            PlayerMovementIntentComponent>();
        var entity = world.CreateEntity();
        world.Add(
            entity,
            new PlayerMovementIdentityComponent(
                AccountId,
                CharacterId,
                LocalObjectId));
        world.Add(
            entity,
            new PlayerMovementTransformComponent(
                mapId: 0,
                currentX: 1f,
                currentZ: 2f));
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(
            new PlayerMovementProjectionSystem());

        world.Add(
            entity,
            Intent(
                sequence: 2,
                targetX: 3f,
                targetZ: 4f));
        scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(
            1,
            scheduler.Events.Count<
                PlayerMovementProjectedEvent>(),
            "forward movement intent projects once");

        world.Add(
            entity,
            Intent(
                sequence: 2,
                targetX: 9f,
                targetZ: 9f));
        scheduler.RunTick(TimeSpan.Zero);
        var rejected = scheduler.Events
            .Read<PlayerMovementRejectedEvent>();
        Check.True(
            rejected.Length == 1 &&
            rejected[0].Reason ==
                PlayerMovementRejectionReason.IntentOutOfOrder,
            "replayed movement intent is rejected");
        var transform = world.Get<
            PlayerMovementTransformComponent>(entity);
        Check.Equal(
            1UL,
            transform.ProjectionRevision,
            "replayed movement cannot advance projection revision");
        Check.Equal(
            3f,
            transform.CurrentX,
            "replayed movement cannot replace current transform");
        Check.Equal(
            4f,
            transform.CurrentZ,
            "replayed movement cannot replace current Z");
    }

    private static void AssertIdentityRejected(
        in PlayerMovementEcsDecision accepted,
        in PlayerMovementEcsDecision rejected,
        string description)
    {
        Check.True(
            !rejected.Accepted &&
            rejected.RejectionReason ==
                PlayerMovementRejectionReason.IdentityMismatch,
            $"{description} is rejected");
        Check.Equal(
            accepted.ProjectionRevision,
            rejected.ProjectionRevision,
            $"{description} does not advance projection");
        Check.Equal(
            accepted.CurrentX,
            rejected.CurrentX,
            $"{description} retains authoritative X");
        Check.Equal(
            accepted.CurrentZ,
            rejected.CurrentZ,
            $"{description} retains authoritative Z");
    }

    private static PlayerMovementIntentComponent Intent(
        ulong sequence,
        float targetX,
        float targetZ) =>
        new(
            sequence,
            VerifiedSourceObjectId: null,
            AccountId,
            AccountId,
            CharacterId,
            MapId: 0,
            targetX,
            targetZ);

    private static GameCharacter CreateCharacter(
        byte mapId,
        float x,
        float z) =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "MovementParityHero",
            CreatedUtc =
                new DateTime(
                    2026,
                    7,
                    23,
                    3,
                    4,
                    5,
                    DateTimeKind.Utc),
            Camp = mapId == 0
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp,
            CurrentMap = mapId,
            PositionX = x,
            PositionZ = z,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500
        };
}
