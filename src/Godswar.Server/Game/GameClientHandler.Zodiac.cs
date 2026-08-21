using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IZodiacSkillGridActivationCommandExecutor?
        _zodiacSkillGridActivationCommands;

    private async Task HandleZodiacAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !ZodiacSyncRequest.TryParse(packet.Buffer, out var request))
        {
            Console.WriteLine(
                $"[zodiac] rejected malformed sync request len={packet.Buffer.Length}");
            return;
        }

        if (request.IsCharacterUiStatsV1Envelope)
        {
            await HandleCharacterUiStatsV1ProbeAsync(
                request,
                cancellationToken);
            return;
        }

        if (request.IsFullSync)
        {
            await SendZodiacFullSyncAsync(cancellationToken);
            return;
        }

        if (request.IsSkillGridActivation)
        {
            await HandleZodiacSkillGridActivationAsync(
                request,
                cancellationToken);
            return;
        }

        if (request.IsSkillGridUpgrade)
        {
            await HandleZodiacSkillGridUpgradeAsync(
                packet,
                request,
                cancellationToken);
            return;
        }

        if (request.IsSkillGridSelection)
        {
            await HandleZodiacSkillGridSelectionAsync(
                packet,
                request,
                cancellationToken);
            return;
        }

        if (!request.IsLevelUpgrade)
        {
            Console.WriteLine(
                $"[zodiac] ignored unsupported request character={_character.Name} module={request.Module} sid={request.Sid}");
            return;
        }

        if (_account is null)
        {
            Console.WriteLine(
                $"[zodiac] rejected level-up without account character={_character.Name}");
            return;
        }

        // Value1/Value2 are fixed client UI mode values, not authoritative
        // levels, costs, or balances. The store derives every outcome.
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        ZodiacLevelUpgradeResult? result;
        try
        {
            result = await _registry.UpgradeZodiacLevelAsync(
                _session,
                _account.Id,
                _character,
                ownership,
                cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (result is null)
        {
            Console.WriteLine(
                $"[zodiac] rejected level-up ownership mismatch account={_account.Id} character={_character.Name}");
            return;
        }

        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        Console.WriteLine(
            $"[zodiac] level-up account={_account.Id} character={_character.Name} status={result.Status} level={result.PreviousLevel}->{result.CurrentLevel} required-level={result.RequiredCharacterLevel} cost={result.EnergyCost} energy={result.CurrentEnergy}.{result.CurrentEnergyRemainderX100:00} client-values={request.Value1},{request.Value2},{request.Value3}");
        if (result.Committed)
        {
            await _session.SendAsync(
                PacketBuilder.ZodiacLevelUpgrade(
                    result.CurrentLevel,
                    result.CurrentEnergy),
                cancellationToken,
                "ZodiacLevelUpgrade");
        }

        // SID 3 drives the native success animation. The full state that follows
        // also corrects rejected/stale clients and refreshes the new storage cap.
        await SendZodiacFullSyncAsync(cancellationToken);
    }

    private async Task HandleZodiacSkillGridActivationAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            Console.WriteLine(
                "[zodiac] rejected skill-grid activation without account/character");
            return;
        }

        if (request.Value2 != -1 || request.Value3 != 0)
        {
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridActivation,
                CommandIdentityStrength.LegacyAggregateVersion,
                CommandOutcome.InvalidIntent);
            Console.WriteLine(
                "[zodiac] rejected noncanonical skill-grid activation " +
                $"character={_character.Name} " +
                $"reserved-values={request.Value2},{request.Value3}");
            await SendZodiacFullSyncAsync(cancellationToken);
            return;
        }

        if (_zodiacSkillGridActivationCommands is not null)
        {
            await HandleDurableZodiacSkillGridActivationAsync(
                request,
                cancellationToken);
            return;
        }

        await HandleCompatibilityZodiacSkillGridActivationAsync(
            request,
            cancellationToken);
    }

    private async Task HandleCompatibilityZodiacSkillGridActivationAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        // The only trusted client value is the requested zero-based grid
        // index. Cost, current level, ownership, and wallet balance are all
        // derived and committed by the store.
        var result = await _registry.ActivateZodiacSkillGridAsync(
            _session,
            _account.Id,
            _character,
            request.Value1,
            cancellationToken);
        if (result is null)
        {
            Console.WriteLine(
                $"[zodiac] rejected skill-grid ownership mismatch account={_account.Id} character={_character.Name}");
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridActivation,
                CommandIdentityStrength.LegacyAggregateVersion,
                CommandOutcome.PreconditionFailed);
            return;
        }

        CommandMetrics.Record(
            CommandFamily.ZodiacSkillGridActivation,
            CommandIdentityStrength.LegacyAggregateVersion,
            result.Committed
                ? CommandOutcome.Accepted
                : CommandOutcome.PreconditionFailed);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        Console.WriteLine(
            $"[zodiac] skill-grid activation account={_account.Id} character={_character.Name} grid={request.Value1} status={result.Status} cost-gold={result.GoldCost} gold={result.CurrentGold} level={result.CurrentLevel}");
        if (result.Committed)
        {
            // Native SID 100 has no failure branch: only a committed result may
            // receive it or the UI would animate a false activation.
            await _session.SendAsync(
                PacketBuilder.ZodiacSkillGridActivated(result.GridIndex),
                cancellationToken,
                "ZodiacSkillGridActivated");
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "ZodiacSkillGridGoldRefresh");
        }

        // A full sync corrects both successful and rejected/stale client state.
        await SendZodiacFullSyncAsync(cancellationToken);
    }

    private async Task HandleDurableZodiacSkillGridActivationAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }

        if (!ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                request.Value1,
                ZodiacSkillGridActivationCommandEnvelope
                    .ExpectedInactiveLevel,
                out var command))
        {
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridActivation,
                CommandIdentityStrength.LegacyAggregateVersion,
                CommandOutcome.InvalidIntent);
            Console.WriteLine(
                $"[zodiac] durable skill-grid activation rejected invalid grid={request.Value1}");
            await SendZodiacFullSyncAsync(cancellationToken);
            return;
        }

        var unownedEnvelope =
            ZodiacSkillGridActivationCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                _session.IsSecure
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        ZodiacSkillGridActivationExecutionResult execution;
        try
        {
            execution =
                await _zodiacSkillGridActivationCommands!.ExecuteAsync(
                    envelope,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.ProviderUnavailable);
            throw;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var outcome = execution.Disposition switch
        {
            ZodiacSkillGridActivationExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            ZodiacSkillGridActivationExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            ZodiacSkillGridActivationExecutionDisposition
                .RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            ZodiacSkillGridActivationExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            ZodiacSkillGridActivationExecutionDisposition
                .PreconditionFailed =>
                CommandOutcome.PreconditionFailed,
            _ => throw new InvalidDataException(
                "The Zodiac activation executor returned an unknown result.")
        };
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            outcome);

        if (execution.HasAuthoritativeProjection)
        {
            ApplyDurableZodiacSkillGridActivationProjection(
                _character,
                command.GridIndex,
                execution);
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);

            if (execution.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition.Committed)
            {
                await _session.SendAsync(
                    PacketBuilder.ZodiacSkillGridActivated(
                        command.GridIndex),
                    cancellationToken,
                    "ZodiacSkillGridActivated");
            }

            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "ZodiacSkillGridGoldRefresh");
        }

        Console.WriteLine(
            "[zodiac] durable skill-grid activation " +
            $"account={_account.Id} character={_character.Name} " +
            $"grid={command.GridIndex} outcome={outcome} " +
            $"gold={(execution.HasAuthoritativeProjection ? execution.CurrentGold : _character.Gold)} " +
            $"level={(execution.HasAuthoritativeProjection ? execution.CurrentLevel : ZodiacSkillGridCatalog.GetLevel(_character, command.GridIndex))}");
        await SendZodiacFullSyncAsync(cancellationToken);
    }

    internal static void ApplyDurableZodiacSkillGridActivationProjection(
        GameCharacter character,
        int gridIndex,
        ZodiacSkillGridActivationExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(execution);
        if (!execution.HasAuthoritativeProjection ||
            !ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            throw new ArgumentException(
                "A valid authoritative Zodiac projection is required.",
                nameof(execution));
        }

        lock (character.ZodiacSync)
        {
            character.Gold = execution.CurrentGold;
            character.ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels);
            character.ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds);
            character.ZodiacSkillGridLevels[gridIndex] =
                execution.CurrentLevel;
            character.ZodiacSkillGridSkillIds[gridIndex] =
                execution.SelectedSkillId;
        }
    }

    private async Task HandleZodiacSkillGridUpgradeAsync(
        GamePacket packet,
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            Console.WriteLine(
                "[zodiac] rejected skill-grid upgrade without account/character");
            return;
        }

        if (packet.ClientOperationId is { } clientOperationId)
        {
            await HandleDurableZodiacSkillGridUpgradeAsync(
                request,
                clientOperationId,
                cancellationToken);
            return;
        }

        if (_session.IsSecure)
        {
            // The secure shim assigns a fresh UUID to every canonical SID 101
            // click. Missing identity on that transport is not a legacy
            // compatibility signal and must never reach a mutable store path.
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.ZodiacSkillGridUpgrade);
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridUpgrade,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.InvalidIntent);
            Console.Error.WriteLine(
                "[zodiac] rejected secure skill-grid upgrade without " +
                $"operation identity account={_account.Id} " +
                $"character={_character.Name}");
            return;
        }

        await HandleCompatibilityZodiacSkillGridUpgradeAsync(
            request,
            cancellationToken);
    }

    private async Task HandleCompatibilityZodiacSkillGridUpgradeAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return;
        }
        if (!AllowLegacyPlayerMutationFallback(
                "zodiac_skill_grid_upgrade"))
        {
            return;
        }

        // Native Origin.exe sends module 255/SID 101 with only the zero-based
        // grid index as intent. Grid level, Zodiac gate, energy/Talent Point
        // costs, balances, and selected skill all come from server state.
        var result = await _registry.UpgradeZodiacSkillGridAsync(
            _session,
            _account.Id,
            _character,
            request.Value1,
            cancellationToken);
        if (result is null)
        {
            Console.WriteLine(
                $"[zodiac] rejected skill-grid upgrade ownership mismatch account={_account.Id} character={_character.Name}");
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.ZodiacSkillGridUpgrade);
            CommandMetrics.Record(
                CommandFamily.ZodiacSkillGridUpgrade,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.PreconditionFailed);
            return;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.ZodiacSkillGridUpgrade);
        CommandMetrics.Record(
            CommandFamily.ZodiacSkillGridUpgrade,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            result.Committed
                ? CommandOutcome.Accepted
                : CommandOutcome.PreconditionFailed);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        Console.WriteLine(
            $"[zodiac] skill-grid upgrade account={_account.Id} character={_character.Name} grid={request.Value1} status={result.Status} level={result.PreviousLevel}->{result.CurrentLevel} required-zodiac={result.RequiredZodiacLevel} cost-energy={result.EnergyCost} cost-talent={result.TalentPointCost} energy={result.CurrentEnergy}.{result.CurrentEnergyRemainderX100:00} talent={result.CurrentTalentPoints} client-values={request.Value2},{request.Value3}");
        if (result.Committed)
        {
            // The native SID 101 handler increments the displayed grid level
            // unconditionally, so a rejection must never receive this packet.
            await _session.SendAsync(
                PacketBuilder.ZodiacSkillGridUpgraded(result.GridIndex),
                cancellationToken,
                "ZodiacSkillGridUpgraded");
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "ZodiacSkillGridTalentPointRefresh");
        }

        // Success and rejection both receive the full authoritative grid and
        // energy state. On rejection this safely repairs stale client state.
        await SendZodiacFullSyncAsync(cancellationToken);
    }

    private Task SendZodiacFullSyncAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return Task.CompletedTask;
        }

        return _session.SendAsync(
            PacketBuilder.ZodiacFullSync(_character),
            cancellationToken,
            "ZodiacFullSync");
    }
}
