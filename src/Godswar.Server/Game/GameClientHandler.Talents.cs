using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleUseOrEquipAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[talent] upgrade ignored: no active character");
            return;
        }

        var receivedAt = DateTimeOffset.UtcNow;
        if (!LegacyTalentUpgradeCommandAdapter.TryAdapt(
                packet.Payload,
                new CommandSubject(_account.Id, _character.Id),
                _commandConnectionId,
                _session.IsSecure
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp,
                receivedAt,
                out var adapted))
        {
            CommandMetrics.Record(
                CommandFamily.TalentUpgrade,
                CommandIdentityStrength.LegacyAggregateVersion,
                CommandOutcome.Malformed);
            Console.WriteLine(
                "[talent] UseOrEquip ignored: malformed command");
            return;
        }

        if (_talentUpgradeCommands is not null)
        {
            await HandleDurableTalentUpgradeAsync(
                adapted!.Envelope,
                cancellationToken);
            return;
        }

        await HandleCompatibilityTalentUpgradeAsync(
            adapted!,
            receivedAt,
            cancellationToken);
    }

    private async Task HandleDurableTalentUpgradeAsync(
        CommandEnvelope<TalentUpgradeCommand> envelope,
        CancellationToken cancellationToken)
    {
        TalentUpgradeExecutionResult execution;
        try
        {
            execution = await _talentUpgradeCommands!.ExecuteAsync(
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
        catch
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.ProviderUnavailable);
            throw;
        }

        if (!execution.IsSuccess)
        {
            var outcome = execution.Disposition switch
            {
                TalentUpgradeExecutionDisposition
                    .RequestHashConflict =>
                    CommandOutcome.RequestHashConflict,
                TalentUpgradeExecutionDisposition.InvalidIntent =>
                    CommandOutcome.InvalidIntent,
                _ => CommandOutcome.PreconditionFailed
            };
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                outcome);
            Console.WriteLine(
                $"[talent] durable command rejected outcome={outcome}");
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A successful talent command returned no receipt.");
        var duplicate =
            execution.Disposition ==
            TalentUpgradeExecutionDisposition.Duplicate;
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            duplicate
                ? CommandOutcome.Duplicate
                : CommandOutcome.Accepted);

        if (!duplicate)
        {
            _character!.TalentPoints =
                receipt.RemainingTalentPoints;
        }
        else
        {
            _character!.TalentPoints =
                execution.AuthoritativeTalentPoints;
        }

        await RefreshActiveCharacterStatsAsync(
            duplicate
                ? "talent-upgrade-replay"
                : "talent-upgrade",
            cancellationToken);
        _registry.UpdateCharacter(_session, _character);

        var wireResult = ToWireResult(receipt);
        if (!duplicate)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "PlayerStatusUpdate");
        }

        await _session.SendAsync(
            PacketBuilder.TalentUpgradeAck(wireResult),
            cancellationToken,
            "TalentUpgradeAck");

        if (duplicate &&
            execution.AuthoritativeRank != receipt.Rank)
        {
            var currentRank = execution.AuthoritativeRank;
            await _session.SendAsync(
                PacketBuilder.TalentRankList(
                [
                    new TalentState
                    {
                        TalentId = receipt.TalentId,
                        Rank = currentRank,
                        DisplayValue =
                            TalentProgression.CalculateDisplayValue(
                                currentRank),
                        NextCost =
                            TalentProgression.CalculateUpgradeCost(
                                currentRank)
                    }
                ]),
                cancellationToken,
                "TalentRankList");
        }

        if (duplicate)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "PlayerStatusUpdate");
        }
        Console.WriteLine(
            "[talent] durable command completed " +
            $"outcome={(duplicate ? "duplicate" : "committed")} " +
            $"talent={receipt.TalentId} rank={receipt.Rank}");
    }

    private TalentUpgradeResult ToWireResult(
        TalentUpgradeExecutionReceipt receipt) =>
        new()
        {
            Character = _character ??
                throw new InvalidOperationException(
                    "A talent acknowledgement requires a character."),
            TalentId = receipt.TalentId,
            NewRank = receipt.Rank,
            Cost = receipt.Cost,
            RemainingTalentPoints =
                receipt.RemainingTalentPoints,
            DisplayValue = receipt.DisplayValue
        };
}
