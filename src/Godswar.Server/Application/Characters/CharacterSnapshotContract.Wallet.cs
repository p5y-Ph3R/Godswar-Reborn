namespace Godswar.Server.Application.Characters;

internal static partial class CharacterSnapshotContract
{
    private static void ValidateWallet(CharacterWalletSnapshot wallet)
    {
        if (wallet is null)
        {
            throw Invalid("Character wallet is missing.");
        }

        if (wallet.Silver < 0 ||
            wallet.Gold < 0 ||
            wallet.BindingGold < 0 ||
            wallet.MedusaHonorPoints < 0 ||
            wallet.MedusaRewardRevision < 0)
        {
            throw Invalid("Character wallet contains a negative balance.");
        }
    }
}
