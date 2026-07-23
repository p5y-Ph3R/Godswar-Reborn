using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
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
        var result = await _registry.UpgradeZodiacLevelAsync(
            _session,
            _account.Id,
            _character,
            cancellationToken);
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
            return;
        }

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

    private async Task HandleZodiacSkillGridUpgradeAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            Console.WriteLine(
                "[zodiac] rejected skill-grid upgrade without account/character");
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
            return;
        }

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
