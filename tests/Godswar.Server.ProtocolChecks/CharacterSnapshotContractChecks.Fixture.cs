using System.Collections.Immutable;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotContractChecks
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 29, 1, 2, 3, TimeSpan.Zero);

    internal static CharacterAccountSnapshot CreateValidSnapshot()
    {
        const int accountId = 7;
        const int characterId = 19;
        var stats = new CharacterCalculatedStatsSnapshot(
            characterId,
            accountId,
            "SnapshotHero",
            80,
            9_500,
            1_200,
            8_900,
            1_100,
            400,
            401,
            402,
            403,
            404,
            405,
            406,
            407,
            408,
            409,
            410,
            411,
            412,
            413,
            414,
            415,
            416,
            417,
            418,
            419,
            420,
            421,
            10,
            422,
            423,
            14,
            424,
            3);
        var pet = new CharacterPetSnapshot(
            31,
            accountId,
            characterId,
            1,
            "Jolo",
            1,
            120,
            252_947_820,
            16,
            25m,
            100,
            0,
            4,
            true,
            true,
            100,
            100,
            1_000,
            100,
            3_600,
            9,
            true,
            true,
            "owned",
            true,
            true,
            true,
            17,
            FixedUtc,
            FixedUtc.AddMinutes(1),
            ImmutableArray.Create(
                new CharacterPetStatValueSnapshot(
                    1,
                    10m,
                    11m,
                    12m,
                    0.5m,
                    2,
                    3m,
                    4m)),
            ImmutableArray.Create(
                new CharacterPetBonusSnapshot(1, 12.5m, 3)),
            ImmutableArray.Create(
                new CharacterPetSkillSnapshot(501, 0, 2, 600, true, 4)));
        var character = new CharacterLoadSnapshot(
            new CharacterIdentitySnapshot(
                characterId,
                accountId,
                "SnapshotHero",
                FixedUtc),
            new CharacterAppearanceSnapshot(1, 2, 3, 4, 5, 6),
            new CharacterLocationSnapshot(7, 12.5f, -33.25f),
            new CharacterProgressionSnapshot(80, 1_234_567, 99, 98, 97),
            new CharacterVitalsSnapshot(1_500, 177, 8_900, 1_100, 42),
            new CharacterWalletSnapshot(10_000_000, 9_000_000),
            new CharacterLoadoutSnapshot(
                "[1000]#",
                "[2000]#",
                10,
                422,
                14,
                424),
            new CharacterZodiacSnapshot(
                8,
                9,
                FixedUtc.AddDays(1),
                10,
                11,
                12,
                DateOnly.FromDateTime(FixedUtc.UtcDateTime),
                13,
                FixedUtc,
                DateOnly.FromDateTime(FixedUtc.UtcDateTime),
                14,
                15,
                ImmutableArray.CreateRange(Enumerable.Range(0, 16)),
                ImmutableArray.CreateRange(Enumerable.Range(100, 16))),
            stats,
            ImmutableArray.Create(new CharacterSkillSnapshot(4904, 1)),
            ImmutableArray.Create(new CharacterTalentSnapshot(64, 10, 20, 30)),
            ImmutableArray.Create(pet),
            ImmutableArray.Create(
                new CharacterProgressionBoostSnapshot(
                    586,
                    14,
                    10_000,
                    1,
                    FixedUtc,
                    TimeSpan.FromHours(1).Ticks,
                    "fixture")));
        return new CharacterAccountSnapshot(
            CharacterSnapshotContractVersions.Current,
            accountId,
            "fixture-snapshot-1",
            FixedUtc,
            CharacterSlotPolicy.SingleCharacterV1,
            character);
    }

    private static CharacterSnapshotUnavailableException CaptureFailure(
        Action action)
    {
        try
        {
            action();
        }
        catch (CharacterSnapshotUnavailableException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "Expected a character snapshot contract failure.");
    }
}
