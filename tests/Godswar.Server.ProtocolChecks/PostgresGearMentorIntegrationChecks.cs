using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const int FillerSlotA = 0;
    private const int FillerSlotB = 1;
    private const int PreservedGearSlot = 2;
    private const int RecipeSlot = 3;
    private const int PiecesSlot = 4;
    private const int TransformSlot = 5;
    private const int SingleConnectionRecipeSlot = 6;

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL Gear Mentor integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"gear_mentor_{token}";
        var characterName = $"Mentor{token}";
        int? accountId = null;
        int? characterId = null;

        try
        {
            await using var storeA = new PostgresGameStore(connectionString);
            await using var storeB = new PostgresGameStore(connectionString);
            await storeA.EnsureSeedDataAsync();

            var account = await storeA.LoginOrCreateAccountAsync(username, string.Empty);
            accountId = account.Id;
            var character = await storeA.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = characterName,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0
                });
            characterId = character.Id;
            character = await storeA.MoveEquipmentToKitBagAsync(
                    account.Id,
                    character.Id,
                    EquipmentSlots.Weapon,
                    PreservedGearSlot)
                ?? throw new InvalidOperationException(
                    "Could not move the PostgreSQL Gear Mentor test weapon into the bag.");
            Check.Equal(
                1000u,
                KitBagSlots.GetItem(character.KitBag, PreservedGearSlot).Id,
                "PostgreSQL Gear Mentor test weapon moved to the bag");

            await StageAuthoritativeRowsAsync(connectionString, character.Id);
            character = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var gearBefore = KitBagSlots.GetItem(character.KitBag, PreservedGearSlot);
            var gearRowBefore = await ReadItemRowAsync(
                connectionString,
                character.Id,
                PreservedGearSlot);
            var dustBefore = KitBagSlots.GetItem(character.KitBag, RecipeSlot);
            Check.Equal(9900u, dustBefore.Id, "PostgreSQL Gear Mentor test Strength Dust staged");
            Check.Equal((short)99, dustBefore.Stack, "PostgreSQL Gear Mentor recipe starts with 99 Dust");
            Check.Equal((short)1, dustBefore.Bound, "PostgreSQL Gear Mentor test Dust is bound");

            var request = new GearMentorRequest(
                GearMentorOperation.MakeAttributeStone,
                [GearMentorSlotSelection.Capture(character.KitBag, RecipeSlot)]);

            var wrongOwner = await storeA.ProcessGearMentorAsync(
                account.Id + 1,
                character.Id,
                request);
            Check.True(
                !wrongOwner.CharacterFound && wrongOwner.Result is null,
                "PostgreSQL Gear Mentor binds character ownership to the account");

            var raced = await Task.WhenAll(
                storeA.ProcessGearMentorAsync(account.Id, character.Id, request),
                storeB.ProcessGearMentorAsync(account.Id, character.Id, request));
            Check.Equal(
                1,
                raced.Count(static result => result.Committed),
                "only one concurrent PostgreSQL Gear Mentor recipe commits");
            Check.Equal(
                1,
                raced.Count(static result => !result.Committed),
                "duplicate PostgreSQL Gear Mentor recipe is rejected");

            var committed = raced.Single(static result => result.Committed).Result
                ?? throw new InvalidOperationException(
                    "Committed PostgreSQL Gear Mentor transaction omitted its result.");
            Check.Equal(
                (int)GearMentorStatus.Succeeded,
                (int)committed.Status,
                "PostgreSQL Dust-to-Stone recipe succeeds");
            Check.Equal(
                new GearMentorOutput(9930, 1, 1),
                committed.Outputs.Single(),
                "99 bound Strength Dust produce one bound Strength Stone");

            var rejection = raced.Single(static result => !result.Committed).Result
                ?? throw new InvalidOperationException(
                    "Rejected PostgreSQL Gear Mentor transaction omitted its result.");
            Check.Equal(
                (int)GearMentorStatus.StaleSelection,
                (int)rejection.Status,
                "PostgreSQL duplicate fails authoritative snapshot revalidation");
            Check.Equal(
                0,
                rejection.Mutations.Count,
                "PostgreSQL stale Gear Mentor duplicate emits no mutations");

            var persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 9930,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 1
                },
                KitBagSlots.GetItem(persisted.KitBag, RecipeSlot),
                "PostgreSQL Gear Mentor race consumes Dust once and persists one Strength Stone");
            Check.Equal(
                gearBefore,
                KitBagSlots.GetItem(persisted.KitBag, PreservedGearSlot),
                "PostgreSQL Gear Mentor recipe preserves unrelated Q20/G25 gear metadata");
            Check.Equal(
                gearRowBefore,
                await ReadItemRowAsync(connectionString, character.Id, PreservedGearSlot),
                "PostgreSQL Gear Mentor recipe leaves the unrelated authoritative gear row byte-for-byte stable");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                PiecesSlot,
                itemId: 4216,
                stack: 99,
                bound: 1);
            persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var combine = await storeA.ProcessGearMentorAsync(
                account.Id,
                character.Id,
                new GearMentorRequest(
                    GearMentorOperation.CombineGemPieces,
                    [GearMentorSlotSelection.Capture(persisted.KitBag, PiecesSlot)]));
            Check.True(combine.Committed, "PostgreSQL Level-5 gem-piece combination commits");
            Check.Equal(
                new GearMentorOutput(4215, 1, 1),
                combine.Result!.Outputs.Single(),
                "99 bound Level-5 Sapphire Pieces produce one bound Level-5 Sapphire");
            Check.Equal(
                4215u,
                KitBagSlots.GetItem(combine.Character!.KitBag, PiecesSlot).Id,
                "PostgreSQL Level-5 Sapphire output replaces its consumed piece stack");
            Check.Equal(
                gearRowBefore,
                await ReadItemRowAsync(connectionString, character.Id, PreservedGearSlot),
                "a second Gear Mentor recipe still preserves unrelated high-ceiling gear metadata");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                TransformSlot,
                itemId: 4234,
                stack: 1,
                bound: 1);
            persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var transform = await storeA.ProcessGearMentorAsync(
                account.Id,
                character.Id,
                new GearMentorRequest(
                    GearMentorOperation.TransformCrystal,
                    [GearMentorSlotSelection.Capture(persisted.KitBag, TransformSlot)]));
            Check.True(transform.Committed, "PostgreSQL Level-5 Crystal transformation commits");
            Check.Equal(
                new GearMentorOutput(4233, 2, 1),
                transform.Result!.Outputs.Single(),
                "one bound Level-5 Crystal produces two bound Level-4 Crystals");
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 4233,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 2
                },
                KitBagSlots.GetItem(transform.Character!.KitBag, TransformSlot),
                "PostgreSQL Crystal transformation persists its output in the consumed slot");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                TransformSlot,
                itemId: 4233,
                stack: 3,
                bound: 1);
            persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var levelFourTransform = await storeA.ProcessGearMentorAsync(
                account.Id,
                character.Id,
                new GearMentorRequest(
                    GearMentorOperation.TransformCrystal,
                    [GearMentorSlotSelection.Capture(persisted.KitBag, TransformSlot)]));
            Check.True(
                levelFourTransform.Committed,
                "PostgreSQL Level-4 Crystal transformation commits");
            Check.Equal(
                new GearMentorOutput(4232, 2, 1),
                levelFourTransform.Result!.Outputs.Single(),
                "one bound Level-4 Crystal produces two bound Level-3 Crystals");
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 4233,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 2
                },
                KitBagSlots.GetItem(
                    levelFourTransform.Character!.KitBag,
                    TransformSlot),
                "PostgreSQL Level-4 Crystal transformation consumes one source");
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 4232,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 2
                },
                KitBagSlots.GetItem(
                    levelFourTransform.Character.KitBag,
                    SingleConnectionRecipeSlot),
                "PostgreSQL Level-4 Crystal transformation persists two bound outputs in the first empty slot");
            Check.Equal(
                gearRowBefore,
                await ReadItemRowAsync(
                    connectionString,
                    character.Id,
                    PreservedGearSlot),
                "Level-4 Crystal transformation preserves unrelated gear metadata");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                SingleConnectionRecipeSlot,
                itemId: 9901,
                stack: 99,
                bound: 0);
            await using (var singleConnectionStore = new PostgresGameStore(
                             CreateSingleConnectionPoolString(connectionString)))
            {
                // A pool capped at one connection deterministically catches a
                // post-commit readback that tries to lease a second connection
                // while the transaction connection is still owned by this call.
                var singleConnectionCharacter =
                    (await singleConnectionStore.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                var singleConnectionResult = await singleConnectionStore.ProcessGearMentorAsync(
                    account.Id,
                    character.Id,
                    new GearMentorRequest(
                        GearMentorOperation.MakeAttributeStone,
                        [GearMentorSlotSelection.Capture(
                            singleConnectionCharacter.KitBag,
                            SingleConnectionRecipeSlot)]));

                Check.True(
                    singleConnectionResult.Committed,
                    "single-connection PostgreSQL Gear Mentor recipe commits");
                Check.Equal(
                    9931u,
                    KitBagSlots.GetItem(
                        singleConnectionResult.Character!.KitBag,
                        SingleConnectionRecipeSlot).Id,
                    "single-connection transaction returns the committed Shield Stone bag refresh");
            }

            await using var reopenedStore = new PostgresGameStore(connectionString);
            await reopenedStore.EnsureSeedDataAsync();
            var reopened = (await reopenedStore.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                9930u,
                KitBagSlots.GetItem(reopened.KitBag, RecipeSlot).Id,
                "PostgreSQL Strength Stone survives a store reopen and reseed");
            Check.Equal(
                4215u,
                KitBagSlots.GetItem(reopened.KitBag, PiecesSlot).Id,
                "PostgreSQL Level-5 Sapphire survives a store reopen and reseed");
            Check.Equal(
                9931u,
                KitBagSlots.GetItem(reopened.KitBag, SingleConnectionRecipeSlot).Id,
                "single-connection PostgreSQL Shield Stone survives a store reopen");
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 4233,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 2
                },
                KitBagSlots.GetItem(reopened.KitBag, TransformSlot),
                "transformed Level-4 Crystal stack survives a store reopen");
            Check.Equal(
                gearBefore,
                KitBagSlots.GetItem(reopened.KitBag, PreservedGearSlot),
                "Q20/G25 gear metadata survives Gear Mentor operations and a store reopen");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await PostgresIntegrationFixtureCleanup.DeleteAccountAndAuditsAsync(
                    connectionString,
                    accountId.Value,
                    username,
                    characterId,
                    "gear-mentor-consume");
            }
        }
    }
}
