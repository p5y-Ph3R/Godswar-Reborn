using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

internal static partial class WorldContentRevisionHasher
{
    private const int CanonicalFormatVersion = 1;

    public static WorldContentFamilyRevision HashMaps(
        IReadOnlyList<short> mapIds)
    {
        using var hash = new CanonicalHashBuilder("maps");
        foreach (var mapId in mapIds)
        {
            hash.AppendInt16(mapId);
        }

        return new WorldContentFamilyRevision(
            "maps",
            hash.Finish(),
            mapIds.Count);
    }

    public static WorldContentFamilyRevision HashNpcs(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        using var hash = new CanonicalHashBuilder("npcs");
        foreach (var definition in definitions)
        {
            hash.AppendInt16(definition.MapId);
            hash.AppendString(definition.SceneKey);
            hash.AppendString(definition.NpcKey);
            hash.AppendString(definition.TemplateKey);
            hash.AppendUInt32(definition.ObjectId);
            hash.AppendSingle(definition.X);
            hash.AppendSingle(definition.Z);
            hash.AppendUInt32(definition.InteractionId);
            hash.AppendUInt32(definition.AppearanceType);
            hash.AppendSingle(definition.Facing);
            hash.AppendBytes(definition.Detail10077);
            hash.AppendBytes(definition.Detail10080);
        }

        return new WorldContentFamilyRevision(
            "npcs",
            hash.Finish(),
            definitions.Count);
    }

    public static WorldContentFamilyRevision HashNpcDialogues(
        IReadOnlyList<NpcTextDefinition> texts,
        IReadOnlyList<NpcDialogueRouteDefinition> routes)
    {
        using var hash = new CanonicalHashBuilder("npc-dialogues");
        var hasOrderedMultiRoutes = routes.Any(
            static route => route.RouteOrder != 0);
        hash.AppendInt32(texts.Count);
        foreach (var text in texts)
        {
            hash.AppendString(text.NpcKey);
            hash.AppendString(text.SceneKey);
            hash.AppendString(text.DisplayName);
            hash.AppendString(text.Description);
        }

        hash.AppendInt32(routes.Count);
        foreach (var route in routes)
        {
            hash.AppendString(route.NpcKey);
            hash.AppendString(route.ClientScriptKey);
            hash.AppendInt32(route.DialogIndex);
            hash.AppendInt32((int)route.Behavior);
            hash.AppendInt32(route.InitialMenuSubIds.Length);
            foreach (var subId in route.InitialMenuSubIds)
            {
                hash.AppendInt32(subId);
            }

            // V1 had exactly one implicit order-zero route per NPC. Only
            // append the explicit order for V2 multi-route publications so
            // the immutable V1 rollback revision retains its golden hash.
            if (hasOrderedMultiRoutes)
            {
                hash.AppendInt32(route.RouteOrder);
            }
        }

        return new WorldContentFamilyRevision(
            "npc-dialogues",
            hash.Finish(),
            checked(texts.Count + routes.Count));
    }

    public static WorldContentFamilyRevision HashMonsters(
        IReadOnlyList<CapturedMonsterSpawn> definitions)
    {
        using var hash = new CanonicalHashBuilder("monsters");
        foreach (var definition in definitions)
        {
            hash.AppendInt16(definition.MapId);
            hash.AppendString(definition.SceneKey);
            hash.AppendString(definition.TemplateKey);
            hash.AppendString(definition.DisplayName);
            hash.AppendUInt32(definition.ObjectId);
            hash.AppendSingle(definition.X);
            hash.AppendSingle(definition.Z);
            hash.AppendBytes(definition.Packet);
        }

        return new WorldContentFamilyRevision(
            "monsters",
            hash.Finish(),
            definitions.Count);
    }

    public static WorldContentFamilyRevision HashEnterBootstrap(
        IReadOnlyList<byte[]> packets)
    {
        using var hash = new CanonicalHashBuilder("enter-bootstrap");
        foreach (var packet in packets)
        {
            hash.AppendBytes(packet);
        }

        return new WorldContentFamilyRevision(
            "enter-bootstrap",
            hash.Finish(),
            packets.Count);
    }

    public static string HashManifest(
        params WorldContentFamilyRevision[] revisions)
    {
        using var hash = new CanonicalHashBuilder("manifest");
        foreach (var revision in revisions
                     .OrderBy(
                         static value => value.Family,
                         StringComparer.Ordinal))
        {
            hash.AppendString(revision.Family);
            hash.AppendString(revision.Sha256);
            hash.AppendInt32(revision.EntryCount);
        }

        return hash.Finish();
    }

    private sealed class CanonicalHashBuilder : IDisposable
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finished;

        public CanonicalHashBuilder(string family)
        {
            AppendInt32(CanonicalFormatVersion);
            AppendString(family);
        }

        public void AppendInt16(short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AppendInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AppendInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AppendUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AppendSingle(float value) =>
            AppendInt32(BitConverter.SingleToInt32Bits(value));

        public void AppendString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            AppendBytes(Encoding.UTF8.GetBytes(value));
        }

        public void AppendBytes(ReadOnlySpan<byte> value)
        {
            AppendInt32(value.Length);
            _hash.AppendData(value);
        }

        public string Finish()
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _finished = true;
            return Convert.ToHexString(_hash.GetHashAndReset());
        }

        public void Dispose()
        {
            _finished = true;
            _hash.Dispose();
        }
    }
}
