using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.State;

internal sealed record CapturedMonsterSpawn(
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet)
{
    private const ushort WorldObjectAppearanceOpcode = 10020;
    private const int MinimumPacketLength = 108;
    private const float CoordinateMetadataTolerance = 0.0001f;

    public float AppearanceX => BinaryPrimitives.ReadSingleLittleEndian(Packet.AsSpan(28, 4));

    public float AppearanceZ => BinaryPrimitives.ReadSingleLittleEndian(Packet.AsSpan(36, 4));

    public uint Tier => BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(12, 4));

    public void Validate(short expectedMapId)
    {
        if (Packet is null)
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} has no appearance packet.");
        }

        if (MapId != expectedMapId)
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} belongs to map {MapId}, not requested map {expectedMapId}.");
        }

        if (ObjectId == 0)
        {
            throw new InvalidDataException("Captured monster object ID cannot be zero.");
        }

        if (string.IsNullOrWhiteSpace(TemplateKey))
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} has no template key.");
        }

        if (!float.IsFinite(X) || !float.IsFinite(Z))
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} has invalid coordinates ({X}, {Z}).");
        }

        if (Packet.Length < MinimumPacketLength)
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} packet is only {Packet.Length} bytes.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(Packet.AsSpan(0, 2));
        if (declaredLength != Packet.Length)
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} packet declares {declaredLength} bytes but contains {Packet.Length}.");
        }

        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(Packet.AsSpan(2, 2));
        var objectType = BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(4, 4));
        var embeddedObjectId = BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(8, 4));
        var tier = BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(12, 4));
        var currentHealth = BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(20, 4));
        var maximumHealth = BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(24, 4));
        var embeddedX = AppearanceX;
        var embeddedY = BinaryPrimitives.ReadSingleLittleEndian(Packet.AsSpan(32, 4));
        var embeddedZ = AppearanceZ;
        var embeddedFacing = BinaryPrimitives.ReadSingleLittleEndian(Packet.AsSpan(40, 4));
        var nameEnd = Array.IndexOf(Packet, (byte)0, 44, declaredLength - 44);
        var embeddedTemplateKey = Encoding.ASCII.GetString(
            Packet,
            44,
            (nameEnd < 0 ? declaredLength : nameEnd) - 44);

        if (opcode != WorldObjectAppearanceOpcode ||
            !IsCapturedMonsterObjectType(objectType) ||
            embeddedObjectId != ObjectId ||
            tier == 0 ||
            currentHealth == 0 ||
            maximumHealth == 0 ||
            !CoordinateMetadataMatches(X, embeddedX) ||
            !float.IsFinite(embeddedY) ||
            !CoordinateMetadataMatches(Z, embeddedZ) ||
            !float.IsFinite(embeddedFacing) ||
            string.IsNullOrWhiteSpace(embeddedTemplateKey) ||
            !string.Equals(embeddedTemplateKey, TemplateKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Captured monster {ObjectId} metadata does not match its opcode-10020 packet.");
        }
    }

    internal static bool IsCapturedMonsterObjectType(uint objectType)
    {
        // Captured normal, field, newbie-map, elite, and boss variants use
        // different high flags while retaining the monster discriminator 0x12.
        return (objectType & 0xFFu) == 0x12u;
    }

    private static bool CoordinateMetadataMatches(float metadataValue, float appearanceValue)
    {
        // The manual importer serializes float coordinates through decimal text,
        // so its PostgreSQL columns can differ from the raw packet by a few ULPs.
        return float.IsFinite(appearanceValue) &&
               Math.Abs((double)metadataValue - appearanceValue) <= CoordinateMetadataTolerance;
    }
}

internal readonly record struct CapturedMonsterAppearanceState(
    CapturedMonsterSpawn Definition,
    float X,
    float Z,
    float Facing,
    uint CurrentHealth,
    uint MaximumHealth);
