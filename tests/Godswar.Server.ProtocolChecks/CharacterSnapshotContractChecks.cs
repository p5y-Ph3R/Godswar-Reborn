using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotContractChecks
{
    public static Task RunAsync()
    {
        CheckValidAndEmptySnapshots();
        CheckFiniteFailures();
        CheckHydration();
        CheckBoundedPostgresProviderToken();
        return Task.CompletedTask;
    }

    private static void CheckBoundedPostgresProviderToken()
    {
        var raw = "10:999:" + string.Join(
            ',',
            Enumerable.Range(1, 100_000));
        var first =
            PostgresCharacterSnapshotToken.FromRawSnapshotForTest(raw);
        var second =
            PostgresCharacterSnapshotToken.FromRawSnapshotForTest(raw);
        var changed =
            PostgresCharacterSnapshotToken.FromRawSnapshotForTest(
                raw + ",100001");

        Check.True(
            first.Length <=
            CharacterSnapshotLimits.ProviderSnapshotTokenLength,
            "large PostgreSQL snapshots produce a bounded provider token");
        Check.Equal(
            first,
            second,
            "PostgreSQL snapshot token fingerprint is deterministic");
        Check.True(
            !string.Equals(first, changed, StringComparison.Ordinal),
            "different PostgreSQL snapshots produce different fingerprints");
    }

    private static void CheckValidAndEmptySnapshots()
    {
        Check.Equal(
            PlayerExperienceCatalog.FighterLevelSealLevel,
            CharacterProgressionSnapshotRules.FighterLevelSealLevel,
            "snapshot and gameplay level-seal rules agree");
        Check.Equal(
            PlayerExperienceCatalog.MaximumLevel,
            CharacterProgressionSnapshotRules.MaximumCharacterLevel,
            "snapshot and gameplay maximum-level rules agree");
        var valid = CreateValidSnapshot();
        CharacterSnapshotContract.Validate(valid);
        CharacterSnapshotContract.Validate(
            valid with { Character = null });
        Check.True(
            CharacterLoadSnapshotHydrator.Hydrate(
                valid with { Character = null }) is null,
            "an empty single-character slot hydrates to no legacy character");
    }

    private static void CheckFiniteFailures()
    {
        var valid = CreateValidSnapshot();
        var unsupported = CaptureFailure(
            () => CharacterSnapshotContract.Validate(
                valid with { ContractVersion = 999 }));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.UnsupportedContractVersion,
            (int)unsupported.Reason,
            "unsupported snapshot contract has a finite reason");

        var wrongOwner = valid with
        {
            Character = valid.Character! with
            {
                Identity = valid.Character!.Identity with { AccountId = 8 }
            }
        };
        var ownership = CaptureFailure(
            () => CharacterSnapshotContract.Validate(wrongOwner));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.OwnershipMismatch,
            (int)ownership.Reason,
            "snapshot ownership mismatch has a finite reason");

        var staleLocation = valid with
        {
            Character = valid.Character! with
            {
                Location = valid.Character.Location with
                {
                    PositionRevision = -1
                }
            }
        };
        var invalidLocation = CaptureFailure(
            () => CharacterSnapshotContract.Validate(staleLocation));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)invalidLocation.Reason,
            "negative position revision has a finite invalid-data reason");

        var defaultSkills = valid with
        {
            Character = valid.Character! with
            {
                Skills = default
            }
        };
        var bounds = CaptureFailure(
            () => CharacterSnapshotContract.Validate(defaultSkills));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.BoundsExceeded,
            (int)bounds.Reason,
            "default immutable collection has a finite bounds reason");

        var nullPet = valid with
        {
            Character = valid.Character! with
            {
                Pets = ImmutableArray.CreateRange(
                    new CharacterPetSnapshot[] { null! })
            }
        };
        var invalidPet = CaptureFailure(
            () => CharacterSnapshotContract.Validate(nullPet));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)invalidPet.Reason,
            "a null pet row has a typed validation failure");

        var duplicateSkills = valid with
        {
            Character = valid.Character! with
            {
                Skills = ImmutableArray.Create(
                    new CharacterSkillSnapshot(4904, 1),
                    new CharacterSkillSnapshot(4904, 2))
            }
        };
        var invalid = CaptureFailure(
            () => CharacterSnapshotContract.Validate(duplicateSkills));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)invalid.Reason,
            "duplicate skill rows have a finite invalid-data reason");

        var invalidLevelSeal = valid with
        {
            Character = valid.Character! with
            {
                Progression = valid.Character.Progression with
                {
                    FighterLevelSealed = true
                }
            }
        };
        var invalidSeal = CaptureFailure(
            () => CharacterSnapshotContract.Validate(invalidLevelSeal));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)invalidSeal.Reason,
            "level sealing outside level 89 has a finite invalid-data reason");

        var aboveMaximumLevel = valid with
        {
            Character = valid.Character! with
            {
                Progression = valid.Character.Progression with
                {
                    Level =
                        CharacterProgressionSnapshotRules.MaximumCharacterLevel + 1
                }
            }
        };
        var invalidMaximum = CaptureFailure(
            () => CharacterSnapshotContract.Validate(aboveMaximumLevel));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.InvalidData,
            (int)invalidMaximum.Reason,
            "fighter level above 200 has a finite invalid-data reason");
    }

    private static void CheckHydration()
    {
        var source = CreateValidSnapshot();
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(source)
            ?? throw new InvalidOperationException(
                "Valid character snapshot did not hydrate.");
        var character = hydrated.Character;

        Check.Equal(19, character.Id, "hydrated character ID");
        Check.Equal(7, character.AccountId, "hydrated account ID");
        Check.Equal("SnapshotHero", character.Name, "hydrated character name");
        Check.Equal((byte)7, character.CurrentMap, "hydrated map");
        Check.Equal(
            4L,
            character.PositionRevision,
            "load preserves persisted position revision");
        Check.Equal(42L, character.VitalsRevision, "load preserves persisted vitals revision");
        Check.Equal(9_500, character.MaxHp, "calculated maximum HP wins over base HP");
        Check.Equal(8_900, character.CurrentHp, "calculated current HP hydrates");
        Check.Equal((short)10, character.WeaponRank, "calculated weapon rank hydrates");
        Check.Equal((short)14, character.ArmorRank, "calculated armor rank hydrates");
        Check.True(
            character.CalculatedStats is not null,
            "calculated stats are attached to the legacy projection");
        Check.Equal(1, hydrated.Skills.Count, "skills hydrate");
        Check.Equal(4904, hydrated.Skills[0].SkillId, "skill identity hydrates");
        Check.Equal(1, hydrated.Talents.Count, "talents hydrate");
        Check.Equal(1, hydrated.Pets.Count, "pets hydrate");
        Check.Equal(
            (short)PetAptitude.Transcendent,
            (short)hydrated.Pets[0].Aptitude,
            "pet aptitude hydrates");
        Check.Equal(1, hydrated.Pets[0].StatValues.Count, "pet stats hydrate");
        Check.Equal(1, hydrated.Pets[0].CharacterBonuses.Count, "pet bonuses hydrate");
        Check.Equal(1, hydrated.Pets[0].Skills.Count, "pet skills hydrate");
        Check.Equal(1, hydrated.PersonalBoosts.Count, "raw personal boosts are retained");

        var sealedHydrated = CharacterLoadSnapshotHydrator.Hydrate(
            CreateValidSnapshot(fighterLevelSealed: true)) ??
            throw new InvalidOperationException(
                "Valid sealed character snapshot did not hydrate.");
        Check.True(
            sealedHydrated.Character.FighterLevelSealed,
            "durable fighter level seal hydrates into the runtime projection");
    }
}
