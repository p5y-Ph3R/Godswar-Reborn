namespace Godswar.Server.Application.Pets;

internal enum PetBasicSavvyResetOperation : byte
{
    Preview = 1,
    Accept = 2
}

internal readonly record struct PetBasicSavvyResetCommand(
    PetCommandOperationIdentity Identity,
    PetBasicSavvyResetOperation Operation =
        PetBasicSavvyResetOperation.Preview,
    Guid PreviewOperationId = default)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}
