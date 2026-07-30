namespace Godswar.Server.Networking.Secure;

internal enum SecureLegacyCommandDisposition : byte
{
    Applied = 1,
    Replayed = 2,
    Rejected = 3,
    Conflict = 4
}

internal readonly record struct SecureLegacyCommandResult
{
    public SecureLegacyCommandResult(
        SecureLegacyCommandDisposition disposition,
        ushort commandFamily,
        uint resultCode,
        ulong authoritativeRevision,
        Guid operationId)
    {
        if (!SecureProtocolValidation.IsLegacyCommandDisposition(
                disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        if (commandFamily == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandFamily));
        }
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The client operation ID must be nonzero.",
                nameof(operationId));
        }
        if (disposition == SecureLegacyCommandDisposition.Applied &&
            authoritativeRevision == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeRevision),
                "An applied durable command must identify its authoritative revision.");
        }

        Disposition = disposition;
        CommandFamily = commandFamily;
        ResultCode = resultCode;
        AuthoritativeRevision = authoritativeRevision;
        OperationId = operationId;
    }

    public SecureLegacyCommandDisposition Disposition { get; }

    public ushort CommandFamily { get; }

    public uint ResultCode { get; }

    public ulong AuthoritativeRevision { get; }

    // Compatibility alias for inventory command callers. The version-1 wire
    // field is aggregate-owned and is not limited to inventory aggregates.
    public ulong InventoryRevision => AuthoritativeRevision;

    public Guid OperationId { get; }
}
