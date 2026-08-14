using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertLockedPetSkillSlotRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId)
    {
        var before = await ReadPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.Equal(
            (short)6,
            before.OpenedSkillSlots,
            "skill-unlearn fixture exposes slots zero through five only");

        var result = await executor.ExecuteAsync(
            CreatePetSkillUnlearnEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                skillSlot: 6));

        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            result.Receipt?.Status ==
                PetDurableReceiptStatus.PetSkillNotFound,
            "skill removal rejects a slot at the opened-cell boundary before potion lookup");
        AssertPetSkillUnlearnValueUnchanged(
            before,
            await ReadPetSkillUnlearnStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "locked-slot skill removal");
    }
}
