using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendTalentBootstrapAsync(
        IReadOnlyList<SkillState> skillStates,
        IReadOnlyList<TalentState> talentStates,
        string reason,
        CancellationToken cancellationToken,
        bool includeTalentRankList = true)
    {
        Console.WriteLine(
            $"[talent] bootstrap reason={reason} character={_character?.Name ?? "<none>"} skills={skillStates.Count} talents={talentStates.Count} points={_character?.TalentPoints ?? 0} includeRanks={includeTalentRankList}");

        if (_character is { } character)
        {
            await _session.SendAsync(
                PacketBuilder.MedusaDesignationInfo(
                    character.SelectedTitleId,
                    character.OwnedTitleIds),
                cancellationToken,
                "DesignationInfo");
        }

        if (!includeTalentRankList)
        {
            return;
        }

        var talentRankList = PacketBuilder.TalentRankList(talentStates);
        if (talentRankList.Length > 0)
        {
            await _session.SendAsync(
                talentRankList,
                cancellationToken,
                "TalentRankList");
        }

        var talentSkillUnlockList =
            PacketBuilder.TalentSkillUnlockList(skillStates);
        if (talentSkillUnlockList.Length > 0)
        {
            await _session.SendAsync(
                talentSkillUnlockList,
                cancellationToken,
                "TalentSkillUnlockList");
        }
    }

    private async Task SendTalentRankPacketsAsync(
        IReadOnlyList<SkillState> skillStates,
        IReadOnlyList<TalentState> talentStates,
        string reason,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[talent] rank-list reason={reason} character={_character?.Name ?? "<none>"} talents={talentStates.Count} points={_character?.TalentPoints ?? 0}");

        var talentRankList = PacketBuilder.TalentRankList(talentStates);
        if (talentRankList.Length > 0)
        {
            await _session.SendAsync(
                talentRankList,
                cancellationToken,
                "TalentRankList");
        }

        var talentSkillUnlockList =
            PacketBuilder.TalentSkillUnlockList(skillStates);
        if (talentSkillUnlockList.Length > 0)
        {
            await _session.SendAsync(
                talentSkillUnlockList,
                cancellationToken,
                "TalentSkillUnlockList");
        }
    }
}
