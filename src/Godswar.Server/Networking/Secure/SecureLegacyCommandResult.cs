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
        ulong inventoryRevision,
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
            inventoryRevision == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision),
                "An applied durable command must identify its inventory revision.");
        }

        Disposition = disposition;
        CommandFamily = commandFamily;
        ResultCode = resultCode;
        InventoryRevision = inventoryRevision;
        OperationId = operationId;
    }

    public SecureLegacyCommandDisposition Disposition { get; }

    public ushort CommandFamily { get; }

    public uint ResultCode { get; }

    public ulong InventoryRevision { get; }

    public Guid OperationId { get; }
}
