namespace Godswar.Server.Networking.Secure;

internal sealed partial class InMemoryGameTicketStore
{
    private sealed record GenerationRecord(
        Guid GenerationId,
        string Username);

    private sealed class TicketRecord
    {
        public TicketRecord(
            int accountId,
            string username,
            Guid generationId,
            SecureConnectionContext connection,
            SecureGameTarget target,
            long issuedTimestamp,
            byte[] ticketHash)
        {
            AccountId = accountId;
            Username = username;
            GenerationId = generationId;
            ProtocolMajor = connection.ProtocolMajor;
            ProtocolMinor = connection.ProtocolMinor;
            ClientInstanceId = connection.ClientInstanceId.ToArray();
            OriginSha256 = connection.OriginSha256.ToArray();
            RouteHost = target.RouteHost;
            TlsHost = target.TlsHost;
            Audience = target.Audience;
            RoutePort = target.RoutePort;
            TlsPort = target.TlsPort;
            ServerId = target.ServerId;
            Permissions = target.Permissions;
            IssuedTimestamp = issuedTimestamp;
            TicketHash = ticketHash;
        }

        public int AccountId { get; }
        public string Username { get; }
        public Guid GenerationId { get; }
        public ushort ProtocolMajor { get; }
        public ushort ProtocolMinor { get; }
        public byte[] ClientInstanceId { get; }
        public byte[] OriginSha256 { get; }
        public string RouteHost { get; }
        public string TlsHost { get; }
        public string Audience { get; }
        public ushort RoutePort { get; }
        public ushort TlsPort { get; }
        public uint ServerId { get; }
        public SecureGamePermissions Permissions { get; }
        public long IssuedTimestamp { get; }
        public byte[] TicketHash { get; }
        public bool Committed { get; set; }
    }
}
