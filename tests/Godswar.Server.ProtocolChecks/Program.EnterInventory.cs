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
    private static Task CheckPostEnterBootstrapGateAsync()
    {
        Check.Equal((ushort)10357, Opcodes.EnterUiReady, "final enter/UI-ready opcode");
        Check.Equal(nameof(Opcodes.EnterUiReady), Opcodes.Name(Opcodes.EnterUiReady), "final enter/UI-ready opcode name");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: false,
                playerDetailSent: true,
                enterUiReadyReceived: true),
            "bootstrap waits for ClientReady");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: false,
                enterUiReadyReceived: true),
            "bootstrap waits for PlayerDetail");
        Check.True(
            !GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: true,
                enterUiReadyReceived: false),
            "bootstrap waits for the final UI-ready signal");
        Check.True(
            GameClientHandler.CanSendPostEnterBootstrap(
                clientReadyReceived: true,
                playerDetailSent: true,
                enterUiReadyReceived: true),
            "bootstrap starts after every enter signal");

        return Task.CompletedTask;
    }

    private static Task CheckCapturedAcceptedQuestReplayExclusionAsync()
    {
        const int acceptedQuestRecordLength = 0x2A8;
        const int acceptedQuestCount = 3;
        var acceptedQuestSnapshot = new byte[8 + acceptedQuestCount * acceptedQuestRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            acceptedQuestSnapshot,
            checked((ushort)acceptedQuestSnapshot.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            acceptedQuestSnapshot.AsSpan(2),
            Opcodes.PlayerAcceptedQuests);
        BinaryPrimitives.WriteInt32LittleEndian(
            acceptedQuestSnapshot.AsSpan(4),
            acceptedQuestCount);

        Check.Equal(2048, acceptedQuestSnapshot.Length, "three-record accepted-quest snapshot length");
        Check.Equal((ushort)10090, Opcodes.PlayerAcceptedQuests, "native MSG_PLAYER_ACCEPTQUESTS opcode");
        Check.Equal(
            nameof(Opcodes.PlayerAcceptedQuests),
            Opcodes.Name(Opcodes.PlayerAcceptedQuests),
            "accepted-quest opcode name");
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(acceptedQuestSnapshot),
            "captured accepted-quest snapshots are never replayed during post-enter bootstrap");

        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(ReadOnlySpan<byte>.Empty),
            "empty captured packet is rejected");
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(new byte[3]),
            "captured packet shorter than its frame header is rejected");
        var malformedFrame = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(malformedFrame, 11);
        BinaryPrimitives.WriteUInt16LittleEndian(malformedFrame.AsSpan(2), Opcodes.UiHeartbeat);
        Check.True(
            !GameClientHandler.CanReplayCapturedPostEnterPacket(malformedFrame),
            "captured packet with a mismatched declared length is rejected");

        var benignFrame = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(benignFrame, checked((ushort)benignFrame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(benignFrame.AsSpan(2), Opcodes.UiHeartbeat);
        Check.True(
            GameClientHandler.CanReplayCapturedPostEnterPacket(benignFrame),
            "valid framed non-quest packet remains eligible for replay");

        return Task.CompletedTask;
    }


    private static Task CheckOccupiedGhostSlotBagMoveParsingAsync()
    {
        // Live account-13 request from 2026-07-21 00:30:35 UTC. The client
        // still believed bag slot 18 contained an equipped weapon, so its
        // full StorageItem request carried an opaque pointer at bytes 12..15
        // instead of the FFFF/FFFF markers used for an ordinary empty slot.
        var occupiedGhostMove = Convert.FromHexString(
            "F0DB7658000001000000120074AC3E67" +
            "4000000038000000282F9A2200000000" +
            "65AE3E670400000001000000E4F71A00" +
            "01000000000000000828291400000100" +
            "34F41A004000000040000000");
        Check.Equal(76, occupiedGhostMove.Length, "captured occupied-slot request payload length");
        Check.True(
            GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove,
                out var capturedSource,
                out var capturedDestination),
            "captured occupied ghost-slot move parses");
        Check.Equal(1, capturedSource, "captured occupied ghost-slot source");
        Check.Equal(18, capturedDestination, "captured occupied ghost-slot destination");

        var ordinaryShortMove = Convert.FromHexString(
            "000000000000010000001200FFFFFFFF");
        Check.True(
            GameClientHandler.TryReadStorageItemKitBagMove(
                ordinaryShortMove,
                out var ordinarySource,
                out var ordinaryDestination),
            "short ordinary move retains strict marker parsing");
        Check.Equal(1, ordinarySource, "short ordinary move source");
        Check.Equal(18, ordinaryDestination, "short ordinary move destination");

        var opaqueShortMove = occupiedGhostMove.AsSpan(0, 16).ToArray();
        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(opaqueShortMove, out _, out _),
            "short move rejects opaque occupied-slot markers");
        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove.AsSpan(0, occupiedGhostMove.Length - 1),
                out _,
                out _),
            "truncated full request rejects opaque occupied-slot markers");

        foreach (var (offset, invalidValue, label) in new (int Offset, ushort InvalidValue, string Label)[]
                 {
                     (4, 4, "source page"),
                     (6, 24, "source index"),
                     (8, 4, "destination page"),
                     (10, 24, "destination index")
                 })
        {
            var malformed = occupiedGhostMove.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(offset, 2), invalidValue);
            Check.True(
                !GameClientHandler.TryReadStorageItemKitBagMove(malformed, out _, out _),
                $"full occupied-slot move rejects out-of-bounds {label}");
        }

        Check.True(
            !GameClientHandler.TryReadStorageItemKitBagMove(
                occupiedGhostMove.AsSpan(0, 15),
                out _,
                out _),
            "malformed undersized move is rejected");

        return Task.CompletedTask;
    }

    private static async Task CheckBagItemDeletionAsync()
    {
        // Live client request after dragging bag slots 0 and 1 onto the ground
        // and accepting both confirmation dialogs. Destination page/index -1/-1
        // is the delete sentinel; trailing request bytes are unrelated stack data.
        var slotZeroPayload = Convert.FromHexString(
            "48F91A0000000000FFFFFFFF070000000800000009000000");
        Check.True(
            GameClientHandler.TryReadStorageItemDelete(slotZeroPayload, out var slotZero),
            "captured ground-drop request parses");
        Check.Equal(0, slotZero, "captured ground-drop source slot zero");

        var slotOnePayload = Convert.FromHexString(
            "48F91A0000000100FFFFFFFF070000000800000009000000");
        Check.True(
            GameClientHandler.TryReadStorageItemDelete(slotOnePayload, out var slotOne),
            "second captured ground-drop request parses");
        Check.Equal(1, slotOne, "captured ground-drop source slot one");

        var ordinaryMovePayload = Convert.FromHexString(
            "48F91A000000010000000200FFFFFFFF");
        Check.True(
            !GameClientHandler.TryReadStorageItemDelete(ordinaryMovePayload, out _),
            "ordinary bag move is not parsed as deletion");

        var acknowledgement = PacketBuilder.StorageItemKitBagDelete(sourceSlot: 25);
        Check.Equal(16, acknowledgement.Length, "bag delete acknowledgement length");
        Check.Equal((ushort)10052, ReadUInt16(acknowledgement, 2), "bag delete acknowledgement opcode");
        Check.Equal(0x1448u, ReadUInt32(acknowledgement, 4), "bag delete local player object ID");
        Check.Equal((ushort)1, ReadUInt16(acknowledgement, 8), "bag delete source page");
        Check.Equal((ushort)1, ReadUInt16(acknowledgement, 10), "bag delete source index");
        Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 12), "bag delete destination page sentinel");
        Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 14), "bag delete destination index sentinel");

        Check.Equal(0u, KitBagSlots.GetItemId(GameDefaults.EmptyKitBag, 0), "empty bag has no slot-zero potion");
        Check.Equal(0u, KitBagSlots.GetItemId(GameDefaults.EmptyKitBag, 1), "empty bag has no slot-one potion");
        Check.Equal(4000u, KitBagSlots.GetItemId(GameDefaults.StarterKitBag, 0), "starter bag has its HP potion");
        Check.Equal(4030u, KitBagSlots.GetItemId(GameDefaults.StarterKitBag, 1), "starter bag has its MP potion");

        var mutationBefore = GameDefaults.EmptyKitBag;
        mutationBefore = KitBagSlots.SetSlot(mutationBefore, 2, "[9950,,,,,,1,1,0,99,0]");
        mutationBefore = KitBagSlots.SetSlot(mutationBefore, 25, "[2007,10,30,100,120,,20,25,1,1,0]");
        mutationBefore = KitBagSlots.SetSlot(mutationBefore, 50, "[4215,,,,,,1,1,1,99,0]");
        mutationBefore = KitBagSlots.SetSlot(mutationBefore, 52, "[4230,,,,,,1,1,1,1,0]");
        var mutationAfter = KitBagSlots.SetSlot(mutationBefore, 2, "[9900,,,,,,1,1,1,1,0]");
        mutationAfter = KitBagSlots.ClearSlot(mutationAfter, 25);
        mutationAfter = KitBagSlots.SetSlot(mutationAfter, 50, "[4215,,,,,,1,1,1,98,0]");
        mutationAfter = KitBagSlots.SetSlot(mutationAfter, 51, "[4231,,,,,,1,1,1,1,0]");
        var mutationEvictions = PacketBuilder.KitBagMutationDeletionAcknowledgements(
            mutationBefore,
            mutationAfter);
        Check.Equal(3, mutationEvictions.Length, "bag mutation evicts each changed pre-existing icon");
        var mutationSlots = mutationEvictions
            .Select(packet =>
                (ReadUInt16(packet, 8) * 24) + ReadUInt16(packet, 10))
            .ToArray();
        Check.True(
            mutationSlots.SequenceEqual(new[] { 2, 25, 50 }),
            "bag mutation evicts replaced, removed, and stack-changed slots in order");

        var blankBagMutation = KitBagSlots.SetSlot(
            string.Empty,
            2,
            "[4230,,,,,,1,1,1,1,0]");
        Check.Equal(0u, KitBagSlots.GetItemId(blankBagMutation, 0), "blank mutation fallback does not grant HP potion");
        Check.Equal(0u, KitBagSlots.GetItemId(blankBagMutation, 1), "blank mutation fallback does not grant MP potion");
        Check.Equal(4230u, KitBagSlots.GetItemId(blankBagMutation, 2), "blank mutation writes only the requested slot");

        var blankCharacter = new GameCharacter();
        var blankDetails = PacketBuilder.KitBagDetailPages(blankCharacter);
        var blankIndexes = PacketBuilder.KitBagSlotIndexes(blankCharacter);
        Check.Equal(uint.MaxValue, ReadUInt32(blankDetails[0], 24), "blank hydration serializes an empty first detail slot");
        Check.Equal(-1, ReadInt32(blankIndexes[0], 20), "blank hydration serializes an empty first slot index");

        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-bag-delete-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            var ownerId = 0;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var owner = await store.LoginOrCreateAccountAsync("bag-delete-owner", "");
                var other = await store.LoginOrCreateAccountAsync("bag-delete-other", "");
                var character = await store.CreateCharacterAsync(
                    owner.Id,
                    new GameCharacter { Name = "BagDeleteHero" });
                ownerId = owner.Id;

                Check.Equal(4000u, KitBagSlots.GetItemId(character.KitBag, 0), "new character receives starter HP potion once");
                Check.Equal(4030u, KitBagSlots.GetItemId(character.KitBag, 1), "new character receives starter MP potion once");

                var unauthorized = await store.DeleteKitBagItemAsync(other.Id, character.Id, 1);
                Check.True(unauthorized is null, "different account cannot delete bag item");

                var firstDelete = await store.DeleteKitBagItemAsync(owner.Id, character.Id, 0)
                    ?? throw new InvalidOperationException("owner HP potion deletion returned no character");
                Check.Equal(0u, KitBagSlots.GetItemId(firstDelete.KitBag, 0), "deleted HP potion slot is empty");
                Check.Equal(4030u, KitBagSlots.GetItemId(firstDelete.KitBag, 1), "neighboring MP potion is unchanged");

                var secondDelete = await store.DeleteKitBagItemAsync(owner.Id, character.Id, 1)
                    ?? throw new InvalidOperationException("owner MP potion deletion returned no character");
                Check.Equal(0u, KitBagSlots.GetItemId(secondDelete.KitBag, 0), "HP potion remains deleted");
                Check.Equal(0u, KitBagSlots.GetItemId(secondDelete.KitBag, 1), "MP potion is deleted");

                await store.EnsureSeedDataAsync();
                var reseeded = await store.GetFirstCharacterAsync(owner.Id)
                    ?? throw new InvalidOperationException("bag deletion fixture was not reloaded after seed check");
                Check.Equal(0u, KitBagSlots.GetItemId(reseeded.KitBag, 0), "seed check does not restore HP potion");
                Check.Equal(0u, KitBagSlots.GetItemId(reseeded.KitBag, 1), "seed check does not restore MP potion");
            }

            await using var restartedStore = new JsonGameStore(dataPath);
            await restartedStore.EnsureSeedDataAsync();
            var restarted = await restartedStore.GetFirstCharacterAsync(ownerId)
                ?? throw new InvalidOperationException("bag deletion fixture was not reloaded after store restart");
            Check.Equal(0u, KitBagSlots.GetItemId(restarted.KitBag, 0), "restart does not restore HP potion");
            Check.Equal(0u, KitBagSlots.GetItemId(restarted.KitBag, 1), "restart does not restore MP potion");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
