using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class GameClientHandler : IClientHandler
{
    private const uint LocalPlayerObjectId = 0x00001448;
    private const int HolyStoneDialogIndex = 30;
    private const int HolyStoneMenuMount = 101;
    private const int HolyStoneMenuRemove = 201;
    private const int HolyStoneMenuDrill = 301;
    private const int HolyStoneMountSuccess = 800;
    private const int HolyStoneRemoveSuccess = 1200;
    private const int HolyStoneInsufficientFunds = 1400;
    private const int HolyStoneDrillSuccess = 1500;
    private static readonly TimeSpan PendingUnequipFollowupTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastItemInfoTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PositionPersistInterval = TimeSpan.FromSeconds(2);
    // The client cadence is 1500 ms. A 25 ms allowance prevents a legitimate
    // swing from being discarded by timer/socket scheduling jitter.
    private static readonly TimeSpan BasicAttackCooldown = TimeSpan.FromMilliseconds(1475);

    private readonly ClientSession _session;
    private readonly IGameStore _store;
    private readonly GameSessionRegistry _registry;
    private GameAccount? _account;
    private GameCharacter? _character;
    private PendingUnequipFollowup? _pendingUnequipFollowup;
    private PendingItemInfo? _lastItemInfo;
    private bool _registered;
    private bool _accountSessionRegistered;
    private bool _worldPresenceAnnounced;
    private bool _clientReadyReceived;
    private bool _playerDetailSent;
    private bool _postEnterBootstrapSent;
    private DateTime _lastPositionPersistUtc = DateTime.MinValue;
    private DateTimeOffset _nextBasicAttackAt = DateTimeOffset.MinValue;
    private bool _positionDirty;
    private readonly Dictionary<uint, NpcSpawnDefinition> _mapNpcsByInteractionId = new();
    private WorldSectorVisibilityTracker<NpcSpawnDefinition>? _npcVisibility;

    public GameClientHandler(ClientSession session, IGameStore store, GameSessionRegistry registry)
    {
        _session = session;
        _store = store;
        _registry = registry;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _session.ReadPacketAsync(cancellationToken);
                if (packet is null)
                {
                    return;
                }

                await HandlePacketAsync(packet, cancellationToken);
            }
        }
        finally
        {
            try
            {
                await PersistCharacterPositionAsync(force: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[world] failed saving final position: {ex.Message}");
            }

            if (_registered)
            {
                try
                {
                    await BroadcastPlayerLeaveAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[world] failed broadcasting leave: {ex.Message}");
                }

                _registry.Remove(_session);
                _registered = false;
            }

            if (_account is not null && _accountSessionRegistered)
            {
                var removedCurrentSession = _registry.RemoveAccountSession(_account.Id, _session);
                if (removedCurrentSession)
                {
                    await _store.MarkAccountOfflineAsync(_account.Id, CancellationToken.None);
                    Console.WriteLine($"[game] marked offline account={_account.Username}");
                }
            }
        }
    }

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogReceived(packet);

        switch (packet.Opcode)
        {
            case Opcodes.LoginGameServer:
                await HandleGameLoginAsync(packet, cancellationToken);
                break;
            case Opcodes.RoleInfo:
                await SendCharacterPreviewAsync(cancellationToken);
                break;
            case Opcodes.CreateRole:
                await HandleCreateRoleAsync(packet, cancellationToken);
                break;
            case Opcodes.DeleteRole:
                await HandleDeleteRoleAsync(packet, cancellationToken);
                break;
            case Opcodes.EnterGame:
                await HandleEnterGameAsync(cancellationToken);
                break;
            case Opcodes.Ping:
                await _session.SendAsync(packet.Buffer, cancellationToken, "PingEcho");
                break;
            case Opcodes.UiHeartbeat:
                await _session.SendAsync(packet.Buffer, cancellationToken, "UiHeartbeatEcho");
                break;
            case Opcodes.Talk:
            case Opcodes.WalkBegin:
            case Opcodes.WalkEnd:
            case Opcodes.Walk:
                if (packet.Opcode == Opcodes.Walk)
                {
                    if (!await HandleWalkAsync(packet, cancellationToken))
                    {
                        break;
                    }
                }
                else if (packet.Opcode == Opcodes.WalkEnd)
                {
                    await PersistCharacterPositionAsync(force: true, cancellationToken);
                }

                await BroadcastToCurrentMapAsync(packet, cancellationToken);
                break;
            case Opcodes.SkillCast:
                await HandleSkillCastAsync(packet, cancellationToken);
                break;
            case Opcodes.BasicAttack:
                await HandleBasicAttackAsync(packet, cancellationToken);
                break;
            case Opcodes.Revive:
                await HandleReviveAsync(packet, cancellationToken);
                break;
            case Opcodes.Kitbag:
            case Opcodes.Storage:
            case Opcodes.PickupDrops:
            case Opcodes.MoveItem:
            case Opcodes.Sell:
                LogInventoryPacket(packet);
                break;
            case Opcodes.UseOrEquip:
                await HandleUseOrEquipAsync(packet, cancellationToken);
                break;
            case Opcodes.BagItemAction:
                await HandleBagItemActionAsync(packet, cancellationToken);
                break;
            case Opcodes.ItemInfoRequest:
                HandleItemInfoRequest(packet);
                break;
            case Opcodes.NpcDialogOpen:
                await HandleNpcDialogOpenAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcDialogPageRequest:
                HandleNpcDialogPageRequest(packet);
                break;
            case Opcodes.NpcFunctionAction:
                await HandleNpcFunctionActionAsync(packet, cancellationToken);
                break;
            case Opcodes.PlayerNameInspectRequest:
                await _session.SendAsync(packet.Buffer, cancellationToken, "PlayerNameInspectAck");
                break;
            case Opcodes.PlayerInspectRequest:
                await HandlePlayerInspectRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PlayerInspectVisualRequest:
                await HandlePlayerInspectVisualRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.BreakItem:
                await HandleBreakItemAsync(packet, cancellationToken);
                break;
            case Opcodes.StorageItem:
                await HandleStorageItemAsync(packet, cancellationToken);
                break;
            case Opcodes.ServerTimeRequest:
                await _session.SendAsync(PacketBuilder.ServerTime(), cancellationToken, "ServerTime");
                break;
            case Opcodes.ClientReady:
                _clientReadyReceived = true;
                Console.WriteLine($"[game] ClientReady character={_character?.Name ?? "<none>"}");
                await SendPostEnterBootstrapAsync(cancellationToken);
                break;
            case Opcodes.PlayerDetailRequest:
                await HandlePlayerDetailRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.PlayerDetailAckRequest:
                await _session.SendAsync(PacketBuilder.PlayerDetailAck(packet.Payload), cancellationToken, "PlayerDetailAck");
                break;
            case Opcodes.GameServerReady:
            case Opcodes.GameServerInfo:
            case Opcodes.Forge:
            case Opcodes.PlayerInspectFollowup:
            case 10192:
            case 10357:
                Console.WriteLine($"[game] ignored {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode}");
                break;
            default:
                Console.WriteLine(
                    $"[game] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                break;
        }
    }

    private async Task HandleGameLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var username = PacketText.ReadFixedAscii(packet.Payload, 0, 32);
        _account = await _store.LoginOrCreateAccountAsync(username, string.Empty, cancellationToken);
        _accountSessionRegistered = true;

        var replacedSession = _registry.ReplaceAccountSession(_account.Id, _session);
        if (replacedSession is not null)
        {
            Console.WriteLine($"[game] replacing stale session account={_account.Username}");
            _registry.Remove(replacedSession);
            replacedSession.Disconnect();
        }

        Console.WriteLine($"[game] accepted {_account.Username}");

        await _session.SendAsync(PacketBuilder.AfterLogin(), cancellationToken, "AfterLogin");
        await SendCharacterPreviewAsync(cancellationToken);
    }

    private async Task SendCharacterPreviewAsync(CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            await _session.SendAsync(PacketBuilder.BlankUser(), cancellationToken, "BlankUser");
            return;
        }

        _character = await _store.GetFirstCharacterAsync(_account.Id, cancellationToken);
        await _session.SendAsync(
            _character is null ? PacketBuilder.BlankUser() : PacketBuilder.CharacterPreview(_character),
            cancellationToken,
            _character is null ? "BlankUser" : "CharacterPreview");
    }

    private async Task HandleCreateRoleAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        _account ??= await _store.LoginOrCreateAccountAsync("player", string.Empty, cancellationToken);

        var payload = packet.Payload;
        var character = new GameCharacter
        {
            Name = PacketText.ReadFixedAscii(payload, 0, 32),
            Gender = ReadByte(payload, 32, 1),
            Camp = ReadByte(payload, 33, 1),
            Profession = ReadByte(payload, 34, 0),
            Hair = ReadByte(payload, 36, 0),
            Face = ReadByte(payload, 37, 0),
            Faith = ReadByte(payload, 70, 1),
            Level = 1,
            CurrentHp = 1500,
            CurrentMp = 177,
            MaxHp = 1500,
            MaxMp = 177
        };

        _character = await _store.CreateCharacterAsync(_account.Id, character, cancellationToken);
        Console.WriteLine($"[game] created character {_character.Name}");
        await _session.SendAsync(PacketBuilder.CreateRoleSuccess(), cancellationToken, "CreateRoleSuccess");
    }

    private async Task HandleDeleteRoleAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var username = PacketText.ReadFixedAscii(packet.Payload, 0, 32);
        _account ??= await _store.LoginOrCreateAccountAsync(username, string.Empty, cancellationToken);

        var characterName = PacketText.ReadFixedAscii(packet.Payload, 32, 32);
        await _store.DeleteCharacterAsync(_account.Id, characterName, cancellationToken);
        _character = null;

        Console.WriteLine($"[game] deleted character {characterName}");
        await _session.SendAsync(PacketBuilder.DeleteRoleSuccess(), cancellationToken, "DeleteRoleSuccess");
    }

    private async Task HandleEnterGameAsync(CancellationToken cancellationToken)
    {
        if (_account is not null && _character is null)
        {
            _character = await _store.GetFirstCharacterAsync(_account.Id, cancellationToken);
        }

        if (_character is null)
        {
            await _session.SendAsync(PacketBuilder.BlankUser(), cancellationToken, "BlankUser");
            return;
        }

        if (_character.CurrentHp <= 0)
        {
            await RestoreFreeRevivalStateAsync(cancellationToken);
            Console.WriteLine(
                $"[revive] restored dead character during enter character={_character.Name} map={_character.CurrentMap} hp={_character.CurrentHp}/{_character.MaxHp}");
        }

        await RefreshActiveCharacterStatsAsync("enter", cancellationToken);

        var enterMain = PacketBuilder.EnterMain(_character);
        var kitBagDetailPages = PacketBuilder.KitBagDetailPages(_character);
        var kitBagSlotIndexes = PacketBuilder.KitBagSlotIndexes(_character);
        var skillStates = _account is null
            ? []
            : await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        var talentStates = _account is null
            ? []
            : await _store.GetTalentStatesAsync(_account.Id, _character.Id, cancellationToken);
        Console.WriteLine(
            $"[game] enter name={_character.Name} profession={_character.Profession} level={_character.Level} equipment={PacketBuilder.EnterEquipmentSummary(_character)} main={enterMain.Length} kitbagDetail={kitBagDetailPages.Length} kitbagIndex={kitBagSlotIndexes.Length} skills={skillStates.Count} talents={talentStates.Count}");

        await _session.SendAsync(enterMain, cancellationToken, "EnterMain");
        await _session.SendAsync(PacketBuilder.EnterUiBootstrap(), cancellationToken, "EnterUiBootstrap");

        foreach (var packet in kitBagDetailPages)
        {
            await _session.SendAsync(packet, cancellationToken, "KitBagDetail");
        }

        foreach (var packet in kitBagSlotIndexes)
        {
            await _session.SendAsync(packet, cancellationToken, "KitBagSlotIndex");
        }

        await _session.SendAsync(PacketBuilder.SkillUiState(), cancellationToken, "SkillUiState");
        await _session.SendAsync(PacketBuilder.SkillListBootstrap(), cancellationToken, "SkillList");
        await _session.SendAsync(PacketBuilder.EnterComplete(), cancellationToken, "EnterComplete");
        await SendExperienceBoostStatusAsync("enter", cancellationToken);
    }

    private async Task SendExperienceBoostStatusAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        ExperienceBoostState boosts;
        try
        {
            boosts = await _store.GetExperienceBoostStateAsync(
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[status] EXP boost sync failed character={_character.Name} reason={reason}: {ex.Message}");
            return;
        }

        var effects = boosts.ActiveBoosts
            .Select(boost => new ClientStatusEffect(
                checked((uint)boost.StatusId),
                checked((ushort)boost.RemainingSeconds(now))))
            .ToArray();
        await _session.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                effects,
                boosts.TotalBonusBasisPoints / 10_000f),
            cancellationToken,
            "PlayerStatusEffects");
        _registry.RememberExperienceBoostStatus(_session, boosts);
        Console.WriteLine(
            $"[status] EXP boost sync character={_character.Name} reason={reason} count={effects.Length} bonus-bps={boosts.TotalBonusBasisPoints}");
    }

    private async Task SendCurrentTalentBootstrapAsync(string reason, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var skillStates = await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        var talentStates = await _store.GetTalentStatesAsync(_account.Id, _character.Id, cancellationToken);
        await SendTalentBootstrapAsync(skillStates, talentStates, reason, cancellationToken);
    }

    private async Task SendTalentBootstrapAsync(
        IReadOnlyList<SkillState> skillStates,
        IReadOnlyList<TalentState> talentStates,
        string reason,
        CancellationToken cancellationToken,
        bool includeTalentRankList = true,
        bool useCapturedSkillList = false)
    {
        Console.WriteLine(
            $"[talent] bootstrap reason={reason} character={_character?.Name ?? "<none>"} skills={skillStates.Count} talents={talentStates.Count} points={_character?.TalentPoints ?? 0} includeRanks={includeTalentRankList} capturedSkillList={useCapturedSkillList}");

        var skillList = useCapturedSkillList
            ? PacketBuilder.SkillListBootstrap()
            : PacketBuilder.SkillList(skillStates);
        if (skillList.Length > 0)
        {
            await _session.SendAsync(skillList, cancellationToken, "SkillList");
        }

        if (!includeTalentRankList)
        {
            return;
        }

        var talentRankList = PacketBuilder.TalentRankList(talentStates);
        if (talentRankList.Length > 0)
        {
            await _session.SendAsync(talentRankList, cancellationToken, "TalentRankList");
        }

        var talentSkillUnlockList = PacketBuilder.TalentSkillUnlockList(skillStates);
        if (talentSkillUnlockList.Length > 0)
        {
            await _session.SendAsync(talentSkillUnlockList, cancellationToken, "TalentSkillUnlockList");
        }
    }

    private async Task SendTalentRankPacketsAsync(
        IReadOnlyList<SkillState> skillStates,
        IReadOnlyList<TalentState> talentStates,
        string reason,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[talent] rank-list reason={reason} character={_character?.Name ?? "<none>"} talents={talentStates.Count} points={_character?.TalentPoints ?? 0}");

        var talentRankList = PacketBuilder.TalentRankList(talentStates);
        if (talentRankList.Length > 0)
        {
            await _session.SendAsync(talentRankList, cancellationToken, "TalentRankList");
        }

        var talentSkillUnlockList = PacketBuilder.TalentSkillUnlockList(skillStates);
        if (talentSkillUnlockList.Length > 0)
        {
            await _session.SendAsync(talentSkillUnlockList, cancellationToken, "TalentSkillUnlockList");
        }
    }

    private async Task SendMapWorldObjectsAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[world] ignored ClientReady: no active character");
            return;
        }

        var loadedNpcDefinitions = await _store.GetNpcSpawnDefinitionsAsync(_character.CurrentMap, cancellationToken);
        var npcDefinitions = new List<NpcSpawnDefinition>(loadedNpcDefinitions.Count);
        foreach (var npc in loadedNpcDefinitions)
        {
            if (WorldObjectIds.IsReservedForPlayer(npc.ObjectId) ||
                !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(npc.X, npc.Z, out _))
            {
                Console.WriteLine(
                    $"[npc] skipped invalid world object map={_character.CurrentMap} object={npc.ObjectId} key={npc.NpcKey} x={npc.X} z={npc.Z}");
                continue;
            }

            npcDefinitions.Add(npc);
        }

        _mapNpcsByInteractionId.Clear();
        foreach (var npc in npcDefinitions)
        {
            _mapNpcsByInteractionId[npc.InteractionId] = npc;
        }

        var npcObjectIds = npcDefinitions
            .Select(npc => npc.ObjectId)
            .ToHashSet();

        var loadedMonsterDefinitions = await _store.GetCapturedMonsterSpawnsAsync(
            _character.CurrentMap,
            cancellationToken);
        var monsterDefinitions = new List<CapturedMonsterSpawn>(loadedMonsterDefinitions.Count);
        foreach (var monster in loadedMonsterDefinitions)
        {
            try
            {
                monster.Validate(_character.CurrentMap);
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine(
                    $"[mob] skipped invalid captured spawn map={_character.CurrentMap} object={monster.ObjectId}: {ex.Message}");
                continue;
            }

            if (WorldObjectIds.IsReservedForPlayer(monster.ObjectId))
            {
                Console.WriteLine(
                    $"[mob] skipped reserved player object ID map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey}");
                continue;
            }

            if (!WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                    monster.AppearanceX,
                    monster.AppearanceZ,
                    out _))
            {
                Console.WriteLine(
                    $"[mob] skipped out-of-grid appearance map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey} x={monster.AppearanceX} z={monster.AppearanceZ}");
                continue;
            }

            if (npcObjectIds.Contains(monster.ObjectId))
            {
                Console.WriteLine(
                    $"[mob] skipped NPC object-ID collision map={_character.CurrentMap} object={monster.ObjectId} template={monster.TemplateKey}");
                continue;
            }

            monsterDefinitions.Add(monster);
        }

        var monsterRuntimeInitializedAt = DateTimeOffset.UtcNow;
        WorldBossRespawnState? activeWorldBossRespawn = null;
        try
        {
            activeWorldBossRespawn = await _store.GetActiveWorldBossRespawnAsync(
                _character.CurrentMap,
                monsterRuntimeInitializedAt,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] failed loading persisted respawn map={_character.CurrentMap}: {ex.Message}");
            if (WorldBossCatalog.Default.TryGet(_character.CurrentMap, out var worldBoss))
            {
                // A database outage must never make a killed world boss reappear
                // early. Suppress it for this runtime and recover on restart.
                activeWorldBossRespawn = new WorldBossRespawnState(
                    _character.CurrentMap,
                    worldBoss.TemplateKey,
                    DateTimeOffset.MaxValue);
            }
        }

        var runtimeMonsterCount = _registry.InitializeMapMonsters(
            _character.CurrentMap,
            monsterDefinitions,
            monsterRuntimeInitializedAt,
            activeWorldBossRespawn);

        _npcVisibility = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            npcDefinitions,
            npc => npc.ObjectId,
            npc => npc.X,
            npc => npc.Z,
            "NPC");
        Console.WriteLine(
            $"[npc] loaded map definitions character={_character.Name} map={_character.CurrentMap} count={npcDefinitions.Count}");
        Console.WriteLine(
            runtimeMonsterCount > 0
                ? $"[mob] loaded shared map runtime character={_character.Name} map={_character.CurrentMap} count={runtimeMonsterCount}"
                : $"[mob] no captured map definitions character={_character.Name} map={_character.CurrentMap}");
        await RefreshNearbyWorldObjectsAsync("initial", cancellationToken);

        await SendMapPlayersAsync(cancellationToken);
    }

    private async Task HandleNpcDialogOpenAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < 4)
        {
            Console.WriteLine("[npc] dialog open ignored: payload too short");
            return;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload[..4]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine($"[npc] dialog open ignored: unknown npc={npcId} map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        if (!IsHolyStoneArtisan(npc))
        {
            Console.WriteLine($"[npc] dialog open has no implemented script npc={npcId} key={npc.NpcKey}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(npc.InteractionId, HolyStoneDialogIndex, npc.NpcKey),
            cancellationToken,
            "NpcDialogOpenAck");
        Console.WriteLine($"[holy-stone] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
    }

    private void HandleNpcDialogPageRequest(GamePacket packet)
    {
        if (packet.Payload.Length < 4)
        {
            Console.WriteLine("[npc] page request ignored: payload too short");
            return;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload[..4]);
        Console.WriteLine(
            TryResolveMapNpc(npcId, out var npc)
                ? $"[npc] page request npc={npcId} key={npc.NpcKey}"
                : $"[npc] page request ignored: unknown npc={npcId}");
    }

    private async Task HandleNpcFunctionActionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            Console.WriteLine("[holy-stone] action ignored: no active character");
            return;
        }

        if (!TryReadNpcFunctionAction(packet.Payload, out var npcId, out var dialogIndex, out var subId, out var args))
        {
            Console.WriteLine("[holy-stone] action ignored: payload does not match captured NPC function shape");
            return;
        }

        if (!TryResolveMapNpc(npcId, out var npc) || !IsHolyStoneArtisan(npc))
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        Console.WriteLine(
            $"[holy-stone] action npc={npcId} dialog={dialogIndex} subId={subId} args={string.Join(',', args)}");

        if (subId == -1)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, 101, 201, 301, 401, 501, 601, 701),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        if (subId == HolyStoneMenuMount && !HasClientKitBagSlot(args))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, 106, 206, 306, 406),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var operation = subId switch
        {
            HolyStoneMenuMount or 106 or 206 or 306 or 406 => HolyStoneOperation.MountStone,
            HolyStoneMenuRemove => HolyStoneOperation.RemoveStone,
            HolyStoneMenuDrill => HolyStoneOperation.DrillSocket,
            _ => (HolyStoneOperation?)null
        };

        if (operation is null)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, HolyStoneInsufficientFunds),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var targetSlot = FirstClientKitBagSlot(args);
        var stoneSlot = NextClientKitBagSlot(args, targetSlot);
        var destinationSlot = stoneSlot >= 0 ? stoneSlot : -1;
        var socketIndex = SocketIndexFromSubId(subId);
        var updatedCharacter = await _store.ApplyWeaponHolyStoneAsync(
            _account.Id,
            _character.Id,
            operation.Value,
            targetSlot,
            socketIndex,
            stoneSlot,
            destinationSlot,
            cancellationToken);

        var responseSubId = updatedCharacter is null
            ? HolyStoneInsufficientFunds
            : operation.Value switch
            {
                HolyStoneOperation.MountStone => HolyStoneMountSuccess,
                HolyStoneOperation.RemoveStone => HolyStoneRemoveSuccess,
                HolyStoneOperation.DrillSocket => HolyStoneDrillSuccess,
                _ => HolyStoneInsufficientFunds
            };

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(npcId, HolyStoneDialogIndex, responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (updatedCharacter is null)
        {
            return;
        }

        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync($"holy-stone-{operation.Value}", cancellationToken);
        _registry.UpdateCharacter(_session, _character);

        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.EquipmentItemSnapshot(_character, EquipmentSlots.Weapon),
            cancellationToken,
            "EquipmentItemSnapshot");
        foreach (var detailPage in PacketBuilder.KitBagDetailPages(_character))
        {
            await _session.SendAsync(detailPage, cancellationToken, "KitBagDetail");
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync($"holy-stone-{operation.Value}", cancellationToken);
    }

    private bool TryResolveMapNpc(uint interactionId, out NpcSpawnDefinition npc)
    {
        if (_character is not null &&
            _mapNpcsByInteractionId.TryGetValue(interactionId, out var candidate) &&
            candidate.MapId == _character.CurrentMap &&
            _npcVisibility is not null &&
            _npcVisibility.IsVisible(candidate.ObjectId))
        {
            npc = candidate;
            return true;
        }

        npc = default!;
        return false;
    }

    private async Task RefreshNearbyWorldObjectsAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _npcVisibility is null ||
            !_npcVisibility.TryCalculate(
                _character.PositionX,
                _character.PositionZ,
                out var npcDelta))
        {
            return;
        }


        await using var monsterTransition = await _registry.BeginMonsterVisibilityTransitionAsync(
            _session,
            _character.CurrentMap,
            _character.PositionX,
            _character.PositionZ,
            cancellationToken);
        if (monsterTransition is null)
        {
            return;
        }

        var monsterDelta = monsterTransition.Delta;

        var leavingObjectIds = npcDelta.Leaving
            .Concat(monsterDelta.Leaving)
            .Distinct()
            .OrderBy(objectId => objectId)
            .ToArray();
        if (leavingObjectIds.Length > 0)
        {
            await _session.SendAsync(
                PacketBuilder.RemoveWorldObjects(leavingObjectIds),
                cancellationToken,
                "NearbyWorldObjectRemovals");
        }

        if (npcDelta.Entering.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.NpcSpawns(npcDelta.Entering),
                cancellationToken,
                "NearbyNpcSpawns",
                framed: false);
        }

        if (monsterDelta.Entering.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.CapturedMonsterSpawns(
                    monsterDelta.Entering.Select(monster => monster.Appearance).ToArray()),
                cancellationToken,
                "NearbyMonsterSpawns",
                framed: false);

            foreach (var monster in monsterDelta.Entering.Where(monster => monster.IsMoving))
            {
                await _session.SendAsync(
                    PacketBuilder.MonsterMovementStart(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        monster.VelocityX,
                        monster.VelocityY,
                        monster.VelocityZ),
                    cancellationToken,
                    "NearbyMonsterMovementContinuation");
            }
        }

        // Only advance either tracker after the complete remove/spawn transition
        // has been sent, so a failed transition is never recorded as visible.
        _npcVisibility.Commit(npcDelta);
        monsterTransition.Commit();
        if (npcDelta.Entering.Count > 0 ||
            npcDelta.Leaving.Count > 0 ||
            monsterDelta.Entering.Count > 0 ||
            monsterDelta.Leaving.Count > 0 ||
            reason == "initial")
        {
            Console.WriteLine(
                $"[world] visibility reason={reason} character={_character.Name} map={_character.CurrentMap} cell={npcDelta.PlayerCell.X},{npcDelta.PlayerCell.Z} x={_character.PositionX:F2} z={_character.PositionZ:F2} npc-entered={npcDelta.Entering.Count} npc-left={npcDelta.Leaving.Count} mob-entered={monsterDelta.Entering.Count} mob-left={monsterDelta.Leaving.Count}");
        }
    }

    private static bool IsHolyStoneArtisan(NpcSpawnDefinition npc)
    {
        return npc.NpcKey is "Sparta_086" or "Athens_086";
    }

    private async Task<bool> HandleWalkAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null || !UpdateCharacterPositionFromWalk(packet))
        {
            return false;
        }

        await RefreshNearbyWorldObjectsAsync("walk", cancellationToken);
        await PersistCharacterPositionAsync(force: false, cancellationToken);

        return true;
    }

    private async Task HandleReviveAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[revive] ignored request before character enter");
            return;
        }

        if (!ReviveRequest.TryParse(packet.Buffer, out var request))
        {
            Console.WriteLine($"[revive] ignored malformed request len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (_character.CurrentHp > 0)
        {
            Console.WriteLine($"[revive] ignored request for living character={_character.Name}");
            return;
        }

        var previousMap = _character.CurrentMap;
        if (_worldPresenceAnnounced)
        {
            await BroadcastPlayerLeaveAsync(cancellationToken);
        }

        if (_registered)
        {
            _registry.Remove(_session);
            _registered = false;
        }

        _worldPresenceAnnounced = false;
        _clientReadyReceived = false;
        _playerDetailSent = false;
        _postEnterBootstrapSent = false;
        _npcVisibility = null;
        _mapNpcsByInteractionId.Clear();
        _nextBasicAttackAt = DateTimeOffset.MinValue;

        // Currency-backed in-place revival is not implemented yet. Every valid
        // revive button therefore takes the original free-revival path instead
        // of accepting an unpaid premium revive or leaving the player stuck.
        await RestoreFreeRevivalStateAsync(cancellationToken);
        await HandleEnterGameAsync(cancellationToken);
        Console.WriteLine(
            $"[revive] free revival character={_character.Name} request-object={request.PlayerObjectId} requested-type={request.ReviveType} map={previousMap}->{_character.CurrentMap} hp={_character.CurrentHp}/{_character.MaxHp} mp={_character.CurrentMp}/{_character.MaxMp}");
    }

    private async Task RestoreFreeRevivalStateAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        GameDefaults.InitializeStartingLocation(_character);
        lock (_character.VitalsSync)
        {
            _character.CurrentHp = Math.Max(1, _character.MaxHp / 10);
            _character.CurrentMp = Math.Max(0, _character.MaxMp / 10);
            _character.MarkVitalsChanged();
        }
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;

        var accountId = _account?.Id ?? _character.AccountId;
        await _store.SaveCharacterPositionAsync(
            accountId,
            _character.Id,
            _character.CurrentMap,
            _character.PositionX,
            _character.PositionZ,
            cancellationToken);
        int revivedHp;
        int revivedMp;
        long revivedVitalsRevision;
        lock (_character.VitalsSync)
        {
            revivedHp = _character.CurrentHp;
            revivedMp = _character.CurrentMp;
            revivedVitalsRevision = _character.VitalsRevision;
        }

        await _store.SaveCharacterVitalsAsync(
            accountId,
            _character.Id,
            revivedHp,
            revivedMp,
            revivedVitalsRevision,
            cancellationToken);
    }

    private async Task HandleBasicAttackAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[attack] ignored basic attack before character enter");
            return;
        }

        if (_character.CurrentHp <= 0)
        {
            Console.WriteLine($"[attack] ignored basic attack from dead character={_character.Name}");
            return;
        }

        if (!BasicAttackRequest.TryParse(packet.Buffer, out var attack))
        {
            Console.WriteLine($"[attack] ignored malformed basic attack len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (attack.AttackerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected spoofed attacker character={_character.Name} supplied={attack.AttackerObjectId} expected={LocalPlayerObjectId}");
            return;
        }

        if (!_registry.IsMonsterVisibleTo(_session, attack.TargetObjectId) ||
            !_registry.TryGetMonsterSnapshot(_character.CurrentMap, attack.TargetObjectId, out var target) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine($"[attack] rejected unavailable monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!MonsterCombatResolver.IsWithinBasicAttackRange(
                _character.PositionX,
                _character.PositionZ,
                target.X,
                target.Z,
                MonsterCombatResolver.ResolvePlayerBasicAttackRange(target.Definition)))
        {
            Console.WriteLine(
                $"[attack] rejected out-of-range monster character={_character.Name} target={attack.TargetObjectId} player={_character.PositionX:F2},{_character.PositionZ:F2} monster={target.X:F2},{target.Z:F2}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextBasicAttackAt)
        {
            Console.WriteLine($"[attack] rejected cooldown character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        var requestedDamage = MonsterCombatResolver.CalculatePlayerBasicAttack(_character);
        if (!_registry.TryApplyMonsterDamage(
                _character.CurrentMap,
                attack.TargetObjectId,
                requestedDamage,
                _character.Id,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            Console.WriteLine($"[attack] rejected stale monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        _nextBasicAttackAt = now + BasicAttackCooldown;
        var attackSelector = _character.Profession is 2 or 3 ? (byte)5 : (byte)3;
        var selfPacket = PacketBuilder.PhysicalDamage(
            LocalPlayerObjectId,
            0f,
            0f,
            0f,
            attack.TargetObjectId,
            requestedDamage,
            result: attackSelector);
        var casterNotified = true;
        try
        {
            await _session.SendAsync(selfPacket, cancellationToken, "BasicAttackSelf");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[attack] caster notification failed character={_character.Name} target={attack.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(_character.Id);
        var viewers = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            attack.TargetObjectId,
            PacketBuilder.PhysicalDamage(
                worldObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                requestedDamage,
                result: attackSelector),
            cancellationToken,
            _session,
            "BasicAttackWorld");

        if (damageResult.Killed)
        {
            await AwardMonsterKillAsync(damageResult, cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={_character.Name} target={attack.TargetObjectId} resolved={requestedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} caster-notified={casterNotified} viewers={viewers}");
    }

    private async Task HandleSkillCastAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[skill] ignored cast before character enter");
            return;
        }

        if (_character.CurrentHp <= 0)
        {
            Console.WriteLine($"[skill] ignored cast from dead character={_character.Name}");
            return;
        }

        if (!SkillCastRequest.TryParse(packet.Buffer, out var cast))
        {
            Console.WriteLine($"[skill] ignored cast payload too short len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        var castX = float.IsFinite(cast.CasterX) ? cast.CasterX : _character.PositionX;
        var castZ = float.IsFinite(cast.CasterZ) ? cast.CasterZ : _character.PositionZ;
        var learned = await IsSkillLearnedAsync(cast.SkillId, cancellationToken);

        Console.WriteLine(
            $"[skill] cast character={_character.Name} skill={cast.SkillId} learned={learned} caster={cast.CasterObjectId} target={cast.TargetObjectId} x={castX:F2} z={castZ:F2}");
        if (!learned)
        {
            Console.WriteLine(
                $"[skill] rejected unlearned skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        if (cast.SkillId > int.MaxValue ||
            !SkillCombatCatalog.TryGet((int)cast.SkillId, out var combat) ||
            !SkillCombatResolver.IsHostileMonsterSkill(combat))
        {
            Console.WriteLine(
                $"[skill] rejected unsupported combat skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        if (!_registry.IsMonsterVisibleTo(_session, cast.TargetObjectId) ||
            !_registry.TryGetMonsterSnapshot(
                _character.CurrentMap,
                cast.TargetObjectId,
                out var target) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine(
                $"[skill] rejected unavailable monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        if (!SkillCombatResolver.IsWithinRange(
                _character.PositionX,
                _character.PositionZ,
                target.X,
                target.Z,
                combat))
        {
            Console.WriteLine(
                $"[skill] rejected out-of-range monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} player={_character.PositionX:F2},{_character.PositionZ:F2} monster={target.X:F2},{target.Z:F2} range={combat.Distance:F2}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (_character.VitalsSync)
        {
            currentMana = _character.CurrentMp;
            if (currentMana >= manaCost)
            {
                _character.CurrentMp = currentMana - manaCost;
                currentMana = _character.CurrentMp;
                if (manaCost > 0)
                {
                    _character.MarkVitalsChanged();
                }
                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={_character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "SkillManaRejected");
            return;
        }

        var requestedDamage = SkillCombatResolver.CalculateDamage(_character, combat);
        if (requestedDamage == 0 ||
            !_registry.TryApplyMonsterDamage(
                _character.CurrentMap,
                cast.TargetObjectId,
                requestedDamage,
                _character.Id,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            if (manaCost > 0)
            {
                int refundedHp;
                long refundedVitalsRevision;
                lock (_character.VitalsSync)
                {
                    _character.CurrentMp = Math.Min(
                        Math.Max(0, _character.MaxMp),
                        (int)Math.Min(int.MaxValue, (long)_character.CurrentMp + manaCost));
                    _character.MarkVitalsChanged();
                    refundedHp = _character.CurrentHp;
                    currentMana = _character.CurrentMp;
                    refundedVitalsRevision = _character.VitalsRevision;
                }

                try
                {
                    await _store.SaveCharacterVitalsAsync(
                        _account?.Id ?? _character.AccountId,
                        _character.Id,
                        refundedHp,
                        currentMana,
                        refundedVitalsRevision,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[skill] refunded vitals persistence deferred character={_character.Name}: {ex.Message}");
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "SkillManaRefund");
            }

            Console.WriteLine(
                $"[skill] rejected stale monster target character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        var appliedDamage = damageResult.BeforeHealth - damageResult.AfterHealth;
        // The working server reports the resolved hit amount even when it exceeds
        // the monster's remaining HP. Shared runtime health is still clamped at 0.
        var reportedDamage = requestedDamage;
        var targetX = damageResult.Monster.X;
        var targetZ = damageResult.Monster.Z;
        var selfVisual = PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId);
        var selfDamage = PacketBuilder.SkillDamage(
            attackerObjectId: LocalPlayerObjectId,
            targetObjectId: cast.TargetObjectId,
            resultFlags: 1,
            damage: reportedDamage,
            skillId: cast.SkillId,
            targetX: targetX,
            targetZ: targetZ);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            cast.TargetObjectId,
            cast.SkillId,
            targetX,
            targetZ);

        var casterNotified = true;
        try
        {
            await _session.SendAsync(
                selfVisual,
                cancellationToken,
                "SkillCastSelf");
            await _session.SendAsync(
                selfDamage,
                cancellationToken,
                "SkillDamageSelf");
            await _session.SendAsync(
                selfImpact,
                cancellationToken,
                "SkillCastImpactSelf");
            if (manaCost > 0)
            {
                lock (_character.VitalsSync)
                {
                    currentMana = _character.CurrentMp;
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "SkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The hit already changed shared state. Continue notifying the other
            // viewers even if the caster disconnected during its own response.
            casterNotified = false;
            Console.WriteLine(
                $"[skill] caster notification failed character={_character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(_character.Id);
        var visualRecipients = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastVisual(packet.Buffer, worldObjectId),
            cancellationToken,
            _session,
            "SkillCastWorld");
        var damageRecipients = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillDamage(
                attackerObjectId: worldObjectId,
                targetObjectId: cast.TargetObjectId,
                resultFlags: 1,
                damage: reportedDamage,
                skillId: cast.SkillId,
                targetX: targetX,
                targetZ: targetZ),
            cancellationToken,
            _session,
            "SkillDamageWorld");
        var impactRecipients = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                cast.TargetObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "SkillCastImpactWorld");

        if (damageResult.Killed)
        {
            await AwardMonsterKillAsync(damageResult, cancellationToken);
        }

        if (_account is not null)
        {
            try
            {
                int currentHp;
                int currentMp;
                long vitalsRevision;
                lock (_character.VitalsSync)
                {
                    currentHp = _character.CurrentHp;
                    currentMp = _character.CurrentMp;
                    vitalsRevision = _character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    _account.Id,
                    _character.Id,
                    currentHp,
                    currentMp,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database availability must not suppress an already-authoritative
                // shared hit. The in-memory session remains correct and can retry.
                Console.WriteLine(
                    $"[skill] vitals persistence deferred character={_character.Name}: {ex.Message}");
            }
        }

        lock (_character.VitalsSync)
        {
            currentMana = _character.CurrentMp;
        }

        Console.WriteLine(
            $"[skill] damage character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} resolved={reportedDamage} applied={appliedDamage} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} mp={currentMana}/{_character.MaxMp} caster-notified={casterNotified} viewers={Math.Max(visualRecipients, Math.Max(damageRecipients, impactRecipients))}");
    }

    private async Task AwardMonsterKillAsync(
        MonsterDamageResult damageResult,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !damageResult.Killed)
        {
            return;
        }

        var reward = MonsterRewardCatalog.Resolve(damageResult.Monster, _character.Level);
        if (reward.Experience == 0 && reward.TalentExperience == 0)
        {
            await ActivateWorldBossAreaIfApplicableAsync(
                damageResult,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await SendMonsterDeathProgressionAsync(
                damageResult.ObjectId,
                _character.Experience,
                _character.TalentExperience,
                _character.TalentPoints,
                cancellationToken);
            Console.WriteLine(
                $"[reward] no eligible reward character={_character.Name} level={_character.Level} monster={damageResult.ObjectId} tier={damageResult.Monster.Definition.Tier}");
            return;
        }

        var rewardTime = DateTimeOffset.UtcNow;
        ExperienceBoostState experienceBoosts;
        try
        {
            experienceBoosts = await _store.GetExperienceBoostStateAsync(
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                rewardTime,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            experienceBoosts = ExperienceBoostState.Empty;
            Console.WriteLine(
                $"[reward] boost resolution failed character={_character.Name}: {ex.Message}");
        }

        var awardedExperience = experienceBoosts.ApplyTo(reward.Experience);
        await ActivateWorldBossAreaIfApplicableAsync(
            damageResult,
            rewardTime,
            cancellationToken);

        CharacterProgressionResult? progression;
        try
        {
            progression = await _store.ApplyMonsterKillRewardAsync(
                _account.Id,
                _character.Id,
                awardedExperience,
                reward.TalentExperience,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] persistence failed character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            return;
        }

        if (progression is null)
        {
            Console.WriteLine(
                $"[reward] character missing account={_account.Id} character={_character.Id} monster={damageResult.ObjectId}");
            return;
        }

        Console.WriteLine(
            $"[reward] character={_character.Name} base-exp={reward.Experience} awarded-exp={awardedExperience} bonus-bps={experienceBoosts.TotalBonusBasisPoints} boosts={string.Join(',', experienceBoosts.ActiveBoosts.Select(boost => boost.StatusId))}");

        _character.Level = progression.CurrentLevel;
        _character.Experience = progression.CurrentExperience;
        _character.TalentExperience = progression.CurrentTalentExperience;
        _character.TalentPoints = progression.CurrentTalentPoints;

        if (progression.LevelUps.Count > 0)
        {
            try
            {
                var refreshedStats = await _store.GetCharacterStatsAsync(
                    _account.Id,
                    _character.Id,
                    cancellationToken);
                if (refreshedStats is not null)
                {
                    // The killing skill's MP cost is persisted after this reward
                    // sequence. Refresh derived maxima without restoring the
                    // older database vitals and accidentally refunding that cost.
                    lock (_character.VitalsSync)
                    {
                        var currentHp = _character.CurrentHp;
                        var currentMp = _character.CurrentMp;
                        refreshedStats.ApplyTo(_character);
                        _character.CurrentHp = Math.Clamp(currentHp, 0, _character.MaxHp);
                        _character.CurrentMp = Math.Clamp(currentMp, 0, _character.MaxMp);
                        _character.MarkVitalsChanged();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[reward] level-up stat refresh deferred character={_character.Name}: {ex.Message}");
            }
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        foreach (var levelUp in progression.LevelUps)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    levelUp.NextLevelExperience,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                "MonsterKillLevelUp");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    WorldObjectIds.ForPlayer(_character.Id),
                    levelUp.Level,
                    levelUp.NextLevelExperience,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "MonsterKillLevelUpWorld");
        }

        if (progression.ExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.ExperienceGain(
                    progression.ExperienceGained,
                    progression.CurrentExperience),
                cancellationToken,
                "MonsterKillExperience");
            await _session.SendAsync(
                PacketBuilder.PlayerStatusUpdate(_character),
                cancellationToken,
                "MonsterKillProgressionStatus");
        }

        if (progression.TalentExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.TalentExperienceGain(progression.TalentExperienceGained),
                cancellationToken,
                "MonsterKillTalentExperience");
        }

        await SendMonsterDeathProgressionAsync(
            damageResult.ObjectId,
            progression.CurrentExperience,
            progression.CurrentTalentExperience,
            progression.CurrentTalentPoints,
            cancellationToken);

        if (progression.TalentPointsGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerStatusUpdate(_character),
                cancellationToken,
                "MonsterKillTalentPointCarry");
        }

        Console.WriteLine(
            $"[reward] kill character={_character.Name} monster={damageResult.ObjectId} tier={damageResult.Monster.Definition.Tier} level={progression.PreviousLevel}->{progression.CurrentLevel} exp=+{progression.ExperienceGained}->{progression.CurrentExperience}/{progression.NextLevelExperience} talent-exp=+{progression.TalentExperienceGained}->{progression.CurrentTalentExperience} talent-points=+{progression.TalentPointsGained}->{progression.CurrentTalentPoints}");
    }

    private async Task ActivateWorldBossAreaIfApplicableAsync(
        MonsterDamageResult damageResult,
        DateTimeOffset killedAt,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !WorldBossCatalog.Default.IsWorldBoss(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey))
        {
            return;
        }

        var deathToken = $"{_character.CurrentMap}:{damageResult.ObjectId}:{killedAt.UtcTicks}";
        try
        {
            var control = await _store.ActivateWorldBossAreaAsync(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey,
                _character.Camp,
                killedAt,
                deathToken,
                cancellationToken);
            if (control is null)
            {
                return;
            }

            Console.WriteLine(
                $"[world-boss] area-control map={control.MapId} camp={control.ControllingCamp} boss={control.BossTemplateKey} expires={control.ExpiresAt:O}");
            await _registry.SendExperienceBoostStatusesAsync(
                mapId: control.MapId,
                camp: null,
                reason: "world-boss-control",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] area-control activation failed map={_character.CurrentMap} boss={damageResult.Monster.Definition.TemplateKey}: {ex.Message}");
        }
    }

    private async Task SendMonsterDeathProgressionAsync(
        uint monsterObjectId,
        int currentExperience,
        int currentTalentExperience,
        int currentTalentPoints,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                LocalPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            cancellationToken,
            "MonsterKillProgressionRefresh");

        await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                WorldObjectIds.ForPlayer(_character.Id),
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            cancellationToken,
            _session,
            "MonsterKillProgressionRefreshWorld");
    }

    private async Task<bool> IsSkillLearnedAsync(uint skillId, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || skillId > int.MaxValue)
        {
            return false;
        }

        var skills = await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        return skills.Any(skill => skill.SkillId == (int)skillId);
    }

    private async Task BroadcastToCurrentMapAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine($"[world] ignored {Opcodes.Name(packet.Opcode)} broadcast before character enter");
            return;
        }

        var outboundPacket = packet.Opcode == Opcodes.Walk
            ? PacketBuilder.PlayerWorldMovement(packet.Buffer.AsSpan(), WorldObjectIds.ForPlayer(_character.Id))
            : packet.Buffer;
        var excludeSelf = packet.Opcode == Opcodes.Walk ? _session : null;
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            outboundPacket,
            cancellationToken,
            excludeSelf);

        if (packet.Opcode == Opcodes.Walk && recipients > 0)
        {
            Console.WriteLine($"[world] broadcast walk map={_character.CurrentMap} character={_character.Name} object={WorldObjectIds.ForPlayer(_character.Id)} recipients={recipients}");
        }

        if (packet.Opcode == Opcodes.Talk)
        {
            Console.WriteLine($"[world] broadcast talk map={_character.CurrentMap} character={_character.Name} recipients={recipients}");
        }
    }

    private async Task BroadcastEquipmentRefreshAsync(string reason, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var objectId = WorldObjectIds.ForPlayer(_character.Id);
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PlayerWorldSpawn(_character, objectId),
            cancellationToken,
            _session,
            "PlayerWorldSpawnRefresh");

        if (recipients > 0)
        {
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentVisualRefresh(_character, objectId),
                cancellationToken,
                _session,
                "PlayerEquipmentVisualRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerAppearanceExtras(_character, objectId),
                cancellationToken,
                _session,
                "PlayerAppearanceExtrasRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerTitleInfo(_character, objectId),
                cancellationToken,
                _session,
                "PlayerTitleInfoRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerInspectEquipmentStatusBundle(_character, objectId),
                cancellationToken,
                _session,
                "PlayerInspectEquipmentStatusBroadcast",
                framed: false);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerDetailRefreshAck(objectId),
                cancellationToken,
                _session,
                "PlayerInspectDetailRefreshAck");
        }

        if (recipients > 0)
        {
            Console.WriteLine(
                $"[world] broadcast equipment refresh reason={reason} map={_character.CurrentMap} character={_character.Name} object={objectId} recipients={recipients} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");
        }
    }

    private async Task BroadcastPlayerLeaveAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var objectId = WorldObjectIds.ForPlayer(_character.Id);
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.RemoveWorldObjects(objectId),
            cancellationToken,
            _session,
            "WorldObjectRemove");

        if (recipients > 0)
        {
            Console.WriteLine(
                $"[world] broadcast leave map={_character.CurrentMap} character={_character.Name} object={objectId} recipients={recipients}");
        }
    }

    private async Task SendMapPlayersAsync(CancellationToken cancellationToken)
    {
        if (_character is null || _worldPresenceAnnounced)
        {
            return;
        }

        var sentWorldRevisions = new Dictionary<uint, long>();
        var initialPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in initialPlayers)
        {
            await SendVisiblePlayerAsync(player, "initial", cancellationToken);
            sentWorldRevisions[player.ObjectId] = player.WorldRevision;
        }

        if (!_registered)
        {
            _registry.JoinMap(
                _session,
                _account?.Id ?? _character.AccountId,
                _character,
                WorldObjectIds.ForPlayer(_character.Id),
                worldReady: false);
            _registered = true;
        }

        // Reconcile the handoff after joining. A player that entered while the
        // initial snapshot was being sent would otherwise be absent, while one
        // that left before registration would remain as a ghost on this client.
        var currentPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in currentPlayers)
        {
            if (sentWorldRevisions.TryGetValue(player.ObjectId, out var sentRevision) &&
                sentRevision == player.WorldRevision)
            {
                continue;
            }

            await SendVisiblePlayerAsync(player, "reconcile", cancellationToken);
            sentWorldRevisions[player.ObjectId] = player.WorldRevision;
        }

        // Activation is atomic with respect to map joins. If another session
        // became ready during the snapshot send, keep this one hidden until its
        // spawn bundle has also been delivered. A session joining after the
        // successful flip sees this player and announces itself normally.
        while (!_registry.TryMarkWorldReady(
                   _session,
                   sentWorldRevisions,
                   out var unseenPlayers))
        {
            if (unseenPlayers.Count == 0)
            {
                throw new InvalidOperationException("Cannot activate an unregistered world session.");
            }

            foreach (var player in unseenPlayers)
            {
                if (sentWorldRevisions.TryGetValue(player.ObjectId, out var sentRevision) &&
                    sentRevision == player.WorldRevision)
                {
                    continue;
                }

                await SendVisiblePlayerAsync(player, "activation-reconcile", cancellationToken);
                sentWorldRevisions[player.ObjectId] = player.WorldRevision;
            }
        }

        // Position changes deliberately do not invalidate the durable-state
        // barrier. Send one current position after activation so movement that
        // occurred while this session was hidden is not lost. Subsequent movement
        // broadcasts remain serialized with this handoff by the session send lock.
        var activationPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in activationPlayers)
        {
            if (!sentWorldRevisions.ContainsKey(player.ObjectId))
            {
                continue;
            }

            await _session.SendAsync(
                PacketBuilder.PlayerWorldPosition(player.Character, player.ObjectId),
                cancellationToken,
                "VisiblePlayerActivationPosition");
        }

        // Re-snapshot after the position sends. If a player disconnected during
        // the loop, its normal remove may have preceded a queued position packet;
        // this final remove is therefore guaranteed to be the last handoff event.
        var finalPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        var currentObjectIds = finalPlayers
            .Select(player => player.ObjectId)
            .ToHashSet();
        var staleObjectIds = sentWorldRevisions.Keys
            .Where(objectId => !currentObjectIds.Contains(objectId))
            .ToArray();
        if (staleObjectIds.Length > 0)
        {
            await _session.SendAsync(
                PacketBuilder.RemoveWorldObjects(staleObjectIds),
                cancellationToken,
                "VisiblePlayerReconcileRemove");
        }

        var objectId = WorldObjectIds.ForPlayer(_character.Id);
        var spawnRecipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PlayerWorldSpawn(_character, objectId),
            cancellationToken,
            _session);
        if (spawnRecipients > 0)
        {
            Console.WriteLine(
                $"[world] announcing player to map character={_character.Name} object={objectId} wr={_character.WeaponRank}/aura{_character.WeaponAuraEffect} ar={_character.ArmorRank}/aura{_character.ArmorAuraEffect} equipment={PacketBuilder.EnterEquipmentSummary(_character)} recipients={spawnRecipients}");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentVisualRefresh(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerAppearanceExtras(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerTitleInfo(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerWorldPosition(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerStatusUpdate(_character, objectId),
                cancellationToken,
                _session,
                "VisiblePlayerStatus");
        }

        _worldPresenceAnnounced = true;
        Console.WriteLine(
            $"[world] player presence map={_character.CurrentMap} character={_character.Name} object={objectId} receivedExisting={currentObjectIds.Count} announcedTo={spawnRecipients}");
    }

    private async Task SendVisiblePlayerAsync(
        GameSessionContext player,
        string phase,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await RefreshCharacterStatsAsync(player.Character, player.AccountId, "visible-player", cancellationToken);
        Console.WriteLine(
            $"[world] sending existing player phase={phase} to={_character.Name} existing={player.CharacterName} object={player.ObjectId} x={player.Character.PositionX:F2} z={player.Character.PositionZ:F2} wr={player.Character.WeaponRank}/aura{player.Character.WeaponAuraEffect} ar={player.Character.ArmorRank}/aura{player.Character.ArmorAuraEffect} equipment={PacketBuilder.EnterEquipmentSummary(player.Character)}");
        await _session.SendAsync(
            PacketBuilder.PlayerWorldSpawn(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerSpawn");
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerEquipment");
        await _session.SendAsync(
            PacketBuilder.PlayerAppearanceExtras(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerAppearanceExtras");
        await _session.SendAsync(
            PacketBuilder.PlayerTitleInfo(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerTitleInfo");
        await _session.SendAsync(
            PacketBuilder.PlayerWorldPosition(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerPosition");
        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerStatus");
    }

    private async Task HandlePlayerDetailRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[game] ignored PlayerDetailRequest: no active character");
            return;
        }

        var requestedA = request.Payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload[..4])
            : 0;
        var requestedB = request.Payload.Length >= 8
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.Slice(4, 4))
            : 0;
        await RefreshActiveCharacterStatsAsync("player-detail", cancellationToken);
        var packet = PacketBuilder.PlayerDetail(_character);
        if (packet.Length == 0)
        {
            Console.WriteLine($"[game] ignored PlayerDetailRequest: no detail template character={_character.Name}");
            return;
        }

        Console.WriteLine(
            $"[game] sending self player detail character={_character.Name} requestA={requestedA} requestB={requestedB} level={_character.Level} bytes={packet.Length}");
        await _session.SendAsync(packet, cancellationToken, "PlayerDetail", framed: false);
        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        _playerDetailSent = true;
        await SendPostEnterBootstrapAsync(cancellationToken);
    }

    private async Task SendPostEnterBootstrapAsync(CancellationToken cancellationToken)
    {
        if (_postEnterBootstrapSent
            || !_clientReadyReceived
            || !_playerDetailSent
            || _account is null
            || _character is null)
        {
            return;
        }

        _postEnterBootstrapSent = true;

        var enterSyncPackets = await _store.GetEnterSyncPacketsAsync(cancellationToken);
        foreach (var packet in enterSyncPackets)
        {
            await _session.SendAsync(packet, cancellationToken, "SynGameData");
        }

        await SendMapWorldObjectsAsync(cancellationToken);

        var skillStates = await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        var talentStates = await _store.GetTalentStatesAsync(_account.Id, _character.Id, cancellationToken);
        await _session.SendAsync(PacketBuilder.PlayerStatusUpdate(_character), cancellationToken, "PlayerStatusUpdate");
        await SendTalentRankPacketsAsync(skillStates, talentStates, "post-enter", cancellationToken);
        await _session.SendAsync(PacketBuilder.PlayerExtendedStatus(_character), cancellationToken, "PlayerExtendedStatus");
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(0), cancellationToken, "PlayerUnknown10098");
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(1), cancellationToken, "PlayerUnknown10098");
        var skillList = PacketBuilder.SkillList(skillStates);
        if (skillList.Length > 0)
        {
            await _session.SendAsync(skillList, cancellationToken, "SkillList");
        }
    }

    private async Task HandlePlayerInspectRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[inspect] ignored PlayerInspectRequest: no active character");
            return;
        }

        var requestedObjectId = request.Payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload[..4])
            : 0;
        var requestedName = PacketText.ReadFixedAscii(request.Payload, 4, 32);
        if (!TryResolveMapPlayer(requestedObjectId, requestedName, out var target))
        {
            Console.WriteLine(
                $"[inspect] target not found requester={_character.Name} object={requestedObjectId} name={requestedName}");
            return;
        }

        var inspectDetailObjectId = target.ObjectId;
        await RefreshCharacterStatsAsync(target.Character, target.AccountId, "inspect-target", cancellationToken);
        Console.WriteLine(
            $"[inspect] sending target equipment requester={_character.Name} target={target.CharacterName} targetObject={target.ObjectId} equipment={PacketBuilder.EnterEquipmentSummary(target.Character)}");
        await _session.SendAsync(
            PacketBuilder.PlayerInspectEquipmentStatusBundle(target.Character, inspectDetailObjectId),
            cancellationToken,
            "PlayerInspectEquipmentStatusBundle",
            framed: false);
        await _session.SendAsync(
            PacketBuilder.PlayerInspectComplete(),
            cancellationToken,
            "PlayerInspectComplete");
    }

    private async Task HandlePlayerInspectVisualRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[inspect] ignored PlayerInspectVisualRequest: no active character");
            return;
        }

        var requestedObjectId = request.Payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload[..4])
            : 0;
        if (!TryResolveMapPlayer(requestedObjectId, string.Empty, out var target))
        {
            Console.WriteLine($"[inspect] visual target not found requester={_character.Name} object={requestedObjectId}");
            return;
        }

        await SendPlayerVisualBundleAsync(target, cancellationToken, "PlayerInspectVisual");
    }

    private async Task SendPlayerVisualBundleAsync(
        GameSessionContext target,
        CancellationToken cancellationToken,
        string labelPrefix)
    {
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(target.Character, target.ObjectId),
            cancellationToken,
            $"{labelPrefix}Equipment");
        await _session.SendAsync(
            PacketBuilder.PlayerAppearanceExtras(target.Character, target.ObjectId),
            cancellationToken,
            $"{labelPrefix}AppearanceExtras");
        await _session.SendAsync(
            PacketBuilder.PlayerTitleInfo(target.Character, target.ObjectId),
            cancellationToken,
            $"{labelPrefix}TitleInfo");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(target.ObjectId),
            cancellationToken,
            $"{labelPrefix}RefreshAck");
    }

    private bool TryResolveMapPlayer(uint objectId, string characterName, out GameSessionContext target)
    {
        target = default!;
        if (_character is null)
        {
            return false;
        }

        if (objectId != 0
            && _registry.TryGetMapSessionByObjectId(_character.CurrentMap, objectId, _session, out target))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            foreach (var player in _registry.GetMapSessions(_character.CurrentMap, _session))
            {
                if (!string.Equals(player.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target = player;
                return true;
            }
        }

        return false;
    }

    private Task RefreshActiveCharacterStatsAsync(string reason, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return Task.CompletedTask;
        }

        var accountId = _account?.Id ?? _character.AccountId;
        return RefreshCharacterStatsAsync(_character, accountId, reason, cancellationToken);
    }

    private async Task RefreshCharacterStatsAsync(
        GameCharacter character,
        int accountId,
        string reason,
        CancellationToken cancellationToken)
    {
        var stats = accountId > 0
            ? await _store.GetCharacterStatsAsync(accountId, character.Id, cancellationToken)
            : CharacterStats.FromCharacter(character);

        if (stats is null)
        {
            Console.WriteLine($"[stats] missing character={character.Name} id={character.Id} account={accountId} reason={reason}");
            return;
        }

        stats.ApplyTo(character);
        Console.WriteLine($"[stats] refreshed reason={reason} character={character.Name} {stats.ToLogSummary()}");
    }

    private bool UpdateCharacterPositionFromWalk(GamePacket packet)
    {
        if (_character is null || packet.Payload.Length < 12)
        {
            return false;
        }

        var positionX = BinaryPrimitives.ReadSingleLittleEndian(packet.Payload.Slice(4, 4));
        var positionZ = BinaryPrimitives.ReadSingleLittleEndian(packet.Payload.Slice(8, 4));
        if (!WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(positionX, positionZ, out _))
        {
            Console.WriteLine(
                $"[world] ignored invalid walk position character={_character.Name} x={positionX} z={positionZ}");
            return false;
        }

        _character.PositionX = positionX;
        _character.PositionZ = positionZ;
        _positionDirty = true;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        return true;
    }

    private async Task PersistCharacterPositionAsync(bool force, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !_positionDirty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastPositionPersistUtc < PositionPersistInterval)
        {
            return;
        }

        try
        {
            await _store.SaveCharacterPositionAsync(
                _account.Id,
                _character.Id,
                _character.CurrentMap,
                _character.PositionX,
                _character.PositionZ,
                cancellationToken);
            _positionDirty = false;
            _lastPositionPersistUtc = now;
            Console.WriteLine(
                $"[world] saved position character={_character.Name} map={_character.CurrentMap} x={_character.PositionX:F2} z={_character.PositionZ:F2} force={force}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[world] failed to save position character={_character.Name}: {ex.Message}");
        }
    }

    private async Task HandleStorageItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] StorageItem ignored: no active character");
            return;
        }

        if (TryReadStorageItemUnequip(packet.Payload, out var equipmentSlot, out var destinationSlot))
        {
            await HandleUnequipItemAsync(equipmentSlot, destinationSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemDelete(packet.Payload, out var deletedSlot))
        {
            await HandleDeleteKitBagItemAsync(deletedSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemKitBagMove(packet.Payload, out var moveSourceSlot, out var moveDestinationSlot))
        {
            await HandleMoveKitBagItemAsync(moveSourceSlot, moveDestinationSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemEquip(packet.Payload, out var sourceSlot, out var clientEquipmentSlot))
        {
            await HandleEquipItemAsync(sourceSlot, clientEquipmentSlot, itemIdHint: 0, cancellationToken);
            return;
        }

        Console.WriteLine("[equip-re] StorageItem ignored: payload does not match known equip/unequip shapes");
    }

    private async Task HandleBreakItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] BreakItem ignored: no active character");
            return;
        }

        if (!TryReadBreakItemEquip(packet.Payload, out var sourceSlot))
        {
            if (!TryResolveEquipSourceFromLastItemInfo(out sourceSlot, out var itemIdHint))
            {
                Console.WriteLine("[equip-re] BreakItem ignored: payload does not match captured bag-to-equipment shape and no recent item info is available");
                return;
            }

            Console.WriteLine($"[equip-re] BreakItem equip resolved from recent item info sourceSlot={sourceSlot} item={itemIdHint}");
            await HandleEquipItemAsync(sourceSlot, requestedEquipmentSlot: -1, itemIdHint: itemIdHint, cancellationToken);
            return;
        }

        await HandleEquipItemAsync(sourceSlot, requestedEquipmentSlot: -1, itemIdHint: 0, cancellationToken);
    }

    private async Task HandleUseOrEquipAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (!TryReadTalentUpgrade(packet.Payload, out var talentId, out var clientRank, out var clientTalentPoints))
        {
            Console.WriteLine("[talent] UseOrEquip ignored: payload does not match captured talent-upgrade shape");
            return;
        }

        if (_account is null || _character is null)
        {
            Console.WriteLine("[talent] upgrade ignored: no active character");
            return;
        }

        var result = await _store.UpgradeTalentAsync(
            _account.Id,
            _character.Id,
            talentId,
            clientRank,
            clientTalentPoints,
            cancellationToken);

        if (result is null)
        {
            Console.WriteLine(
                $"[talent] upgrade failed character={_character.Name} talent={talentId} clientRank={clientRank} clientPoints={clientTalentPoints}");
            return;
        }

        _character = result.Character;
        await RefreshActiveCharacterStatsAsync("talent-upgrade", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        Console.WriteLine(
            $"[talent] upgraded character={_character.Name} talent={result.TalentId} rank={result.NewRank} cost={result.Cost} remaining={result.RemainingTalentPoints} value={result.DisplayValue}");

        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.TalentUpgradeAck(result),
            cancellationToken,
            "TalentUpgradeAck");
    }

    private async Task HandleBagItemActionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] BagItemAction ignored: no active character");
            return;
        }

        if (!TryReadBagItemAction(packet.Payload, out var sourceSlot, out var itemId))
        {
            Console.WriteLine("[equip-re] BagItemAction ignored: payload does not match captured bag-to-equipment shape");
            return;
        }

        if (TryConsumeUnequipFollowup(sourceSlot, itemId))
        {
            Console.WriteLine(
                $"[equip-re] BagItemAction unequip follow-up acknowledged character={_character.Name} sourceSlot={sourceSlot} item={itemId}");
            await _session.SendAsync(
                PacketBuilder.BagItemActionAck(packet.Buffer),
                cancellationToken,
                "BagItemActionAck");
            return;
        }

        _lastItemInfo = new PendingItemInfo(sourceSlot, itemId, DateTime.UtcNow);
        Console.WriteLine($"[equip-re] BagItemAction remembered pending equip source character={_character.Name} sourceSlot={sourceSlot} item={itemId}");
        await _session.SendAsync(
            PacketBuilder.BagItemActionAck(packet.Buffer),
            cancellationToken,
            "BagItemActionInspectAck");
    }

    private void HandleItemInfoRequest(GamePacket packet)
    {
        LogInventoryPacket(packet);

        if (TryReadItemInfoRequest(packet.Payload, out var sourceSlot, out var itemId))
        {
            _lastItemInfo = new PendingItemInfo(sourceSlot, itemId, DateTime.UtcNow);
            Console.WriteLine($"[equip-re] ItemInfoRequest sourceSlot={sourceSlot} item={itemId}");
            return;
        }

        Console.WriteLine("[equip-re] ItemInfoRequest ignored: payload does not match captured kitbag item-info shape");
    }

    private async Task HandleUnequipItemAsync(int equipmentSlot, int destinationSlot, CancellationToken cancellationToken)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot))
        {
            Console.WriteLine($"[equip-re] StorageItem unequip ignored: unsupported slot={equipmentSlot} destination={destinationSlot}");
            return;
        }

        if (_account is null || _character is null)
        {
            return;
        }

        var previousItemId = EquipmentSlots.GetItemId(_character.Equipment, _character.Profession, equipmentSlot);
        var updatedCharacter = await _store.MoveEquipmentToKitBagAsync(
            _account.Id,
            _character.Id,
            equipmentSlot,
            destinationSlot,
            cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem unequip failed: character={_character.Name} id={_character.Id} slot={equipmentSlot}");
            return;
        }

        if (previousItemId != 0
            && EquipmentSlots.GetItemId(updatedCharacter.Equipment, updatedCharacter.Profession, equipmentSlot) == previousItemId)
        {
            _character = updatedCharacter;
            Console.WriteLine(
                $"[equip-re] StorageItem unequip did not move item: character={_character.Name} slot={equipmentSlot} item={previousItemId} destination={destinationSlot}");
            return;
        }

        var actualDestinationSlot = ResolveMovedKitBagDestination(updatedCharacter, destinationSlot, previousItemId);
        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync("unequip", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        _pendingUnequipFollowup = previousItemId == 0
            ? null
            : new PendingUnequipFollowup(actualDestinationSlot, previousItemId, DateTime.UtcNow);

        var clientEquipmentSlot = PacketBuilder.ToClientEquipmentSlot(equipmentSlot);
        Console.WriteLine(
            $"[equip-re] unequipped character={_character.Name} slot={equipmentSlot} clientSlot={clientEquipmentSlot} previousItem={previousItemId} destination={actualDestinationSlot} requestedDestination={destinationSlot} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");

        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.StorageItemUnequipToKitBag(clientEquipmentSlot, actualDestinationSlot),
            cancellationToken,
            "StorageItemUnequipAck");
        if (equipmentSlot is not (EquipmentSlots.Ring1 or EquipmentSlots.Ring2))
        {
            await _session.SendAsync(
                PacketBuilder.EquipmentItemClearSnapshot(equipmentSlot),
                cancellationToken,
                "EquipmentItemClearSnapshot");
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync("unequip", cancellationToken);
    }

    private static int ResolveMovedKitBagDestination(GameCharacter character, int requestedDestinationSlot, uint itemId)
    {
        if (itemId == 0)
        {
            return requestedDestinationSlot;
        }

        if (requestedDestinationSlot is >= 0 and < 96
            && KitBagSlots.GetItemId(character.KitBag, requestedDestinationSlot) == itemId)
        {
            return requestedDestinationSlot;
        }

        for (var slot = 0; slot < 96; slot++)
        {
            if (KitBagSlots.GetItemId(character.KitBag, slot) == itemId)
            {
                return slot;
            }
        }

        return requestedDestinationSlot;
    }

    private bool TryConsumeUnequipFollowup(int sourceSlot, uint itemId)
    {
        if (_pendingUnequipFollowup is not { } pending)
        {
            return false;
        }

        if (DateTime.UtcNow - pending.CreatedUtc > PendingUnequipFollowupTtl)
        {
            _pendingUnequipFollowup = null;
            return false;
        }

        if (pending.DestinationSlot != sourceSlot || pending.ItemId != itemId)
        {
            return false;
        }

        _pendingUnequipFollowup = null;
        return true;
    }

    private async Task HandleEquipItemAsync(
        int sourceSlot,
        int requestedEquipmentSlot,
        uint itemIdHint,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var kitBagItemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        var effectiveItemIdHint = itemIdHint != 0 ? itemIdHint : kitBagItemId;
        var updatedCharacter = await _store.MoveKitBagToEquipmentAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            requestedEquipmentSlot,
            cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip failed: character={_character.Name} id={_character.Id} sourceSlot={sourceSlot}");
            return;
        }

        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync("equip", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        _lastItemInfo = null;
        var equippedSlot = ResolveEquippedSlotForAck(_character, requestedEquipmentSlot, effectiveItemIdHint);
        Console.WriteLine(
            $"[equip-re] equipped character={_character.Name} sourceSlot={sourceSlot} requestedTarget={requestedEquipmentSlot} equippedSlot={equippedSlot} itemHint={effectiveItemIdHint} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");

        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        if (EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot) && EquipmentSlots.IsEquipmentSlot(equippedSlot))
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemEquipFromKitBag(sourceSlot, PacketBuilder.ToClientEquipmentSlot(equippedSlot)),
                cancellationToken,
                "StorageItemEquipAck");
        }

        var snapshot = PacketBuilder.KitBagItemSnapshot(_character, sourceSlot);
        if (snapshot.Length > 0)
        {
            await _session.SendAsync(
                snapshot,
                cancellationToken,
                "EquipmentItemSnapshot");
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync("equip", cancellationToken);
    }

    private async Task HandleMoveKitBagItemAsync(int sourceSlot, int destinationSlot, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (sourceSlot is < 0 or >= 96 || destinationSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem kitbag move ignored: unsupported source={sourceSlot} destination={destinationSlot}");
            return;
        }

        var updatedCharacter = await _store.MoveKitBagItemAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            destinationSlot,
            cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem kitbag move failed: character={_character.Name} id={_character.Id} source={sourceSlot} destination={destinationSlot}");
            return;
        }

        _character = updatedCharacter;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        Console.WriteLine(
            $"[equip-re] kitbag move character={_character.Name} source={sourceSlot} destination={destinationSlot}");

        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagMove(sourceSlot, destinationSlot),
            cancellationToken,
            "StorageItemKitBagMoveAck");
    }

    private async Task HandleDeleteKitBagItemAsync(int sourceSlot, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var itemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (itemId == 0)
        {
            Console.WriteLine($"[inventory] kitbag delete ignored: empty source={sourceSlot}");
            return;
        }

        var updatedCharacter = await _store.DeleteKitBagItemAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            cancellationToken);

        if (updatedCharacter is null
            || KitBagSlots.GetItemId(updatedCharacter.KitBag, sourceSlot) == itemId)
        {
            Console.WriteLine(
                $"[inventory] kitbag delete failed: character={_character.Name} id={_character.Id} source={sourceSlot} item={itemId}");
            return;
        }

        _character = updatedCharacter;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        if (_lastItemInfo is { SourceSlot: var rememberedSlot } && rememberedSlot == sourceSlot)
        {
            _lastItemInfo = null;
        }

        Console.WriteLine(
            $"[inventory] deleted kitbag item character={_character.Name} source={sourceSlot} item={itemId}");
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(sourceSlot),
            cancellationToken,
            "StorageItemKitBagDeleteAck");
    }

    private bool TryResolveEquipSourceFromLastItemInfo(out int sourceSlot, out uint itemId)
    {
        sourceSlot = 0;
        itemId = 0;

        if (_lastItemInfo is not { } itemInfo)
        {
            return false;
        }

        if (DateTime.UtcNow - itemInfo.CreatedUtc > LastItemInfoTtl)
        {
            _lastItemInfo = null;
            return false;
        }

        sourceSlot = itemInfo.SourceSlot;
        itemId = itemInfo.ItemId;
        return true;
    }

    private static bool TryReadNpcFunctionAction(
        ReadOnlySpan<byte> payload,
        out uint npcId,
        out int dialogIndex,
        out int subId,
        out int[] args)
    {
        npcId = 0;
        dialogIndex = 0;
        subId = 0;
        args = [];

        if (payload.Length < 16)
        {
            return false;
        }

        npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        subId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));

        var count = Math.Max(0, (payload.Length - 16) / 4);
        args = new int[count];
        for (var i = 0; i < count; i++)
        {
            args[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16 + (i * 4), 4));
        }

        return true;
    }

    private static bool HasClientKitBagSlot(IReadOnlyList<int> args)
    {
        return args.Any(arg => DecodeClientKitBagSlot(arg) >= 0);
    }

    private static int FirstClientKitBagSlot(IReadOnlyList<int> args)
    {
        foreach (var arg in args)
        {
            var slot = DecodeClientKitBagSlot(arg);
            if (slot >= 0)
            {
                return slot;
            }
        }

        return -1;
    }

    private static int NextClientKitBagSlot(IReadOnlyList<int> args, int firstSlot)
    {
        foreach (var arg in args)
        {
            var slot = DecodeClientKitBagSlot(arg);
            if (slot >= 0 && slot != firstSlot)
            {
                return slot;
            }
        }

        return -1;
    }

    private static int DecodeClientKitBagSlot(int value)
    {
        if (value is >= 100 and < 196)
        {
            return value - 100;
        }

        if (value is >= 0 and < 96)
        {
            return value;
        }

        return -1;
    }

    private static int SocketIndexFromSubId(int subId)
    {
        return subId switch
        {
            106 => 0,
            206 => 1,
            306 => 2,
            406 => 3,
            _ => -1
        };
    }

    private static void LogReceived(GamePacket packet)
    {
        Console.WriteLine(
            $"[game] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} hex={packet.ToHexPreview(32)}");
    }

    private static void LogInventoryPacket(GamePacket packet)
    {
        var payload = packet.Payload;
        Console.WriteLine(
            $"[equip-re] {Opcodes.Name(packet.Opcode)} payloadLen={payload.Length} bytes={FormatBytes(payload)} u16={FormatUInt16(payload)} u32={FormatUInt32(payload)}");
    }

    private static int ResolveEquippedSlotForAck(GameCharacter character, int requestedEquipmentSlot, uint itemIdHint)
    {
        if (EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot))
        {
            return requestedEquipmentSlot;
        }

        if (itemIdHint == 0)
        {
            return -1;
        }

        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Stylish; slot++)
        {
            if (EquipmentSlots.GetItemId(character.Equipment, character.Profession, slot) == itemIdHint)
            {
                return slot;
            }
        }

        return -1;
    }

    private static string FormatBytes(ReadOnlySpan<byte> payload)
    {
        return payload.Length == 0 ? "[]" : "[" + string.Join(",", payload.ToArray().Select(b => b.ToString())) + "]";
    }

    private static string FormatUInt16(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return "[]";
        }

        var values = new List<ushort>();
        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload[i..(i + 2)]));
        }

        return "[" + string.Join(",", values) + "]";
    }

    private static string FormatUInt32(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return "[]";
        }

        var values = new List<uint>();
        for (var i = 0; i + 3 < payload.Length; i += 4)
        {
            values.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload[i..(i + 4)]));
        }

        return "[" + string.Join(",", values) + "]";
    }

    private static bool TryReadStorageItemUnequip(
        ReadOnlySpan<byte> payload,
        out int equipmentSlot,
        out int destinationSlot)
    {
        equipmentSlot = 0;
        destinationSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        equipmentSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var emptyMarker = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        destinationSlot = (destinationPage * 24) + destinationIndex;
        return emptyMarker == ushort.MaxValue;
    }

    private static bool TryReadStorageItemKitBagMove(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out int destinationSlot)
    {
        sourceSlot = 0;
        destinationSlot = 0;

        if (payload.Length < 16)
        {
            return false;
        }

        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        var marker1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
        var marker2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));

        if (marker1 != ushort.MaxValue || marker2 != ushort.MaxValue)
        {
            return false;
        }

        if (sourcePage >= 4 || destinationPage >= 4 || sourceIndex >= 24 || destinationIndex >= 24)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        destinationSlot = (destinationPage * 24) + destinationIndex;
        return true;
    }

    internal static bool TryReadStorageItemDelete(ReadOnlySpan<byte> payload, out int sourceSlot)
    {
        sourceSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));

        if (sourcePage >= 4
            || sourceIndex >= 24
            || destinationPage != ushort.MaxValue
            || destinationIndex != ushort.MaxValue)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        return true;
    }

    private static bool TryReadStorageItemEquip(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out int clientEquipmentSlot)
    {
        sourceSlot = 0;
        clientEquipmentSlot = 0;

        if (payload.Length < 16)
        {
            return false;
        }

        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var targetPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        clientEquipmentSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        var marker1 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
        var marker2 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));

        if (marker1 != ushort.MaxValue || marker2 != ushort.MaxValue || targetPage != 0)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        return sourcePage < 4 && sourceIndex < 24;
    }

    private static bool TryReadBreakItemEquip(
        ReadOnlySpan<byte> payload,
        out int sourceSlot)
    {
        sourceSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        var marker = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        if (marker == 0xFFFFFF00 || BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) == LocalPlayerObjectId)
        {
            var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
            var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
            if (sourcePage >= 4 || sourceIndex >= 24)
            {
                return false;
            }

            sourceSlot = (sourcePage * 24) + sourceIndex;
            return true;
        }

        if (!TryResolvePackedBagSlot(marker, out sourceSlot))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolvePackedBagSlot(uint marker, out int sourceSlot)
    {
        sourceSlot = 0;
        var sourcePage = (int)(marker & 0xFFFF);
        var sourceIndex = (int)((marker >> 16) & 0xFFFF);
        if (sourcePage >= 4 || sourceIndex >= 24)
        {
            return false;
        }

        sourceSlot = (sourcePage * 24) + sourceIndex;
        return true;
    }

    private static bool TryReadBagItemAction(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out uint itemId)
    {
        sourceSlot = 0;
        itemId = 0;

        if (payload.Length < 20)
        {
            return false;
        }

        sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12, 4));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        return sourceSlot is >= 0 and < 96 && itemId != 0;
    }

    internal static bool TryReadTalentUpgrade(
        ReadOnlySpan<byte> payload,
        out int talentId,
        out int clientRank,
        out int clientTalentPoints)
    {
        talentId = 0;
        clientRank = 0;
        clientTalentPoints = 0;

        if (payload.Length != 24)
        {
            return false;
        }

        talentId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        clientRank = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8, 4));
        clientTalentPoints = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4));
        return talentId >= 0 && clientRank >= 0 && clientTalentPoints >= 0;
    }

    private static bool TryReadItemInfoRequest(
        ReadOnlySpan<byte> payload,
        out int sourceSlot,
        out uint itemId)
    {
        sourceSlot = 0;
        itemId = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        sourceSlot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        return sourceSlot is >= 0 and < 96 && itemId != 0;
    }

    private static byte ReadByte(ReadOnlySpan<byte> buffer, int offset, byte fallback)
    {
        return offset >= 0 && offset < buffer.Length ? buffer[offset] : fallback;
    }

    private sealed record PendingUnequipFollowup(int DestinationSlot, uint ItemId, DateTime CreatedUtc);

    private sealed record PendingItemInfo(int SourceSlot, uint ItemId, DateTime CreatedUtc);
}
