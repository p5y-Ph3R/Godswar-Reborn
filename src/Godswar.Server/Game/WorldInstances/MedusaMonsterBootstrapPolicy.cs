using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Pure preparation boundary for the fixed Medusa monster runtime. Authored
/// preparation validates and freezes content. Live preparation adds the
/// independently certified placement requirement and currently fails closed.
/// </summary>
internal static partial class MedusaMonsterBootstrapPolicy
{
    public const string FingerprintVersion =
        "medusa-monster-bootstrap-v2";

    public static MedusaMonsterBootstrapValidationResult PrepareAuthored(
        MedusaInstanceOwnershipSnapshot? ownership,
        IReadOnlyList<CapturedMonsterSpawn>? definitions) =>
        Prepare(ownership, definitions, requireLivePlacement: false);

    public static MedusaMonsterBootstrapValidationResult PrepareProductionLive(
        MedusaInstanceOwnershipSnapshot? ownership,
        IReadOnlyList<CapturedMonsterSpawn>? definitions) =>
        Prepare(ownership, definitions, requireLivePlacement: true);

    private static MedusaMonsterBootstrapValidationResult Prepare(
        MedusaInstanceOwnershipSnapshot? ownership,
        IReadOnlyList<CapturedMonsterSpawn>? definitions,
        bool requireLivePlacement)
    {
        if (!TryValidateOwnership(
                ownership,
                out var difficulty,
                out var bindings,
                out var bindingFailure))
        {
            return bindingFailure;
        }

        var ambientCount = MedusaIslandAmbientSpawnPolicy.CountFor(
            ownership!.Difficulty);
        if (definitions is null ||
            definitions.Count !=
                MedusaIslandRosterPolicy.TotalSpawnCount + ambientCount)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome
                    .InvalidDefinitionCount);
        }

        var cloned = new CapturedMonsterSpawn[definitions.Count];
        for (var index = 0; index < definitions.Count; index++)
        {
            var source = definitions[index];
            if (source is null || source.Packet is null)
            {
                return Rejected(
                    MedusaMonsterBootstrapValidationOutcome
                        .InvalidCapturedDefinition);
            }

            cloned[index] = source with
            {
                Packet = source.Packet.ToArray()
            };
        }

        if (cloned.Select(static definition => definition.ObjectId)
                .Distinct().Count() != cloned.Length)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.DuplicateObjectId);
        }

        var byObjectId = cloned.ToDictionary(
            static definition => definition.ObjectId);
        if (byObjectId.Count != bindings.Length + ambientCount ||
            bindings.Any(binding =>
                !byObjectId.ContainsKey(binding.Identity.ObjectId)))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.DefinitionSetMismatch);
        }

        var prepared = ImmutableArray.CreateBuilder<
            MedusaMonsterBootstrapPreparedSpawn>(bindings.Length);
        foreach (var binding in bindings.OrderBy(static binding =>
                     binding.Identity.ObjectId))
        {
            var definition = byObjectId[binding.Identity.ObjectId];
            var validation = ValidateDefinition(
                ownership!,
                binding,
                definition,
                requireLivePlacement);
            if (validation.Outcome !=
                MedusaMonsterBootstrapValidationOutcome.Prepared)
            {
                return validation;
            }

            var roster = MedusaIslandRosterPolicy.Spawns.Single(spawn =>
                string.Equals(
                    spawn.SpawnId,
                    binding.RosterSpawnId,
                    StringComparison.Ordinal));
            var monsterRule = MedusaMonsterContentCatalog.Current
                .TryGetMonster(
                    ownership.Difficulty,
                    roster.TemplateAlias,
                    out var configured)
                ? configured
                : throw new InvalidDataException(
                    $"Missing Medusa rule for {roster.TemplateAlias}.");
            prepared.Add(new(
                binding.RosterSpawnId,
                definition.ObjectId,
                binding.Identity.SpawnGeneration,
                binding.Role,
                binding.Rank,
                definition.MapId,
                definition.SceneKey,
                definition.TemplateKey,
                definition.DisplayName,
                definition.Tier,
                monsterRule.MaximumHealth,
                definition.X,
                definition.Z,
                ImmutableArray.CreateRange(definition.Packet)));
        }

        var immutable = prepared.MoveToImmutable();
        var ambientValidation = TryPrepareAmbientSpawns(
            ownership,
            byObjectId,
            requireLivePlacement,
            out var ambientSpawns);
        if (ambientValidation.Outcome !=
            MedusaMonsterBootstrapValidationOutcome.Prepared)
        {
            return ambientValidation;
        }
        var preparation = new MedusaMonsterBootstrapPreparation(
            ownership!.WorldInstanceId,
            ownership.Difficulty,
            ownership.ContentMapId,
            ownership.Run.StartedAt.ToUniversalTime(),
            MonsterRespawnPolicy.Never,
            CreateFingerprint(ownership, immutable, ambientSpawns),
            immutable,
            ambientSpawns);
        return new(
            MedusaMonsterBootstrapValidationOutcome.Prepared,
            RejectedSpawnId: null,
            preparation);
    }

    private static bool TryValidateOwnership(
        MedusaInstanceOwnershipSnapshot? ownership,
        out MedusaEncounterDifficultyDefinition difficulty,
        out ImmutableArray<MedusaOwnedMonsterBinding> bindings,
        out MedusaMonsterBootstrapValidationResult failure)
    {
        difficulty = null!;
        bindings = default;
        if (ownership is null ||
            !ownership.WorldInstanceId.IsValid ||
            ownership.Run is null ||
            ownership.Run.WorldInstanceId != ownership.WorldInstanceId ||
            ownership.Run.Difficulty != ownership.Difficulty ||
            ownership.Run.ContentMapId != ownership.ContentMapId ||
            ownership.Run.State != MedusaRunState.Active ||
            ownership.Run.TeamScore != 0 ||
            ownership.Run.CompletionMarker is not null ||
            ownership.Run.Spawns is null ||
            ownership.Run.Spawns.Any(static spawn => spawn.Defeated) ||
            !MedusaIslandEncounterPolicy.TryGetDifficulty(
                ownership.Difficulty,
                out difficulty) ||
            difficulty.ContentMapId != ownership.ContentMapId)
        {
            failure = Rejected(
                MedusaMonsterBootstrapValidationOutcome.InvalidOwnership);
            return false;
        }

        bindings = ownership.MonsterBindings;
        if (bindings.IsDefault ||
            bindings.Length != MedusaIslandRosterPolicy.TotalSpawnCount ||
            ownership.Run.Spawns.Count != bindings.Length)
        {
            failure = Rejected(
                MedusaMonsterBootstrapValidationOutcome.InvalidBindingCount);
            return false;
        }

        var uniqueObjects = bindings.Select(static binding =>
            binding.Identity.ObjectId).Distinct().Count();
        var uniqueRoster = bindings.Select(static binding =>
                binding.RosterSpawnId)
            .Distinct(StringComparer.Ordinal).Count();
        if (uniqueObjects != bindings.Length ||
            uniqueRoster != bindings.Length)
        {
            failure = Rejected(
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding);
            return false;
        }

        if (ownership.Run.Spawns.Select(static spawn => spawn.ObjectId)
                .Distinct().Count() != ownership.Run.Spawns.Count)
        {
            failure = Rejected(
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding);
            return false;
        }

        var runByObject = ownership.Run.Spawns.ToDictionary(
            static spawn => spawn.ObjectId);
        foreach (var binding in bindings)
        {
            if (binding.Identity is not
                    { ObjectId: > 0, SpawnGeneration: 1 } ||
                binding.Difficulty != ownership.Difficulty ||
                binding.ContentMapId != ownership.ContentMapId ||
                !MedusaIslandRosterPolicy.TryGetSpawn(
                    binding.RosterSpawnId,
                    out var roster) ||
                roster.EncounterRole != binding.Role ||
                roster.Rank != binding.Rank ||
                !MedusaIslandRosterPolicy.TryResolveTemplate(
                    ownership.Difficulty,
                    roster.TemplateAlias,
                    out var template) ||
                template.MapId != ownership.ContentMapId.Value ||
                template.Rank != binding.Rank ||
                !string.Equals(
                    template.TemplateKey,
                    binding.TemplateKey,
                    StringComparison.Ordinal) ||
                !runByObject.TryGetValue(
                    binding.Identity.ObjectId,
                    out var runSpawn) ||
                runSpawn.SpawnGeneration != 1 ||
                !string.Equals(
                    runSpawn.RosterSpawnId,
                    binding.RosterSpawnId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    runSpawn.TemplateKey,
                    binding.TemplateKey,
                    StringComparison.Ordinal) ||
                runSpawn.Role != binding.Role ||
                runSpawn.Rank != binding.Rank)
            {
                failure = Rejected(
                    MedusaMonsterBootstrapValidationOutcome.InvalidBinding,
                    binding.RosterSpawnId);
                return false;
            }
        }

        failure = default;
        return true;
    }

    private static MedusaMonsterBootstrapValidationResult ValidateDefinition(
        MedusaInstanceOwnershipSnapshot ownership,
        MedusaOwnedMonsterBinding binding,
        CapturedMonsterSpawn definition,
        bool requireLivePlacement)
    {
        if (definition.MapId != ownership.ContentMapId.Value)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.MapMismatch,
                binding.RosterSpawnId);
        }

        try
        {
            definition.Validate(ownership.ContentMapId.Value);
        }
        catch (InvalidDataException)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome
                    .InvalidCapturedDefinition,
                binding.RosterSpawnId);
        }

        if (!MedusaIslandRosterPolicy.TryGetSpawn(
                binding.RosterSpawnId,
                out var roster) ||
            !MedusaIslandRosterPolicy.TryResolveTemplate(
                ownership.Difficulty,
                roster.TemplateAlias,
                out var template) ||
            !MedusaMonsterContentCatalog.Current.TryGetMonster(
                ownership.Difficulty,
                roster.TemplateAlias,
                out var monsterRule))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.InvalidBinding,
                binding.RosterSpawnId);
        }

        if (!string.Equals(
                definition.SceneKey,
                template.SceneKey,
                StringComparison.Ordinal))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.SceneMismatch,
                binding.RosterSpawnId);
        }
        if (!string.Equals(
                definition.TemplateKey,
                binding.TemplateKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                definition.TemplateKey,
                template.TemplateKey,
                StringComparison.Ordinal))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.TemplateMismatch,
                binding.RosterSpawnId);
        }
        if (!string.Equals(
                definition.DisplayName,
                template.DisplayName,
                StringComparison.Ordinal))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.DisplayNameMismatch,
                binding.RosterSpawnId);
        }
        if (definition.Tier != monsterRule.Level)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.TierMismatch,
                binding.RosterSpawnId);
        }

        var currentHealth = BinaryPrimitives.ReadUInt32LittleEndian(
            definition.Packet.AsSpan(20, 4));
        var maximumHealth = BinaryPrimitives.ReadUInt32LittleEndian(
            definition.Packet.AsSpan(24, 4));
        var authoredHealth = monsterRule.MaximumHealth;
        if (currentHealth != authoredHealth ||
            maximumHealth != authoredHealth)
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.HealthMismatch,
                binding.RosterSpawnId);
        }

        if (!requireLivePlacement)
        {
            return PreparedMarker();
        }

        if (!MedusaIslandPlacementPolicy.TryResolveServerSpawn(
                ownership.Difficulty,
                binding.RosterSpawnId,
                out var placement))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome
                    .PlacementNotCertified,
                binding.RosterSpawnId);
        }

        if (placement.MapId != definition.MapId ||
            !string.Equals(
                placement.SceneKey,
                definition.SceneKey,
                StringComparison.Ordinal) ||
            !SameFloat(placement.Placement.X, definition.X) ||
            !SameFloat(placement.Placement.Z, definition.Z) ||
            !SameFloat(placement.Placement.X, definition.AppearanceX) ||
            !SameFloat(placement.Placement.Z, definition.AppearanceZ))
        {
            return Rejected(
                MedusaMonsterBootstrapValidationOutcome.PlacementMismatch,
                binding.RosterSpawnId);
        }

        return PreparedMarker();
    }

    private static string CreateFingerprint(
        MedusaInstanceOwnershipSnapshot ownership,
        ImmutableArray<MedusaMonsterBootstrapPreparedSpawn> spawns,
        ImmutableArray<MedusaMonsterBootstrapPreparedAmbientSpawn>
            ambientSpawns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, FingerprintVersion);
        AppendString(hash, ownership.WorldInstanceId.Value.ToString("N"));
        AppendInt32(hash, (int)ownership.Difficulty);
        AppendInt32(hash, ownership.ContentMapId.Value);
        AppendInt64(
            hash,
            ownership.Run.StartedAt.ToUniversalTime().Ticks);
        AppendInt32(hash, (int)MonsterRespawnPolicy.Never);
        AppendInt32(hash, spawns.Length);
        foreach (var spawn in spawns)
        {
            AppendString(hash, spawn.RosterSpawnId);
            AppendUInt32(hash, spawn.ObjectId);
            AppendUInt32(hash, spawn.SpawnGeneration);
            AppendInt32(hash, (int)spawn.Role);
            AppendInt32(hash, (int)spawn.Rank);
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
        AppendAmbientFingerprint(hash, ambientSpawns);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value) =>
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(
        IncrementalHash hash,
        ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static bool SameFloat(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) ==
        BitConverter.SingleToInt32Bits(right);

    private static MedusaMonsterBootstrapValidationResult PreparedMarker() =>
        new(
            MedusaMonsterBootstrapValidationOutcome.Prepared,
            RejectedSpawnId: null,
            Preparation: null);

    private static MedusaMonsterBootstrapValidationResult Rejected(
        MedusaMonsterBootstrapValidationOutcome outcome,
        string? spawnId = null) =>
        new(outcome, spawnId, Preparation: null);
}
