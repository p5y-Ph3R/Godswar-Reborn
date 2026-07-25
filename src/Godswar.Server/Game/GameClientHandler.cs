using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler : IClientHandler
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
    private readonly SemaphoreSlim _characterStateGate = new(1, 1);
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

    private byte[] BuildLocalPlayerStatusUpdate()
    {
        if (_character is null)
        {
            throw new InvalidOperationException(
                "A local player status update requires an active character.");
        }

        // A mounted player must keep the same locomotion multiplier on every
        // later 10166 refresh. Sending the packet builder's walking default
        // after forging, progression, equipment, or inspection silently
        // cancels the client's mount-speed change.
        var status = _registry.GetRuntimeStatusAggregate(
            _session,
            DateTimeOffset.UtcNow);
        return PacketBuilder.PlayerStatusUpdate(
            _character,
            status.MovementSpeedMultiplier);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            StartNpcCatalogUpdates();
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _session.ReadPacketAsync(cancellationToken);
                if (packet is null)
                {
                    return;
                }

                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    await HandlePacketAsync(packet, cancellationToken);
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        finally
        {
            await StopNpcCatalogUpdatesAsync();
            _rideCastLifetime.Cancel();
            if (_rideCastCompletionTask is { } rideCastCompletionTask)
            {
                await rideCastCompletionTask;
            }

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

            _characterStateGate.Dispose();
            _rideCastLifetime.Dispose();
        }
    }

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_session.AllowsPayloadDiagnostics)
        {
            LogReceived(packet);
        }

        if (_session.BoundGamePrincipal is not null &&
            _account is null &&
            packet.Opcode is not (
                Opcodes.LoginGameServer or
                Opcodes.Ping or
                Opcodes.UiHeartbeat))
        {
            _session.Disconnect();
            return;
        }

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
            case Opcodes.PlayerStateAction:
                await HandlePlayerStateActionAsync(packet, cancellationToken);
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

}

internal enum EquipmentBagTransferAction
{
    Reject,
    Unequip,
    Equip
}
