using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckHolySuitDesignProtocolAsync()
    {
        Check.True(
            HolySuitDesignProtocol.IsNpcKey("Sparta_085") &&
            HolySuitDesignProtocol.IsNpcKey("Athens_085"),
            "paired Master Vestment Forgers own the Holy Suit Design protocol");
        Check.True(
            !HolySuitDesignProtocol.IsNpcKey("Sparta_070") &&
            !HolySuitDesignProtocol.IsNpcKey("Sparta_044") &&
            !HolySuitDesignProtocol.IsNpcKey("Sparta_122"),
            "Gear Mentor, Class Shifter, and Ingredients Vendor cannot enter Holy Suit Design");
        Check.Equal(29, HolySuitDesignProtocol.DialogIndex, "Master Vestment Forger uses captured dialog 29");

        // Deliberately use a coordinate which would sort before the captured
        // value. This proves the authoritative actor correction wins by source
        // priority, not by the old incidental coordinate ordering.
        var staleIngredientReference = new NpcSpawnReferenceDefinition(
            0,
            "Sparta",
            "Sparta_122",
            "Sparta_122_FemVillager3",
            -500f,
            -500f);
        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(
            0,
            [],
            [],
            [staleIngredientReference]);
        Check.Equal(108, spartaDefinitions.Count, "all authoritative Sparta actor-table NPCs are spawned");
        var spartaClassShifter = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_044");
        Check.Equal(5041u, spartaClassShifter.InteractionId, "Sparta Class Shifter object 5041");
        Check.Equal("Sparta_044_Male34", spartaClassShifter.TemplateKey, "Sparta Class Shifter original appearance");
        Check.Equal(141f, spartaClassShifter.X, "Sparta Class Shifter captured x coordinate");
        Check.Equal(-174f, spartaClassShifter.Z, "Sparta Class Shifter captured z coordinate");
        var spartaForger = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_085");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, spartaForger.InteractionId, "Sparta forger object 5082");
        Check.Equal("Sparta_085_Male34", spartaForger.TemplateKey, "Sparta forger original appearance");
        Check.Equal(126f, spartaForger.X, "Sparta forger captured x coordinate");
        Check.Equal(-162f, spartaForger.Z, "Sparta forger actor-table z coordinate");
        Check.Equal(4.7f, spartaForger.Facing, "Sparta forger actor-table facing");
        var spartaIngredients = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_122");
        Check.Equal(5119u, spartaIngredients.InteractionId, "Sparta Ingredients Vendor object 5119");
        Check.Equal(97f, spartaIngredients.X, "Ingredients Vendor captured x overrides stale quest reference");
        Check.Equal(-174f, spartaIngredients.Z, "Ingredients Vendor captured z overrides stale quest reference");
        Check.Equal(1.7f, spartaIngredients.Facing, "Ingredients Vendor actor-table facing");
        var spartaOriginEnhancer = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_143");
        Check.Equal(
            GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
            spartaOriginEnhancer.InteractionId,
            "Sparta Origin Enhancer retains its protocol-safe interaction id");
        Check.Equal("Sparta_143_Hallo", spartaOriginEnhancer.TemplateKey, "Sparta Origin Enhancer uses its client-supported appearance");
        Check.Equal(97f, spartaOriginEnhancer.X, "Sparta Origin Enhancer actor-table x coordinate");
        Check.Equal(-163f, spartaOriginEnhancer.Z, "Sparta Origin Enhancer actor-table z coordinate");
        Check.Equal(1.7f, spartaOriginEnhancer.Facing, "Sparta Origin Enhancer actor-table facing");
        var previouslyMissingSpartaActor = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_028");
        Check.Equal(5025u, previouslyMissingSpartaActor.ObjectId, "Sparta actor uses the protocol-safe city object id");
        Check.Equal(113f, previouslyMissingSpartaActor.X, "Sparta actor-table X is imported");
        Check.Equal(-137f, previouslyMissingSpartaActor.Z, "Sparta actor-table Y maps to protocol Z");
        Check.Equal(3f, previouslyMissingSpartaActor.Facing, "Sparta actor-table Z maps to facing");

        var athensDefinitions = NpcSpawnDefinitionFactory.Create(1, [], [], []);
        Check.Equal(111, athensDefinitions.Count, "all authoritative Athens actor-table NPCs are spawned");
        var athensClassShifter = athensDefinitions.Single(definition => definition.NpcKey == "Athens_044");
        Check.Equal(5183u, athensClassShifter.InteractionId, "Athens paired Class Shifter object 5183");
        Check.Equal("Athens_044_Male34", athensClassShifter.TemplateKey, "Athens paired Class Shifter appearance");
        Check.Equal(141f, athensClassShifter.X, "Athens paired Class Shifter x coordinate");
        Check.Equal(-174f, athensClassShifter.Z, "Athens paired Class Shifter z coordinate");
        Check.Equal(2.3f, athensClassShifter.Facing, "Athens Class Shifter actor-table facing");
        var athensForger = athensDefinitions.Single(definition => definition.NpcKey == "Athens_085");
        Check.Equal(HolySuitDesignProtocol.AthensNpcId, athensForger.InteractionId, "Athens paired forger object 5224");
        Check.Equal("Athens_085_Male34", athensForger.TemplateKey, "Athens paired forger appearance");
        Check.Equal(126f, athensForger.X, "Athens paired forger x coordinate");
        Check.Equal(-162f, athensForger.Z, "Athens actor-table forger z coordinate");
        Check.Equal(4.7f, athensForger.Facing, "Athens actor-table forger facing");
        var athensIngredients = athensDefinitions.Single(definition => definition.NpcKey == "Athens_122");
        Check.Equal(5261u, athensIngredients.InteractionId, "Athens paired Ingredients Vendor object 5261");
        Check.Equal(97f, athensIngredients.X, "Athens Ingredients Vendor paired x coordinate");
        Check.Equal(-174f, athensIngredients.Z, "Athens Ingredients Vendor paired z coordinate");
        Check.Equal(1.7f, athensIngredients.Facing, "Athens Ingredients Vendor actor-table facing");
        var athensOriginEnhancer = athensDefinitions.Single(definition => definition.NpcKey == "Athens_143");
        Check.Equal(GearEnhancerProtocol.AthensOriginEnhancerNpcId, athensOriginEnhancer.InteractionId, "Athens Origin Enhancer retains its protocol-safe interaction id");
        Check.Equal("Athens_143_Hallo", athensOriginEnhancer.TemplateKey, "Athens Origin Enhancer uses its client-supported appearance");
        Check.Equal(97f, athensOriginEnhancer.X, "Athens Origin Enhancer actor-table x coordinate");
        Check.Equal(-163f, athensOriginEnhancer.Z, "Athens Origin Enhancer actor-table z coordinate");
        Check.Equal(1.7f, athensOriginEnhancer.Facing, "Athens Origin Enhancer actor-table facing");

        var dialogOpen = PacketBuilder.NpcDialogOpenAck(
            HolySuitDesignProtocol.SpartaNpcId,
            HolySuitDesignProtocol.DialogIndex,
            "Sparta_085");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, ReadUInt32(dialogOpen, 4), "Holy Suit open keeps object 5082");
        Check.Equal(HolySuitDesignProtocol.DialogIndex, ReadInt32(dialogOpen, 12), "Holy Suit open keeps dialog 29");
        Check.Equal("Sparta_085", ReadFixedAscii(dialogOpen, 16, 32), "Holy Suit open keeps NPC085 key");

        Check.True(
            HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                "Sparta_085",
                HolySuitDesignProtocol.SpartaNpcId,
                HolySuitDesignProtocol.DialogIndex,
                HolySuitDesignProtocol.InitialMenuRequestSubId,
                out var menu),
            "captured Master Vestment Forger initial request is accepted");
        Check.Equal((ushort)28, ReadUInt16(menu, 0), "Holy Suit original menu packet length");
        Check.Equal(HolySuitDesignProtocol.SpartaNpcId, ReadUInt32(menu, 4), "Holy Suit menu NPC id");
        Check.Equal(HolySuitDesignProtocol.DialogIndex, ReadInt32(menu, 8), "Holy Suit menu dialog index");
        Check.Equal(HolySuitDesignProtocol.StoreExperienceSubId, ReadInt32(menu, 12), "Holy Suit first captured menu id");
        Check.Equal(HolySuitDesignProtocol.TransferExperienceSubId, ReadInt32(menu, 16), "Holy Suit second captured menu id");
        Check.Equal(HolySuitDesignProtocol.ConsumeEquipmentSubId, ReadInt32(menu, 20), "Holy Suit third captured menu id");
        Check.Equal(HolySuitDesignProtocol.TransformExperienceSubId, ReadInt32(menu, 24), "Holy Suit fourth captured menu id");
        Check.True(
            !HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                "Sparta_085",
                HolySuitDesignProtocol.SpartaNpcId,
                37,
                HolySuitDesignProtocol.InitialMenuRequestSubId,
                out var classSuitResponse) &&
            classSuitResponse.Length == 0,
            "dialog 37 Class Suit cannot replace captured dialog 29 Holy Suit Design");

        return Task.CompletedTask;
    }

    private static Task CheckNpcDefinitionsAndSpawnLayoutAsync()
    {
        var spartaActorPlacements = NpcActorPlacementCatalog.All
            .Where(static placement => placement.MapId == 0)
            .ToArray();
        Check.Equal(108, spartaActorPlacements.Length, "Sparta NPC.INI actor count");
        Check.Equal(
            108,
            spartaActorPlacements.Select(static placement => placement.NpcKey).Distinct().Count(),
            "Sparta NPC.INI actor keys are unique");
        Check.Equal(
            108,
            spartaActorPlacements.Select(static placement => placement.SourceObjectId).Distinct().Count(),
            "Sparta NPC.INI source object IDs are unique");

        var capturedPacket = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(0, 2), 108);
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(4, 4), 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(8, 4), 5083);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(28, 4), 126f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(36, 4), -169.9f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(40, 4), 4.7f);
        Encoding.ASCII.GetBytes("Sparta_086_Male35").CopyTo(capturedPacket, 44);

        var detail10077 = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(0, 2), (ushort)detail10077.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(2, 2), 10077);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10077.AsSpan(4, 4), 5083);
        var detail10080 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(0, 2), (ushort)detail10080.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(2, 2), 10080);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10080.AsSpan(4, 4), 5083);

        var capturedSpartaArtisan = new CapturedNpcSpawn(
            0,
            "Sparta",
            "Sparta_086",
            "Sparta_086_Male35",
            5083,
            126f,
            -169.9f,
            capturedPacket,
            detail10077,
            detail10080);

        var originPacket = capturedPacket.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(originPacket.AsSpan(8, 4), 5140);
        BinaryPrimitives.WriteSingleLittleEndian(originPacket.AsSpan(28, 4), 126f);
        BinaryPrimitives.WriteSingleLittleEndian(originPacket.AsSpan(36, 4), -165.9f);
        originPacket.AsSpan(44, 64).Clear();
        Encoding.ASCII.GetBytes("Sparta_143_Hallo").CopyTo(originPacket, 44);
        var capturedOriginEnhancer = new CapturedNpcSpawn(
            0,
            "Sparta",
            "Sparta_143",
            "Sparta_143_Hallo",
            5140,
            126f,
            -165.9f,
            originPacket,
            [],
            []);
        var athensDefinitions = NpcSpawnDefinitionFactory.Create(1, [], [capturedSpartaArtisan], []);
        var athensArtisan = athensDefinitions.Single(definition => definition.NpcKey == "Athens_086");
        Check.Equal(5225u, athensArtisan.ObjectId, "Athens artisan object id");
        Check.Equal(5225u, athensArtisan.InteractionId, "Athens artisan interaction id");
        Check.Equal(126f, athensArtisan.X, "Athens artisan paired X");
        Check.Equal(-169f, athensArtisan.Z, "Athens artisan actor-table Z");
        Check.Equal(4.7f, athensArtisan.Facing, "Athens artisan paired facing");
        Check.Equal("Athens_086_Male35", athensArtisan.TemplateKey, "Athens artisan paired template");
        Check.Equal(0, athensArtisan.Detail10077.Length, "Athens fallback does not inherit Sparta detail 10077");
        Check.Equal(0, athensArtisan.Detail10080.Length, "Athens fallback does not inherit Sparta detail 10080");

        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(
            0,
            [capturedSpartaArtisan, capturedOriginEnhancer],
            [],
            []);
        var spartaArtisan = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_086");
        Check.Equal(5083u, spartaArtisan.ObjectId, "Sparta artisan object id");
        Check.Equal(126f, spartaArtisan.X, "Sparta artisan actor-table X");
        Check.Equal(-169f, spartaArtisan.Z, "Sparta artisan actor-table Z");
        Check.Equal(4.7f, spartaArtisan.Facing, "Sparta artisan actor-table facing");
        Check.True(spartaArtisan.Detail10077.SequenceEqual(detail10077), "Sparta detail 10077 is preserved");
        Check.True(spartaArtisan.Detail10080.SequenceEqual(detail10080), "Sparta detail 10080 is preserved");
        var spartaGearMentor = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_070");
        var spartaOriginEnhancer = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_143");
        Check.Equal(5067u, spartaGearMentor.ObjectId, "Gear Mentor has its own physical object id");
        Check.Equal(142f, spartaGearMentor.X, "Gear Mentor uses actor-table x coordinate");
        Check.Equal(-165f, spartaGearMentor.Z, "Gear Mentor uses actor-table z coordinate");
        Check.Equal(1.7f, spartaGearMentor.Facing, "Gear Mentor uses actor-table facing");
        Check.Equal(5140u, spartaOriginEnhancer.ObjectId, "Origin Enhancer keeps captured object id 5140");
        Check.Equal(97f, spartaOriginEnhancer.X, "Origin Enhancer uses actor-table x coordinate");
        Check.Equal(-163f, spartaOriginEnhancer.Z, "Origin Enhancer uses actor-table z coordinate");
        Check.Equal(1.7f, spartaOriginEnhancer.Facing, "Origin Enhancer uses actor-table facing");

        var stream = PacketBuilder.NpcSpawns([spartaArtisan, athensArtisan]);
        var athensOffset = 108 + detail10077.Length + detail10080.Length;
        Check.Equal(athensOffset + 108, stream.Length, "authoritative NPC frames include captured details");
        CheckNpcSpawnFrame(stream, 0, spartaArtisan);
        Check.True(
            stream.AsSpan(108, detail10077.Length).SequenceEqual(detail10077),
            "detail 10077 follows captured NPC appearance");
        Check.True(
            stream.AsSpan(108 + detail10077.Length, detail10080.Length).SequenceEqual(detail10080),
            "detail 10080 follows captured NPC appearance");
        CheckNpcSpawnFrame(stream, athensOffset, athensArtisan);
        return Task.CompletedTask;
    }
}
