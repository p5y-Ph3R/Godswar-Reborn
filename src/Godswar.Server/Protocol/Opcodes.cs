namespace Godswar.Server.Protocol;

internal static class Opcodes
{
    public const ushort Login = 1;
    public const ushort SelectServer = 4;
    public const ushort LoginReturnInfo = 6;

    public const ushort LoginGameServer = 10000;
    public const ushort ResponseGameServer = 10001;
    public const ushort RoleInfo = 10002;
    public const ushort CreateRole = 10003;
    public const ushort DeleteRole = 10004;
    public const ushort GameServerReady = 10005;
    public const ushort EnterGame = 10006;
    public const ushort ClientReady = 10007;
    public const ushort GameServerInfo = 10008;
    public const ushort WalkBegin = 10013;
    public const ushort WalkEnd = 10014;
    public const ushort Revive = 10019;
    public const ushort Kitbag = 10022;
    public const ushort Storage = 10023;
    public const ushort BasicAttack = 10026;
    public const ushort Talk = 10035;
    public const ushort SkillCast = 10040;
    public const ushort PickupDrops = 10048;
    public const ushort UseOrEquip = 10049;
    public const ushort MoveItem = 10050;
    public const ushort BreakItem = 10051;
    public const ushort StorageItem = 10052;
    public const ushort Sell = 10053;
    public const ushort BagItemAction = 10056;
    public const ushort NpcDialogOpen = 10067;
    public const ushort NpcDialogPageRequest = 10068;
    public const ushort NpcFunctionAction = 10069;
    public const ushort NpcFunctionActionResponse = 10070;
    public const ushort ItemInfoRequest = 10114;
    public const ushort Forge = 10117;
    public const ushort PlayerNameInspectRequest = 10125;
    public const ushort SkillCastFinishRequest = 10171;
    public const ushort PlayerInspectRequest = 10191;
    public const ushort PlayerDetailRequest = 10200;
    public const ushort PlayerDetailAckRequest = 10202;
    public const ushort PlayerInspectVisualRequest = 10279;
    public const ushort Walk = 10194;
    public const ushort ServerTimeRequest = 10311;
    public const ushort UiHeartbeat = 10312;
    public const ushort PlayerInspectFollowup = 10342;
    public const ushort Ping = 10015;

    public static string Name(ushort opcode)
    {
        return opcode switch
        {
            Login => nameof(Login),
            SelectServer => nameof(SelectServer),
            LoginReturnInfo => nameof(LoginReturnInfo),
            LoginGameServer => nameof(LoginGameServer),
            ResponseGameServer => nameof(ResponseGameServer),
            RoleInfo => nameof(RoleInfo),
            CreateRole => nameof(CreateRole),
            DeleteRole => nameof(DeleteRole),
            GameServerReady => nameof(GameServerReady),
            EnterGame => nameof(EnterGame),
            ClientReady => nameof(ClientReady),
            GameServerInfo => nameof(GameServerInfo),
            WalkBegin => nameof(WalkBegin),
            WalkEnd => nameof(WalkEnd),
            Revive => nameof(Revive),
            Kitbag => nameof(Kitbag),
            Storage => nameof(Storage),
            BasicAttack => nameof(BasicAttack),
            Ping => nameof(Ping),
            Talk => nameof(Talk),
            SkillCast => nameof(SkillCast),
            PickupDrops => nameof(PickupDrops),
            UseOrEquip => nameof(UseOrEquip),
            MoveItem => nameof(MoveItem),
            BreakItem => "EquipmentItemEquipRequest",
            StorageItem => nameof(StorageItem),
            Sell => nameof(Sell),
            BagItemAction => nameof(BagItemAction),
            NpcDialogOpen => nameof(NpcDialogOpen),
            NpcDialogPageRequest => nameof(NpcDialogPageRequest),
            NpcFunctionAction => nameof(NpcFunctionAction),
            NpcFunctionActionResponse => nameof(NpcFunctionActionResponse),
            ItemInfoRequest => nameof(ItemInfoRequest),
            Forge => nameof(Forge),
            PlayerNameInspectRequest => nameof(PlayerNameInspectRequest),
            SkillCastFinishRequest => nameof(SkillCastFinishRequest),
            PlayerInspectRequest => nameof(PlayerInspectRequest),
            PlayerDetailRequest => nameof(PlayerDetailRequest),
            PlayerDetailAckRequest => nameof(PlayerDetailAckRequest),
            PlayerInspectVisualRequest => nameof(PlayerInspectVisualRequest),
            Walk => nameof(Walk),
            ServerTimeRequest => nameof(ServerTimeRequest),
            UiHeartbeat => nameof(UiHeartbeat),
            PlayerInspectFollowup => nameof(PlayerInspectFollowup),
            10192 => "ClientMovementOrLoad",
            10357 => "EnterUnknown10357",
            _ => "Unknown"
        };
    }
}
