using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.ProtocolChecks.CompatibilityFixtures.JsonAuthority;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class JsonCharacterSnapshotReaderChecks
{
    private static async Task AssertGameplayPersistenceCompositionAsync()
    {
        Check.Throws<ArgumentNullException>(
            () => ServerGameplayPersistenceComposition.Create(null!),
            "production gameplay composition requires its PostgreSQL runtime");

        await WithStoreAsync(async (_, store) =>
        {
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "json-gameplay-composition",
                "local-test");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "JsonGameplay",
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 1,
                    Level = 80,
                    ZodiacLevel = 1,
                    ZodiacEnergy = 500,
                    MaxHp = 2_000,
                    CurrentHp = 1_900
                });

            var providers =
                JsonCompatibilityGameplayPersistenceComposition.Create(
                    store);
            Check.True(
                ReferenceEquals(store, providers.CharacterRuntime) &&
                ReferenceEquals(store, providers.OwnedPets) &&
                ReferenceEquals(store, providers.ExperienceBoosts) &&
                ReferenceEquals(store, providers.WorldBossAreaControl) &&
                ReferenceEquals(store, providers.WorldBossRespawns) &&
                ReferenceEquals(store, providers.ZodiacLevels) &&
                ReferenceEquals(store, providers.CharacterCheckpoints),
                "JSON gameplay composition binds every focused contract to one local provider");

            var stats = await providers.CharacterRuntime
                .ReadCalculatedStatsAsync(account.Id, character.Id) ??
                throw new InvalidOperationException(
                    "Focused JSON stats projection was not returned.");
            Check.Equal(
                character.Id,
                stats.CharacterId,
                "focused JSON stats preserve character identity");
            Check.Equal(
                account.Id,
                stats.AccountId,
                "focused JSON stats preserve account ownership");
            Check.True(
                await providers.CharacterRuntime.IsSkillLearnedAsync(
                    account.Id,
                    character.Id,
                    MountCatalog.RideSkillId),
                "focused JSON scalar skill lookup resolves a learned skill");
            Check.True(
                !await providers.CharacterRuntime.IsSkillLearnedAsync(
                    account.Id,
                    character.Id,
                    int.MaxValue),
                "focused JSON scalar skill lookup rejects an unknown skill");

            var pets = await providers.OwnedPets.ReadOwnedPetsAsync(
                account.Id,
                character.Id);
            Check.Equal(
                0,
                pets.Length,
                "focused JSON pet projection exposes the local provider's bounded empty collection");

            var checkpointStore = providers.CharacterCheckpoints;
            var ownership = await checkpointStore.AcquireAsync(
                    account.Id,
                    character.Id,
                    Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Focused JSON Zodiac fixture did not acquire ownership.");
            await AssertOwnershipRejectedAsync(
                () => providers.ZodiacLevels.UpgradeAsync(
                    account.Id,
                    character.Id,
                    new PlayerOwnershipFence(Guid.NewGuid(), 1)),
                "focused JSON Zodiac rejects a stale ownership fence");
            var zodiac = await providers.ZodiacLevels.UpgradeAsync(
                account.Id,
                character.Id,
                ownership.Owner) ??
                throw new InvalidOperationException(
                    "Focused JSON Zodiac mutation returned no character.");
            zodiac.Validate();
            Check.True(
                zodiac.Committed,
                "focused JSON Zodiac contract commits a valid level upgrade");
            Check.Equal(
                2,
                (int)zodiac.CurrentLevel,
                "focused JSON Zodiac contract returns the authoritative level");

            Check.True(
                await store.DeleteCharacterAsync(
                    account.Id,
                    character.Name),
                "focused JSON Zodiac fixture deletes its active character");
            await AssertOwnershipRejectedAsync(
                () => providers.ZodiacLevels.UpgradeAsync(
                    account.Id,
                    character.Id,
                    ownership.Owner),
                "focused JSON Zodiac rejects a deleted character");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.OwnershipLost,
                (int)(await checkpointStore.WriteVitalsAsync(
                    new CharacterVitalsCheckpoint(
                        account.Id,
                        character.Id,
                        ownership.Owner,
                        CurrentHp: 1_800,
                        CurrentMp: 0,
                        Revision: 1))).Status,
                "focused JSON checkpoints reject an inactive character");
            Check.True(
                await checkpointStore.AcquireAsync(
                    account.Id,
                    character.Id,
                    ownership.Owner.OwnerId) is null,
                "focused JSON checkpoints do not reacquire an inactive character");
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpointStore.ReleaseAsync(
                    account.Id,
                    character.Id,
                    ownership.Owner),
                "focused JSON Zodiac fixture releases local ownership");
        });
    }

    private static async Task AssertOwnershipRejectedAsync(
        Func<Task<Godswar.Server.Application.Zodiac
            .ZodiacLevelUpgradeStoreResult?>> action,
        string description)
    {
        try
        {
            _ = await action();
        }
        catch (PlayerOwnershipValidationException exception)
        {
            Check.Equal(
                (int)PlayerOwnershipValidationStatus.OwnershipLost,
                (int)exception.Status,
                description);
            return;
        }

        throw new InvalidOperationException(description);
    }

}
