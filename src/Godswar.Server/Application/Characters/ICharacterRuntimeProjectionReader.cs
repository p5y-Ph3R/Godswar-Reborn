namespace Godswar.Server.Application.Characters;

/// <summary>
/// Reads bounded character projections needed while a character is online.
/// This is intentionally narrower than the consistent login snapshot.
/// </summary>
internal interface ICharacterRuntimeProjectionReader
{
    Task<CharacterCalculatedStatsSnapshot?> ReadCalculatedStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<bool> IsSkillLearnedAsync(
        int accountId,
        int characterId,
        int skillId,
        CancellationToken cancellationToken = default);
}
