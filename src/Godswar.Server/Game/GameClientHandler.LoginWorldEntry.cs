using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
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
        ResetPlayerMovementEcs();
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
        ResetPlayerMovementEcs();
        Console.WriteLine($"[game] created character {_character.Name}");
        await _session.SendAsync(PacketBuilder.CreateRoleSuccess(), cancellationToken, "CreateRoleSuccess");
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
        ResetPlayerMovementEcs();

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

        ResetPlayerMovementEcs();
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

        var npcCatalog = await _registry.PublishMapNpcDefinitionsAsync(
            _character.CurrentMap,
            npcDefinitions,
            _session,
            cancellationToken);
        npcDefinitions = npcCatalog.Definitions.ToList();
        InstallNpcCatalog(npcCatalog);

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

}
