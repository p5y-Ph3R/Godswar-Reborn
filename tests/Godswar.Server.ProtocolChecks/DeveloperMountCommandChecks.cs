using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperMountCommandChecks
{
    private static readonly Guid OperationId =
        Guid.Parse("d2d76c5a-61e3-4fbe-b642-f33fc013a305");

    public static async Task RunAsync()
    {
        Check.Equal(350, DeveloperMountCatalog.All.Count, "all client mount templates are catalogued");
        Check.Equal(349, DeveloperMountCatalog.Grantable.Count, "only the orphaned client mount is excluded");
        Check.Equal(
            DeveloperMountCatalog.All.Count,
            DeveloperMountCatalog.All.Select(static mount => mount.ItemId).Distinct().Count(),
            "each client mount ID belongs to exactly one developer family");
        Check.True(
            DeveloperMountCatalog.TryGet(DeveloperMountCatalog.OrphanedMountItemId, out var orphan) &&
            !orphan.CanGrant,
            "orphaned client mount remains visible but cannot be generated");

        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("greeksteed", "80", out var greekEighty) &&
            greekEighty.ItemId == 14224,
            "family and level resolve the level-80 Greek Steed");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("greeksteed", "max", out var greekMax) &&
            greekMax.ItemId == 14228,
            "max resolves the normal level-120 family endpoint");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("greeksteed", "120", out var greek120) &&
            greek120.ItemId == 14228,
            "120 is an alias for the normal max endpoint");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("greeksteed", "special", out var greekSpecial) &&
            greekSpecial.ItemId == 14229,
            "special resolves the separate 50-percent-speed family variant");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("argentdragon-a", "40", out var dragonA) &&
            dragonA.ItemId == 14320 &&
            DeveloperMountCatalog.TryResolveGrantable("argentdragon-b", "40", out var dragonB) &&
            dragonB.ItemId == 14400,
            "duplicate Argent Dragon display names remain unambiguous");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("timedreindeer", "7d", out var timedReindeer) &&
            timedReindeer.ItemId == 14426,
            "timed family aliases preserve the client duration variant");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("erebuslion", "80", out var erebusEighty) &&
            erebusEighty.ItemId == 16204 &&
            erebusEighty.DisplayName == "Erebus Lion" &&
            erebusEighty.SpeedBonus == 0.24f,
            "Erebus Lion resolves its level-80 family item and authored speed");
        Check.True(
            DeveloperMountCatalog.TryResolveGrantable("blacklion", "special", out var erebusSpecial) &&
            erebusSpecial.ItemId == 16209,
            "Erebus Lion secondary alias resolves the special family endpoint");
        Check.Equal(
            DeveloperMountCatalog.FamiliesPerPage,
            DeveloperMountCatalog.GetPage(1).Count,
            "mount list pages have a fixed bounded size");
        Check.True(
            DeveloperMountCatalog.GetPage(DeveloperMountCatalog.PageCount + 1).Count == 0,
            "out-of-range mount catalog pages are empty");

        Check.True(
            DeveloperItemCommand.TryParse("/item mount list", out var defaultList, out _) &&
            defaultList is
            {
                Operation: DeveloperItemOperation.MountList,
                MountList.Page: 1,
                MountList.Family: null
            },
            "mount list defaults to page one");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount list 2", out var pageList, out _) &&
            pageList is { Operation: DeveloperItemOperation.MountList, MountList.Page: 2 },
            "mount list accepts a valid page");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount list greeksteed", out var familyList, out _) &&
            familyList is
            {
                Operation: DeveloperItemOperation.MountList,
                MountList.Page: null,
                MountList.Family.Alias: "greeksteed"
            },
            "mount list accepts a family alias");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add 14224", out var numericAdd, out _) &&
            numericAdd is
            {
                Operation: DeveloperItemOperation.MountAdd,
                Mount.ItemId: 14224,
                Quantity: 1,
                ClientOperationId: null
            },
            "numeric mount add resolves only an allowlisted client mount");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add greeksteed 80", out var familyAdd, out _) &&
            familyAdd is
            {
                Operation: DeveloperItemOperation.MountAdd,
                Mount.ItemId: 14224,
                ClientOperationId: null
            },
            "family mount add resolves its level tier");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add 14224 op={OperationId:D}",
                out var identifiedNumericAdd,
                out _) &&
            identifiedNumericAdd is
            {
                Operation: DeveloperItemOperation.MountAdd,
                Mount.ItemId: 14224,
                Quantity: 1,
                ClientOperationId: not null
            } &&
            identifiedNumericAdd.ClientOperationId == OperationId,
            "numeric mount add accepts a final D-format operation ID");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add greeksteed 80 op={OperationId:D}",
                out var identifiedFamilyAdd,
                out _) &&
            identifiedFamilyAdd is
            {
                Operation: DeveloperItemOperation.MountAdd,
                Mount.ItemId: 14224,
                Quantity: 1,
                ClientOperationId: not null
            } &&
            identifiedFamilyAdd.ClientOperationId == OperationId,
            "family-tier mount add accepts a final D-format operation ID");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add greeksteed max", out var maxAdd, out _) &&
            maxAdd is { Mount.ItemId: 14228 },
            "family max command resolves the normal endpoint");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add greeksteed special", out var specialAdd, out _) &&
            specialAdd is { Mount.ItemId: 14229 },
            "family special command resolves the distinct speed variant");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add greeksteed special OP={OperationId:D}",
                out var identifiedSpecialAdd,
                out _) &&
            identifiedSpecialAdd is
            {
                Mount.ItemId: 14229,
                ClientOperationId: not null
            } &&
            identifiedSpecialAdd.ClientOperationId == OperationId,
            "symbolic mount tier accepts the case-insensitive operation token");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add erebuslion 80", out var erebusAdd, out _) &&
            erebusAdd is { Mount.ItemId: 16204 },
            "custom Erebus Lion family is available through the bounded mount command");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add {DeveloperMountCatalog.OrphanedMountItemId}",
                out var orphanAdd,
                out var orphanError) &&
            orphanAdd is null &&
            orphanError.Contains("orphaned", StringComparison.OrdinalIgnoreCase),
            "orphaned mount is consumed but explicitly rejected");
        Check.True(
            DeveloperItemCommand.TryParse("/item mount add greeksteed 999", out var badTier, out _) &&
            badTier is null,
            "unknown family tiers are rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item mount add 14224 op=",
                out var emptyOperationAdd,
                out var emptyOperationError) &&
            emptyOperationAdd is null &&
            emptyOperationError.Contains("D-format UUID", StringComparison.Ordinal),
            "mount add rejects an empty operation ID with bounded guidance");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item mount add greeksteed 80 " +
                "op=00000000-0000-0000-0000-000000000000",
                out var emptyUuidAdd,
                out var emptyUuidError) &&
            emptyUuidAdd is null &&
            emptyUuidError.Contains("non-empty", StringComparison.Ordinal),
            "mount add rejects the empty UUID value");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add 14224 op={{{OperationId:D}}}",
                out var nonCanonicalAdd,
                out _) &&
            nonCanonicalAdd is null,
            "mount add rejects non-D UUID forms");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item mount add 14224 op={OperationId:D} trailing",
                out var trailingAdd,
                out var trailingError) &&
            trailingAdd is null &&
            trailingError.Contains("[op=<UUID>]", StringComparison.Ordinal),
            "mount add rejects arguments after the final operation token");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item mount add",
                out var incompleteAdd,
                out var incompleteAddError) &&
            incompleteAdd is null &&
            incompleteAddError.Contains("[op=<UUID>]", StringComparison.Ordinal),
            "mount-add usage advertises the optional operation token");
        Check.True(
            DeveloperItemCommand.TryParse("/item add crystal1 2", out var materialRequest, out _) &&
            materialRequest is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4230,
                Quantity: 2,
                Mount: null,
                MountList: null
            },
            "mount parsing preserves the existing material command");

        var talkRequest = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(talkRequest.AsSpan(0, 4), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(talkRequest.AsSpan(8, 4), 1800);
        const string feedback = "[mount] Greek Steed (14224)";
        var talkReply = PacketBuilder.DeveloperCommandTalkReply(talkRequest, feedback);
        Check.Equal((ushort)talkReply.Length, BinaryPrimitives.ReadUInt16LittleEndian(talkReply), "mount feedback packet length");
        Check.Equal(Opcodes.Talk, BinaryPrimitives.ReadUInt16LittleEndian(talkReply.AsSpan(2, 2)), "mount feedback Talk opcode");
        Check.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(talkReply.AsSpan(4, 4)), "mount feedback preserves sender identity");
        Check.Equal((uint)(Encoding.Unicode.GetByteCount(feedback) + 2), BinaryPrimitives.ReadUInt32LittleEndian(talkReply.AsSpan(8, 4)), "mount feedback uses native text length semantics");
        Check.Equal(1800u, BinaryPrimitives.ReadUInt32LittleEndian(talkReply.AsSpan(12, 4)), "mount feedback preserves private channel metadata");
        Check.Equal(feedback, Encoding.Unicode.GetString(talkReply.AsSpan(16)), "mount feedback text");

        await CheckJsonPersistenceAndCapacityAsync();
    }

    private static async Task CheckJsonPersistenceAndCapacityAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-developer-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("developer-mount-owner", string.Empty);
            var other = await store.LoginOrCreateAccountAsync("developer-mount-other", string.Empty);
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "DeveloperMountHero" });
            character = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("Mount command fixture bag could not be cleared.");

            var wrongOwner = await store.AddDeveloperMountAsync(other.Id, character.Id, 14224);
            Check.True(
                wrongOwner.Status == KitBagItemGrantStatus.CharacterNotFound,
                "different account cannot generate a mount in another character bag");

            var rejectedOrphan = false;
            try
            {
                await store.AddDeveloperMountAsync(
                    owner.Id,
                    character.Id,
                    DeveloperMountCatalog.OrphanedMountItemId);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedOrphan = true;
            }

            Check.True(rejectedOrphan, "JSON store revalidates the mount allowlist");

            var granted = await store.AddDeveloperMountAsync(owner.Id, character.Id, 14224);
            Check.True(granted.Added && granted.Character is not null, "JSON mount grant succeeds");
            var firstMount = KitBagSlots.GetItem(granted.Character!.KitBag, 0);
            Check.Equal(14224u, firstMount.Id, "mount grant uses the first empty authoritative slot");
            Check.Equal((short)1, firstMount.Quality, "generated mount starts at quality one");
            Check.Equal((short)1, firstMount.Grade, "generated mount starts at grade one");
            Check.Equal((short)1, firstMount.Bound, "generated mount is bound");
            Check.Equal((short)1, firstMount.Stack, "generated mount is non-stackable");

            var reloaded = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("Generated JSON mount was not reloaded.");
            Check.Equal(14224u, KitBagSlots.GetItemId(reloaded.KitBag, 0), "generated mount persists");

            var current = granted;
            for (var slot = 1; slot < KitBagItemGrantPlanner.SlotCount; slot++)
            {
                current = await store.AddDeveloperMountAsync(owner.Id, character.Id, 14224);
                Check.True(current.Added, $"mount grant fills authoritative slot {slot}");
            }

            var fullBag = current.Character?.KitBag
                ?? throw new InvalidOperationException("Full mount bag fixture was not returned.");
            var rejectedCapacity = await store.AddDeveloperMountAsync(owner.Id, character.Id, 14224);
            Check.True(
                rejectedCapacity.Status == KitBagItemGrantStatus.InsufficientCapacity &&
                rejectedCapacity.Character is not null,
                "full JSON bag rejects the complete mount grant");
            Check.Equal(
                fullBag,
                rejectedCapacity.Character!.KitBag,
                "failed mount capacity check leaves the bag byte-for-byte unchanged");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
