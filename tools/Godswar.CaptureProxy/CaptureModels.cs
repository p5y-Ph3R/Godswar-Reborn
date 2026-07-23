readonly record struct CapturedNpcSpawnRecord(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet);

readonly record struct CapturedNpcDetailRecord(
    int Opcode,
    uint ObjectId,
    byte[] Packet);

readonly record struct CapturedMonsterSpawnRecord(
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet);

readonly record struct CapturedMonsterTemplate(
    short MapId,
    string SceneKey,
    string DisplayName);

sealed record CapturedPacketFrame(
    int PacketIndex,
    long StreamOffset,
    int? DeclaredLength,
    int ActualLength,
    int? Opcode,
    byte[] ClearBytes,
    byte[] RawBytes,
    string Notes);

sealed record PacketTransactionRecord(
    Guid CaptureSessionId,
    DateTimeOffset CapturedAt,
    Guid ConnectionId,
    string ConnectionName,
    string Direction,
    string SourceEndPoint,
    string DestinationEndPoint,
    long ChunkSequence,
    long PacketSequence,
    int PacketIndex,
    long StreamOffset,
    int? DeclaredLength,
    int ActualLength,
    int? Opcode,
    byte[] ClearBytes,
    byte[] RawBytes,
    string Notes);
