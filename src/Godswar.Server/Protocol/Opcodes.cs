namespace Godswar.Server.Protocol;

internal static class Opcodes
{
    public const ushort Login = 1;
    public const ushort ServerList = 3;
    public const ushort SelectServer = 4;
    public const ushort LoginReturnInfo = 6;
    public const ushort SendServer = 7;

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
    // The native client overloads this opcode by object identity. A 28-byte
    // packet for another world object is its captured death notification,
    // while a 24-byte packet for the local player loads a new scene.
    public const ushort SceneChange = 10018;
    public const ushort Revive = 10019;
    public const ushort Kitbag = 10022;
    public const ushort Storage = 10023;
    // The installed Origin client maps its warehouse item snapshot handler to
    // MSG_STORAGE (10034). Opcode 10023 is the unrelated world-object marker
    // retained above; captured eight-byte 10023 frames are not warehouse data.
    public const ushort WarehouseSnapshot = 10034;
    public const ushort BasicAttack = 10026;
    public const ushort MonsterDrops = 10029;
    public const ushort Talk = 10035;
    // MSG_PYTHON_NOTE drives the stock client's on-screen announcement text.
    public const ushort PythonNote = 10038;
    public const ushort SkillCast = 10040;
    public const ushort PickupDrops = 10048;
    public const ushort UseOrEquip = 10049;
    public const ushort MoveItem = 10050;
    public const ushort BreakItem = 10051;
    public const ushort StorageItem = 10052;
    public const ushort Sell = 10053;
    public const ushort BagItemAction = 10056;
    // MSG_STORAGE_ITEM is bidirectional: the client requests a transfer and
    // the server echoes the same canonical 20-byte descriptor after commit.
    public const ushort WarehouseTransfer = 10059;
    // The installed client dispatch table maps 10090 to
    // MSG_PLAYER_ACCEPTQUESTS. Quest snapshots are character-specific and
    // must never be replayed from a captured login session.
    public const ushort PlayerAcceptedQuests = 10090;
    public const ushort NpcDialogOpen = 10067;
    public const ushort NpcDialogPageRequest = 10068;
    public const ushort NpcFunctionAction = 10069;
    public const ushort NpcFunctionActionResponse = 10070;
    public const ushort NpcShopCatalog = 10071;
    public const ushort NpcShopPurchase = 10073;
    // Equipment forging uses the same opcode for the client request and the
    // server result. The selection and cancel packets are separate messages.
    public const ushort ForgeStart = 10109;
    public const ushort ForgeSelection = 10110;
    public const ushort ForgeReplacementSelection = 10111;
    public const ushort ForgeReplacementAction = 10112;
    public const ushort ItemInfoRequest = 10114;
    public const ushort ForgeCancel = 10117;
    public const ushort PartyInvite = 10123;
    public const ushort PartyRequest = 10124;
    public const ushort PlayerNameInspectRequest = 10125;
    public const ushort PartyAccept = 10126;
    public const ushort PartyRemove = 10127;
    public const ushort PartyChangeLeader = 10128;
    public const ushort PartyDissolve = 10129;
    public const ushort PartyLeave = 10130;
    public const ushort PartyTip = 10131;
    public const ushort PartyReject = 10132;
    public const ushort PartyRefresh = 10133;
    public const ushort PartyDestroy = 10134;
    public const ushort ServerNote = 10169;
    public const ushort DesignationInfo = 10196;
    // Cast interruption is bidirectional in the native protocol. Both the
    // client report and the authoritative server notification use the same
    // eight-byte frame: length, opcode, and caster ID in the receiver's
    // object namespace (0x1448 for self; authoritative world ID for viewers).
    public const ushort SkillCastInterrupt = 10171;
    public const ushort PlayerInspectRequest = 10191;
    // The native Gear Mentor sends one of these whenever an item is inserted
    // into or removed from its three operation controls. The 12-byte payload
    // carries bag page, page slot, and a one-byte selected flag.
    public const ushort GearEnhancerItemSelection = 10193;
    // Native 10200 is overloaded: it participates in login/map-detail
    // readiness and carries the Fashion Show checkbox in its final DWORD.
    public const ushort PlayerDetailRequest = 10200;
    // Native Fashion Effect sends a 16-byte request. The server publishes a
    // 12-byte per-avatar effect-visibility projection on the same opcode.
    public const ushort FashionEffectVisibility = 10202;
    public const ushort RepetitionNotice = 10216;
    public const ushort RepetitionResponse = 10217;
    public const ushort RepetitionInstanceMembers = 10218;
    public const ushort RepetitionLeave = 10221;
    public const ushort RepetitionInvitation = 10224;
    public const ushort RepetitionCompletionState = 10227;
    public const ushort RepetitionFightInfo = 10229;
    public const ushort RepetitionReward = 10230;
    public const ushort RepetitionReset = 10231;
    public const ushort RepetitionSync = 10232;
    // The active repetition panel sends a six-byte action frame. Its first
    // payload byte is the action (zero is the Terminate button); the stock
    // client leaves the final byte uninitialized, so it is never authoritative.
    public const ushort RepetitionPanelAction = 10313;
    public const ushort PetCaptureRequest = 10252;
    public const ushort MonsterClaimState = 10322;
    public const ushort PlayerInspectVisualRequest = 10279;
    public const ushort PetTakeRequest = 10239;
    public const ushort PetCallOutRequest = 10240;
    public const ushort PetRecallRequest = 10241;
    public const ushort PetOperationResult = 10244;
    public const ushort PetExperience = 10261;
    public const ushort PetToPetMergeRequest = 10268;
    public const ushort PetToPetMergeResult = 10269;
    public const ushort PetSoulContractRequest = 10270;
    public const ushort PetSoulContractResult = 10271;
    public const ushort PetRebirthRequest = 10272;
    public const ushort PetRebirthResult = 10273;
    // Header-only request emitted by the stock client's innate Merge action.
    // The request carries no pet, item, slot, or stat data; the server resolves
    // every input from the authenticated character's authoritative state.
    public const ushort PetOwnerMergeRequest = 10274;
    // Native pet-unite lifecycle projections recovered independently from
    // the installed client. Both are fixed eight-byte server-to-client frames.
    public const ushort PetOwnerMergeStarted = 10275;
    // Current energy for the locally carried pet. The stock client uses a
    // fixed 0..1800 scale even though durable state is normalized separately.
    public const ushort PetEnergy = 10278;
    public const ushort PetOwnerMergeEnded = 10282;
    public const ushort PackedPetDetailRequest = 10283;
    public const ushort PackedPetDetailResponse = 10284;
    public const ushort PetLevelUpgradeRequest = 10285;
    public const ushort PetLevelUpgrade = 10286;
    public const ushort Zodiac = 10297;
    public const ushort Walk = 10194;
    public const ushort ServerTimeRequest = 10311;
    public const ushort UiHeartbeat = 10312;
    // Mounted reuse of the Riding skill takes this native player-state path
    // instead of sending a second ordinary SkillCast request. Action 6 is the
    // Ride cancellation observed in the installed client.
    public const ushort PlayerStateAction = 10320;
    public const ushort PlayerInspectFollowup = 10342;
    public const ushort EnterUiReady = 10357;
    public const ushort Ping = 10015;

    public static string Name(ushort opcode)
    {
        return opcode switch
        {
            Login => nameof(Login),
            ServerList => nameof(ServerList),
            SelectServer => nameof(SelectServer),
            LoginReturnInfo => nameof(LoginReturnInfo),
            SendServer => nameof(SendServer),
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
            SceneChange => nameof(SceneChange),
            Revive => nameof(Revive),
            Kitbag => nameof(Kitbag),
            Storage => nameof(Storage),
            WarehouseSnapshot => nameof(WarehouseSnapshot),
            BasicAttack => nameof(BasicAttack),
            MonsterDrops => nameof(MonsterDrops),
            Ping => nameof(Ping),
            Talk => nameof(Talk),
            PythonNote => nameof(PythonNote),
            SkillCast => nameof(SkillCast),
            PickupDrops => nameof(PickupDrops),
            UseOrEquip => nameof(UseOrEquip),
            MoveItem => nameof(MoveItem),
            BreakItem => "EquipmentItemEquipRequest",
            StorageItem => nameof(StorageItem),
            Sell => nameof(Sell),
            BagItemAction => nameof(BagItemAction),
            WarehouseTransfer => nameof(WarehouseTransfer),
            PlayerAcceptedQuests => nameof(PlayerAcceptedQuests),
            NpcDialogOpen => nameof(NpcDialogOpen),
            NpcDialogPageRequest => nameof(NpcDialogPageRequest),
            NpcFunctionAction => nameof(NpcFunctionAction),
            NpcFunctionActionResponse => nameof(NpcFunctionActionResponse),
            NpcShopCatalog => nameof(NpcShopCatalog),
            NpcShopPurchase => nameof(NpcShopPurchase),
            ForgeStart => nameof(ForgeStart),
            ForgeSelection => nameof(ForgeSelection),
            ForgeReplacementSelection => nameof(ForgeReplacementSelection),
            ForgeReplacementAction => nameof(ForgeReplacementAction),
            ItemInfoRequest => nameof(ItemInfoRequest),
            ForgeCancel => nameof(ForgeCancel),
            PartyInvite => nameof(PartyInvite),
            PartyRequest => nameof(PartyRequest),
            PlayerNameInspectRequest => nameof(PlayerNameInspectRequest),
            PartyAccept => nameof(PartyAccept),
            PartyRemove => nameof(PartyRemove),
            PartyChangeLeader => nameof(PartyChangeLeader),
            PartyDissolve => nameof(PartyDissolve),
            PartyLeave => nameof(PartyLeave),
            PartyTip => nameof(PartyTip),
            PartyReject => nameof(PartyReject),
            PartyRefresh => nameof(PartyRefresh),
            PartyDestroy => nameof(PartyDestroy),
            ServerNote => nameof(ServerNote),
            DesignationInfo => nameof(DesignationInfo),
            SkillCastInterrupt => nameof(SkillCastInterrupt),
            PlayerInspectRequest => nameof(PlayerInspectRequest),
            GearEnhancerItemSelection => nameof(GearEnhancerItemSelection),
            PlayerDetailRequest => nameof(PlayerDetailRequest),
            FashionEffectVisibility => nameof(FashionEffectVisibility),
            RepetitionNotice => nameof(RepetitionNotice),
            RepetitionResponse => nameof(RepetitionResponse),
            RepetitionInstanceMembers => nameof(RepetitionInstanceMembers),
            RepetitionLeave => nameof(RepetitionLeave),
            RepetitionInvitation => nameof(RepetitionInvitation),
            RepetitionCompletionState => nameof(RepetitionCompletionState),
            RepetitionFightInfo => nameof(RepetitionFightInfo),
            RepetitionReward => nameof(RepetitionReward),
            RepetitionReset => nameof(RepetitionReset),
            RepetitionSync => nameof(RepetitionSync),
            RepetitionPanelAction => nameof(RepetitionPanelAction),
            PetCaptureRequest => nameof(PetCaptureRequest),
            MonsterClaimState => nameof(MonsterClaimState),
            PlayerInspectVisualRequest => nameof(PlayerInspectVisualRequest),
            PetTakeRequest => nameof(PetTakeRequest),
            PetCallOutRequest => nameof(PetCallOutRequest),
            PetRecallRequest => nameof(PetRecallRequest),
            PetOperationResult => nameof(PetOperationResult),
            PetExperience => nameof(PetExperience),
            PetToPetMergeRequest => nameof(PetToPetMergeRequest),
            PetToPetMergeResult => nameof(PetToPetMergeResult),
            PetSoulContractRequest => nameof(PetSoulContractRequest),
            PetSoulContractResult => nameof(PetSoulContractResult),
            PetRebirthRequest => nameof(PetRebirthRequest),
            PetRebirthResult => nameof(PetRebirthResult),
            PetOwnerMergeRequest => nameof(PetOwnerMergeRequest),
            PetOwnerMergeStarted => nameof(PetOwnerMergeStarted),
            PetEnergy => nameof(PetEnergy),
            PetOwnerMergeEnded => nameof(PetOwnerMergeEnded),
            PackedPetDetailRequest => nameof(PackedPetDetailRequest),
            PackedPetDetailResponse => nameof(PackedPetDetailResponse),
            PetLevelUpgradeRequest => nameof(PetLevelUpgradeRequest),
            PetLevelUpgrade => nameof(PetLevelUpgrade),
            Zodiac => nameof(Zodiac),
            Walk => nameof(Walk),
            ServerTimeRequest => nameof(ServerTimeRequest),
            UiHeartbeat => nameof(UiHeartbeat),
            PlayerStateAction => nameof(PlayerStateAction),
            PlayerInspectFollowup => nameof(PlayerInspectFollowup),
            EnterUiReady => nameof(EnterUiReady),
            10192 => "ClientMovementOrLoad",
            _ => "Unknown"
        };
    }
}
