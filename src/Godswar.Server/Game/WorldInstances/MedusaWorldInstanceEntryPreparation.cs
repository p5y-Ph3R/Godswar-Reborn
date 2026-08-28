using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Builds the fixed Medusa roster before an instance becomes visible.
/// </summary>
internal sealed class MedusaWorldInstanceEntryPreparation :
    IWorldInstanceRuntimePreparation
{
    private const int AppearancePacketLength = 108;
    private const int TemplateKeyOffset = 44;

    private readonly MedusaEncounterDifficulty _difficulty;
    private readonly int[] _admittedCharacterIds;
    private MedusaMonsterAttachmentSnapshot? _preparedAttachment;

    public MedusaWorldInstanceEntryPreparation(
        MedusaEncounterDifficulty difficulty,
        IEnumerable<int> admittedCharacterIds)
    {
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out _))
        {
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        }

        ArgumentNullException.ThrowIfNull(admittedCharacterIds);
        _difficulty = difficulty;
        _admittedCharacterIds = admittedCharacterIds
            .Distinct()
            .Order()
            .ToArray();
        if (_admittedCharacterIds.Length is <
                MedusaIslandPolicy.MinimumPartySize or >
                MedusaIslandPolicy.MaximumPartySize ||
            _admittedCharacterIds.Any(static id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(admittedCharacterIds));
        }
    }

    public void Prepare(IWorldInstanceRuntimePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_preparedAttachment is not null)
        {
            throw new InvalidOperationException(
                "Medusa entry preparation was invoked more than once.");
        }

        var result = Attach(context);
        if (result.Outcome != MedusaMonsterAttachmentOutcome.Attached ||
            result.Snapshot is null)
        {
            throw AttachmentFailure(result);
        }

        _preparedAttachment = result.Snapshot;
    }

    public void ValidatePrepared(
        IWorldInstanceRuntimePreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expected = _preparedAttachment ??
            throw new InvalidOperationException(
                "Medusa entry validation ran before preparation.");
        var result = Attach(context);
        if (result.Outcome !=
                MedusaMonsterAttachmentOutcome.AlreadyAttached ||
            result.Snapshot != expected)
        {
            throw AttachmentFailure(result);
        }
    }

    private MedusaMonsterAttachmentResult Attach(
        IWorldInstanceRuntimePreparationContext context)
    {
        if (context.Population != 0 ||
            !MedusaIslandEncounterPolicy.TryGetDifficulty(
                _difficulty,
                out var definition) ||
            context.Descriptor.MapId != definition.ContentMapId)
        {
            throw new InvalidOperationException(
                "The Medusa runtime does not match the requested encounter.");
        }

        var runSpawns = new MedusaRunSpawnDefinition[
            MedusaIslandRosterPolicy.TotalSpawnCount];
        var captures = new CapturedMonsterSpawn[
            runSpawns.Length +
            MedusaIslandAmbientSpawnPolicy.CountFor(_difficulty)];
        for (var index = 0; index < runSpawns.Length; index++)
        {
            var roster = MedusaIslandRosterPolicy.Spawns[index];
            if (!MedusaIslandRosterPolicy.TryResolveTemplate(
                    _difficulty,
                    roster.TemplateAlias,
                    out var template) ||
                !MedusaIslandPlacementPolicy.TryResolveServerSpawn(
                    _difficulty,
                    roster.SpawnId,
                    out var placement) ||
                !MedusaMonsterContentCatalog.Current.TryGetMonster(
                    _difficulty,
                    roster.TemplateAlias,
                    out var monsterRule))
            {
                throw new InvalidOperationException(
                    $"Medusa spawn {roster.SpawnId} cannot be resolved.");
            }

            var objectId = checked(
                WorldObjectIds.FirstMedusaMonsterObjectId +
                (uint)index);
            if (!WorldObjectIds.IsMonster(objectId))
            {
                throw new InvalidOperationException(
                    "The Medusa roster exceeds the native monster object-ID range.");
            }
            runSpawns[index] = new(
                roster.SpawnId,
                objectId,
                SpawnGeneration: 1,
                template.TemplateKey,
                roster.EncounterRole,
                roster.Rank);
            captures[index] = CreateCapture(
                template,
                objectId,
                monsterRule.Level,
                monsterRule.MaximumHealth,
                placement.Placement.X,
                placement.Placement.Z);
        }

        var ambientSpawns =
            MedusaIslandAmbientSpawnPolicy.SpawnsFor(_difficulty);
        for (var index = 0; index < ambientSpawns.Count; index++)
        {
            captures[runSpawns.Length + index] = CreateCapture(
                ambientSpawns[index],
                WorldObjectIds.MedusaBabyRockElfObjectIds[index]);
        }

        return context.PrepareAndAttachMedusaProductionLive(
            _difficulty,
            _admittedCharacterIds,
            runSpawns,
            captures);
    }

    private static CapturedMonsterSpawn CreateCapture(
        MedusaIslandResolvedTemplate template,
        uint objectId,
        uint tier,
        uint health,
        float x,
        float z)
        => CreateCapture(
            template.MapId,
            template.SceneKey,
            template.TemplateKey,
            template.DisplayName,
            objectId,
            tier,
            health,
            x,
            z);

    private static CapturedMonsterSpawn CreateCapture(
        MedusaIslandAmbientSpawn ambient,
        uint objectId)
        => CreateCapture(
            ambient.MapId,
            ambient.SceneKey,
            ambient.TemplateKey,
            ambient.DisplayName,
            objectId,
            ambient.Tier,
            ambient.MaximumHealth,
            ambient.X,
            ambient.Z);

    private static CapturedMonsterSpawn CreateCapture(
        short mapId,
        string sceneKey,
        string templateKey,
        string displayName,
        uint objectId,
        uint tier,
        uint health,
        float x,
        float z)
    {
        var packet = new byte[AppearancePacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)AppearancePacketLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0x00000212);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(6),
            checked((ushort)mapId));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            tier);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            health);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            health);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), z);

        var templateBytes = Encoding.ASCII.GetBytes(templateKey);
        if (templateBytes.Length >= packet.Length - TemplateKeyOffset)
        {
            throw new InvalidOperationException(
                $"Medusa template {templateKey} is too long.");
        }
        templateBytes.CopyTo(packet.AsSpan(TemplateKeyOffset));

        var capture = new CapturedMonsterSpawn(
            mapId,
            sceneKey,
            templateKey,
            displayName,
            objectId,
            x,
            z,
            packet);
        capture.Validate(mapId);
        return capture;
    }

    private static InvalidOperationException AttachmentFailure(
        MedusaMonsterAttachmentResult result) =>
        new(
            "Medusa roster attachment failed: " +
            $"{result.Outcome}/" +
            $"{result.OwnershipOutcome?.ToString() ?? "none"}/" +
            $"{result.BootstrapOutcome?.ToString() ?? "none"}.");
}
