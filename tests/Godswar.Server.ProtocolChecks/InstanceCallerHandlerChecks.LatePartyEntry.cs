using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckLatePartyMemberEntryAsync()
    {
        await using var fixture = await CreatePartyChoiceFixtureAsync();
        var left = fixture.Leader.Registry.LeaveParty(
            fixture.MemberSession,
            fixture.Member.Name);
        Check.True(
            left.Status == PartyOperationStatus.Applied &&
            fixture.Leader.Registry.GetPartyMembership(
                fixture.Leader.Session) is null,
            "late-entry fixture starts with a genuinely solo leader");

        await OpenMedusaPageAsync(fixture.Leader);
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        var targetInstanceId = GetSourceInstanceId(fixture.Leader);
        Check.True(
            fixture.Leader.Character.CurrentMap == 200 &&
            fixture.Member.CurrentMap == fixture.Leader.SourceMapId,
            "solo leader enters before inviting the late member");
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateControlPacket(Opcodes.ClientReady));
        await InvokeAsync(
            fixture.Leader.Handler,
            CreatePlayerDetailRequest());

        await InvokeAsync(
            fixture.Leader.Handler,
            new GamePacket(PacketBuilder.PartyAction(
                Opcodes.PartyInvite,
                0x1448,
                fixture.Leader.Character.Name,
                fixture.Member.Name)));
        var memberBeforeAccept =
            fixture.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            fixture.MemberHandler,
            new GamePacket(PacketBuilder.PartyAction(
                Opcodes.PartyAccept,
                0x1448,
                fixture.Leader.Character.Name,
                fixture.Member.Name)));

        var notice = fixture.Transport.ReadLegacyPackets()
            .Skip(memberBeforeAccept)
            .Single(IsRepetitionInvitation);
        var (repetitionId, invitationId) =
            ReadRepetitionInvitation(notice);
        await InvokeAsync(
            fixture.MemberHandler,
            CreateRepetitionResponse(
                repetitionId,
                invitationId,
                accepted: true));

        Check.True(
            fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                fixture.MemberSession,
                out var memberInstanceId) &&
            memberInstanceId == targetInstanceId &&
            fixture.Member.CurrentMap == 200 &&
            fixture.Leader.Registry.IsMedusaCharacterAdmitted(
                targetInstanceId,
                fixture.Member.Id),
            "a member invited after solo entry receives confirmation, joins the same runtime, and gains combat admission");
    }
}
