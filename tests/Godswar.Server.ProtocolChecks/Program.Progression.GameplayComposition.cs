using System.Text.Json.Nodes;
using Godswar.Server.Application.Progression;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckJsonFocusedExperienceBoostReadAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-focused-boost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync(
                    "focused-boost-read",
                    "local-test");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "FocusedBoost",
                        Camp = GameDefaults.SpartaCamp,
                        Profession = 1
                    });
                accountId = account.Id;
                characterId = character.Id;
            }

            var activatedAt = new DateTimeOffset(
                2026,
                7,
                20,
                1,
                0,
                0,
                TimeSpan.Zero);
            var readAt = activatedAt.AddMinutes(5);
            var statePath = Path.Combine(dataPath, "state.json");
            var state = JsonNode.Parse(
                    await File.ReadAllTextAsync(statePath))?.AsObject() ??
                throw new InvalidOperationException(
                    "Focused boost check could not parse the JSON state.");
            state["characterExperienceBoosts"] = new JsonArray
            {
                new JsonObject
                {
                    ["characterId"] = characterId,
                    ["statusId"] = global::Godswar.Server.Application
                        .Progression.ExperienceStatusIds.MaxExperiencePotion,
                    ["kind"] = global::Godswar.Server.Application
                        .Progression.ExperienceBoostKinds.Consumable,
                    ["bonusBasisPoints"] = 30_000,
                    ["priority"] = 11,
                    ["activatedAt"] = activatedAt,
                    ["expiresAt"] = activatedAt.AddHours(1),
                    ["source"] = "focused-json-provider"
                }
            };
            await File.WriteAllTextAsync(
                statePath,
                state.ToJsonString(JsonDefaults.Indented));

            await using var restartedStore = new JsonGameStore(dataPath);
            var providers = ServerGameplayPersistenceComposition.Create(
                null,
                restartedStore);
            var snapshot = await providers.ExperienceBoosts.ReadAsync(
                new ExperienceBoostReadRequest(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    GameDefaults.SpartaCapitalMap,
                    readAt));
            Check.Equal(
                1,
                snapshot.ActiveBoosts.Length,
                "focused JSON boost reader returns the active durable grant");
            Check.Equal(
                30_000,
                snapshot.TotalBonusBasisPoints,
                "focused JSON boost reader maps the authoritative bonus");
            Check.Equal(
                220,
                snapshot.ApplyTo(55),
                "focused JSON boost snapshot applies its mapped multiplier");
            Check.Equal(
                "focused-json-provider",
                snapshot.ActiveBoosts[0].Source,
                "focused JSON boost reader preserves bounded source evidence");

            var persistedCharacter = state["characters"]?.AsArray()
                .Select(node => node?.AsObject())
                .Single(candidate =>
                    candidate?["id"]?.GetValue<int>() == characterId) ??
                throw new InvalidOperationException(
                    "Focused boost fixture character was not persisted.");
            persistedCharacter["lifecycleState"] =
                (int)CharacterLifecycleState.Deleted;
            await File.WriteAllTextAsync(
                statePath,
                state.ToJsonString(JsonDefaults.Indented));
            var deletedSnapshot = await providers.ExperienceBoosts.ReadAsync(
                new ExperienceBoostReadRequest(
                    accountId,
                    characterId,
                    GameDefaults.SpartaCamp,
                    GameDefaults.SpartaCapitalMap,
                    readAt));
            Check.Equal(
                0,
                deletedSnapshot.ActiveBoosts.Length,
                "focused JSON boost reader rejects a deleted character");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
