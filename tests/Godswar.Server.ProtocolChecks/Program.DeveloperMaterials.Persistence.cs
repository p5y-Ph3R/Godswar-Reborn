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
    private static async Task CheckDeveloperForgingMaterialPersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-developer-item-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("developer-item-owner", "");
            var other = await store.LoginOrCreateAccountAsync("developer-item-other", "");
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "DeveloperItemHero" });

            var unauthorized = await store.AddForgingMaterialAsync(
                other.Id,
                character.Id,
                4230,
                1);
            Check.True(
                unauthorized.Status == KitBagItemGrantStatus.CharacterNotFound,
                "different account cannot grant into another character bag");
            var unauthorizedEnhancement = await store.AddForgingMaterialAsync(
                other.Id,
                character.Id,
                9990,
                1);
            Check.True(
                unauthorizedEnhancement.Status == KitBagItemGrantStatus.CharacterNotFound,
                "different account cannot grant a gear-enhancement material into another character bag");

            var granted = await store.AddForgingMaterialAsync(
                owner.Id,
                character.Id,
                4230,
                150);
            Check.True(granted.Added && granted.Character is not null, "owner material grant succeeds atomically");
            Check.Equal(4230u, KitBagSlots.GetItemId(granted.Character!.KitBag, 2), "grant uses first empty slot");
            Check.Equal((short)99, KitBagSlots.GetItem(granted.Character.KitBag, 2).Stack, "persisted first stack uses native cap");
            Check.Equal((short)0, KitBagSlots.GetItem(granted.Character.KitBag, 2).Bound, "native unbound material remains unbound");
            Check.Equal((short)51, KitBagSlots.GetItem(granted.Character.KitBag, 3).Stack, "persisted second stack has remainder");

            var detailPages = PacketBuilder.KitBagDetailPages(granted.Character);
            Check.Equal(8, detailPages.Length, "bag refresh contains all detail half-pages");
            var slotTwoRecordOffset = 24 + (2 * 72);
            Check.Equal(4230u, ReadUInt32(detailPages[0], slotTwoRecordOffset), "bag refresh details include granted item");
            Check.Equal((byte)99, detailPages[0][slotTwoRecordOffset + 27], "bag refresh details include granted stack");
            var slotIndexes = PacketBuilder.KitBagSlotIndexes(granted.Character);
            Check.Equal(96, slotIndexes.Length, "bag refresh contains every slot index");
            Check.Equal(4230u, ReadUInt32(slotIndexes[2], 20), "bag refresh slot index includes granted item");

            var reloaded = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer item fixture was not reloaded");
            Check.Equal((short)99, KitBagSlots.GetItem(reloaded.KitBag, 2).Stack, "first material stack persists after reload");
            Check.Equal((short)51, KitBagSlots.GetItem(reloaded.KitBag, 3).Stack, "second material stack persists after reload");

            var enhancementGranted = await store.AddForgingMaterialAsync(
                owner.Id,
                character.Id,
                9930,
                100);
            Check.True(
                enhancementGranted.Added && enhancementGranted.Character is not null,
                "owner gear-enhancement material grant succeeds through the same authoritative store path");
            Check.Equal(
                9930u,
                KitBagSlots.GetItemId(enhancementGranted.Character!.KitBag, 4),
                "gear-enhancement grant uses the next authoritative empty slot");
            Check.Equal(
                (short)99,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 4).Stack,
                "gear-enhancement grant obeys its server-owned stack cap");
            Check.Equal(
                (short)0,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 4).Bound,
                "gear-enhancement grant obeys its server-owned native binding state");
            Check.Equal(
                (short)1,
                KitBagSlots.GetItem(enhancementGranted.Character.KitBag, 5).Stack,
                "gear-enhancement grant allocates its remainder atomically");

            var rejectedArbitraryId = false;
            try
            {
                await store.AddForgingMaterialAsync(owner.Id, character.Id, 999999, 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedArbitraryId = true;
            }

            Check.True(rejectedArbitraryId, "store rejects IDs outside the unified developer material allowlist");

            var beforeClear = enhancementGranted.Character!;
            var bagBeforeClear = beforeClear.KitBag;
            var equipmentBeforeClear = beforeClear.Equipment;
            var occupiedSlotsBeforeClear = Enumerable
                .Range(0, KitBagItemGrantPlanner.SlotCount)
                .Where(slot => !KitBagSlots.GetItem(beforeClear.KitBag, slot).IsEmpty)
                .ToArray();
            var deletionAcknowledgements =
                PacketBuilder.KitBagDeletionAcknowledgements(beforeClear);
            Check.Equal(
                occupiedSlotsBeforeClear.Length,
                deletionAcknowledgements.Length,
                "bulk clear emits one native deletion acknowledgement per occupied client slot");
            for (var index = 0; index < deletionAcknowledgements.Length; index++)
            {
                var expectedSlot = occupiedSlotsBeforeClear[index];
                var expectedPage = Math.DivRem(expectedSlot, 24, out var expectedPageIndex);
                var acknowledgement = deletionAcknowledgements[index];
                Check.Equal((ushort)10052, ReadUInt16(acknowledgement, 2), "bulk clear uses native deletion opcode");
                Check.Equal((ushort)expectedPage, ReadUInt16(acknowledgement, 8), "bulk clear deletion source page");
                Check.Equal((ushort)expectedPageIndex, ReadUInt16(acknowledgement, 10), "bulk clear deletion source index");
                Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 12), "bulk clear deletion destination page sentinel");
                Check.Equal(ushort.MaxValue, ReadUInt16(acknowledgement, 14), "bulk clear deletion destination index sentinel");
            }

            var skillsBeforeClear = string.Join(
                ',',
                (await store.GetSkillStatesAsync(owner.Id, character.Id))
                    .OrderBy(skill => skill.SkillId)
                    .Select(skill => $"{skill.SkillId}:{skill.Level}"));

            var unauthorizedClear = await store.ClearKitBagAsync(other.Id, character.Id);
            Check.True(
                unauthorizedClear is null,
                "different account cannot clear another character's bag");
            var afterUnauthorizedClear = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer clear-bag fixture was not reloaded after denied clear");
            Check.Equal(
                bagBeforeClear,
                afterUnauthorizedClear.KitBag,
                "denied clear leaves the authoritative bag byte-for-byte unchanged");

            var cleared = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("owner clear-bag operation unexpectedly failed");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                cleared.KitBag,
                "owner clear replaces only the kit bag with its canonical empty representation");
            Check.Equal(
                0,
                PacketBuilder.KitBagDeletionAcknowledgements(cleared).Length,
                "an already-empty bag produces no redundant client deletion acknowledgements");
            for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
            {
                Check.True(
                    KitBagSlots.GetItem(cleared.KitBag, slot).IsEmpty,
                    $"clear-bag operation empties authoritative slot {slot}");
            }

            Check.Equal(
                equipmentBeforeClear,
                cleared.Equipment,
                "clear-bag operation preserves equipped gear byte-for-byte");
            Check.Equal(beforeClear.Silver, cleared.Silver, "clear-bag operation preserves silver");
            Check.Equal(beforeClear.Gold, cleared.Gold, "clear-bag operation preserves gold");
            Check.Equal(beforeClear.Level, cleared.Level, "clear-bag operation preserves level");
            Check.Equal(beforeClear.Experience, cleared.Experience, "clear-bag operation preserves experience");
            Check.Equal(beforeClear.TalentPoints, cleared.TalentPoints, "clear-bag operation preserves talent points");
            Check.Equal(beforeClear.CurrentMap, cleared.CurrentMap, "clear-bag operation preserves current map");
            Check.Equal(beforeClear.PositionX, cleared.PositionX, "clear-bag operation preserves X position");
            Check.Equal(beforeClear.PositionZ, cleared.PositionZ, "clear-bag operation preserves Z position");
            Check.Equal(beforeClear.CurrentHp, cleared.CurrentHp, "clear-bag operation preserves current HP");
            Check.Equal(beforeClear.CurrentMp, cleared.CurrentMp, "clear-bag operation preserves current MP");

            var skillsAfterClear = string.Join(
                ',',
                (await store.GetSkillStatesAsync(owner.Id, character.Id))
                    .OrderBy(skill => skill.SkillId)
                    .Select(skill => $"{skill.SkillId}:{skill.Level}"));
            Check.Equal(
                skillsBeforeClear,
                skillsAfterClear,
                "clear-bag operation preserves character skills");

            var clearedDetailPages = PacketBuilder.KitBagDetailPages(cleared);
            Check.Equal(8, clearedDetailPages.Length, "empty-bag refresh still contains all detail half-pages");
            foreach (var detailPage in clearedDetailPages)
            {
                for (var record = 0; record < 12; record++)
                {
                    Check.Equal(
                        uint.MaxValue,
                        ReadUInt32(detailPage, 24 + (record * 72)),
                        "empty-bag detail refresh reports the client's empty-item sentinel");
                }
            }

            var clearedSlotIndexes = PacketBuilder.KitBagSlotIndexes(cleared);
            Check.Equal(96, clearedSlotIndexes.Length, "empty-bag refresh still contains all slot indexes");
            foreach (var slotIndex in clearedSlotIndexes)
            {
                Check.Equal(
                    uint.MaxValue,
                    ReadUInt32(slotIndex, 20),
                    "empty-bag slot-index refresh reports the client's empty-item sentinel");
            }

            var clearedAgain = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("idempotent clear-bag operation unexpectedly failed");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                clearedAgain.KitBag,
                "clearing an already-empty bag is idempotent");

            await using var restartedStore = new JsonGameStore(dataPath);
            await restartedStore.EnsureSeedDataAsync();
            var restarted = await restartedStore.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("developer clear-bag fixture was not reloaded after restart");
            Check.Equal(
                GameDefaults.EmptyKitBag,
                restarted.KitBag,
                "clear-bag state persists across a JSON-store restart without starter-item restoration");
            Check.Equal(
                equipmentBeforeClear,
                restarted.Equipment,
                "equipped gear remains unchanged after clear-bag restart persistence");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
