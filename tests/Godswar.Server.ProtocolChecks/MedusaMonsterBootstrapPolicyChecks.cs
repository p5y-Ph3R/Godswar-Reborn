using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaMonsterBootstrapPolicyChecks
{
    public const string CheckName =
        "Medusa monster bootstrap content and live gates";

    public static Task RunAsync()
    {
        CheckAuthoredPreparationAndPermutation();
        CheckDriftChangesFingerprintOrRejects();
        CheckMalformedRunObjectsFailClosed();
        CheckCallerMutationCannotChangePreparation();
        CheckSharedMapDifficultiesRemainDistinct();
        CheckProductionPlacement();
        return Task.CompletedTask;
    }

    private static void CheckAuthoredPreparationAndPermutation()
    {
        var fixture = MedusaMonsterBootstrapPolicyCheckFixture.Create();
        var baseline = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            fixture.Definitions);
        var permuted = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            fixture.Definitions.Reverse().ToArray());

        Check.True(baseline.IsPrepared, "authored bootstrap prepares");
        Check.True(permuted.IsPrepared, "permuted bootstrap prepares");
        var prepared = baseline.Preparation!;
        Check.True(
            prepared.RespawnPolicy == MonsterRespawnPolicy.Never &&
            prepared.Spawns.Length == 136 &&
            prepared.Spawns.All(spawn =>
                spawn.SpawnGeneration == 1 &&
                MedusaIslandRosterPolicy.TryGetSpawn(
                    spawn.RosterSpawnId,
                    out var roster) &&
                MedusaMonsterContentCatalog.Current.TryGetMonster(
                    fixture.Ownership.Difficulty,
                    roster.TemplateAlias,
                    out var rule) &&
                spawn.Tier == rule.Level &&
                WorldObjectIds.IsMonster(spawn.ObjectId)) &&
            prepared.Spawns[0].ObjectId ==
                WorldObjectIds.FirstMedusaMonsterObjectId &&
            prepared.Spawns.Select(spawn => spawn.ObjectId)
                .SequenceEqual(prepared.Spawns
                    .Select(spawn => spawn.ObjectId)
                    .Order()),
            "captured content is canonical generation-one Never content");
        var ambient = prepared.AmbientSpawns;
        Check.True(
            ambient.Length == 2 &&
            ambient[0].SpawnId ==
                MedusaIslandAmbientSpawnPolicy.BabyRockElfSpawnId &&
            ambient[1].SpawnId ==
                MedusaIslandAmbientSpawnPolicy.SecondBabyRockElfSpawnId &&
            ambient.Select(spawn => spawn.ObjectId).SequenceEqual(
                WorldObjectIds.MedusaBabyRockElfObjectIds) &&
            ambient.All(spawn =>
                spawn.MapId == 200 &&
                spawn.TemplateKey ==
                    MedusaIslandAmbientSpawnPolicy.BabyRockElfTemplateKey &&
                spawn.Tier == 1 &&
                spawn.MaximumHealth == 10) &&
            ambient[0].X == MedusaIslandAmbientSpawnPolicy.BabyRockElfX &&
            ambient[0].Z == MedusaIslandAmbientSpawnPolicy.BabyRockElfZ &&
            ambient[1].X ==
                MedusaIslandAmbientSpawnPolicy.SecondBabyRockElfX &&
            ambient[1].Z ==
                MedusaIslandAmbientSpawnPolicy.SecondBabyRockElfZ &&
            prepared.RuntimeSpawnCount == 138,
            "enhanced content includes both captured passive Baby Rock Elves");
        Check.True(
            prepared.Fingerprint.Length == 64 &&
            prepared.Fingerprint.All(character =>
                character is >= '0' and <= '9' or
                    >= 'A' and <= 'F') &&
            string.Equals(
                prepared.Fingerprint,
                permuted.Preparation!.Fingerprint,
                StringComparison.Ordinal),
            "fingerprint is uppercase SHA-256 and permutation-stable");

        foreach (var spawn in prepared.Spawns)
        {
            var current = BinaryPrimitives.ReadUInt32LittleEndian(
                spawn.Packet.AsSpan(20, 4));
            var maximum = BinaryPrimitives.ReadUInt32LittleEndian(
                spawn.Packet.AsSpan(24, 4));
            Check.True(
                current == spawn.MaximumHealth &&
                maximum == spawn.MaximumHealth,
                $"{spawn.RosterSpawnId} retains exact authored health");
        }
    }

    private static void CheckDriftChangesFingerprintOrRejects()
    {
        var fixture = MedusaMonsterBootstrapPolicyCheckFixture.Create();
        var baseline = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            fixture.Definitions);
        Check.True(baseline.IsPrepared, "drift baseline prepares");

        var packetDrift =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Definitions);
        packetDrift[0].Packet[^1] ^= 0x5A;
        AssertChangedOrRejected(
            baseline.Preparation!.Fingerprint,
            MedusaMonsterBootstrapPolicy.PrepareAuthored(
                fixture.Ownership,
                packetDrift),
            "single packet-byte drift");

        var sceneDrift =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Definitions);
        sceneDrift[0] = sceneDrift[0] with
        {
            SceneKey = "Medusa_Island2"
        };
        var wrongScene = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            sceneDrift);
        Check.True(
            wrongScene.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.SceneMismatch,
            "scene metadata drift rejects");

        var healthDrift =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Definitions);
        var health = BinaryPrimitives.ReadUInt32LittleEndian(
            healthDrift[0].Packet.AsSpan(20, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            healthDrift[0].Packet.AsSpan(20, 4),
            health - 1);
        var wrongHealth = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            healthDrift);
        Check.True(
            wrongHealth.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.HealthMismatch,
            "non-pristine authored health rejects");

        var tierDrift =
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                fixture.Definitions);
        BinaryPrimitives.WriteUInt32LittleEndian(
            tierDrift[0].Packet.AsSpan(12, 4),
            tierDrift[0].Tier - 1);
        var wrongTier = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            tierDrift);
        Check.True(
            wrongTier.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.TierMismatch,
            "tier drift rejects");

        var bindings = fixture.Ownership.MonsterBindings.SetItem(
            0,
            fixture.Ownership.MonsterBindings[0] with
            {
                Identity = fixture.Ownership.MonsterBindings[0].Identity with
                {
                    SpawnGeneration = 2
                }
            });
        var wrongGeneration = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership with
            {
                MonsterBindings = bindings
            },
            fixture.Definitions);
        Check.True(
            wrongGeneration.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding,
            "non-initial owned generation rejects");
    }

    private static void CheckCallerMutationCannotChangePreparation()
    {
        var fixture = MedusaMonsterBootstrapPolicyCheckFixture.Create();
        var result = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership,
            fixture.Definitions);
        Check.True(result.IsPrepared, "immutability baseline prepares");
        var preparation = result.Preparation!;
        var originalPreparedByte = preparation.Spawns[0].Packet[^1];
        fixture.Definitions[0].Packet[^1] ^= 0x7F;
        Check.True(
            preparation.Spawns[0].Packet[^1] == originalPreparedByte,
            "caller packet mutation cannot alter prepared bytes");

        var firstProjection = preparation.CreateCapturedDefinitions();
        firstProjection[0].Packet[^1] ^= 0x33;
        var secondProjection = preparation.CreateCapturedDefinitions();
        Check.True(
            secondProjection[0].Packet[^1] == originalPreparedByte &&
            !ReferenceEquals(
                firstProjection[0].Packet,
                secondProjection[0].Packet),
            "each runtime projection receives independent packet copies");
    }

    private static void CheckMalformedRunObjectsFailClosed()
    {
        var fixture = MedusaMonsterBootstrapPolicyCheckFixture.Create();
        var duplicateSpawns = fixture.Ownership.Run.Spawns.ToArray();
        duplicateSpawns[1] = duplicateSpawns[1] with
        {
            ObjectId = duplicateSpawns[0].ObjectId
        };
        var duplicate = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership with
            {
                Run = fixture.Ownership.Run with
                {
                    Spawns = duplicateSpawns
                }
            },
            fixture.Definitions);
        Check.True(
            duplicate.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding,
            "duplicate run object IDs fail closed before indexing");

        var missingSpawns = fixture.Ownership.Run.Spawns.ToArray();
        missingSpawns[0] = missingSpawns[0] with
        {
            ObjectId = 999_999
        };
        var missing = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            fixture.Ownership with
            {
                Run = fixture.Ownership.Run with
                {
                    Spawns = missingSpawns
                }
            },
            fixture.Definitions);
        Check.True(
            missing.Outcome ==
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding,
            "missing bound run object ID fails closed during set join");
    }

    private static void CheckSharedMapDifficultiesRemainDistinct()
    {
        var id = new Godswar.Server.Domain.World.Instances.WorldInstanceId(
            new Guid("d83907b1-7d82-45ef-a9c6-ac0bbd25897d"));
        var enhanced = MedusaMonsterBootstrapPolicyCheckFixture.Create(
            MedusaEncounterDifficulty.Enhanced,
            id);
        var mythic = MedusaMonsterBootstrapPolicyCheckFixture.Create(
            MedusaEncounterDifficulty.Mythic,
            id);
        var enhancedResult = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            enhanced.Ownership,
            enhanced.Definitions);
        var mythicResult = MedusaMonsterBootstrapPolicy.PrepareAuthored(
            mythic.Ownership,
            mythic.Definitions);

        Check.True(
            enhancedResult.IsPrepared &&
            mythicResult.IsPrepared &&
            enhancedResult.Preparation!.ContentMapId.Value == 200 &&
            mythicResult.Preparation!.ContentMapId.Value == 200 &&
            enhancedResult.Preparation.Difficulty !=
                mythicResult.Preparation.Difficulty &&
            !string.Equals(
                enhancedResult.Preparation.Fingerprint,
                mythicResult.Preparation.Fingerprint,
                StringComparison.Ordinal),
            "Enhanced and Mythic remain distinct despite shared map 200");
    }

    private static void CheckProductionPlacement()
    {
        foreach (var difficulty in new[]
                 {
                     MedusaEncounterDifficulty.Normal,
                     MedusaEncounterDifficulty.Enhanced,
                     MedusaEncounterDifficulty.Mythic
                 })
        {
            var fixture = MedusaMonsterBootstrapPolicyCheckFixture.Create(
                difficulty);
            var result =
                MedusaMonsterBootstrapPolicy.PrepareProductionLive(
                    fixture.Ownership,
                    fixture.Definitions);
            Check.True(
                result.Outcome ==
                    MedusaMonsterBootstrapValidationOutcome.Prepared &&
                result.Preparation is not null &&
                result.IsPrepared,
                $"{difficulty} live bootstrap accepts decoded unblocked spawns");
        }
    }

    private static void AssertChangedOrRejected(
        string baselineFingerprint,
        MedusaMonsterBootstrapValidationResult result,
        string description)
    {
        Check.True(
            !result.IsPrepared ||
            !string.Equals(
                baselineFingerprint,
                result.Preparation!.Fingerprint,
                StringComparison.Ordinal),
            $"{description} changes fingerprint or rejects");
    }
}
