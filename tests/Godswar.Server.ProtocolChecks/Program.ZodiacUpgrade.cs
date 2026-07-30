using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckZodiacLevelUpgradeAsync()
    {
        var expectedCharacterLevels = new[]
        {
            10, 25, 40, 60, 82, 94, 103, 108, 113, 116,
            119, 122, 125, 128, 131, 134, 136, 138, 140, 142,
            144, 146, 148, 150, 152, 154, 156, 158, 160
        };
        var expectedEnergyCosts = new[]
        {
            500, 2_000, 4_000, 8_000, 20_000, 30_000, 44_000,
            65_000, 85_000, 105_000, 130_000, 155_000, 185_000,
            215_000, 250_000, 285_000, 325_000, 365_000, 420_000,
            475_000, 530_000, 585_000, 640_000, 700_000, 760_000,
            820_000, 880_000, 940_000, 1_000_000
        };
        for (var currentLevel = 1; currentLevel <= 29; currentLevel++)
        {
            Check.True(
                ZodiacLevelUpgradeCatalog.TryGetRequirement(
                    currentLevel,
                    out var requirement),
                $"Zodiac level {currentLevel} has an upgrade requirement");
            Check.Equal(
                currentLevel,
                (int)requirement.CurrentLevel,
                $"Zodiac requirement {currentLevel} current level");
            Check.Equal(
                currentLevel + 1,
                (int)requirement.NextLevel,
                $"Zodiac requirement {currentLevel} next level");
            Check.Equal(
                expectedCharacterLevels[currentLevel - 1],
                requirement.RequiredCharacterLevel,
                $"Zodiac requirement {currentLevel} character gate");
            Check.Equal(
                expectedEnergyCosts[currentLevel - 1],
                requirement.EnergyCost,
                $"Zodiac requirement {currentLevel} energy cost");
        }

        Check.True(
            !ZodiacLevelUpgradeCatalog.TryGetRequirement(30, out _),
            "Zodiac level thirty is terminal");

        var requestBytes = Convert.FromHexString(
            "180039280000000000000300010000000100000000000000");
        Check.True(
            ZodiacSyncRequest.TryParse(requestBytes, out var request) &&
            request.IsLevelUpgrade,
            "native SID 3 Zodiac level-up intent parses");
        Check.Equal(1, request.Value1, "native level-up mode value one");
        Check.Equal(1, request.Value2, "native level-up mode value two");
        Check.Equal(0, request.Value3, "native level-up unused value");
        Check.True(
            !ZodiacSyncRequest.TryParse(
                requestBytes.Concat(new byte[] { 0 }).ToArray(),
                out _),
            "oversized Zodiac request is rejected");

        var character = new GameCharacter
        {
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 1_000
        };
        var succeeded = ZodiacLevelUpgrade.Apply(character);
        Check.Equal(
            (int)ZodiacLevelUpgradeStatus.Succeeded,
            (int)succeeded.Status,
            "eligible Zodiac upgrade succeeds");
        Check.Equal(1, (int)succeeded.PreviousLevel, "successful prior Zodiac level");
        Check.Equal(2, (int)succeeded.CurrentLevel, "successful new Zodiac level");
        Check.Equal(500, succeeded.CurrentEnergy, "successful remaining Zodiac energy");
        Check.Equal(0, succeeded.CurrentEnergyRemainderX100, "successful energy remainder");
        Check.Equal(2, (int)character.ZodiacLevel, "successful mutation updates character level");

        var fractional = ZodiacLevelUpgrade.Apply(new GameCharacter
        {
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 999,
            ZodiacEnergyRemainderX100 = 25
        });
        Check.Equal(499, fractional.CurrentEnergy, "fractional upgrade integer balance");
        Check.Equal(25, fractional.CurrentEnergyRemainderX100, "fractional energy is preserved");

        var responseBytes = PacketBuilder.ZodiacLevelUpgrade(
            succeeded.CurrentLevel,
            succeeded.CurrentEnergy);
        Check.True(
            responseBytes.SequenceEqual(Convert.FromHexString(
                "18003928481400000000030002000000F401000000000000")),
            "SID 3 response carries authoritative level and remaining energy");

        var lowCharacterLevel = ZodiacLevelUpgrade.Apply(new GameCharacter
        {
            Level = 81,
            ZodiacLevel = 5,
            ZodiacEnergy = 28_000
        });
        Check.Equal(
            (int)ZodiacLevelUpgradeStatus.CharacterLevelTooLow,
            (int)lowCharacterLevel.Status,
            "character-level gate is authoritative");
        Check.Equal(82, lowCharacterLevel.RequiredCharacterLevel, "level-five gate value");

        var insufficientEnergyCharacter = new GameCharacter
        {
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 499,
            ZodiacEnergyRemainderX100 = 99
        };
        var insufficientEnergy = ZodiacLevelUpgrade.Apply(insufficientEnergyCharacter);
        Check.Equal(
            (int)ZodiacLevelUpgradeStatus.InsufficientEnergy,
            (int)insufficientEnergy.Status,
            "fraction below the full cost cannot upgrade");
        Check.Equal(
            1,
            (int)insufficientEnergyCharacter.ZodiacLevel,
            "rejected energy check does not mutate level");

        var maximumLevel = ZodiacLevelUpgrade.Apply(new GameCharacter
        {
            Level = 200,
            ZodiacLevel = 30,
            ZodiacEnergy = 1_090_000
        });
        Check.Equal(
            (int)ZodiacLevelUpgradeStatus.MaximumLevelReached,
            (int)maximumLevel.Status,
            "maximum Zodiac level cannot upgrade");

        var overCapCharacter = new GameCharacter
        {
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 5_000
        };
        var overCap = ZodiacLevelUpgrade.Apply(overCapCharacter);
        Check.Equal(
            4_500,
            overCap.CurrentEnergy,
            "an explicit administrative over-cap balance remains spendable");

        return Task.CompletedTask;
    }

    private static async Task CheckJsonZodiacLevelUpgradePersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-zodiac-level-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync(
                    "zodiac-level-up",
                    "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "ZodiacUpgradeHero",
                        Level = 80,
                        ZodiacLevel = 1,
                        ZodiacEnergy = 1_000
                    });
                accountId = account.Id;
                characterId = character.Id;
                var ownership =
                    new PlayerOwnershipFence(Guid.NewGuid(), 1);

                var wrongOwner = await store.UpgradeZodiacLevelAsync(
                    account.Id + 1,
                    character.Id,
                    ownership);
                Check.True(wrongOwner is null, "wrong account cannot upgrade Zodiac");

                var result = await store.UpgradeZodiacLevelAsync(
                    account.Id,
                    character.Id,
                    ownership)
                    ?? throw new InvalidOperationException(
                        "JSON Zodiac character was not found");
                Check.True(result.Committed, "eligible JSON Zodiac upgrade commits");

                var second = await store.UpgradeZodiacLevelAsync(
                    account.Id,
                    character.Id,
                    ownership)
                    ?? throw new InvalidOperationException(
                        "JSON Zodiac character disappeared after upgrade");
                Check.Equal(
                    (int)ZodiacLevelUpgradeStatus.InsufficientEnergy,
                    (int)second.Status,
                    "second upgrade is revalidated against committed energy");
            }

            await using var reloaded = new JsonGameStore(dataPath);
            var persisted = await reloaded.GetFirstCharacterAsync(accountId)
                ?? throw new InvalidOperationException(
                    "JSON Zodiac character did not persist");
            Check.Equal(characterId, persisted.Id, "reloaded Zodiac character identity");
            Check.Equal(2, (int)persisted.ZodiacLevel, "reloaded Zodiac level");
            Check.Equal(500, persisted.ZodiacEnergy, "reloaded Zodiac energy");
            Check.Equal(
                0,
                persisted.ZodiacEnergyRemainderX100,
                "reloaded fractional Zodiac energy");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckZodiacLevelUpgradeSerializationAsync()
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var store = new SerializedZodiacStore();
        var registry = new GameSessionRegistry(store);
        var startedAt = new DateTimeOffset(
            2026,
            7,
            23,
            10,
            0,
            0,
            TimeSpan.Zero);
        var character = new GameCharacter
        {
            Id = 13,
            AccountId = 7,
            Name = "SerializedZodiac",
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 1_000
        };
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        character.CheckpointOwnerId = ownership.OwnerId;
        character.CheckpointOwnerGeneration = ownership.Generation;
        registry.ReplaceAccountSession(
            character.AccountId,
            socket.Session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                socket.Session,
                ownership),
            "serialized Zodiac fixture binds player ownership");
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId: 0x1448,
            worldReady: true,
            joinedAt: startedAt);

        var accrual = registry.AdvanceZodiacEnergyAccrualOnceAsync(
            startedAt.AddMinutes(1),
            CancellationToken.None);
        await store.AccrualEntered;

        var upgrade = registry.UpgradeZodiacLevelAsync(
            socket.Session,
            character.AccountId,
            character,
            ownership,
            CancellationToken.None);
        var prematureEntry = await Task.WhenAny(
            store.UpgradeEntered,
            Task.Delay(TimeSpan.FromMilliseconds(100)));
        Check.True(
            !ReferenceEquals(prematureEntry, store.UpgradeEntered),
            "level-up waits behind in-flight online accrual");

        store.ReleaseAccrual();
        await accrual;
        var result = await upgrade
            ?? throw new InvalidOperationException(
                "serialized Zodiac upgrade returned no result");
        Check.True(await store.UpgradeEntered, "level-up enters after accrual releases");
        Check.True(result.Committed, "serialized Zodiac level-up commits");
        Check.Equal(2, (int)character.ZodiacLevel, "serialized live Zodiac level");
        Check.Equal(500, character.ZodiacEnergy, "serialized live Zodiac energy");
    }

    private sealed class SerializedZodiacStore : GameStoreTestStub
    {
        private readonly TaskCompletionSource<bool> _accrualEntered =
            NewCompletionSource();
        private readonly TaskCompletionSource<bool> _releaseAccrual =
            NewCompletionSource();
        private readonly TaskCompletionSource<bool> _upgradeEntered =
            NewCompletionSource();

        public Task<bool> AccrualEntered => _accrualEntered.Task;

        public Task<bool> UpgradeEntered => _upgradeEntered.Task;

        public void ReleaseAccrual() => _releaseAccrual.TrySetResult(true);

        public override async Task<ZodiacEnergyAccrualResult?>
            ApplyZodiacOnlineTimeAsync(
                int accountId,
                int characterId,
                DateTimeOffset onlineFrom,
                DateTimeOffset onlineUntil,
                ZodiacEnergyPolicy policy,
                CancellationToken cancellationToken = default)
        {
            _accrualEntered.TrySetResult(true);
            await _releaseAccrual.Task.WaitAsync(cancellationToken);
            return new ZodiacEnergyAccrualResult(
                GainedEnergyX100: 0,
                CurrentEnergy: 1_000,
                CurrentEnergyRemainderX100: 0,
                OnlineDay: DateOnly.FromDateTime(onlineUntil.UtcDateTime),
                OnlineDurationTicksToday: (onlineUntil - onlineFrom).Ticks,
                LastOnlineAt: onlineUntil,
                LastCompensationDay: null,
                CompensationApplied: false);
        }

        public override Task<ZodiacLevelUpgradeResult?>
            UpgradeZodiacLevelAsync(
                int accountId,
                int characterId,
                PlayerOwnershipFence ownership,
                CancellationToken cancellationToken = default)
        {
            _upgradeEntered.TrySetResult(true);
            return Task.FromResult<ZodiacLevelUpgradeResult?>(
                new ZodiacLevelUpgradeResult(
                    ZodiacLevelUpgradeStatus.Succeeded,
                    PreviousLevel: 1,
                    CurrentLevel: 2,
                    RequiredCharacterLevel: 10,
                    EnergyCost: 500,
                    CurrentEnergy: 500,
                    CurrentEnergyRemainderX100: 0));
        }

        private static TaskCompletionSource<bool> NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
