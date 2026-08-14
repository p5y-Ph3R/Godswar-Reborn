using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal readonly record struct PetCommandOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid ConnectionId,
    bool ServerLifecycle)
{
    public static PetCommandOperationIdentity SecureClient(Guid value) =>
        new(
            CommandIdentityStrength.ClientOperationId,
            value,
            Guid.Empty,
            ServerLifecycle: false);

    public static PetCommandOperationIdentity RawLocalServer(
        Guid value,
        Guid connectionId) =>
        new(
            CommandIdentityStrength.ServerOperationId,
            value,
            connectionId,
            ServerLifecycle: false);

    public static PetCommandOperationIdentity ServerSessionLifecycle(
        Guid value,
        Guid connectionId) =>
        new(
            CommandIdentityStrength.ServerOperationId,
            value,
            connectionId,
            ServerLifecycle: true);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty &&
        ConnectionId == Guid.Empty &&
        !ServerLifecycle;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        ConnectionId != Guid.Empty &&
        !ServerLifecycle;

    public bool IsServerSessionLifecycle =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        ConnectionId != Guid.Empty &&
        ServerLifecycle;

    // Compatibility accessor for existing raw-local command assertions.
    public Guid RawLocalConnectionId =>
        IsRawLocalServer ? ConnectionId : Guid.Empty;
}
