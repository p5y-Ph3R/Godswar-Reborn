using System.Text.Json;
using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Focused local-store coverage for the registered B06 bootstrap boundary.
/// </summary>
internal static class JsonCharacterSnapshotReaderChecks
{
    public static async Task RunAsync()
    {
        await AssertInputAndMissingAccountFailuresAsync();
        await AssertExistingEmptySlotAsync();
        await AssertFullSnapshotMappingAsync();
        await AssertAmbiguousSlotFailsAsync();
        await AssertConcurrentReadsAreNotTornAsync();
        await AssertLocalCheckpointCompatibilityAsync();
    }

    private static async Task AssertInputAndMissingAccountFailuresAsync()
    {
        await WithStoreAsync(async (_, store) =>
        {
            var invalid = await CaptureFailureAsync(
                () => store.ReadAsync(0));
            Check.Equal(
                (int)CharacterSnapshotFailureReason.InvalidData,
                (int)invalid.Reason,
                "JSON snapshot rejects a non-positive account ID");

            var missing = await CaptureFailureAsync(
                () => store.ReadAsync(91_001));
            Check.Equal(
                (int)CharacterSnapshotFailureReason.AccountNotFound,
                (int)missing.Reason,
                "JSON snapshot rejects a missing authenticated account");
        });
    }

    private static async Task AssertExistingEmptySlotAsync()
    {
        await WithStoreAsync(async (_, store) =>
        {
            var account = await store.LoginOrCreateAccountAsync(
                "json-snapshot-empty",
                "local-test");
            ICharacterSnapshotReader reader = store;
            var snapshot = await reader.ReadAsync(account.Id);

            Check.Equal(
                account.Id,
                snapshot.AccountId,
                "empty JSON slot preserves account identity");
            Check.True(
                snapshot.Character is null,
                "existing JSON account exposes an explicit empty slot");
            Check.True(
                snapshot.ProviderSnapshotToken.StartsWith(
                    "json-v1:",
                    StringComparison.Ordinal),
                "JSON snapshot includes a bounded provider token");
            Check.Equal(
                TimeSpan.Zero,
                snapshot.ReadAtUtc.Offset,
                "JSON snapshot read time is UTC");
        });
    }

    private static async Task AssertFullSnapshotMappingAsync()
    {
        await WithStoreAsync(async (path, store) =>
        {
            var now = DateTimeOffset.UtcNow;
            var talent = SkillTalentSeeds.Talents.First(seed =>
                seed.ClassId == 1);
            var character = CreateCharacter(711, 411, "JsonSnapshot");
            character.CurrentHp = character.MaxHp + 250;
            character.CurrentMp = character.MaxMp + 50;
            character.ZodiacSkillGridLevels =
                Enumerable.Range(0, 16).ToArray();
            character.ZodiacSkillGridSkillIds =
                Enumerable.Range(-1, 16).ToArray();
            var legacyDuration = TimeSpan.FromMinutes(30);
            await WriteDatabaseAsync(
                path,
                new GameDatabase
                {
                    NextAccountId = 412,
                    NextCharacterId = 712,
                    Accounts =
                    [
                        new GameAccount
                        {
                            Id = character.AccountId,
                            Username = "json-snapshot-full",
                            Password = "local-test",
                            CreatedUtc = now.UtcDateTime
                        }
                    ],
                    Characters = [character],
                    CharacterTalents =
                    [
                        new GameCharacterTalent
                        {
                            CharacterId = character.Id,
                            TalentId = talent.Id,
                            Rank = 7
                        }
                    ],
                    CharacterExperienceBoosts =
                    [
                        new CharacterExperienceBoost
                        {
                            CharacterId = character.Id,
                            StatusId = 9001,
                            Kind = 41,
                            BonusBasisPoints = 2_500,
                            Priority = 3,
                            ActivatedAt = now.AddMinutes(-5),
                            ExpiresAt =
                                now.AddMinutes(-5) + legacyDuration,
                            Source = "json-snapshot-fixture"
                        },
                        new CharacterExperienceBoost
                        {
                            CharacterId = character.Id,
                            StatusId = 9002,
                            Kind = 42,
                            BonusBasisPoints = 500,
                            Priority = 1,
                            ActivatedAt = now.AddMinutes(5),
                            RemainingOnlineTicks =
                                TimeSpan.FromMinutes(10).Ticks,
                            Source = "future-fixture"
                        }
                    ]
                });

            var snapshot = await store.ReadAsync(character.AccountId);
            CharacterSnapshotContract.Validate(snapshot);
            var loaded = snapshot.Character ??
                throw new InvalidOperationException(
                    "Expected one JSON character snapshot.");

            Check.Equal(
                character.Id,
                loaded.Identity.CharacterId,
                "JSON identity mapping");
            Check.Equal(
                character.Name,
                loaded.Identity.Name,
                "JSON character-name mapping");
            Check.Equal(
                character.CurrentMap,
                loaded.Location.CurrentMap,
                "JSON location map");
            Check.Equal(
                character.PositionX,
                loaded.Location.PositionX,
                "JSON location X");
            Check.Equal(
                character.PositionZ,
                loaded.Location.PositionZ,
                "JSON location Z");
            Check.Equal(
                character.PositionRevision,
                loaded.Location.PositionRevision,
                "JSON position revision");
            Check.Equal(
                character.Level,
                loaded.Progression.Level,
                "JSON progression level");
            Check.Equal(
                character.Silver,
                loaded.Wallet.Silver,
                "JSON silver wallet");
            Check.Equal(
                character.WeaponRank,
                loaded.Loadout.WeaponRank,
                "JSON weapon rank");
            Check.Equal(
                character.MaxHp,
                loaded.CalculatedStats.CurrentHp,
                "JSON calculated HP clamps persisted overflow");
            Check.Equal(
                character.MaxMp,
                loaded.CalculatedStats.CurrentMp,
                "JSON calculated MP clamps persisted overflow");
            Check.Equal(
                loaded.Skills.Length,
                loaded.CalculatedStats.LearnedSkillCount,
                "JSON calculated skill count matches snapshot skills");
            Check.True(
                loaded.Skills.Length > 0,
                "JSON snapshot derives class skills");
            Check.Equal(
                7,
                loaded.Talents.Single(row => row.TalentId == talent.Id).Rank,
                "JSON snapshot overlays persisted talent rank");
            Check.Equal(
                16,
                loaded.Zodiac.SkillGridLevels.Length,
                "JSON zodiac level grid is exact");
            Check.Equal(
                16,
                loaded.Zodiac.SkillGridSkillIds.Length,
                "JSON zodiac skill grid is exact");
            Check.True(
                loaded.Pets.IsEmpty,
                "JSON snapshot has no pets when the local model has none");
            Check.Equal(
                1,
                loaded.PersonalBoosts.Length,
                "JSON snapshot excludes future personal boosts");
            Check.Equal(
                legacyDuration.Ticks,
                loaded.PersonalBoosts[0].RemainingOnlineTicks!.Value,
                "JSON snapshot migrates legacy duration without writing");
        });
    }

    private static async Task AssertAmbiguousSlotFailsAsync()
    {
        await WithStoreAsync(async (path, store) =>
        {
            var first = CreateCharacter(721, 421, "JsonSlotA");
            var second = CreateCharacter(722, 421, "JsonSlotB");
            await WriteDatabaseAsync(
                path,
                new GameDatabase
                {
                    NextAccountId = 422,
                    NextCharacterId = 723,
                    Accounts =
                    [
                        new GameAccount
                        {
                            Id = 421,
                            Username = "json-snapshot-multi",
                            Password = "local-test"
                        }
                    ],
                    Characters = [first, second]
                });

            var exception = await CaptureFailureAsync(
                () => store.ReadAsync(421));
            Check.Equal(
                (int)CharacterSnapshotFailureReason.AmbiguousCharacterSlot,
                (int)exception.Reason,
                "SingleCharacterV1 fails closed for multiple JSON characters");
        });
    }

    private static async Task AssertConcurrentReadsAreNotTornAsync()
    {
        await WithStoreAsync(async (path, readerStore) =>
        {
            const int accountId = 431;
            const int characterId = 731;
            var character = CreateCharacter(
                characterId,
                accountId,
                "JsonAtomic");
            character.PositionX = 1;
            character.PositionZ = 2;
            await WriteDatabaseAsync(
                path,
                new GameDatabase
                {
                    NextAccountId = accountId + 1,
                    NextCharacterId = characterId + 1,
                    Accounts =
                    [
                        new GameAccount
                        {
                            Id = accountId,
                            Username = "json-snapshot-atomic",
                            Password = "local-test"
                        }
                    ],
                    Characters = [character]
                });
            await using var writerStore = new JsonGameStore(path);

            var writer = Task.Run(async () =>
            {
                for (var index = 0; index < 24; index++)
                {
                    var first = index % 2 == 0;
                    await writerStore.SaveCharacterPositionAsync(
                        accountId,
                        characterId,
                        character.CurrentMap,
                        first ? 10 : 30,
                        first ? 20 : 40);
                }
            });
            var readers = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(async () =>
                {
                    for (var index = 0; index < 24; index++)
                    {
                        var location =
                            (await readerStore.ReadAsync(accountId))
                            .Character!.Location;
                        var isInitial =
                            location.PositionX == 1 &&
                            location.PositionZ == 2;
                        var isFirst =
                            location.PositionX == 10 &&
                            location.PositionZ == 20;
                        var isSecond =
                            location.PositionX == 30 &&
                            location.PositionZ == 40;
                        Check.True(
                            isInitial || isFirst || isSecond,
                            "JSON snapshot location never tears across writes");
                    }
                }))
                .ToArray();
            await Task.WhenAll(readers.Append(writer));
        });
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            Gender = 1,
            Camp = GameDefaults.SpartaCamp,
            Profession = 1,
            Hair = 2,
            Face = 3,
            Faith = 1,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            Level = 80,
            Experience = 123_456,
            Silver = 9_876,
            Gold = 54,
            MaxHp = 1_500,
            MaxMp = 177,
            CurrentHp = 1_250,
            CurrentMp = 150,
            VitalsRevision = 4,
            PositionRevision = 6,
            TalentPoints = 100,
            TalentExperience = 25,
            HolySuitPoints = 12,
            WeaponRank = 7,
            WeaponAuraEffect = 11,
            ArmorRank = 14,
            ArmorAuraEffect = 13,
            PositionX = 1.25f,
            PositionZ = -2.5f,
            Equipment = GameDefaults.DefaultEquipment(1),
            KitBag = GameDefaults.EmptyKitBag,
            ZodiacType = 2,
            ZodiacLevel = 3,
            ZodiacEnergy = 400,
            ZodiacEnergyRemainderX100 = 50,
            ZodiacOnlineDurationTicksToday =
                TimeSpan.FromMinutes(8).Ticks,
            ZodiacAccumulatedExperienceX100 = 100,
            ZodiacAccumulatedTalentExperienceX100 = 200,
            CreatedUtc = DateTime.UtcNow.AddDays(-2)
        };

    private static async Task
        AssertLocalCheckpointCompatibilityAsync()
    {
        await WithStoreAsync(async (_, store) =>
        {
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "json-checkpoint-owner",
                "local-test");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "JsonCheckpoint",
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 1,
                    Level = 10,
                    MaxHp = 1_000,
                    MaxMp = 200,
                    CurrentHp = 900,
                    CurrentMp = 150
                });
            var checkpoints =
                new LegacyCharacterCheckpointStore(store);
            var first = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Local checkpoint owner was not acquired.");

            var position = new CharacterPositionCheckpoint(
                account.Id,
                character.Id,
                first.Owner,
                CurrentMap: 7,
                PositionX: 12.5f,
                PositionZ: -8.25f,
                Revision: 1);
            Check.True(
                (await checkpoints.WritePositionAsync(position))
                    .Satisfies(1),
                "local position checkpoint applies");
            Check.True(
                (await checkpoints.WriteVitalsAsync(
                    new CharacterVitalsCheckpoint(
                        account.Id,
                        character.Id,
                        first.Owner,
                        CurrentHp: 777,
                        CurrentMp: 123,
                        Revision: 1))).Satisfies(1),
                "local vitals checkpoint applies");

            var replacement = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Replacement local owner was not acquired.");
            Check.Equal(
                first.Owner.Generation + 1,
                replacement.Owner.Generation,
                "local replacement advances the owner generation");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.OwnershipLost,
                (int)(await checkpoints.WritePositionAsync(
                    position with { Revision = 2 })).Status,
                "replaced local owner is fenced");
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpoints.ReleaseAsync(
                    account.Id,
                    character.Id,
                    replacement.Owner),
                "local owner releases cleanly");

            var snapshot = await store.ReadAsync(account.Id);
            var persisted = snapshot.Character ??
                throw new InvalidOperationException(
                    "Local checkpoint character disappeared.");
            Check.Equal(
                1L,
                persisted.Location.PositionRevision,
                "local adapter persists position revision");
            Check.Equal(
                7,
                (int)persisted.Location.CurrentMap,
                "local adapter persists position values");
            Check.Equal(
                1L,
                persisted.Vitals.Revision,
                "local adapter persists vitals revision");
            Check.Equal(
                777,
                persisted.CalculatedStats.CurrentHp,
                "local adapter persists vitals values");
        });
    }

    private static async Task WriteDatabaseAsync(
        string path,
        GameDatabase database)
    {
        await using var stream = File.Create(Path.Combine(path, "state.json"));
        await JsonSerializer.SerializeAsync(
            stream,
            database,
            JsonDefaults.Indented);
    }

    private static async Task<CharacterSnapshotUnavailableException>
        CaptureFailureAsync(Func<Task<CharacterAccountSnapshot>> action)
    {
        try
        {
            _ = await action();
        }
        catch (CharacterSnapshotUnavailableException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "Expected a typed character snapshot failure.");
    }

    private static async Task WithStoreAsync(
        Func<string, JsonGameStore, Task> action)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"godswar-json-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            await using var store = new JsonGameStore(path);
            await action(path, store);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
