using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
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
    private static readonly TimeSpan ForgeSelectionTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PositionPersistInterval = TimeSpan.FromSeconds(2);
    // The client cadence is 1500 ms. A 25 ms allowance prevents a legitimate
    // swing from being discarded by timer/socket scheduling jitter.
    private static readonly TimeSpan BasicAttackCooldown = TimeSpan.FromMilliseconds(1475);

    private readonly ClientSession _session;
    private readonly IGameStore _store;
    private readonly GameSessionRegistry _registry;
    private readonly DeveloperCommandOptions _developerCommands;
    private GameAccount? _account;
    private GameCharacter? _character;
    private PendingUnequipFollowup? _pendingUnequipFollowup;
    private GearEnhancerSelectionContext? _gearEnhancerSelectionContext;
    private int? _gearMentorOperationPageSubId;
    private ForgeSlotSelection? _forgeEquipment;
    private ForgeSlotSelection? _forgePrimaryMaterial;
    private readonly ForgeOddsReservationSet _forgeOddsMaterials = new();
    private int? _forgeAccountId;
    private int? _forgeCharacterId;
    private long _forgeSelectionStartedTimestamp;
    private bool _registered;
    private bool _accountSessionRegistered;
    private bool _worldPresenceAnnounced;
    private bool _clientReadyReceived;
    private bool _playerDetailSent;
    private bool _enterUiReadyReceived;
    private bool _postEnterBootstrapSent;
    private DateTime _lastPositionPersistUtc = DateTime.MinValue;
    private DateTimeOffset _nextBasicAttackAt = DateTimeOffset.MinValue;
    private readonly Dictionary<uint, DateTimeOffset> _nextSkillCastAt = [];
    private bool _positionDirty;
    private readonly Dictionary<uint, NpcSpawnDefinition> _mapNpcsByInteractionId = new();
    private WorldSectorVisibilityTracker<NpcSpawnDefinition>? _npcVisibility;

    public GameClientHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        DeveloperCommandOptions? developerCommands = null)
    {
        _session = session;
        _store = store;
        _registry = registry;
        _developerCommands = developerCommands ?? new DeveloperCommandOptions();
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
            ClearGearEnhancerSelection();
            try
            {
                await _registry.FinishProgressionBoostOnlineSessionAsync(
                    _session,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[status] failed saving final online boost interval: {ex.Message}");
            }

            try
            {
                await _registry.FinishZodiacOnlineSessionAsync(
                    _session,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[zodiac] failed saving final online interval: {ex.Message}");
            }

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

            // Also clears a status state preserved across a revive if re-entry
            // failed before the session could rejoin the world registry.
            _registry.RemovePlayerStatusState(_session);

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
                if (!await HandleDeveloperItemCommandAsync(packet, cancellationToken))
                {
                    await BroadcastToCurrentMapAsync(packet, cancellationToken);
                }

                break;
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
            case Opcodes.ForgeSelection:
                HandleForgeSelection(packet);
                break;
            case Opcodes.ForgeStart:
                await HandleForgeStartAsync(packet, cancellationToken);
                break;
            case Opcodes.ForgeCancel:
                // The stock Gear Mentor emits this ordinary-forge cancel when
                // gear is unequipped into the bag while its dialog is open.
                // Its subsequent 10193 item selections still belong to the
                // active Gear Mentor workflow, so only ordinary forge state is
                // invalidated here.
                ClearForgeSelection();
                break;
            case Opcodes.ForgeReplacementSelection:
            case Opcodes.ForgeReplacementAction:
                // Replacement mode is not implemented. It shares the forge UI,
                // so entering it must invalidate any ordinary-forge batch.
                ClearForgeSelection();
                Console.WriteLine(
                    $"[forge] ignored unsupported {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode}");
                break;
            case Opcodes.NpcDialogOpen:
                await HandleNpcDialogOpenAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcDialogPageRequest:
                await HandleNpcDialogPageRequestAsync(packet, cancellationToken);
                break;
            case Opcodes.NpcFunctionAction:
                await HandleNpcFunctionActionAsync(packet, cancellationToken);
                break;
            case Opcodes.GearEnhancerItemSelection:
                HandleGearEnhancerItemSelection(packet);
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
            case Opcodes.Zodiac:
                await HandleZodiacAsync(packet, cancellationToken);
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
            case Opcodes.EnterUiReady:
                _enterUiReadyReceived = true;
                Console.WriteLine($"[game] EnterUiReady character={_character?.Name ?? "<none>"}");
                await SendPostEnterBootstrapAsync(cancellationToken);
                break;
            case Opcodes.GameServerReady:
            case Opcodes.GameServerInfo:
            case Opcodes.PlayerInspectFollowup:
            case 10192:
                Console.WriteLine($"[game] ignored {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode}");
                break;
            default:
                Console.WriteLine(
                    $"[game] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                break;
        }
    }

    private void HandleForgeSelection(GamePacket packet)
    {
        if (_account is null ||
            _character is null ||
            !ForgeItemSelectionPacket.TryParse(packet.Payload, out var selection) ||
            selection.RequestMode != ForgeItemSelectionPacket.OrdinaryForgeMode)
        {
            Console.WriteLine(
                $"[forge] ignored malformed/unsupported selection character={_character?.Name ?? "<none>"} len={packet.Length}");
            return;
        }

        if (selection.IsOddsMaterialIncrement)
        {
            HandleForgeOddsIncrement(selection);
            return;
        }

        var item = KitBagSlots.GetItem(_character.KitBag, selection.KitBagSlot);
        if (!selection.Matches(item))
        {
            // Some client builds emit a second descriptor containing stale
            // scratch-buffer data. Never let that overwrite a valid staged
            // selection; only current authoritative bag contents are accepted.
            Console.WriteLine(
                $"[forge] ignored stale selection character={_character.Name} slot={selection.KitBagSlot} item={selection.ItemId}");
            return;
        }

        if (ForgingMaterialRuleCatalog.TryGet(item.Id, out var materialRule))
        {
            if (materialRule.MaterialType is >= 1 and <= 3)
            {
                if (selection.DestinationSlot != ForgeItemSelectionPacket.PrimaryMaterialDestinationSlot)
                {
                    Console.WriteLine(
                        $"[forge] ignored primary in invalid destination character={_character.Name} destination={selection.DestinationSlot}");
                    return;
                }

                EnsureForgeSelectionBatch();
                _forgePrimaryMaterial = new ForgeSlotSelection(
                    selection.KitBagSlot,
                    item,
                    1);
            }
            else if (materialRule.MaterialType == 4)
            {
                if (selection.DestinationSlot != ForgeItemSelectionPacket.OddsMaterialDestinationSlot)
                {
                    Console.WriteLine(
                        $"[forge] ignored crystal in invalid destination character={_character.Name} destination={selection.DestinationSlot}");
                    return;
                }

                EnsureForgeSelectionBatch();
                // Destination 5 is the trustworthy item descriptor paired
                // with action 88. It validates the source but does not add a
                // crystal by itself (important when the client is at cap 25).
                _forgeOddsMaterials.ValidateDescriptor(selection.KitBagSlot, item);
            }
            else
            {
                Console.WriteLine(
                    $"[forge] ignored non-ordinary material character={_character.Name} item={item.Id} type={materialRule.MaterialType}");
                return;
            }
        }
        else
        {
            if (selection.DestinationSlot != ForgeItemSelectionPacket.EquipmentDestinationSlot ||
                !EquipmentForgeCatalog.TryGet(item.Id, out _))
            {
                Console.WriteLine(
                    $"[forge] ignored non-forgeable equipment character={_character.Name} item={item.Id} destination={selection.DestinationSlot}");
                return;
            }

            EnsureForgeSelectionBatch();
            _forgeEquipment = new ForgeSlotSelection(selection.KitBagSlot, item, 1);
        }

        Console.WriteLine(
            $"[forge] staged character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} destination={selection.DestinationSlot} reserved={GetForgeReservedQuantity(selection.DestinationSlot)}");
    }

    private void HandleForgeOddsIncrement(ForgeItemSelectionPacket selection)
    {
        if (_character is null)
        {
            return;
        }

        // Action 88 intentionally has no valid item descriptor. Resolve its
        // trustworthy bag coordinates against the current server inventory.
        var item = KitBagSlots.GetItem(_character.KitBag, selection.KitBagSlot);
        if (item.IsEmpty ||
            !ForgingMaterialRuleCatalog.TryGet(item.Id, out var materialRule) ||
            materialRule.MaterialType != 4)
        {
            Console.WriteLine(
                $"[forge] ignored crystal increment character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id}");
            return;
        }

        EnsureForgeSelectionBatch();
        if (!_forgeOddsMaterials.TryIncrement(selection.KitBagSlot, item))
        {
            Console.WriteLine(
                $"[forge] ignored excess/stale crystal character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} reserved={_forgeOddsMaterials.TotalQuantity} stack={item.Stack}");
            return;
        }

        Console.WriteLine(
            $"[forge] staged character={_character.Name} bagSlot={selection.KitBagSlot} item={item.Id} destination={selection.DestinationSlot} reserved={_forgeOddsMaterials.TotalQuantity}");
    }

    private async Task HandleForgeStartAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        const int expectedPayloadLength = 36;
        const int modeOffset = 4;
        if (_account is null ||
            _character is null ||
            packet.Payload.Length != expectedPayloadLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(modeOffset, 4)) !=
                ForgeItemSelectionPacket.OrdinaryForgeMode ||
            !HasCurrentForgeIdentity() ||
            _forgeEquipment is null ||
            _forgePrimaryMaterial is null ||
            (_forgeOddsMaterials.TotalQuantity > 0 && !_forgeOddsMaterials.IsFullyLinked) ||
            IsForgeSelectionExpired())
        {
            ClearForgeSelection();
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                $"[forge] rejected incomplete/expired start character={_character?.Name ?? "<none>"} len={packet.Length}");
            return;
        }

        var oddsMaterials = _forgeOddsMaterials.CaptureSelections();
        var request = new ForgeTransactionRequest(
            _forgeEquipment,
            _forgePrimaryMaterial,
            oddsMaterials.FirstOrDefault(),
            oddsMaterials.Skip(1).ToArray());

        // Clear before awaiting persistence so a duplicated Start packet can
        // never consume the same reservation twice.
        ClearForgeSelection();

        ForgeTransactionResult result;
        try
        {
            result = await _store.ForgeEquipmentAsync(
                _account.Id,
                _character.Id,
                request,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[forge] persistence failure account={_account.Id} character={_character.Name}: {ex.Message}");
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            return;
        }

        if (!result.Committed || result.Character is null)
        {
            await _session.SendAsync(
                PacketBuilder.ForgeResult(success: false, resultKind: 0),
                cancellationToken,
                "ForgeRejected");
            Console.WriteLine(
                $"[forge] rejected account={_account.Id} character={_character.Name} status={result.Status} reason={result.RejectionReason}");
            return;
        }

        _character = result.Character;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        // The result must arrive before authoritative bag/status refreshes:
        // the legacy client releases its forge wait flag and applies its local
        // animation/result transition while processing this packet.
        await _session.SendAsync(
            PacketBuilder.ForgeResult(result.Succeeded, resultKind: 1),
            cancellationToken,
            "ForgeResult");
        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "ForgePlayerStatus");
        await SendKitBagRefreshAsync(cancellationToken);

        Console.WriteLine(
            $"[forge] committed account={_account.Id} character={_character.Name} status={result.Status} operation={result.MaterialType} chance={result.Probability} silver={result.SilverSpent} equipment={result.EquipmentBefore.Id}->{result.EquipmentAfter.Id} quality={result.EquipmentBefore.Quality}->{result.EquipmentAfter.Quality} grade={result.EquipmentBefore.Grade}->{result.EquipmentAfter.Grade}");
    }

    private void ClearForgeSelection()
    {
        _forgeEquipment = null;
        _forgePrimaryMaterial = null;
        _forgeOddsMaterials.Clear();
        _forgeAccountId = null;
        _forgeCharacterId = null;
        _forgeSelectionStartedTimestamp = 0;
    }

    private void EnsureForgeSelectionBatch()
    {
        if (HasCurrentForgeIdentity() && !IsForgeSelectionExpired())
        {
            return;
        }

        ClearForgeSelection();
        _forgeAccountId = _account!.Id;
        _forgeCharacterId = _character!.Id;
        _forgeSelectionStartedTimestamp = Stopwatch.GetTimestamp();
    }

    private int GetForgeReservedQuantity(int destinationSlot)
    {
        return destinationSlot switch
        {
            ForgeItemSelectionPacket.EquipmentDestinationSlot => _forgeEquipment?.Quantity ?? 0,
            ForgeItemSelectionPacket.PrimaryMaterialDestinationSlot => _forgePrimaryMaterial?.Quantity ?? 0,
            ForgeItemSelectionPacket.OddsMaterialDestinationSlot or
                ForgeItemSelectionPacket.OddsMaterialIncrementAction => _forgeOddsMaterials.TotalQuantity,
            _ => 0
        };
    }

    private bool HasCurrentForgeIdentity()
    {
        return ForgeSelectionMatchesIdentity(
            _forgeAccountId,
            _forgeCharacterId,
            _account,
            _character);
    }

    internal static bool ForgeSelectionMatchesIdentity(
        int? stagedAccountId,
        int? stagedCharacterId,
        GameAccount? account,
        GameCharacter? character)
    {
        return account is not null &&
               character is not null &&
               character.AccountId == account.Id &&
               stagedAccountId == account.Id &&
               stagedCharacterId == character.Id;
    }

    private bool IsForgeSelectionExpired()
    {
        return _forgeAccountId.HasValue &&
               Stopwatch.GetElapsedTime(_forgeSelectionStartedTimestamp) > ForgeSelectionTtl;
    }

    private async Task<bool> HandleDeveloperItemCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text) ||
            !DeveloperItemCommand.TryParse(text, out var request, out var error))
        {
            return false;
        }

        // A recognized developer command is always consumed locally. It must
        // never be echoed into public map chat, including on denied attempts.
        if (_account is null || _character is null)
        {
            Console.WriteLine("[developer-item] denied before account/character enter");
            return true;
        }

        if (!_developerCommands.Allows(_account.Id))
        {
            Console.WriteLine(
                $"[developer-item] denied account={_account.Id} character={_character.Name}");
            return true;
        }

        if (request is null)
        {
            Console.WriteLine(
                $"[developer-item] invalid account={_account.Id} character={_character.Name}: {error}");
            return true;
        }

        if (request.Operation == DeveloperItemOperation.ClearBag)
        {
            // Opcode 10033 detail pages and opcode 10056 slot-index packets do
            // not evict an icon already instantiated by this client. Capture
            // the occupied slots so the successful clear can use the native
            // source-to-FFFF deletion acknowledgement for every old icon.
            var deletionAcknowledgements =
                PacketBuilder.KitBagDeletionAcknowledgements(_character);

            var cleared = await _store.ClearKitBagAsync(
                _account.Id,
                _character.Id,
                cancellationToken);
            if (cleared is null)
            {
                Console.WriteLine(
                    $"[developer-item] clear bag failed account={_account.Id} character={_character.Name}");
                return true;
            }

            _character = cleared;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            _pendingUnequipFollowup = null;
            ClearForgeSelection();
            ClearGearEnhancerSelection();
            foreach (var acknowledgement in deletionAcknowledgements)
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "DeveloperItemClearBagDeleteAck");
            }

            Console.WriteLine(
                $"[developer-item] cleared bag account={_account.Id} character={_character.Name} removed={deletionAcknowledgements.Length}");
            return true;
        }

        if (request.Operation != DeveloperItemOperation.Add || request.Material is null)
        {
            Console.WriteLine(
                $"[developer-item] invalid operation account={_account.Id} character={_character.Name}");
            return true;
        }

        var material = request.Material;

        var result = await _store.AddForgingMaterialAsync(
            _account.Id,
            _character.Id,
            material.ItemId,
            request.Quantity,
            cancellationToken);
        if (!result.Added || result.Character is null)
        {
            Console.WriteLine(
                $"[developer-item] grant failed account={_account.Id} character={_character.Name} item={material.ItemId} quantity={request.Quantity} status={result.Status}");
            return true;
        }

        _character = result.Character;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[developer-item] granted account={_account.Id} character={_character.Name} item={material.ItemId} name=\"{material.DisplayName}\" quantity={request.Quantity}");
        return true;
    }

    private async Task SendKitBagRefreshAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        foreach (var packet in PacketBuilder.KitBagDetailPages(_character))
        {
            await _session.SendAsync(packet, cancellationToken, "DeveloperItemKitBagDetail");
        }

        foreach (var packet in PacketBuilder.KitBagSlotIndexes(_character))
        {
            await _session.SendAsync(packet, cancellationToken, "DeveloperItemKitBagSlotIndex");
        }
    }

    internal static bool TryReadTalkText(ReadOnlySpan<byte> payload, out string text)
    {
        const int textLengthOffset = 4;
        const int textOffset = 12;
        text = string.Empty;
        if (payload.Length < textOffset)
        {
            return false;
        }

        var lengthWithTerminator = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(textLengthOffset, sizeof(uint)));
        if (lengthWithTerminator < sizeof(ushort) ||
            (lengthWithTerminator & 1) != 0 ||
            lengthWithTerminator - sizeof(ushort) > payload.Length - textOffset)
        {
            return false;
        }

        var textLength = checked((int)lengthWithTerminator - sizeof(ushort));
        text = Encoding.Unicode.GetString(payload.Slice(textOffset, textLength)).TrimEnd('\0');
        return text.Length > 0;
    }

    private async Task HandleGameLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        var username = PacketText.ReadFixedAscii(packet.Payload, 0, 32);
        _account = await _store.LoginOrCreateAccountAsync(username, string.Empty, cancellationToken);
        _accountSessionRegistered = true;

        var replacedSession = _registry.ReplaceAccountSession(_account.Id, _session);
        if (replacedSession is not null)
        {
            Console.WriteLine($"[game] replacing stale session account={_account.Username}");
            try
            {
                await _registry.FinishProgressionBoostOnlineSessionAsync(
                    replacedSession,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A reconciliation checkpoint bounds any lost duration. A
                // transient persistence failure must never reject the new
                // account session or reproduce the switch-login crash.
                Console.WriteLine(
                    $"[status] stale-session boost tail deferred account={_account.Username}: {ex.Message}");
            }

            _registry.Remove(replacedSession);
            replacedSession.Disconnect();
        }

        Console.WriteLine($"[game] accepted {_account.Username}");

        await _session.SendAsync(PacketBuilder.AfterLogin(), cancellationToken, "AfterLogin");
        await SendCharacterPreviewAsync(cancellationToken);
    }

    private async Task SendCharacterPreviewAsync(CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
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
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        _account ??= await _store.LoginOrCreateAccountAsync("player", string.Empty, cancellationToken);

        var payload = packet.Payload;
        var character = new GameCharacter
        {
            Name = PacketText.ReadFixedAscii(payload, 0, 32),
            Gender = ReadByte(payload, 32, 1),
            Camp = ReadByte(payload, 33, 1),
            Profession = ReadByte(payload, 34, 0),
            ZodiacType = ReadZodiacTypeFromCreationPayload(payload),
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

    private async Task HandleZodiacAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null ||
            !ZodiacSyncRequest.TryParse(packet.Buffer, out var request))
        {
            Console.WriteLine($"[zodiac] rejected malformed sync request len={packet.Buffer.Length}");
            return;
        }

        if (!request.IsFullSync)
        {
            Console.WriteLine(
                $"[zodiac] ignored unsupported request character={_character.Name} module={request.Module} sid={request.Sid}");
            return;
        }

        await _session.SendAsync(
            PacketBuilder.ZodiacFullSync(_character),
            cancellationToken,
            "ZodiacFullSync");
    }

    private async Task HandleDeleteRoleAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        ClearForgeSelection();
        ClearGearEnhancerSelection();
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
        ClearForgeSelection();
        ClearGearEnhancerSelection();
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
            boosts = await _registry.GetExperienceBoostStateAsync(
                _session,
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            boosts = ExperienceBoostState.Empty;
            Console.WriteLine(
                $"[status] EXP boost sync failed character={_character.Name} reason={reason}: {ex.Message}");
        }

        await _registry.RefreshExperienceStatusesAndPublishAsync(
            _session,
            boosts,
            reason,
            cancellationToken);
        Console.WriteLine(
            $"[status] EXP boost sync character={_character.Name} reason={reason} count={boosts.ActiveBoosts.Count} bonus-bps={boosts.TotalBonusBasisPoints}");
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

        // Monster visibility state is map-owned. Register as non-ready before
        // the initial NPC/monster snapshot so the transition can commit while
        // this player remains hidden from all live world broadcasts.
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

        ClearGearEnhancerSelection();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload[..4]);

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine($"[npc] dialog open ignored: unknown npc={npcId} map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        if (GearEnhancerProtocol.IsEnhancerNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    GearEnhancerProtocol.DialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine($"[gear-enhancer] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
            return;
        }

        if (GearEnhancerProtocol.IsOriginEnhancerNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    GearEnhancerProtocol.OriginDialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine($"[origin-enhancer] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
            return;
        }

        if (HolySuitDesignProtocol.IsNpcKey(npc.NpcKey))
        {
            await _session.SendAsync(
                PacketBuilder.NpcDialogOpenAck(
                    npc.InteractionId,
                    HolySuitDesignProtocol.DialogIndex,
                    npc.NpcKey),
                cancellationToken,
                "NpcDialogOpenAck");
            Console.WriteLine(
                $"[holy-suit-design] dialog open npc={npc.InteractionId} script={npc.NpcKey}");
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

    private async Task HandleNpcDialogPageRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
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
            Console.WriteLine("[npc] function action ignored: no active character");
            return;
        }

        if (!TryReadNpcFunctionAction(packet.Payload, out var npcId, out var dialogIndex, out var subId, out var args))
        {
            Console.WriteLine("[npc] function action ignored: payload does not match captured NPC function shape");
            return;
        }

        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine($"[npc] function action ignored: npc={npcId} dialog={dialogIndex} subId={subId}");
            return;
        }

        if (GearEnhancerProtocol.IsEnhancerNpcKey(npc.NpcKey))
        {
            if (GearEnhancerProtocol.TryBuildInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var gearMentorResponse))
            {
                ClearGearEnhancerSelection();
                // Stock NpcFunBreak changes from its menu to Enhance/Add/Delete
                // entirely client-side. Start an operation-unbound staging
                // context here so the following native 10193 selections are
                // retained until final 10069 identifies operation 2/3/6.
                _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    operation: null,
                    expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
                await _session.SendAsync(
                    gearMentorResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine($"[gear-mentor] original initial menu npc={npcId} items=1,2,3,4,5,6,7,8,9");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    cancellationToken);
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                subId == GearEnhancerProtocol.CombineGemPiecesMenuSubId)
            {
                ClearGearEnhancerSelection();
                _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    operation: null,
                    expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
                _gearMentorOperationPageSubId = GearEnhancerProtocol.CombineGemPiecesActionSubId;
                await _session.SendAsync(
                    GearEnhancerProtocol.BuildGemPieceCombinationPageResponse(npcId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] gem-piece combination page character={_character.Name} npc={npcId}");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsGearMentorTransactionSubId(subId))
            {
                await HandleGearMentorTransactionAsync(
                    npcId,
                    subId,
                    args,
                    cancellationToken);
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.DialogIndex &&
                GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId))
            {
                ClearGearEnhancerSelection();
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        GearEnhancerProtocol.TemporarilyDisabledResultSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[gear-mentor] unsupported original operation npc={npcId} subId={subId} response={GearEnhancerProtocol.TemporarilyDisabledResultSubId}");
            }
            return;
        }

        if (GearEnhancerProtocol.IsOriginEnhancerNpcKey(npc.NpcKey))
        {
            if (GearEnhancerProtocol.TryBuildOriginInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var originResponse))
            {
                ClearGearEnhancerSelection();
                await _session.SendAsync(
                    originResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine($"[origin-enhancer] initial menu npc={npcId} items=2,3,6");
                return;
            }

            if (dialogIndex == GearEnhancerProtocol.OriginDialogIndex &&
                GearEnhancerProtocol.IsOperationSubId(subId))
            {
                await HandleGearEnhancerOperationAsync(
                    npcId,
                    dialogIndex,
                    subId,
                    args,
                    cancellationToken);
            }
            return;
        }

        if (HolySuitDesignProtocol.IsNpcKey(npc.NpcKey))
        {
            if (HolySuitDesignProtocol.TryBuildInitialMenuResponse(
                    npc.NpcKey,
                    npcId,
                    dialogIndex,
                    subId,
                    out var holySuitResponse))
            {
                await _session.SendAsync(
                    holySuitResponse,
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[holy-suit-design] original initial menu npc={npcId} items=101,201,301,401");
                return;
            }

            if (dialogIndex == HolySuitDesignProtocol.DialogIndex &&
                HolySuitDesignProtocol.IsMenuSubId(subId))
            {
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        HolySuitDesignProtocol.TemporarilyDisabledResultSubId),
                    cancellationToken,
                    "NpcFunctionActionResponse");
                Console.WriteLine(
                    $"[holy-suit-design] unsupported original operation npc={npcId} subId={subId} response={HolySuitDesignProtocol.TemporarilyDisabledResultSubId}");
            }
            return;
        }

        if (!IsHolyStoneArtisan(npc))
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

    private void HandleGearEnhancerItemSelection(GamePacket packet)
    {
        if (_account is null ||
            _character is null ||
            !GearEnhancerItemSelectionPacket.TryParse(packet.Payload, out var selection))
        {
            Console.WriteLine(
                $"[gear-enhancer] ignored malformed/inactive item selection len={packet.Payload.Length}");
            return;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow))
        {
            ClearGearEnhancerSelection();
            Console.WriteLine(
                $"[gear-enhancer] ignored item selection without active operation character={_character.Name} bagSlot={selection.KitBagSlot} selected={selection.Selected}");
            return;
        }

        var staged = context.Apply(selection, _character.KitBag);
        Console.WriteLine(
            $"[gear-enhancer] item selection character={_character.Name} npc={context.NpcId} dialog={context.DialogIndex} operation={context.Operation?.ToString() ?? "pending-final-action"} selected={selection.Selected} bagSlot={staged.KitBagSlot} item={staged.Item.Id} stack={staged.Item.Stack} role={staged.Role?.ToString() ?? "none"} status={staged.Status}");
    }

    private async Task HandleGearEnhancerOperationAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> args,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var operation = (GearEnhancementOperation)subId;
        var now = DateTimeOffset.UtcNow;
        var stagedContext = _gearEnhancerSelectionContext;
        var contextIsActive = GearEnhancerCommitContextMatches(
            stagedContext,
            _gearMentorOperationPageSubId,
            _account.Id,
            _character.Id,
            npcId,
            dialogIndex,
            operation,
            now);
        GearEnhancerSelectionTriplet? nativeSelections = null;
        var selectionShape = GearEnhancerProtocol.ReadSelection(
            args,
            out var gearKitBagSlot,
            out var catalystKitBagSlot,
            out var attributeStoneKitBagSlot);
        if (selectionShape is GearEnhancerSelectionShape.MenuSelection or
            GearEnhancerSelectionShape.MalformedCommit)
        {
            if (contextIsActive &&
                stagedContext!.TryResolveNativeCommit(
                    selectionShape,
                    out var stagedSelections))
            {
                nativeSelections = stagedSelections;
                gearKitBagSlot = stagedSelections.GearKitBagSlot;
                catalystKitBagSlot = stagedSelections.CatalystKitBagSlot;
                attributeStoneKitBagSlot = stagedSelections.AttributeStoneKitBagSlot;
                selectionShape = GearEnhancerSelectionShape.Commit;
            }
        }

        if (selectionShape == GearEnhancerSelectionShape.MenuSelection)
        {
            ClearGearEnhancerSelection();
            _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                _account.Id,
                _character.Id,
                npcId,
                dialogIndex,
                operation,
                DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
            var workflow = dialogIndex == GearEnhancerProtocol.DialogIndex
                ? "gear-mentor"
                : "origin-enhancer";
            await _session.SendAsync(
                GearEnhancerProtocol.BuildOperationPageResponse(npcId, dialogIndex, subId),
                cancellationToken,
                "NpcFunctionActionResponse");
            Console.WriteLine(
                $"[{workflow}] operation page character={_character.Name} npc={npcId} dialog={dialogIndex} operation={operation}");
            return;
        }

        // Consume the staged workflow before awaiting persistence. A replayed
        // confirmation cannot reuse the same three native selections.
        ClearGearEnhancerSelection();

        var responseSubId = GearEnhancerProtocol.InvalidSelectionResultSubId;
        GearEnhancementRequest? request = null;
        GearEnhancementTransactionResult? transaction = null;
        var selectionSummary =
            $"gear={DescribeGearEnhancerSelection(_character.KitBag, gearKitBagSlot)} " +
            $"catalyst={DescribeGearEnhancerSelection(_character.KitBag, catalystKitBagSlot)} " +
            $"stone={DescribeGearEnhancerSelection(_character.KitBag, attributeStoneKitBagSlot)}";

        if (selectionShape == GearEnhancerSelectionShape.Commit && contextIsActive)
        {
            if (gearKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingGearResultSubId;
            }
            else if (catalystKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingCatalystResultSubId(operation);
            }
            else if (attributeStoneKitBagSlot < 0)
            {
                responseSubId = GearEnhancerProtocol.MissingAttributeStoneResultSubId;
            }
            else
            {
                var selections = nativeSelections ?? new GearEnhancerSelectionTriplet(
                    CaptureGearEnhancerSelection(_character.KitBag, gearKitBagSlot),
                    CaptureGearEnhancerSelection(_character.KitBag, catalystKitBagSlot),
                    CaptureGearEnhancerSelection(_character.KitBag, attributeStoneKitBagSlot));
                request = new GearEnhancementRequest(
                    operation,
                    ToGearEnhancementSelection(selections.Gear),
                    ToGearEnhancementSelection(selections.AttributeStone),
                    ToGearEnhancementSelection(selections.Catalyst));

                try
                {
                    transaction = await _store.EnhanceGearAsync(
                        _account.Id,
                        _character.Id,
                        request,
                        cancellationToken);
                    responseSubId = GearEnhancerProtocol.ResolveResultSubId(
                        operation,
                        transaction.Enhancement,
                        request);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[gear-enhancer] persistence failure account={_account.Id} character={_character.Name} operation={operation}: {ex.Message}");
                }
            }
        }

        if (transaction?.Character is not null)
        {
            _character = transaction.Character;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        }

        var authoritativeBagChanged = transaction?.Committed == true;
        var staleSelection = transaction?.Enhancement?.Status ==
            GearEnhancementStatus.StaleSelection;
        if (authoritativeBagChanged || staleSelection)
        {
            ClearForgeSelection();
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (authoritativeBagChanged || staleSelection)
        {
            // The native result must release the dialog's pending state before
            // authoritative inventory packets replace its staged client view.
            await SendKitBagRefreshAsync(cancellationToken);
        }

        var resultWorkflow = dialogIndex == GearEnhancerProtocol.DialogIndex
            ? "gear-mentor"
            : "origin-enhancer";
        Console.WriteLine(
            $"[{resultWorkflow}] result account={_account.Id} character={_character.Name} npc={npcId} dialog={dialogIndex} operation={operation} status={transaction?.Enhancement?.Status.ToString() ?? selectionShape.ToString()} response={responseSubId} committed={transaction?.Committed == true} selections=({selectionSummary}) reason=\"{transaction?.Enhancement?.RejectionReason ?? "none"}\"");
    }

    private async Task HandleGearMentorTransactionAsync(
        uint npcId,
        int subId,
        IReadOnlyList<int> args,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var operation = (GearMentorOperation)subId;
        var maximumSelections = operation == GearMentorOperation.Decompose ? 3 : 1;
        var now = DateTimeOffset.UtcNow;
        var stagedContext = _gearEnhancerSelectionContext;
        var contextIsActive = GearMentorCommitContextMatches(
            stagedContext,
            _gearMentorOperationPageSubId,
            _account.Id,
            _character.Id,
            npcId,
            subId,
            now);
        var operationPageMatches = _gearMentorOperationPageSubId == subId;
        var selectionShape = GearEnhancerProtocol.ReadSelection(
            args,
            out var firstSlot,
            out var secondSlot,
            out var thirdSlot);
        IReadOnlyList<GearEnhancerSelectionSnapshot> selectedSelections =
            selectionShape == GearEnhancerSelectionShape.Commit
            ? new[] { firstSlot, secondSlot, thirdSlot }
                .Where(static slot => slot >= 0)
                .Select(slot => CaptureGearEnhancerSelection(_character.KitBag, slot))
                .ToArray()
            : [];

        if (selectionShape is GearEnhancerSelectionShape.MenuSelection or
            GearEnhancerSelectionShape.MalformedCommit)
        {
            if (contextIsActive &&
                operationPageMatches &&
                stagedContext!.TryResolveNativeSlots(
                    selectionShape,
                    minimumCount: 1,
                    maximumSelections,
                    out var stagedSelections))
            {
                selectedSelections = stagedSelections;
                selectionShape = GearEnhancerSelectionShape.Commit;
            }
        }

        if (selectedSelections.Count == 0 &&
            selectionShape == GearEnhancerSelectionShape.MenuSelection &&
            subId != GearEnhancerProtocol.CombineGemPiecesActionSubId &&
            !operationPageMatches)
        {
            ClearGearEnhancerSelection();
            _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                _account.Id,
                _character.Id,
                npcId,
                GearEnhancerProtocol.DialogIndex,
                operation: null,
                expiresAt: DateTimeOffset.UtcNow + GearEnhancerProtocol.SelectionContextLifetime);
            _gearMentorOperationPageSubId = subId;
            await _session.SendAsync(
                GearEnhancerProtocol.BuildGearMentorOperationPageResponse(npcId, subId),
                cancellationToken,
                "NpcFunctionActionResponse");
            Console.WriteLine(
                $"[gear-mentor] operation page character={_character.Name} npc={npcId} operation={operation}");
            return;
        }

        var canCommit = contextIsActive && operationPageMatches;
        var request = new GearMentorRequest(
            operation,
            canCommit
                ? selectedSelections.Select(ToGearMentorSelection).ToArray()
                : []);
        var selectionSummary = selectedSelections.Count == 0
            ? "none"
            : string.Join(
                ',',
                selectedSelections.Select(selection =>
                    DescribeGearEnhancerSelection(_character.KitBag, selection.KitBagSlot)));

        // A final action consumes the session-scoped selection before any
        // persistence await so it cannot be replayed.
        ClearGearEnhancerSelection();

        GearMentorTransactionResult? transaction = null;
        var responseSubId = GearEnhancerProtocol.SelectedItemMissingResultSubId;
        try
        {
            if (canCommit)
            {
                transaction = await _store.ProcessGearMentorAsync(
                    _account.Id,
                    _character.Id,
                    request,
                    cancellationToken);
                responseSubId = GearEnhancerProtocol.ResolveGearMentorResultSubId(transaction.Result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[gear-mentor] persistence failure account={_account.Id} character={_character.Name} operation={operation}: {ex.Message}");
        }

        if (transaction?.Character is not null)
        {
            _character = transaction.Character;
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        }

        var staleSelection = transaction?.Result?.Status == GearMentorStatus.StaleSelection;
        if (transaction?.Committed == true || staleSelection)
        {
            ClearForgeSelection();
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (transaction?.Committed == true || staleSelection)
        {
            await SendKitBagRefreshAsync(cancellationToken);
        }

        var outputs = transaction?.Result?.Outputs.Count > 0
            ? string.Join(
                ',',
                transaction.Result.Outputs.Select(output =>
                    $"{output.ItemId}x{output.Quantity}/bound:{output.Bound}"))
            : "none";
        Console.WriteLine(
            $"[gear-mentor] result account={_account.Id} character={_character.Name} npc={npcId} operation={operation} status={transaction?.Result?.Status.ToString() ?? selectionShape.ToString()} response={responseSubId} committed={transaction?.Committed == true} selections=({selectionSummary}) outputs=({outputs}) reason=\"{transaction?.Result?.RejectionReason ?? "none"}\"");
    }

    internal static bool GearEnhancerCommitContextMatches(
        GearEnhancerSelectionContext? context,
        int? gearMentorOperationPageSubId,
        int accountId,
        int characterId,
        uint npcId,
        int dialogIndex,
        GearEnhancementOperation operation,
        DateTimeOffset now)
    {
        return (dialogIndex != GearEnhancerProtocol.DialogIndex ||
                !gearMentorOperationPageSubId.HasValue) &&
               context is not null &&
               context.IsActiveFor(
                   accountId,
                   characterId,
                   npcId,
                   dialogIndex,
                   operation,
                   now);
    }

    internal static bool GearMentorCommitContextMatches(
        GearEnhancerSelectionContext? context,
        int? operationPageSubId,
        int accountId,
        int characterId,
        uint npcId,
        int actionSubId,
        DateTimeOffset now)
    {
        return operationPageSubId == actionSubId &&
               context is not null &&
               context.NpcId == npcId &&
               context.DialogIndex == GearEnhancerProtocol.DialogIndex &&
               context.IsActiveForSelection(accountId, characterId, now);
    }

    private static GearEnhancerSelectionSnapshot CaptureGearEnhancerSelection(
        string kitBag,
        int kitBagSlot)
    {
        return new GearEnhancerSelectionSnapshot(
            kitBagSlot,
            KitBagSlots.GetItem(kitBag, kitBagSlot));
    }

    private static GearEnhancementSlotSelection ToGearEnhancementSelection(
        GearEnhancerSelectionSnapshot selection)
    {
        return new GearEnhancementSlotSelection(
            selection.KitBagSlot,
            selection.ExpectedItem);
    }

    private static GearMentorSlotSelection ToGearMentorSelection(
        GearEnhancerSelectionSnapshot selection)
    {
        return new GearMentorSlotSelection(
            selection.KitBagSlot,
            selection.ExpectedItem);
    }

    private static string DescribeGearEnhancerSelection(string kitBag, int kitBagSlot)
    {
        if (kitBagSlot < 0)
        {
            return "missing";
        }

        var item = KitBagSlots.GetItem(kitBag, kitBagSlot);
        return $"slot:{kitBagSlot}/item:{item.Id}/stack:{item.Stack}";
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

    private void ClearGearEnhancerSelection()
    {
        _gearEnhancerSelectionContext = null;
        _gearMentorOperationPageSubId = null;
    }

    private async Task RefreshNearbyWorldObjectsAsync(
        string reason,
        CancellationToken cancellationToken,
        bool forceMonsterRefresh = false)
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
            cancellationToken,
            forceMonsterRefresh);
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
            _registry.Remove(_session, preservePlayerStatus: true);
            _registered = false;
        }

        _worldPresenceAnnounced = false;
        _clientReadyReceived = false;
        _playerDetailSent = false;
        _enterUiReadyReceived = false;
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

        if (!_registry.TryGetMonsterSnapshot(
                _character.CurrentMap,
                attack.TargetObjectId,
                out var target) ||
            !_registry.IsMonsterVisibleTo(
                _session,
                attack.TargetObjectId,
                target.SpawnGeneration) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine($"[attack] rejected unavailable monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                _character.PositionX,
                _character.PositionZ,
                attack.AttackerX,
                attack.AttackerZ,
                out var attackX,
                out var attackZ))
        {
            Console.WriteLine(
                $"[attack] rejected mismatched position character={_character.Name} server={_character.PositionX:F2},{_character.PositionZ:F2} reported={attack.AttackerX:F2},{attack.AttackerZ:F2}");
            return;
        }

        if (!MonsterCombatResolver.IsWithinBasicAttackRange(
                attackX,
                attackZ,
                target.X,
                target.Z,
                MonsterCombatResolver.ResolvePlayerBasicAttackRange(target.Definition)))
        {
            Console.WriteLine(
                $"[attack] rejected out-of-range monster character={_character.Name} target={attack.TargetObjectId} player={attackX:F2},{attackZ:F2} monster={target.X:F2},{target.Z:F2}");
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
                target.SpawnGeneration,
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
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                selfPacket,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "BasicAttackSelf");
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
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

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

        if (cast.SkillId <= int.MaxValue &&
            SkillStatusEffectCatalog.TryGet((int)cast.SkillId, out var statusEffect))
        {
            await HandleSelfStatusSkillCastAsync(
                packet,
                cast,
                statusEffect,
                cancellationToken);
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

        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
        {
            await HandleHostileMonsterAreaSkillCastAsync(
                packet,
                cast,
                combat,
                cancellationToken);
            return;
        }

        if (!_registry.TryGetMonsterSnapshot(
                _character.CurrentMap,
                cast.TargetObjectId,
                out var target) ||
            !_registry.IsMonsterVisibleTo(
                _session,
                cast.TargetObjectId,
                target.SpawnGeneration) ||
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

        if (cast.SkillId <= int.MaxValue &&
            MonsterStunSkillCatalog.TryGet((int)cast.SkillId, out var stun))
        {
            await HandleHostileMonsterStunSkillCastAsync(
                packet,
                cast,
                combat,
                stun,
                target.SpawnGeneration,
                cancellationToken);
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
                target.SpawnGeneration,
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
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                selfVisual,
                damageResult.Monster.SpawnGeneration,
                cancellationToken,
                "SkillCastSelf");
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                selfDamage,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "SkillDamageSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                selfImpact,
                damageResult.Monster.SpawnGeneration,
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
            "SkillCastWorld",
            expectedSpawnGeneration: damageResult.Monster.SpawnGeneration);
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
            "SkillDamageWorld",
            healthMutation: damageResult.HealthMutation);
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
            "SkillCastImpactWorld",
            expectedSpawnGeneration: damageResult.Monster.SpawnGeneration);

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

    private async Task HandleHostileMonsterStunSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        MonsterStunSkillDefinition definition,
        uint expectedSpawnGeneration,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextSkillCastAt.TryGetValue(cast.SkillId, out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[skill] rejected cooldown character={character.Name} skill={cast.SkillId} remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana >= manaCost)
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StunSkillManaRejected");
            return;
        }

        if (!_registry.TryApplyMonsterStun(
                character.CurrentMap,
                cast.TargetObjectId,
                character.Id,
                definition.Duration,
                expectedSpawnGeneration,
                now,
                out var stunResult) ||
            !stunResult.Applied)
        {
            lock (character.VitalsSync)
            {
                character.CurrentMp = Math.Min(
                    Math.Max(0, character.MaxMp),
                    (int)Math.Min(int.MaxValue, (long)character.CurrentMp + manaCost));
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                currentMana = character.CurrentMp;
            }

            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StunSkillManaRefund");
            Console.WriteLine(
                $"[skill] rejected stale stun target character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + definition.Cooldown;
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var monster = stunResult.Monster;
        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var statusSeconds = checked((uint)Math.Max(1d, Math.Ceiling(definition.Duration.TotalSeconds)));
        var statusPacket = PacketBuilder.WorldObjectStatusEffects(
            cast.TargetObjectId,
            [new ClientStatusEffect(definition.StatusId, statusSeconds)]);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                monster.SpawnGeneration,
                cancellationToken,
                "StunSkillCastSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                statusPacket,
                monster.SpawnGeneration,
                cancellationToken,
                "StunStatusSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastImpact(
                    LocalPlayerObjectId,
                    cast.TargetObjectId,
                    cast.SkillId,
                    monster.X,
                    monster.Z),
                monster.SpawnGeneration,
                cancellationToken,
                "StunSkillImpactSelf");
            if (manaCost > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "StunSkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] stun caster notification failed character={character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var visualRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastVisual(packet.Buffer, worldObjectId),
            cancellationToken,
            _session,
            "StunSkillCastWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);
        var statusRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            statusPacket,
            cancellationToken,
            _session,
            "StunStatusWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);
        var impactRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                cast.TargetObjectId,
                cast.SkillId,
                monster.X,
                monster.Z),
            cancellationToken,
            _session,
            "StunSkillImpactWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);

        if (_account is not null)
        {
            try
            {
                int currentHp;
                long vitalsRevision;
                lock (character.VitalsSync)
                {
                    currentHp = character.CurrentHp;
                    currentMana = character.CurrentMp;
                    vitalsRevision = character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    _account.Id,
                    character.Id,
                    currentHp,
                    currentMana,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[skill] stun vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[skill] stun character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} status={definition.StatusId} duration={definition.Duration.TotalSeconds:F0} cooldown={definition.Cooldown.TotalSeconds:F0} status-odds={definition.StatusOdds} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={Math.Max(visualRecipients, Math.Max(statusRecipients, impactRecipients))}");
    }

    private async Task HandleSelfStatusSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillStatusEffectDefinition definition,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextSkillCastAt.TryGetValue(cast.SkillId, out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[skill] rejected cooldown character={character.Name} skill={cast.SkillId} remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        if (!SkillCombatCatalog.TryGet(definition.SkillId, out var combat))
        {
            Console.WriteLine(
                $"[skill] rejected missing self-status combat data character={character.Name} skill={cast.SkillId}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana >= manaCost)
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StatusSkillManaRejected");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + definition.Cooldown;
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var targetX = float.IsFinite(cast.TargetX) ? cast.TargetX : character.PositionX;
        var targetZ = float.IsFinite(cast.TargetZ) ? cast.TargetZ : character.PositionZ;
        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);

        await _session.SendAsync(
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, LocalPlayerObjectId),
            cancellationToken,
            "StatusSkillCastSelf");
        var visualRecipients = await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, worldObjectId),
            cancellationToken,
            _session,
            "StatusSkillCastWorld");

        // AddStatus on the working server publishes the complete MSG_STATUS map
        // before MAGIC_PERFORM. The registry composer preserves every active EXP
        // source while adding/replacing this skill's same-kind runtime status.
        var statusApplied = await _registry.ApplyRuntimeStatusAndPublishAsync(
            _session,
            definition,
            now,
            $"skill-{definition.SkillId}",
            cancellationToken);

        await _session.SendAsync(
            PacketBuilder.SkillCastImpact(
                LocalPlayerObjectId,
                LocalPlayerObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            "StatusSkillImpactSelf");
        var impactRecipients = await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                worldObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "StatusSkillImpactWorld");

        if (manaCost > 0)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StatusSkillManaSelf");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerManaUpdate(worldObjectId, currentMana),
                cancellationToken,
                _session,
                "StatusSkillManaWorld");
        }

        if (_account is not null)
        {
            try
            {
                int currentHp;
                long vitalsRevision;
                lock (character.VitalsSync)
                {
                    currentHp = character.CurrentHp;
                    currentMana = character.CurrentMp;
                    vitalsRevision = character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    _account.Id,
                    character.Id,
                    currentHp,
                    currentMana,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[skill] self-status vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[skill] self status character={character.Name} skill={cast.SkillId} status={definition.StatusId} applied={statusApplied} duration={definition.Duration.TotalSeconds:F0} mp={currentMana}/{character.MaxMp} viewers={Math.Max(visualRecipients, impactRecipients)}");
    }

    private async Task HandleHostileMonsterAreaSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana >= manaCost)
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "AreaSkillManaRejected");
            return;
        }

        var requestedDamage = SkillCombatResolver.CalculateDamage(character, combat);
        var candidates = _registry.GetMapMonsterSnapshots(character.CurrentMap)
            .Where(monster =>
                monster.IsSpawned &&
                monster.IsAlive &&
                _registry.IsMonsterVisibleTo(
                    _session,
                    monster.ObjectId,
                    monster.SpawnGeneration) &&
                SkillCombatResolver.IsWithinArea(
                    character.PositionX,
                    character.PositionZ,
                    monster.X,
                    monster.Z,
                    combat))
            .OrderBy(static monster => monster.ObjectId)
            .ToArray();
        var hits = new List<(MonsterDamageResult Result, uint ReportedDamage)>(candidates.Length);
        if (requestedDamage > 0)
        {
            foreach (var candidate in candidates)
            {
                if (_registry.TryApplyMonsterDamage(
                        character.CurrentMap,
                        candidate.ObjectId,
                        requestedDamage,
                        character.Id,
                        candidate.SpawnGeneration,
                        out var damageResult) &&
                    damageResult.BeforeHealth != damageResult.AfterHealth)
                {
                    // The original protocol reports resolved damage, even if the
                    // target had less health remaining.
                    hits.Add((damageResult, requestedDamage));
                }
            }
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var selfVisual = PacketBuilder.SelfTargetSkillCastVisual(
            packet.Buffer,
            LocalPlayerObjectId);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            uint.MaxValue,
            cast.SkillId,
            character.PositionX,
            character.PositionZ);
        var selfCluster = PacketBuilder.SkillClusterDamage(
            LocalPlayerObjectId,
            cast.SkillId,
            hits.Select(static hit => new SkillClusterDamageEntry(
                    hit.Result.ObjectId,
                    hit.ReportedDamage))
                .ToArray());

        var casterNotified = true;
        try
        {
            await _session.SendAsync(selfVisual, cancellationToken, "AreaSkillCastSelf");
            await _session.SendAsync(selfImpact, cancellationToken, "AreaSkillImpactSelf");
            if (hits.Count == 0)
            {
                await _session.SendAsync(selfCluster, cancellationToken, "AreaSkillDamageSelf");
            }
            else
            {
                await _registry.DeliverMonsterAreaDamageToViewerAsync(
                    _session,
                    character.CurrentMap,
                    LocalPlayerObjectId,
                    cast.SkillId,
                    hits.Select(static hit => new MonsterAreaDamageBroadcastHit(
                            hit.Result.HealthMutation!.Value,
                            hit.ReportedDamage))
                        .ToArray(),
                    cancellationToken,
                    "AreaSkillSelf");
            }
            if (manaCost > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "AreaSkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] area caster notification failed character={character.Name} skill={cast.SkillId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var areaRecipients = await _registry.BroadcastMonsterAreaDamageToViewersAsync(
            character.CurrentMap,
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, worldObjectId),
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                uint.MaxValue,
                cast.SkillId,
                character.PositionX,
                character.PositionZ),
            worldObjectId,
            cast.SkillId,
            hits.Select(static hit => new MonsterAreaDamageBroadcastHit(
                    hit.Result.HealthMutation!.Value,
                    hit.ReportedDamage))
                .ToArray(),
            cancellationToken,
            _session,
            "AreaSkill");

        foreach (var hit in hits)
        {
            if (hit.Result.Killed)
            {
                await AwardMonsterKillAsync(hit.Result, cancellationToken);
            }
        }

        if (_account is not null)
        {
            try
            {
                int currentHp;
                long vitalsRevision;
                lock (character.VitalsSync)
                {
                    currentHp = character.CurrentHp;
                    currentMana = character.CurrentMp;
                    vitalsRevision = character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    _account.Id,
                    character.Id,
                    currentHp,
                    currentMana,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[skill] area vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        var appliedDamage = hits.Aggregate(
            0UL,
            static (total, hit) => total + hit.Result.BeforeHealth - hit.Result.AfterHealth);
        Console.WriteLine(
            $"[skill] area damage character={character.Name} skill={cast.SkillId} radius={combat.Range:F2} candidates={candidates.Length} hits={hits.Count} resolved-each={requestedDamage} applied-total={appliedDamage} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={areaRecipients}");
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
                damageResult.Monster.SpawnGeneration,
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
            experienceBoosts = await _registry.GetExperienceBoostStateAsync(
                _session,
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
        var awardedTalentExperience = experienceBoosts.ApplyToTalent(reward.TalentExperience);
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
                awardedTalentExperience,
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
            $"[reward] character={_character.Name} base-exp={reward.Experience} awarded-exp={awardedExperience} exp-bonus-bps={experienceBoosts.TotalBonusBasisPoints} base-talent-exp={reward.TalentExperience} awarded-talent-exp={awardedTalentExperience} talent-bonus-bps={experienceBoosts.TotalTalentBonusBasisPoints} boosts={string.Join(',', experienceBoosts.ActiveBoosts.Select(boost => boost.StatusId))}");

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
            damageResult.Monster.SpawnGeneration,
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
        uint monsterSpawnGeneration,
        int currentExperience,
        int currentTalentExperience,
        int currentTalentPoints,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _registry.DeliverMonsterPacketToViewerAsync(
            _session,
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                LocalPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            monsterSpawnGeneration,
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
            "MonsterKillProgressionRefreshWorld",
            expectedSpawnGeneration: monsterSpawnGeneration);
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

        // The initial monster snapshot is committed before this session becomes
        // WorldReady, so its generation or runtime health/state may drift during
        // bootstrap without a live broadcast. Force one ordered remove + fresh
        // appearance at activation; normal AOI updates remain incremental after it.
        await RefreshNearbyWorldObjectsAsync(
            "activation-reconcile",
            cancellationToken,
            forceMonsterRefresh: true);

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
        await _registry.SendStatusSnapshotToViewerAsync(
            player,
            _session,
            cancellationToken);
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
            || !CanSendPostEnterBootstrap(
                _clientReadyReceived,
                _playerDetailSent,
                _enterUiReadyReceived)
            || _account is null
            || _character is null)
        {
            return;
        }

        _postEnterBootstrapSent = true;

        var enterSyncPackets = await _store.GetEnterSyncPacketsAsync(cancellationToken);
        var suppressedEnterSyncPackets = 0;
        foreach (var packet in enterSyncPackets)
        {
            if (!CanReplayCapturedPostEnterPacket(packet))
            {
                suppressedEnterSyncPackets++;
                continue;
            }

            await _session.SendAsync(packet, cancellationToken, "SynGameData");
        }

        if (suppressedEnterSyncPackets > 0)
        {
            Console.WriteLine(
                $"[game] suppressed unsafe captured enter packets count={suppressedEnterSyncPackets} " +
                "reason=accepted-quest snapshots are character-specific");
        }

        await SendMapWorldObjectsAsync(cancellationToken);

        var skillStates = await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        var talentStates = await _store.GetTalentStatesAsync(_account.Id, _character.Id, cancellationToken);
        await _session.SendAsync(PacketBuilder.PlayerStatusUpdate(_character), cancellationToken, "PlayerStatusUpdate");
        await SendTalentRankPacketsAsync(skillStates, talentStates, "post-enter", cancellationToken);
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(0), cancellationToken, "PlayerUnknown10098");
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(1), cancellationToken, "PlayerUnknown10098");
        var skillList = PacketBuilder.SkillList(skillStates);
        if (skillList.Length > 0)
        {
            await _session.SendAsync(skillList, cancellationToken, "SkillList");
        }

        // Opcode 10357 is the final enter/UI-ready boundary. Publish exactly one
        // complete 10167 snapshot here, after both the local object and UI exist.
        await SendExperienceBoostStatusAsync("post-enter", cancellationToken);
    }

    internal static bool CanSendPostEnterBootstrap(
        bool clientReadyReceived,
        bool playerDetailSent,
        bool enterUiReadyReceived)
    {
        return clientReadyReceived && playerDetailSent && enterUiReadyReceived;
    }

    internal static bool CanReplayCapturedPostEnterPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        return declaredLength == packet.Length
            && opcode != Opcodes.PlayerAcceptedQuests;
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

        if (TryReadStorageItemEquipmentBagTransfer(packet.Payload, out var equipmentSlot, out var bagSlot))
        {
            await HandleEquipmentBagTransferAsync(equipmentSlot, bagSlot, cancellationToken);
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

        Console.WriteLine("[equip-re] StorageItem ignored: payload does not match known equip/unequip shapes");
    }

    private async Task HandleEquipmentBagTransferAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var action = ResolveEquipmentBagTransferAction(_character, equipmentSlot, bagSlot);
        if (action == EquipmentBagTransferAction.Unequip)
        {
            await HandleUnequipItemAsync(equipmentSlot, bagSlot, cancellationToken);
            return;
        }

        var equippedItem = EquipmentSlots.GetItem(
            _character.Equipment,
            _character.Profession,
            equipmentSlot);
        var bagItem = KitBagSlots.GetItem(_character.KitBag, bagSlot);
        if (action == EquipmentBagTransferAction.Equip)
        {
            await HandleEquipItemAsync(
                bagSlot,
                requestedEquipmentSlot: equipmentSlot,
                itemIdHint: bagItem.Id,
                cancellationToken,
                sendStorageTransferAck: true);
            return;
        }

        // Opcode 10052 has no direction bit. The native client treats a pair of
        // occupied locations as a swap, but this server deliberately rejects it so
        // dropping equipped gear onto an occupied bag slot cannot unequip it.
        Console.WriteLine(
            $"[equip-re] StorageItem transfer ignored: equipmentSlot={equipmentSlot} equipmentItem={equippedItem.Id} bagSlot={bagSlot} bagItem={bagItem.Id}");
        await SendEquipmentBagTransferRejectionRefreshAsync(
            equipmentSlot,
            bagSlot,
            cancellationToken);
    }

    private async Task SendEquipmentBagTransferRejectionRefreshAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var equipmentRefresh = PacketBuilder.EquipmentItemSnapshot(_character, equipmentSlot);
        if (equipmentRefresh.Length == 0)
        {
            equipmentRefresh = PacketBuilder.EquipmentItemClearSnapshot(equipmentSlot);
        }

        await _session.SendAsync(
            equipmentRefresh,
            cancellationToken,
            "RejectedStorageEquipmentRefresh");
        await _session.SendAsync(
            PacketBuilder.KitBagSlotIndex(_character, bagSlot),
            cancellationToken,
            "RejectedStorageKitBagIndexRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "RejectedStorageEquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "RejectedStoragePlayerDetailRefreshAck");
    }

    internal static EquipmentBagTransferAction ResolveEquipmentBagTransferAction(
        GameCharacter character,
        int equipmentSlot,
        int bagSlot)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot) || bagSlot is < 0 or >= 96)
        {
            return EquipmentBagTransferAction.Reject;
        }

        var equippedItem = EquipmentSlots.GetItem(
            character.Equipment,
            character.Profession,
            equipmentSlot);
        var bagItem = KitBagSlots.GetItem(character.KitBag, bagSlot);
        if (!equippedItem.IsEmpty && bagItem.IsEmpty)
        {
            return EquipmentBagTransferAction.Unequip;
        }

        if (equippedItem.IsEmpty
            && !bagItem.IsEmpty
            && EquipmentSlots.ResolveSlotForItem(bagItem.Id, equipmentSlot) == equipmentSlot)
        {
            return EquipmentBagTransferAction.Equip;
        }

        return EquipmentBagTransferAction.Reject;
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
            Console.WriteLine("[equip-re] BreakItem ignored: payload does not contain a valid bag page/index");
            return;
        }

        var itemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (!EquipmentSlots.TryGetAuthoritativeSlot(itemId, out _))
        {
            Console.WriteLine(
                $"[equip-re] BreakItem ignored: sourceSlot={sourceSlot} item={itemId} is not genuine equipment");
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

        Console.WriteLine(
            MatchesCurrentKitBagItem(_character, sourceSlot, itemId)
                ? $"[equip-re] BagItemAction acknowledged character={_character.Name} sourceSlot={sourceSlot} item={itemId}"
                : $"[equip-re] BagItemAction acknowledged without matching authoritative item sourceSlot={sourceSlot} item={itemId}");

        await _session.SendAsync(
            PacketBuilder.BagItemActionAck(packet.Buffer),
            cancellationToken,
            "BagItemActionInspectAck");
    }

    private void HandleItemInfoRequest(GamePacket packet)
    {
        LogInventoryPacket(packet);

        Console.WriteLine(
            TryReadItemInfoRequest(packet.Payload, out var sourceSlot, out var itemId)
            && MatchesCurrentKitBagItem(_character, sourceSlot, itemId)
                ? $"[equip-re] ItemInfoRequest sourceSlot={sourceSlot} item={itemId}"
                : "[equip-re] ItemInfoRequest ignored: payload does not match the authoritative kitbag item");
    }

    private async Task HandleUnequipItemAsync(int equipmentSlot, int destinationSlot, CancellationToken cancellationToken)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot) || destinationSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem unequip ignored: unsupported slot={equipmentSlot} destination={destinationSlot}");
            return;
        }

        if (_account is null || _character is null)
        {
            return;
        }

        var previousEquipmentEntry = EquipmentSlots.GetEntry(
            _character.Equipment,
            _character.Profession,
            equipmentSlot);
        var previousItemId = CompactItemEntry.Parse(previousEquipmentEntry).Id;
        if (previousItemId == 0)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem unequip ignored: empty equipment slot={equipmentSlot} destination={destinationSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        var previousKitBag = _character.KitBag;
        var updatedCharacter = await _store.MoveEquipmentToKitBagAsync(
            _account.Id,
            _character.Id,
            equipmentSlot,
            kitBagSlot: destinationSlot,
            cancellationToken: cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem unequip failed: character={_character.Name} id={_character.Id} slot={equipmentSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        if (previousItemId != 0
            && EquipmentSlots.GetItemId(updatedCharacter.Equipment, updatedCharacter.Profession, equipmentSlot) == previousItemId)
        {
            _character = updatedCharacter;
            Console.WriteLine(
                $"[equip-re] StorageItem unequip did not move item: character={_character.Name} slot={equipmentSlot} item={previousItemId} destination={destinationSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        var actualDestinationSlot = ResolveMovedKitBagDestination(
            previousKitBag,
            updatedCharacter.KitBag,
            previousEquipmentEntry);
        if (actualDestinationSlot != destinationSlot)
        {
            _character = updatedCharacter;
            await RefreshActiveCharacterStatsAsync("unequip-destination-mismatch", cancellationToken);
            _registry.UpdateCharacter(_session, _character);
            Console.WriteLine(
                $"[equip-re] StorageItem unequip destination mismatch: character={_character.Name} slot={equipmentSlot} item={previousItemId} actualDestination={actualDestinationSlot} requestedDestination={destinationSlot}");
            await _session.SendAsync(
                PacketBuilder.PlayerStatusUpdate(_character),
                cancellationToken,
                "PlayerStatusUpdate");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            if (actualDestinationSlot is >= 0 and < 96)
            {
                await _session.SendAsync(
                    PacketBuilder.KitBagSlotIndex(_character, actualDestinationSlot),
                    cancellationToken,
                    "RejectedStorageActualKitBagIndexRefresh");
            }
            return;
        }

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
            PacketBuilder.StorageItemEquipmentBagTransfer(clientEquipmentSlot, actualDestinationSlot),
            cancellationToken,
            "StorageItemUnequipAck");

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

    internal static int ResolveMovedKitBagDestination(
        string previousKitBag,
        string updatedKitBag,
        string movedEquipmentEntry)
    {
        var movedItem = CompactItemEntry.Parse(movedEquipmentEntry);
        if (movedItem.IsEmpty)
        {
            return -1;
        }

        for (var slot = 0; slot < 96; slot++)
        {
            var before = KitBagSlots.GetItem(previousKitBag, slot);
            var after = KitBagSlots.GetItem(updatedKitBag, slot);
            if (before.IsEmpty && after == movedItem)
            {
                return slot;
            }
        }

        return -1;
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
        CancellationToken cancellationToken,
        bool sendStorageTransferAck = false)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (sourceSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem equip ignored: unsupported sourceSlot={sourceSlot}");
            return;
        }

        var previousEquipment = _character.Equipment;
        var previousKitBagEntry = KitBagSlots.GetEntry(_character.KitBag, sourceSlot);
        var kitBagItemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (kitBagItemId == 0)
        {
            Console.WriteLine($"[equip-re] StorageItem equip ignored: empty sourceSlot={sourceSlot}");
            if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot))
            {
                await SendEquipmentBagTransferRejectionRefreshAsync(
                    requestedEquipmentSlot,
                    sourceSlot,
                    cancellationToken);
            }
            return;
        }

        if (itemIdHint != 0 && itemIdHint != kitBagItemId)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip ignored: stale item sourceSlot={sourceSlot} hint={itemIdHint} actual={kitBagItemId}");
            if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot))
            {
                await SendEquipmentBagTransferRejectionRefreshAsync(
                    requestedEquipmentSlot,
                    sourceSlot,
                    cancellationToken);
            }
            return;
        }

        var effectiveItemIdHint = kitBagItemId;
        var updatedCharacter = await _store.MoveKitBagToEquipmentAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            requestedEquipmentSlot,
            cancellationToken,
            requireEmptyEquipmentSlot: sendStorageTransferAck);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip failed: character={_character.Name} id={_character.Id} sourceSlot={sourceSlot}");
            if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot))
            {
                await SendEquipmentBagTransferRejectionRefreshAsync(
                    requestedEquipmentSlot,
                    sourceSlot,
                    cancellationToken);
            }
            return;
        }

        if (string.Equals(
                KitBagSlots.GetEntry(updatedCharacter.KitBag, sourceSlot),
                previousKitBagEntry,
                StringComparison.Ordinal))
        {
            _character = updatedCharacter;
            Console.WriteLine(
                $"[equip-re] StorageItem equip did not move item: character={_character.Name} sourceSlot={sourceSlot} requestedTarget={requestedEquipmentSlot} item={kitBagItemId}");
            if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot))
            {
                await SendEquipmentBagTransferRejectionRefreshAsync(
                    requestedEquipmentSlot,
                    sourceSlot,
                    cancellationToken);
            }

            return;
        }

        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync("equip", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        var equippedSlot = ResolveEquippedSlotForAck(
            _character,
            previousEquipment,
            requestedEquipmentSlot,
            effectiveItemIdHint);
        Console.WriteLine(
            $"[equip-re] equipped character={_character.Name} sourceSlot={sourceSlot} requestedTarget={requestedEquipmentSlot} equippedSlot={equippedSlot} itemHint={effectiveItemIdHint} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");

        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(_character),
            cancellationToken,
            "PlayerStatusUpdate");
        if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(equippedSlot))
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemEquipmentBagTransfer(
                    PacketBuilder.ToClientEquipmentSlot(equippedSlot),
                    sourceSlot),
                cancellationToken,
                "StorageItemEquipmentBagTransferAck");
        }

        if (!sendStorageTransferAck)
        {
            var snapshot = PacketBuilder.EquipmentItemEquipSnapshot(_character, sourceSlot, equippedSlot);
            if (snapshot.Length > 0)
            {
                await _session.SendAsync(
                    snapshot,
                    cancellationToken,
                    "EquipmentItemSnapshot");
            }
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

        await _session.SendAsync(
            PacketBuilder.KitBagSlotIndex(_character, sourceSlot),
            cancellationToken,
            "StorageItemKitBagSourceRefresh");
        if (destinationSlot != sourceSlot)
        {
            await _session.SendAsync(
                PacketBuilder.KitBagSlotIndex(_character, destinationSlot),
                cancellationToken,
                "StorageItemKitBagDestinationRefresh");
        }
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
        Console.WriteLine(
            $"[inventory] deleted kitbag item character={_character.Name} source={sourceSlot} item={itemId}");
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(sourceSlot),
            cancellationToken,
            "StorageItemKitBagDeleteAck");
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

    internal static int ResolveEquippedSlotForAck(
        GameCharacter character,
        string previousEquipment,
        int requestedEquipmentSlot,
        uint itemIdHint)
    {
        if (EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot)
            && EquipmentSlots.GetItemId(character.Equipment, character.Profession, requestedEquipmentSlot) == itemIdHint)
        {
            return requestedEquipmentSlot;
        }

        if (itemIdHint == 0)
        {
            return -1;
        }

        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Stylish; slot++)
        {
            if (EquipmentSlots.GetItemId(character.Equipment, character.Profession, slot) == itemIdHint
                && !string.Equals(
                    EquipmentSlots.GetEntry(previousEquipment, character.Profession, slot),
                    EquipmentSlots.GetEntry(character.Equipment, character.Profession, slot),
                    StringComparison.Ordinal))
            {
                return slot;
            }
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

    internal static bool TryReadStorageItemEquipmentBagTransfer(
        ReadOnlySpan<byte> payload,
        out int equipmentSlot,
        out int bagSlot)
    {
        equipmentSlot = 0;
        bagSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        equipmentSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        var emptyMarker = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        var destinationPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var destinationIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot)
            || emptyMarker != ushort.MaxValue
            || destinationPage >= 4
            || destinationIndex >= 24)
        {
            return false;
        }

        bagSlot = (destinationPage * 24) + destinationIndex;
        return true;
    }

    internal static bool TryReadStorageItemKitBagMove(
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

        const int fullStorageItemRequestPayloadLength = 76;
        var hasStrictEmptyMarkers = marker1 == ushort.MaxValue && marker2 == ushort.MaxValue;
        var isFullStorageItemRequest = payload.Length == fullStorageItemRequestPayloadLength;
        if (!hasStrictEmptyMarkers && !isFullStorageItemRequest)
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

    internal static bool TryReadBreakItemEquip(
        ReadOnlySpan<byte> payload,
        out int sourceSlot)
    {
        sourceSlot = 0;

        if (payload.Length < 12)
        {
            return false;
        }

        // The dword at offset 4 is the currently selected world object. It may
        // be the player, a monster, or an NPC, so it cannot be used to decide
        // whether this is an equip request. Captured clients consistently put
        // the authoritative bag page/index at offsets 8 and 10.
        var sourcePage = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        var sourceIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));
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

    internal static bool MatchesCurrentKitBagItem(GameCharacter? character, int sourceSlot, uint itemId)
    {
        return character is not null
            && sourceSlot is >= 0 and < 96
            && itemId != 0
            && KitBagSlots.GetItemId(character.KitBag, sourceSlot) == itemId;
    }

    internal static byte ReadZodiacTypeFromCreationPayload(ReadOnlySpan<byte> payload)
    {
        var zodiacType = ReadByte(payload, 35, 0);
        return zodiacType <= 11 ? zodiacType : (byte)0;
    }

    private static byte ReadByte(ReadOnlySpan<byte> buffer, int offset, byte fallback)
    {
        return offset >= 0 && offset < buffer.Length ? buffer[offset] : fallback;
    }

    private sealed record PendingUnequipFollowup(int DestinationSlot, uint ItemId, DateTime CreatedUtc);

}

internal enum EquipmentBagTransferAction
{
    Reject,
    Unequip,
    Equip
}
