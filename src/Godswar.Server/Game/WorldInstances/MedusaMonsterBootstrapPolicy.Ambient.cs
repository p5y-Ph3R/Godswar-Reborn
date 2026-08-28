using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal static partial class MedusaMonsterBootstrapPolicy
{
    private static MedusaMonsterBootstrapValidationResult
        TryPrepareAmbientSpawns(
            MedusaInstanceOwnershipSnapshot ownership,
            IReadOnlyDictionary<uint, CapturedMonsterSpawn> byObjectId,
            bool requireLivePlacement,
            out ImmutableArray<MedusaMonsterBootstrapPreparedAmbientSpawn>
                prepared)
    {
        var authoredSpawns =
            MedusaIslandAmbientSpawnPolicy.SpawnsFor(ownership.Difficulty);
        if (authoredSpawns.Count == 0)
        {
            prepared = [];
            return PreparedMarker();
        }

        var builder = ImmutableArray.CreateBuilder<
            MedusaMonsterBootstrapPreparedAmbientSpawn>(
            authoredSpawns.Count);
        for (var index = 0; index < authoredSpawns.Count; index++)
        {
            var authored = authoredSpawns[index];
            var objectId = WorldObjectIds.MedusaBabyRockElfObjectIds[index];
            if (!byObjectId.TryGetValue(objectId, out var definition) ||
                ownership.MonsterBindings.Any(binding =>
                    binding.Identity.ObjectId == definition.ObjectId))
            {
                prepared = [];
                return Rejected(
                    MedusaMonsterBootstrapValidationOutcome.AmbientMismatch,
                    authored.SpawnId);
            }

            try
            {
                definition.Validate(authored.MapId);
            }
            catch (InvalidDataException)
            {
                prepared = [];
                return Rejected(
                    MedusaMonsterBootstrapValidationOutcome.AmbientMismatch,
                    authored.SpawnId);
            }

            var currentHealth = BinaryPrimitives.ReadUInt32LittleEndian(
                definition.Packet.AsSpan(20, 4));
            var maximumHealth = BinaryPrimitives.ReadUInt32LittleEndian(
                definition.Packet.AsSpan(24, 4));
            if (definition.MapId != ownership.ContentMapId.Value ||
                definition.MapId != authored.MapId ||
                !string.Equals(
                    definition.SceneKey,
                    authored.SceneKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    definition.TemplateKey,
                    authored.TemplateKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    definition.DisplayName,
                    authored.DisplayName,
                    StringComparison.Ordinal) ||
                definition.Tier != authored.Tier ||
                currentHealth != authored.MaximumHealth ||
                maximumHealth != authored.MaximumHealth ||
                !SameFloat(definition.X, authored.X) ||
                !SameFloat(definition.Z, authored.Z) ||
                !SameFloat(definition.AppearanceX, authored.X) ||
                !SameFloat(definition.AppearanceZ, authored.Z) ||
                requireLivePlacement &&
                !MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                    authored.X,
                    authored.Z,
                    out _))
            {
                prepared = [];
                return Rejected(
                    MedusaMonsterBootstrapValidationOutcome.AmbientMismatch,
                    authored.SpawnId);
            }

            builder.Add(new(
                authored.SpawnId,
                definition.ObjectId,
                SpawnGeneration: 1,
                definition.MapId,
                definition.SceneKey,
                definition.TemplateKey,
                definition.DisplayName,
                definition.Tier,
                authored.MaximumHealth,
                definition.X,
                definition.Z,
                ImmutableArray.CreateRange(definition.Packet)));
        }

        prepared = builder.MoveToImmutable();
        return PreparedMarker();
    }

    private static void AppendAmbientFingerprint(
        IncrementalHash hash,
        ImmutableArray<MedusaMonsterBootstrapPreparedAmbientSpawn> spawns)
    {
        AppendInt32(hash, spawns.Length);
        foreach (var spawn in spawns)
        {
            AppendString(hash, spawn.SpawnId);
            AppendUInt32(hash, spawn.ObjectId);
            AppendUInt32(hash, spawn.SpawnGeneration);
            AppendInt32(hash, spawn.MapId);
            AppendString(hash, spawn.SceneKey);
            AppendString(hash, spawn.TemplateKey);
            AppendString(hash, spawn.DisplayName);
            AppendUInt32(hash, spawn.Tier);
            AppendUInt32(hash, spawn.MaximumHealth);
            AppendInt32(hash, BitConverter.SingleToInt32Bits(spawn.X));
            AppendInt32(hash, BitConverter.SingleToInt32Bits(spawn.Z));
            AppendBytes(hash, spawn.Packet.AsSpan());
        }
    }
}
