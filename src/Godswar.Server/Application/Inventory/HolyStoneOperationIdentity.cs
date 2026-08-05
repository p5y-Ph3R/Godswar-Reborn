using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct HolyStoneOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid RawLocalConnectionId)
{
    public static HolyStoneOperationIdentity SecureClient(Guid value) =>
        new(CommandIdentityStrength.ClientOperationId, value, Guid.Empty);

    public static HolyStoneOperationIdentity RawLocalServer(
        Guid value,
        Guid connectionId) =>
        new(CommandIdentityStrength.ServerOperationId, value, connectionId);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId == Guid.Empty;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId != Guid.Empty;
}
