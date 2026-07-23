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
