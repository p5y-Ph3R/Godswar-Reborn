using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static void CheckFashionAppearanceProjection()
    {
        var catalog = TestItemContent.Content.FashionAppearances;
        Check.True(catalog.Count > 0, "pinned fashion appearance catalog is populated");
        Check.True(
            catalog.TryGet(8068, out var christmas),
            "perpetual Christmas fashion has an appearance projection");
        Check.Equal(
            FashionAppearanceCatalog.PartCount,
            christmas.PartIds.Length,
            "fashion projection has exactly twelve native body parts");

        var character = CreateAppearanceCharacter();
        character.Hair = 53;
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Stylish,
            "[8068,,,,,,1,1,1,1,0]");
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Amulet,
            "[3103,,,,,,1,1,1,1,0]");
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Shield,
            "[2000,,,,,,1,1,1,1,0]");

        var packet = PacketBuilder.EquipmentVisualRefresh(
            character,
            0x717u,
            catalog);
        Check.Equal(53u, ReadUInt32(packet, 8), "fashion without PartHair keeps character hair");
        AssertFashionParts(
            packet,
            [8061u, 0u, 8062u, 8063u, 8064u, 0u, 8065u, 8066u, 0u, 0u, 1834u, 2000u],
            "Christmas fashion");
        Check.Equal(
            0u,
            ReadUInt32(packet, 16 + (EquipmentSlots.Amulet * sizeof(uint))),
            "fashion does not leak ordinary amulet into a costume-owned body part");
        Check.Equal(
            1834u,
            ReadUInt32(packet, 16 + (EquipmentSlots.Weapon * sizeof(uint))),
            "fashion retains the authoritative held weapon");
        Check.Equal(
            2000u,
            ReadUInt32(packet, 16 + (EquipmentSlots.Shield * sizeof(uint))),
            "fashion retains the authoritative held shield");
        AssertFashionInventoryProjection(character);

        character = CreateAppearanceCharacter();
        character.Hair = 74;
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Stylish,
            "[8000,,,,,,1,1,1,1,0]");
        packet = PacketBuilder.EquipmentVisualRefresh(character, 0x718u, catalog);
        Check.Equal(184u, ReadUInt32(packet, 8), "PartHair keeps the character hair variant digit");
        AssertFashionParts(
            packet,
            [0u, 0u, 8002u, 8003u, 0u, 0u, 8005u, 0u, 0u, 0u, 1834u, 0u],
            "Maid fashion");

        character = CreateAppearanceCharacter();
        character.Hair = 61;
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Stylish,
            "[999999,,,,,,1,1,1,1,0]");
        packet = PacketBuilder.EquipmentVisualRefresh(character, 0x719u, catalog);
        Check.Equal(61u, ReadUInt32(packet, 8), "unknown fashion keeps ordinary hair");
        Check.Equal(2443u, ReadUInt32(packet, 16), "unknown fashion keeps ordinary head");
        Check.Equal(2261u, ReadUInt32(packet, 28), "unknown fashion keeps ordinary armor");
        Check.Equal(1834u, ReadUInt32(packet, 56), "unknown fashion keeps ordinary weapon");

        AssertHiddenFashionProjection(catalog);
        AssertFashionEffectVisibilityProtocol();
        AssertFashionLifecycleProjection();
    }

    private static void AssertHiddenFashionProjection(
        FashionAppearanceCatalog catalog)
    {
        var character = CreateAppearanceCharacter();
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Stylish,
            "[8068,,,,,,1,1,1,1,0]");
        var authoritativeEquipment = character.Equipment;
        character.FashionHidden = true;

        var refresh = PacketBuilder.EquipmentVisualRefresh(
            character,
            0x71Cu,
            catalog);
        Check.Equal(
            (uint)character.Hair,
            ReadUInt32(refresh, 8),
            "hidden Fashion restores ordinary hair");
        Check.Equal(
            2443u,
            ReadUInt32(refresh, 16),
            "hidden Fashion restores ordinary head appearance");
        Check.Equal(
            2261u,
            ReadUInt32(refresh, 28),
            "hidden Fashion restores ordinary armor appearance");

        var spawn = PacketBuilder.PlayerWorldSpawn(character, 0x71Cu);
        Check.Equal(
            0u,
            ReadUInt32(spawn, 168) &
                (1u << EquipmentSlots.Stylish),
            "hidden Fashion is omitted from remote spawn mask");

        var selfSnapshot = PacketBuilder.EquipmentItemSnapshot(
            character,
            EquipmentSlots.Stylish);
        Check.Equal(
            8068u,
            ReadUInt32(selfSnapshot, 20),
            "hidden Fashion remains equipped in self inventory");

        var inspection = PacketBuilder.PlayerInspectEquipment(
            character,
            0x71Cu);
        Check.Equal(
            1u << EquipmentSlots.Stylish,
            ReadUInt32(inspection, 1520) &
                (1u << EquipmentSlots.Stylish),
            "hidden Fashion remains present in equipment inspection");
        Check.Equal(
            authoritativeEquipment,
            character.Equipment,
            "hidden Fashion projection does not mutate equipment state");
        Check.True(
            !JsonSerializer.Serialize(character).Contains(
                nameof(GameCharacter.FashionHidden),
                StringComparison.Ordinal),
            "Fashion visibility is runtime-only and excluded from persistence JSON");
        Check.True(
            !JsonSerializer.Serialize(character).Contains(
                nameof(GameCharacter.EquipmentEffectsVisible),
                StringComparison.Ordinal),
            "Fashion Effect visibility is runtime-only and excluded from persistence JSON");

        Span<byte> request = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(
            request,
            0xA5A5_5A5Au);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 1u);
        Check.True(
            GameClientHandler.TryReadFashionVisibilityRequest(
                request,
                out var hidden),
            "fashion visibility accepts the captured eight-byte body");
        Check.True(hidden, "fashion visibility flag 1 means hidden");

        BinaryPrimitives.WriteUInt32LittleEndian(request, 0x1020_3040u);
        Check.True(
            GameClientHandler.TryReadFashionVisibilityRequest(
                request,
                out hidden) && hidden,
            "fashion visibility ignores the native noise DWORD");

        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 0u);
        Check.True(
            GameClientHandler.TryReadFashionVisibilityRequest(
                request,
                out hidden) && !hidden,
            "fashion visibility flag 0 means shown");

        var noFashion = CreateAppearanceCharacter();
        noFashion.Equipment = EquipmentSlots.ClearSlot(
            noFashion.Equipment,
            noFashion.Profession,
            EquipmentSlots.Stylish);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 1u);
        Check.True(
            GameClientHandler.TryReadFashionVisibilityRequest(
                request,
                out var forcedHidden),
            "no-costume native hidden packet remains structurally valid");
        if (GameClientHandler.HasEquippedFashion(noFashion))
        {
            noFashion.FashionHidden = forcedHidden;
        }
        Check.True(
            !noFashion.FashionHidden,
            "no-costume native hidden=1 does not overwrite the user preference");
        noFashion.Equipment = EquipmentSlots.SetSlot(
            noFashion.Equipment,
            noFashion.Profession,
            EquipmentSlots.Stylish,
            "[8068,,,,,,1,1,1,1,0]");
        var laterEquipRefresh = PacketBuilder.EquipmentVisualRefresh(
            noFashion,
            0x71Du,
            catalog);
        Check.Equal(
            8061u,
            ReadUInt32(laterEquipRefresh, 16),
            "Fashion equipped after no-costume login remains shown");
        Check.True(
            GameClientHandler.HasEquippedFashion(character),
            "equipped Fashion permits explicit visibility changes");

        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 2u);
        Check.True(
            !GameClientHandler.TryReadFashionVisibilityRequest(
                request,
                out _),
            "fashion visibility rejects non-checkbox flag values");
        Check.True(
            !GameClientHandler.TryReadFashionVisibilityRequest(
                request[..4],
                out _),
            "fashion visibility rejects truncated payloads");
    }

    private static void AssertFashionEffectVisibilityProtocol()
    {
        const uint objectId = 0xA1B2_C3D4u;
        var enabled = PacketBuilder.EquipmentEffectVisibility(
            objectId,
            visible: true);
        Check.Equal(12, enabled.Length, "Fashion Effect S2C packet length");
        Check.Equal(
            (ushort)10202,
            BinaryPrimitives.ReadUInt16LittleEndian(enabled.AsSpan(2, 2)),
            "Fashion Effect S2C opcode");
        Check.Equal(
            objectId,
            ReadUInt32(enabled, 4),
            "Fashion Effect S2C authoritative object id");
        Check.Equal(
            1u,
            ReadUInt32(enabled, 8),
            "Fashion Effect enabled projection");

        var disabled = PacketBuilder.EquipmentEffectVisibility(
            objectId,
            visible: false);
        Check.Equal(
            0u,
            ReadUInt32(disabled, 8),
            "Fashion Effect disabled projection");

        Span<byte> request = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(
            request,
            0xDEAD_BEEFu);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            request[8..],
            0xA5A5_5A5Au);
        Check.True(
            GameClientHandler.TryReadFashionEffectVisibilityRequest(
                request,
                out var effectsVisible) && effectsVisible,
            "Fashion Effect accepts native identity and trailing noise without trusting either");

        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 0u);
        Check.True(
            GameClientHandler.TryReadFashionEffectVisibilityRequest(
                request,
                out effectsVisible) && !effectsVisible,
            "Fashion Effect flag 0 disables armor and weapon aura renderers");

        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 2u);
        Check.True(
            !GameClientHandler.TryReadFashionEffectVisibilityRequest(
                request,
                out _),
            "Fashion Effect rejects non-checkbox flag values");
        Check.True(
            !GameClientHandler.TryReadFashionEffectVisibilityRequest(
                request[..8],
                out _),
            "Fashion Effect rejects truncated native requests");
    }

    private static void AssertFashionLifecycleProjection()
    {
        var shownFashion = CreateAppearanceCharacter();
        shownFashion.Equipment = EquipmentSlots.SetSlot(
            shownFashion.Equipment,
            shownFashion.Profession,
            EquipmentSlots.Stylish,
            "[8068,,,,,,1,1,1,1,0]");
        shownFashion.EquipmentEffectsVisible = false;
        Check.True(
            !GameClientHandler.ResolveEquipmentEffectProjection(
                shownFashion),
            "shown Fashion applies the client-owned Effect-off preference");

        shownFashion.FashionHidden = true;
        Check.True(
            GameClientHandler.ResolveEquipmentEffectProjection(
                shownFashion),
            "Show-off restores ordinary armor and weapon rank effects");

        var withoutFashion = CreateAppearanceCharacter();
        withoutFashion.Equipment = EquipmentSlots.ClearSlot(
            withoutFashion.Equipment,
            withoutFashion.Profession,
            EquipmentSlots.Stylish);
        withoutFashion.EquipmentEffectsVisible = false;
        Check.True(
            GameClientHandler.ResolveEquipmentEffectProjection(
                withoutFashion),
            "unequipped Fashion always restores ordinary rank effects");

        withoutFashion.FashionHidden = true;
        var newlyEquipped = CreateAppearanceCharacter();
        newlyEquipped.Equipment = EquipmentSlots.SetSlot(
            newlyEquipped.Equipment,
            newlyEquipped.Profession,
            EquipmentSlots.Stylish,
            "[8068,,,,,,1,1,1,1,0]");
        Check.True(
            !GameClientHandler.ResolveFashionHiddenAfterEquipmentChange(
                withoutFashion,
                newlyEquipped),
            "equipping Fashion defaults its authoritative Show state to on");

        newlyEquipped.FashionHidden = true;
        Check.True(
            !GameClientHandler.ResolveFashionHiddenAfterEquipmentChange(
                newlyEquipped,
                withoutFashion),
            "unequipping Fashion clears the stale hidden preference");

        var refreshedFashion = CreateAppearanceCharacter();
        refreshedFashion.Equipment = newlyEquipped.Equipment;
        Check.True(
            GameClientHandler.ResolveFashionHiddenAfterEquipmentChange(
                newlyEquipped,
                refreshedFashion),
            "ordinary character refresh preserves an unchanged Fashion choice");
    }

    private static void AssertFashionInventoryProjection(
        GameCharacter character)
    {
        var spawn = PacketBuilder.PlayerWorldSpawn(character, 0x71Au);
        Check.Equal(
            1u << EquipmentSlots.Stylish,
            ReadUInt32(spawn, 168) & (1u << EquipmentSlots.Stylish),
            "world appearance marks native Fashion slot 12");
        var mask = ReadUInt32(spawn, 168);
        var packedIndex = 0;
        for (var slot = EquipmentSlots.Head;
             slot < EquipmentSlots.Stylish;
             slot++)
        {
            if ((mask & (1u << slot)) != 0)
            {
                packedIndex++;
            }
        }
        Check.Equal(
            (ushort)8068,
            ReadUInt16(spawn, 124 + (packedIndex * sizeof(ushort))),
            "world appearance packs the Fashion item at mask bit 12");

        var snapshot = PacketBuilder.EquipmentItemSnapshot(
            character,
            EquipmentSlots.Stylish);
        Check.Equal(
            (ushort)EquipmentSlots.Stylish,
            ReadUInt16(snapshot, 14),
            "self Fashion snapshot retains native slot 12");
        Check.Equal(
            8068u,
            ReadUInt32(snapshot, 20),
            "self Fashion snapshot contains the costume item");

        var legacy = EquipmentSlots.SetSlot(
            EquipmentSlots.ClearSlot(
                character.Equipment,
                character.Profession,
                EquipmentSlots.Stylish),
            character.Profession,
            13,
            "[8068,,,,,,1,1,1,1,0]");
        character.Equipment = legacy;
        spawn = PacketBuilder.PlayerWorldSpawn(character, 0x71Bu);
        Check.Equal(
            0u,
            ReadUInt32(spawn, 168) & (1u << EquipmentSlots.Stylish),
            "reserved legacy slot 13 is not serialized as Fashion");
    }

    private static void AssertFashionParts(
        byte[] packet,
        IReadOnlyList<uint> expected,
        string label)
    {
        Check.Equal(
            FashionAppearanceCatalog.PartCount,
            expected.Count,
            $"{label} expected-part count");
        for (var index = 0; index < expected.Count; index++)
        {
            Check.Equal(
                expected[index],
                ReadUInt32(packet, 16 + (index * sizeof(uint))),
                $"{label} body part {index}");
        }
    }
}
