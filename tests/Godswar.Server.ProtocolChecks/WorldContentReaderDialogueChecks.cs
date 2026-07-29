using System.Collections.Immutable;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldContentReaderDialogueChecks
{
    private static readonly DateTimeOffset FixedLoadTime =
        new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckPinnedLookupAndRevisionAsync();
        CheckMalformedDialogueRejections();
        CheckReviewedRouteCapabilities();
        await CheckGeneratedBaselineAsync();
    }

    private static async Task CheckPinnedLookupAndRevisionAsync()
    {
        var mentor = CreateNpc("Athens_070", 0x9001);
        var civilian = CreateNpc("Athens_001", 0x9002);
        var mentorText = new NpcTextDefinition(
            mentor.NpcKey,
            mentor.SceneKey,
            "Gear Mentor",
            "I can enhance your gear.");
        var civilianText = new NpcTextDefinition(
            civilian.NpcKey,
            civilian.SceneKey,
            "Meletiou",
            "Keep Athens safe.");
        var route = new NpcDialogueRouteDefinition(
            mentor.NpcKey,
            mentor.NpcKey,
            4,
            NpcDialogueBehavior.GearMentor,
            [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var first = PinnedWorldContentReader.Create(
            "dialogue-test-v1",
            [1],
            [mentor, civilian],
            [],
            [],
            FixedLoadTime,
            [mentorText, civilianText],
            [route]);
        var second = PinnedWorldContentReader.Create(
            "different-source-label",
            [1],
            [civilian, mentor],
            [],
            [],
            FixedLoadTime.AddDays(1),
            [civilianText, mentorText],
            [route]);

        Check.Equal(
            first.Manifest.NpcDialogues.Sha256,
            second.Manifest.NpcDialogues.Sha256,
            "dialogue revision ignores caller enumeration order");
        Check.Equal(
            3,
            first.Manifest.NpcDialogues.EntryCount,
            "dialogue revision counts texts and routes");
        Check.Equal(
            "F6067770B72085D9E9496EB2E1BBB821201E78CBA8846C06A5967B15B3C149E5",
            first.Manifest.NpcDialogues.Sha256,
            "NPC-dialogue canonical revision golden vector");

        var mentorDialogue = await first.ReadNpcDialogueAsync(mentor.NpcKey);
        Check.Equal(
            "Gear Mentor",
            mentorDialogue.Text.DisplayName,
            "NPC text comes from the pinned dialogue publication");
        Check.True(
            mentorDialogue.Route is not null,
            "implemented NPC behavior route is present");
        Check.True(
            mentorDialogue.Route!.Behavior ==
            NpcDialogueBehavior.GearMentor,
            "finite behavior selector is preserved");
        Check.True(
            mentorDialogue.Route.InitialMenuSubIds.SequenceEqual(
                [1, 2, 3, 4, 5, 6, 7, 8, 9]),
            "initial menu IDs are preserved");

        var civilianDialogue =
            await first.ReadNpcDialogueAsync(civilian.NpcKey);
        Check.True(
            civilianDialogue.Route is null,
            "NPC text does not invent an unsupported runtime behavior");

        var missing = await CaptureUnavailableAsync(
            () => first.ReadNpcDialogueAsync("Athens_999"));
        Check.Equal(
            "npc-dialogues",
            missing.Family,
            "missing NPC dialogue reports its content family");
        Check.True(
            missing.Reason == WorldContentFailureReason.Missing,
            "missing NPC dialogue uses the typed missing reason");
    }

    private static void CheckMalformedDialogueRejections()
    {
        var mentor = CreateNpc("Athens_070", 0x9010);
        var civilian = CreateNpc("Athens_001", 0x9011);
        var mentorText = CreateText(mentor);

        var missingCoverage = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "dialogue-test-v1",
                [1],
                [mentor, civilian],
                [],
                [],
                FixedLoadTime,
                [mentorText],
                []));
        AssertInvalidDialogue(
            missingCoverage,
            "published NPCs require one authoritative text definition");

        var invalidBehavior = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "dialogue-test-v1",
                [1],
                [mentor],
                [],
                [],
                FixedLoadTime,
                [mentorText],
                [
                    CreateRoute(
                        mentor.NpcKey,
                        (NpcDialogueBehavior)999,
                        [1])
                ]));
        AssertInvalidDialogue(
            invalidBehavior,
            "unknown runtime behavior selectors are rejected");

        var duplicateMenuId = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "dialogue-test-v1",
                [1],
                [mentor],
                [],
                [],
                FixedLoadTime,
                [mentorText],
                [
                    CreateRoute(
                        mentor.NpcKey,
                        NpcDialogueBehavior.GearMentor,
                        [1, 1])
                ]));
        AssertInvalidDialogue(
            duplicateMenuId,
            "duplicate initial-menu IDs are rejected");

        var unboundRoute = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "dialogue-test-v1",
                [1],
                [mentor],
                [],
                [],
                FixedLoadTime,
                [mentorText],
                [
                    CreateRoute(
                        "Athens_999",
                        NpcDialogueBehavior.GearMentor,
                        [1])
                ]));
        AssertInvalidDialogue(
            unboundRoute,
            "routes cannot target an unpublished NPC key");

        var duplicateText = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "dialogue-test-v1",
                [1],
                [mentor],
                [],
                [],
                FixedLoadTime,
                [mentorText, mentorText],
                []));
        AssertInvalidDialogue(
            duplicateText,
            "duplicate NPC text definitions are rejected");

    }

    private static async Task CheckGeneratedBaselineAsync()
    {
        var reader = await GeneratedWorldContentReaderLoader.LoadAsync();
        Check.Equal(
            364,
            reader.Manifest.NpcDialogues.EntryCount,
            "generated fallback pins its 356 NPC texts and 8 reviewed routes");
        Check.Equal(
            "C0AB5E8B4173245565DCD36BE54DA06881479E376FD9303CC8FCC0D93C12B36C",
            reader.Manifest.NpcDialogues.Sha256,
            "generated fallback dialogue publication has a golden revision");

        var mentor = await reader.ReadNpcDialogueAsync("Athens_070");
        Check.Equal(
            "Gear Mentor",
            mentor.Text.DisplayName,
            "generated NPC text is readable by spawn key");
        Check.True(
            mentor.Route?.Behavior == NpcDialogueBehavior.GearMentor,
            "generated reviewed route selects the gear mentor behavior");

        var civilian = await reader.ReadNpcDialogueAsync("Athens_001");
        Check.True(
            civilian.Route is null,
            "generated text-only NPC has no invented behavior");
    }

    private static void CheckReviewedRouteCapabilities()
    {
        var npcs = NpcSpawnDefinitionFactory.Create(0, [], [], [])
            .Concat(NpcSpawnDefinitionFactory.Create(1, [], [], []))
            .ToDictionary(
                static npc => npc.NpcKey,
                StringComparer.Ordinal);
        var routes = NpcDialogueBaselineV1.CreateRoutes();
        Check.Equal(
            NpcDialogueBaselineV1.ExpectedRouteCount,
            routes.Length,
            "reviewed dialogue baseline has all eight city bindings");
        foreach (var route in routes)
        {
            Check.True(
                npcs.TryGetValue(route.NpcKey, out var npc) &&
                NpcDialogueBehaviorRegistry.IsAllowed(npc, route),
                $"reviewed route {route.NpcKey} matches its stock protocol");
        }
    }

    private static NpcSpawnDefinition CreateNpc(
        string npcKey,
        uint objectId) =>
        new(
            1,
            "Athens",
            npcKey,
            $"{npcKey}_Male1",
            objectId,
            10.25f,
            -20.5f,
            objectId,
            0x00040002,
            1.25f,
            [0x77],
            [0x80]);

    private static NpcTextDefinition CreateText(
        NpcSpawnDefinition npc) =>
        new(
            npc.NpcKey,
            npc.SceneKey,
            npc.NpcKey,
            $"Description for {npc.NpcKey}.");

    private static NpcDialogueRouteDefinition CreateRoute(
        string npcKey,
        NpcDialogueBehavior behavior,
        ImmutableArray<int> menuSubIds) =>
        new(
            npcKey,
            npcKey,
            4,
            behavior,
            menuSubIds);

    private static void AssertInvalidDialogue(
        WorldContentUnavailableException exception,
        string description)
    {
        Check.Equal(
            "npc-dialogues",
            exception.Family,
            $"{description} family");
        Check.True(
            exception.Reason == WorldContentFailureReason.Invalid,
            description);
    }

    private static WorldContentUnavailableException CaptureUnavailable(
        Action action)
    {
        try
        {
            action();
        }
        catch (WorldContentUnavailableException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "Expected WorldContentUnavailableException.");
    }

    private static async Task<WorldContentUnavailableException>
        CaptureUnavailableAsync(Func<ValueTask<NpcDialogueContent>> action)
    {
        try
        {
            _ = await action();
        }
        catch (WorldContentUnavailableException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "Expected WorldContentUnavailableException.");
    }
}
