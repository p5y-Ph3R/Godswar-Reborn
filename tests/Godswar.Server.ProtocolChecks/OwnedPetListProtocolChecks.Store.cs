using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class OwnedPetListProtocolChecks
{
    private sealed class LoginBootstrapStore : GameStoreTestStub
    {
        private readonly GameCharacter _character;
        private readonly IReadOnlyList<PetBootstrapSnapshot> _pets;

        public LoginBootstrapStore(
            GameCharacter character,
            IReadOnlyList<PetBootstrapSnapshot> pets)
        {
            _character = character;
            _pets = pets;
        }

        public int OwnedPetReadCount { get; private set; }

        public int LastPetAccountId { get; private set; }

        public int LastPetCharacterId { get; private set; }

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterStats?>(
                CharacterStats.FromCharacter(_character));

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillState>>([]);

        public override Task<IReadOnlyList<TalentState>>
            GetTalentStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TalentState>>([]);

        public override Task<IReadOnlyList<PetBootstrapSnapshot>>
            GetOwnedPetsAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            OwnedPetReadCount++;
            LastPetAccountId = accountId;
            LastPetCharacterId = characterId;
            return Task.FromResult(_pets);
        }
    }
}
